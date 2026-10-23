using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Tzar2.Content.Compiled;
using Tzar2.Replay;
using Tzar2.Sim;
using Tzar2.Sim.Commands;

namespace Tzar2.Game.Session;

/// <summary>
/// The replay viewer's model (§19.4, ADR 0168): a <see cref="GameSession"/> whose ticks come from a <c>.t2rep</c> at a
/// playback speed of 0.25x-16x, with pause, seeking through keyframes (forward: fast-forward; backward: restore the
/// nearest keyframe into a new session, see <see cref="Generation"/>), a perspective (any kingdom's fog, or everything),
/// and hash checking on the way (a replay that no longer matches is reported, never silently shown). The viewer's own
/// commands are discarded.
/// </summary>
public sealed class ReplayPlayback
{
    /// <summary>Speeds offered (multiples of the recorded tick rate).</summary>
    public static readonly ImmutableArray<double> Speeds = [0.25, 0.5, 1, 2, 4, 8, 16];

    private readonly ReplayFile _replay;
    private readonly MatchSetup _setup;
    private readonly Ruleset _ruleset;
    private int _speedIndex = 2;
    private volatile bool _paused;
    private int _mismatchTick = -1;
    private byte? _follow;

    private ReplayPlayback(ReplayFile replay, MatchSetup setup, Ruleset ruleset)
    {
        _replay = replay;
        _setup = setup;
        _ruleset = ruleset;
        Session = NewSession(null, 0);
    }

    /// <summary>Opens replay bytes for viewing.</summary>
    /// <exception cref="InvalidDataException">Not a replay, not rebuildable (no setup description), or recorded with another ruleset or AI.</exception>
    public static ReplayPlayback Open(byte[] replay, Ruleset ruleset, SkirmishAi ai)
    {
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(ai);
        ReplayFile file = ReplayFile.Parse(replay);
        if (file.Header.RulesetHash != ruleset.ContentHash)
        {
            throw new InvalidDataException("The replay was recorded with another ruleset (another package or game version).");
        }

        MatchSetup setup = SetupOf(file.Header, ai);
        if (!file.Header.AiPrograms.SequenceEqual(setup.AiPrograms))
        {
            throw new InvalidDataException("The replay's computer kingdoms ran other AI scripts.");
        }

        return new ReplayPlayback(file, setup, ruleset);
    }

    /// <summary>The setup that re-simulates a replay (multiplayer if a human kingdom had a standby AI, ADR 0163).</summary>
    /// <exception cref="InvalidDataException">The replay has no setup description.</exception>
    public static MatchSetup SetupOf(ReplayHeader header, SkirmishAi ai)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(ai);
        if (header.Setup.IsDefaultOrEmpty)
        {
            throw new InvalidDataException("The replay does not describe its match (a scenario or campaign map); it cannot be rebuilt.");
        }

