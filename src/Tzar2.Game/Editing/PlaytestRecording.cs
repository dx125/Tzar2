using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using Tzar2.Content.Compiled;
using Tzar2.Content.Editing;
using Tzar2.Core.Serialization;
using Tzar2.Game.Session;
using Tzar2.Replay;
using Tzar2.Sim;
using Tzar2.Sim.Commands;

namespace Tzar2.Game.Editing;

/// <summary>
/// A playtest saved as one file (ADR 0291): the map as it was played (its <c>.t2map</c> bytes), the playtest's settings
/// and the replay of the timeline the tester ended on. Anyone with the same game opens it in the editor and watches the
/// playtest happen again, tick for tick, so a map maker can attach it to a bug report.
/// </summary>
/// <remarks>
/// Layout: magic <c>T2PL</c>, format version, the settings (kingdom, reveal, computer AI, cheats), the map package and
/// the <c>.t2rep</c> bytes, each with its length. The replay's header has no setup description: the map beside it
/// rebuilds the match, and its hash must be the one the replay recorded.
/// </remarks>
/// <param name="Options">The playtest's settings.</param>
/// <param name="Map">The map package as it was played.</param>
/// <param name="Replay">The replay.</param>
public sealed record PlaytestRecording(PlaytestOptions Options, ImmutableArray<byte> Map, ImmutableArray<byte> Replay)
{
    /// <summary>The file extension.</summary>
    public const string Extension = ".t2play";

    /// <summary>"T2PL" little-endian.</summary>
    public const uint Magic = 0x4C503254;

    /// <summary>The format version.</summary>
    public const int FormatVersion = 1;

    /// <summary>The largest file read (a map package and a long replay).</summary>
    public const int MaxBytes = 512 << 20;

    /// <summary>The file's bytes.</summary>
    public byte[] ToBytes()
    {
        var w = new BinaryBufferWriter(Map.Length + Replay.Length + 64);
        w.WriteUInt32(Magic);
        w.WriteVarUInt64(FormatVersion);
        w.WriteByte((byte)Math.Clamp(Options.Kingdom, 0, 8));
        w.WriteBoolean(Options.RevealMap);
        w.WriteBoolean(Options.ComputerAi);
        w.WriteBoolean(Options.Cheats);
        w.WriteVarUInt64((ulong)Map.Length);
        w.WriteBytes(Map.AsSpan());
        w.WriteVarUInt64((ulong)Replay.Length);
        w.WriteBytes(Replay.AsSpan());
        return w.ToArray();
    }

    /// <summary>Reads a recording.</summary>
    /// <exception cref="InvalidDataException">Not a playtest recording, or damaged.</exception>
    public static PlaytestRecording Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxBytes)
        {
            throw new InvalidDataException("The playtest recording is too large.");
        }

        var r = new BinaryBufferReader(data);
        if (r.ReadUInt32() != Magic)
        {
            throw new InvalidDataException("Not a playtest recording.");
        }

        ulong version = r.ReadVarUInt64();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"A playtest recording of format {version}; this game reads format {FormatVersion}.");
        }

        byte kingdom = r.ReadByte();
        if (kingdom > 8)
        {
            throw new InvalidDataException("The recording plays an unknown kingdom.");
        }

        var options = new PlaytestOptions(kingdom, r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean());
        ImmutableArray<byte> map = Section(ref r);
        ImmutableArray<byte> replay = Section(ref r);
        return r.IsAtEnd ? new PlaytestRecording(options, map, replay) : throw new InvalidDataException("Trailing bytes.");
    }

    private static ImmutableArray<byte> Section(ref BinaryBufferReader r)
    {
        ulong length = r.ReadVarUInt64();
        if (length > (ulong)r.Remaining)
        {
            throw new InvalidDataException("The playtest recording is cut short.");
        }

        return [.. r.ReadBytes((int)length)];
    }

    /// <summary>The map as it was played, as a document (unsaved: it is the tester's copy).</summary>
    /// <exception cref="InvalidDataException">The map does not read.</exception>
    public MapDocument Document(Func<string?, TerrainRules> rules) => MapPackage.Read([.. Map], rules, "recording.t2map");

    /// <summary>The match the recording replays, built from <paramref name="document"/> as the playtest built it.</summary>
    public MatchSetup Setup(MapDocument document, Ruleset ruleset) => Playtest.Setup(Playtest.Mission(document), ruleset, Options);

    /// <summary>The recording in the replay viewer.</summary>
    /// <exception cref="InvalidDataException">Recorded with another ruleset, AI or map.</exception>
    public ReplayPlayback Playback(MapDocument document, Ruleset ruleset) => ReplayPlayback.Open([.. Replay], ruleset, Setup(document, ruleset));

    /// <summary>Re-simulates the recording headless and checks every stored hash (tests, bug triage).</summary>
    /// <exception cref="InvalidDataException">The rebuilt map is not the recorded one.</exception>
    public ReplayVerification Verify(MapDocument document, Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ReplayFile replay = ReplayFile.Parse([.. Replay]);
        using GameSession session = GameSession.ForReplay(Setup(document, ruleset), ruleset);
        if (session.Simulation.MapHash != replay.Header.MapHash)
        {
            throw new InvalidDataException("The recording was made on another version of the map.");
        }

        return ReplayVerifier.Verify(replay, session.Simulation);
    }
}

/// <summary>
/// Records a playtest for <see cref="PlaytestRecording"/> (ADR 0291): every tick's command set and hashes from the
/// first tick, following the playtest's rewinds (ADR 0285): a rewind to a point drops what was recorded after it, so the
/// recording is the timeline the tester ended on, and it replays exactly (the restored match plays as the original did).
/// </summary>
/// <remarks>The ticks are recorded on the sim thread; a rewind and <see cref="Finish"/> come from the main thread.</remarks>
public sealed class PlaytestRecorder : IDisposable
{
    private readonly object _gate = new();
    private readonly BinaryBufferWriter _commands = new(16 * 1024);
    private readonly List<(int Offset, TickResult Result)> _ticks = [];
    private readonly ReplayHeader _header;
    private readonly ImmutableArray<byte> _map;
    private readonly PlaytestOptions _options;
    private readonly MatchRewind? _rewind;
    private GameSession _session;
    private bool _broken;

    /// <summary>Starts recording a playtest that has not run a tick yet.</summary>
    /// <param name="session">The playtest's session at tick 0.</param>
    /// <param name="document">The map played (its bytes are taken now: later edits are not in the recording).</param>
    /// <param name="options">The playtest's settings.</param>
    /// <param name="rewind">The playtest's rewind, if it has one: its rewinds are followed.</param>
    /// <exception cref="InvalidOperationException">The session already ran.</exception>
    public PlaytestRecorder(GameSession session, MapDocument document, PlaytestOptions options, MatchRewind? rewind = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        if (session.IsRunning || session.Simulation.World.Tick.Value != 0)
        {
            throw new InvalidOperationException("A playtest is recorded from its first tick.");
        }

        _header = session.ReplayHeader();
        _map = [.. MapPackage.Write(document)];
        _options = options;
        _session = session;
        _rewind = rewind;
        session.AfterTick += OnTick;
        if (rewind is not null)
        {
            rewind.Rewound += OnRewound;
        }
    }

    /// <summary>The ticks recorded on the current timeline.</summary>
    public int Ticks
    {
        get
        {
            lock (_gate)
            {
                return _ticks.Count;
            }
        }
    }

    /// <summary>The recording so far.</summary>
    /// <exception cref="InvalidOperationException">A tick was missed (the recorder was attached to another match).</exception>
    public PlaytestRecording Finish()
    {
        lock (_gate)
        {
            if (_broken)
            {
                throw new InvalidOperationException("The playtest recording missed ticks.");
            }

            var recorder = new ReplayRecorder(_header);
            var commands = new CommandList();
            var reader = new BinaryBufferReader(_commands.WrittenSpan);
            foreach ((int _, TickResult result) in _ticks)
            {
                commands.Clear();
                CommandCodec.ReadTickSet(ref reader, commands);
                recorder.RecordTick(commands, result);
            }

            return new PlaytestRecording(_options, _map, [.. recorder.Finish()]);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _session.AfterTick -= OnTick;
        if (_rewind is not null)
        {
            _rewind.Rewound -= OnRewound;
        }
    }

    // Sim thread, after every tick of the current session.
    private void OnTick(GameSession session)
    {
        lock (_gate)
        {
            TickResult result = session.LastTickResult;
            if (result.Tick.Value != _ticks.Count)
            {
                _broken = true;
                return;
            }

            int offset = _commands.Length;
            CommandCodec.WriteTickSet(_commands, result.Tick, session.LastTickCommands);
            _ticks.Add((offset, result));
        }
    }

    // Main thread: the match goes on from a point; what was recorded after it is not this timeline any more.
    private void OnRewound(GameSession next)
    {
        lock (_gate)
        {
            int tick = next.Simulation.World.Tick.Value;
            if (tick > _ticks.Count)
            {
                _broken = true;
            }
            else if (tick < _ticks.Count)
            {
                _commands.Truncate(_ticks[tick].Offset);
                _ticks.RemoveRange(tick, _ticks.Count - tick);
            }

            _session.AfterTick -= OnTick;
            _session = next;
            next.AfterTick += OnTick;
        }
    }
}