        MatchDescriptor descriptor = MatchDescriptor.Parse(header.Setup.AsSpan());
        bool multiplayer = header.AiPrograms.Any(p => p.Slot < descriptor.Players.Length && descriptor.Players[p.Slot].Controller == ControllerKind.Human);
        return descriptor.ToSetup(ai, multiplayer, localSlot: null);
    }

    /// <summary>The session being shown (replaced by a backward seek).</summary>
    public GameSession Session { get; private set; }

    /// <summary>Increments whenever <see cref="Session"/> is replaced (the viewer then shows the new one).</summary>
    public int Generation { get; private set; }

    /// <summary>The tick being shown (any thread).</summary>
    public int Tick => CurrentStart + Session.TicksRun;

    /// <summary>Ticks in the replay.</summary>
    public int Length => _replay.Ticks.Count;

    /// <summary>Tick length in milliseconds.</summary>
    public int TickMilliseconds => _replay.Header.Config.TickLengthMilliseconds;

    /// <summary>The players' names by kingdom ("Computer n" for computer kingdoms).</summary>
    public IReadOnlyList<string> Players => _setup.Players.Select(p =>
        p.Slot < _replay.Header.PlayerNames.Length && _replay.Header.PlayerNames[p.Slot].Length > 0 ? _replay.Header.PlayerNames[p.Slot] : $"Computer {p.Slot + 1}").ToArray();

    /// <summary>The recorded chat.</summary>
    public IReadOnlyList<ReplayChat> Chat => _replay.Chat;

    /// <summary>The playback speed, a multiple of the recorded rate.</summary>
    public double Speed => Speeds[_speedIndex];

    /// <summary>Steps the speed up or down.</summary>
    public void ChangeSpeed(int step) => _speedIndex = Math.Clamp(_speedIndex + step, 0, Speeds.Length - 1);

    /// <summary>Whether playback is paused.</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>The first tick whose stored hash did not match the re-simulation, or -1.</summary>
    public int MismatchTick => Volatile.Read(ref _mismatchTick);

    /// <summary>The kingdom whose view is shown (its fog), or null for everything.</summary>
    public byte? Follow
    {
        get => _follow;
        set
        {
            _follow = value is { } p && p < _setup.Players.Count ? p : null;
            Session.View(_follow);
        }
    }

    /// <summary>
    /// Seeks to tick <paramref name="tick"/>: forward by fast-forwarding, backward by restoring the nearest keyframe
    /// into a new <see cref="Session"/> (call between frames; the caller shows the new session and disposes the old).
    /// </summary>
    public void Seek(int tick)
    {
        tick = Math.Clamp(tick, 0, Length);
        int now = Tick;
        if (tick >= now)
        {
            _driver!.FastForwardTo = tick;
            return;
        }

        ReplayKeyframe? keyframe = _replay.KeyframeAtOrBefore(new Core.Ids.Tick(tick));
        Session.Stop();
        Session = NewSession(keyframe, tick);
        Generation++;
    }

    private int CurrentStart => _driver?.Start ?? 0;

    private PlaybackDriver? _driver;

    private GameSession NewSession(ReplayKeyframe? keyframe, int fastForwardTo)
    {
        _driver = new PlaybackDriver(this, keyframe?.Tick.Value ?? 0) { FastForwardTo = fastForwardTo };
        var session = new GameSession(_setup, _ruleset, _driver, keyframe?.Snapshot.ToArray(), fog: false, record: false);
        session.View(_follow);
        return session;
    }

    // Feeds the replay's command sets at the playback speed; checks every stored hash.
    private sealed class PlaybackDriver(ReplayPlayback playback, int start) : ILockstepDriver
    {
        private readonly CommandList _discard = new();
        private long _lastUs = Net.MonotonicClock.NowMicroseconds();
        private double _budget;

        public int Start { get; } = start;

        public int FastForwardTo { get; set; }

        public int SpeedPercent => (int)(playback.Speed * 100);

        public bool Paused => playback.Paused;

        public void Pump(GameSession session)
        {
            session.TakeLocalCommands(_discard); // a replay takes no orders
            long now = Net.MonotonicClock.NowMicroseconds();
            double rate = 1_000_000.0 / (playback.TickMilliseconds * 1000.0) * playback.Speed;
            _budget = playback.Paused ? 0 : Math.Min(_budget + ((now - _lastUs) / 1_000_000.0 * rate), 64);
            _lastUs = now;
        }

        public bool TryTakeTick(int tick, CommandList into)
        {
            if (tick >= playback._replay.Ticks.Count)
            {
                return false;
            }

            if (tick >= FastForwardTo)
            {
                if (_budget < 1)
                {
                    return false;
                }

                _budget--;
            }

            playback._replay.ReadCommands(tick, into);
            return true;
        }

        public void AfterTick(GameSession session, TickResult result)
        {
            ReplayTick record = playback._replay.Ticks[result.Tick.Value];
            bool bad = (record.StateHash is { } s && s != result.StateHash) || (record.FullHash is { } f && f != result.FullHash);
            if (bad)
            {
                Interlocked.CompareExchange(ref playback._mismatchTick, result.Tick.Value, -1);
            }
        }

        public void RequestPause(bool paused) => playback.Paused = paused;

        public void SendChat(ChatTarget target, byte customMask, string text)
        {
            // A replay has no one to talk to.
        }
    }
}
