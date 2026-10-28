using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Tzar2.AI;
using Tzar2.AI.Runtime;
using Tzar2.Content.Compiled;
using Tzar2.Core.Diagnostics;
using Tzar2.Core.Ids;
using Tzar2.Game.Scenario;
using Tzar2.Replay;
using Tzar2.Scripting.Legacy;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Tzar2.Sim.Snapshots;

namespace Tzar2.Game.Session;

/// <summary>
/// A running single-player match (TDD §7.3/§7.4, ADR 0070): the simulation on its own thread at the classic tick rate
/// (scaled by the game speed), a local scheduler that turns the player's batches into the tick's closed command set
/// (stamped with the local slot, like the relay), computer kingdoms run by the legacy AI as a sim system inside the tick
/// (<see cref="LegacyAiSystem"/>, the lockstep path of ADR 0051), a triple-buffered <see cref="GameFrame"/> per tick
/// and the sim event stream for audio and effects.
/// </summary>
/// <remarks>
/// The main thread only calls <see cref="Submit"/>, <see cref="AcquireFrame"/>, <see cref="DrainEvents"/> and the speed
/// and pause setters. <see cref="Step"/> runs ticks synchronously (headless tests, smoke runs) and must not be mixed
/// with <see cref="Start"/>.
/// </remarks>
public sealed class GameSession : IDisposable
{
    /// <summary>Game speeds in percent of the classic tick rate (the original's <c>+</c>/<c>-</c> keys step through them).</summary>
    public static readonly ImmutableArray<int> SpeedSteps = [25, 50, 75, 100, 150, 200, 300];

    private readonly object _gate = new();
    private readonly CommandList[] _pending;
    private readonly CommandList _tickSet = new();
    private readonly List<SimEvent> _events = [];
    private readonly int _slots;
    private readonly FrameExchange _frames = new();
    private readonly bool _fog;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile int _speedIndex = 3;
    private volatile bool _paused;
    private int _stepRequests;
    private Exception? _fault;

    /// <summary>Creates the match at tick 0.</summary>
    /// <param name="setup">Players, settings and the AI script (<see cref="MatchSetup.Ai"/>).</param>
    /// <param name="ruleset">The compiled ruleset named by <see cref="MatchSetup.Ruleset"/>.</param>
    /// <param name="fog">Whether the frames show the local player's fog.</param>
    /// <param name="peers">
    /// The fingerprints of the other peers (multiplayer lobbies, M6); a peer whose match differs — including another
    /// resolved AI script — is refused before the first tick.
    /// </param>
    /// <exception cref="MatchMismatchException">A peer's match differs.</exception>
    public GameSession(MatchSetup setup, Ruleset ruleset, bool fog = true, IReadOnlyList<MatchFingerprint>? peers = null)
        : this(setup, ruleset, fog, peers, snapshot: null, driver: null, record: true)
    {
    }

    /// <summary>
    /// A match for re-simulating a replay of <paramref name="setup"/> (<c>t2 replay verify</c>): tick 0, the same AI and
    /// triggers, no recording, no sim thread.
    /// </summary>
    public static GameSession ForReplay(MatchSetup setup, Ruleset ruleset) => new(setup, ruleset, fog: false, peers: null, snapshot: null, driver: null, record: false);

    /// <summary>A match whose ticks come from a lockstep driver (multiplayer, ADR 0162); restored from <paramref name="snapshot"/> if given.</summary>
    internal GameSession(MatchSetup setup, Ruleset ruleset, ILockstepDriver driver, byte[]? snapshot, bool fog = true, bool record = true)
        : this(setup, ruleset, fog, peers: null, snapshot, driver, record: record && snapshot is null)
    {
    }

    private volatile int _view = -1; // -1: the local player's fog (when fog is on); 255: everything; else that kingdom's fog

    /// <summary>
    /// Shows the frames from kingdom <paramref name="player"/>'s view (its fog), or everything with null (replay viewer,
    /// observers). The commands still belong to <see cref="LocalPlayer"/>.
    /// </summary>
    public void View(byte? player) => _view = player ?? 255;

    private GameSession(MatchSetup setup, Ruleset ruleset, bool fog, IReadOnlyList<MatchFingerprint>? peers, byte[]? snapshot, ILockstepDriver? driver, bool record, SnapshotTolerance tolerance = SnapshotTolerance.Exact)
    {
        _driver = driver;
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(ruleset);
        setup.Validate(ruleset);
        Setup = setup;
        Ruleset = ruleset;
        LocalPlayer = setup.LocalPlayer;
        Map = setup.Map.Create(ruleset, setup);
        Map.Validate(ruleset);
        Fingerprint = MatchFingerprint.Of(setup, ruleset, Map);
        SinglePlayer = (peers is null || peers.Count == 0) && driver is null;
        foreach (MatchFingerprint peer in peers ?? [])
        {
            Fingerprint.EnsureCompatible(peer);
        }

        if (snapshot is null)
        {
            Simulation = new Simulation(ruleset, Map, Config(setup));

            // The kingdoms' civilizations come from the lobby (the tech tree and the AI's script bindings use them).
            foreach (PlayerSetup p in setup.Players)
            {
                if (System.Linq.Enumerable.Contains(ruleset.Civilizations, p.Civilization, StringComparer.Ordinal))
                {
                    Simulation.World.SetCivilization(p.Slot, p.Civilization);
                }
            }
        }
        else
        {
            Simulation = Simulation.Restore(snapshot, ruleset, Map, tolerance: tolerance);
        }

        _slots = setup.Players.Count;
        _pending = new CommandList[_slots];
        for (int i = 0; i < _slots; i++)
        {
            _pending[i] = new CommandList();
        }

        // Computer kingdoms: the legacy AI joins the sim pipeline before the first tick, exactly as on every lockstep
        // peer (ADR 0051): its orders are commands validated in the same tick, its state is in the snapshots (a restored
        // match reads it back, and refuses a snapshot saved with another script).
        var computers = new List<AiPlayerSettings>();
        if (setup.ComputerAi)
        {
            foreach (PlayerSetup p in setup.Players)
            {
                if (p.Controller == ControllerKind.Computer || ((setup.Multiplayer || setup.AiAssist) && p.Controller == ControllerKind.Human))
                {
                    // Multiplayer humans get a standby AI for the drop policy (REQ-NET-07; ADR 0163), and with AI
                    // assistance for the halves of the kingdom its human hands to it (ADR 0183).
                    computers.Add(new AiPlayerSettings
                    {
                        Slot = p.Slot,
                        Program = setup.Ai.ForScript(p.AiScript).Program,
                        Civilization = p.Civilization,
                        Difficulty = SkirmishAi.ToAi(p.Difficulty),
                        Standby = p.Controller == ControllerKind.Human,

                        // The resign flag is offered by the random map and multiplayer screens only (1.09, SHELL-013):
                        // a scenario's computer kingdoms never resign (ReTzar: ai_resign is off in a campaign mission; ADR 0220).
                        MayResign = setup.Scenario is null && setup.AiResign,

                        // A scenario's items are often its story's (ADR 0269): its computer kingdoms leave them alone.
                        TakesItems = setup.Scenario is null,
                    });
                }
            }
        }

        Ai = computers.Count > 0 ? LegacyAiSystem.Attach(Simulation, computers) : null;

        // The scenario's triggers (ADR 0130) join the pipeline the same way; their state is in the snapshot too.
        if (setup.Scenario is { } scenario)
        {
            Scenario = new ScenarioPresenter(LegacyTriggerSystem.Attach(Simulation, scenario, setup.EffectiveTriggerOptions), Simulation.World, LocalPlayer);
        }

        if (snapshot is null)
        {
            setup.Preparation?.Prepare(Simulation, Scenario?.Triggers);
            Scenario?.Refresh();
        }

        _fog = fog;

        // Every match that starts at tick 0 is recorded (REQ-NET-09); a restored one (save, rejoin) is not.
        if (record && snapshot is null)
        {
            MatchDescriptor? descriptor = MatchDescriptor.Of(setup);
            _recorder = new ReplayRecorder(ReplayHeader() with
            {
                Setup = descriptor is null ? [] : [.. descriptor.ToBytes()],
                PlayerNames = descriptor is null ? [] : [.. descriptor.Players.Select(p => p.Name)],
            });
        }

        Publish();
    }

    private readonly ILockstepDriver? _driver;
    private readonly ReplayRecorder? _recorder;
    private readonly List<ChatLine> _chat = [];
    private (int Tick, byte[] Snapshot)[] _keyframes = [];

    /// <summary>Whether the ticks come from the network (multiplayer).</summary>
    public bool IsMultiplayer => _driver is not null;

    /// <summary>Whether this match is being recorded.</summary>
    public bool IsRecording => _recorder is not null;

    /// <summary>
    /// Ends the recording and returns the <c>.t2rep</c> bytes, or null when the match was not recorded. Call after
    /// <see cref="Stop"/> (or without a sim thread).
    /// </summary>
    public byte[]? FinishReplay()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Stop the sim thread before finishing the replay.");
        }

        return _recorder?.Finish();
    }

    /// <summary>The latest keyframe snapshot taken before tick <paramref name="tick"/> (the state before its tick), or null.</summary>
    internal (int Tick, byte[] Snapshot)? KeyframeBefore(int tick)
    {
        (int Tick, byte[] Snapshot)? best = null;
        foreach ((int Tick, byte[] Snapshot) k in _keyframes)
        {
            if (k.Tick <= tick && (best is null || k.Tick > best.Value.Tick))
            {
                best = k;
            }
        }

        return best;
    }

    /// <summary>Moves the chat and system lines of every tick since the last call into <paramref name="into"/> (main thread).</summary>
    public void DrainChat(List<ChatLine> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        lock (_gate)
        {
            into.AddRange(_chat);
            _chat.Clear();
        }
    }

    // A chat or system line for the UI and the replay (any thread; bounded like the events).
    internal void PostChat(ChatLine line, int tick)
    {
        lock (_gate)
        {
            if (_chat.Count < 1024)
            {
                _chat.Add(line);
            }

            if (line.Kind == ChatLineKind.Player && _recorder is not null && _pendingChatRecords.Count < 1024)
            {
                _pendingChatRecords.Add((tick, line));
            }
        }
    }

    private readonly List<(int Tick, ChatLine Line)> _pendingChatRecords = [];

    // The local player's commands queued since the last call (the lockstep driver sends them as one payload).
    internal bool TakeLocalCommands(CommandList into)
    {
        lock (_gate)
        {
            CommandList local = _pending[LocalPlayer];
            if (local.Count == 0)
            {
                return false;
            }

            into.Clear();
            into.AppendStamped(LocalPlayer, local);
            local.Clear();
            return true;
        }
    }

    /// <summary>
    /// Runs the ticks the lockstep driver has (at most <paramref name="maxTicks"/>) on the calling thread, pumping the
    /// network first (headless multiplayer peers, tests). Returns the number of ticks run.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not a multiplayer match, or the sim thread runs.</exception>
    public int PumpLockstep(int maxTicks)
    {
        if (_driver is null || _thread is not null)
        {
            throw new InvalidOperationException("Only a multiplayer match without a sim thread is pumped from outside.");
        }

        return Lockstep(maxTicks);
    }

    private int Lockstep(int maxTicks)
    {
        _driver!.Pump(this);
        int ran = 0;
        while (ran < maxTicks && !_stop && _driver.TryTakeTick(Simulation.World.Tick.Value, _tickSet))
        {
            ExecuteTick();
            ran++;
            if (ran % 16 == 0)
            {
                _driver.Pump(this); // keep the network alive while catching up
            }
        }

        return ran;
    }

    /// <summary>
    /// Continues a match from a snapshot taken by <see cref="CaptureSnapshot"/> (saves, reconnects): the same setup
    /// rebuilds the map and re-attaches the AI, whose state comes from the snapshot.
    /// </summary>
    /// <exception cref="System.IO.InvalidDataException">The snapshot does not belong to this setup (map, ruleset, AI script).</exception>
    /// <param name="saveGame">A player's save: an older format or other rules with the same tables go on (ADR 0271).</param>
    public static GameSession Restore(MatchSetup setup, Ruleset ruleset, byte[] snapshot, bool fog = true, bool saveGame = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new GameSession(setup, ruleset, fog, peers: null, snapshot, driver: null, record: false, saveGame ? SnapshotTolerance.SaveGame : SnapshotTolerance.Exact);
    }

    /// <summary>
    /// Whether the match was restored from a save as it was saved; false when the save was of an older version and
    /// continues under the current rules (ADR 0271), which the game tells the player.
    /// </summary>
    public bool RestoredExactly => Simulation.RestoredExactly;

    private TaskCompletionSource<(byte[] Snapshot, int Tick)>? _snapshotRequest;

    /// <summary>
    /// A snapshot of the running match (quick save; ADR 0133) with the tick it belongs to. It is always taken by the
    /// thread that runs the ticks, between two ticks: with a sim thread, this call posts a request that the thread
    /// serves at the top of its loop (never inside <c>RunTick</c>) and blocks until then; without one it captures
    /// directly. The tick is read together with the snapshot, so they always match.
    /// </summary>
    /// <exception cref="TimeoutException">The sim thread did not answer within 10 s (stopped or faulted).</exception>
    public (byte[] Snapshot, int Tick) CaptureSnapshotNow()
    {
        if (_thread is null)
        {
            return (Simulation.CaptureSnapshot(), Simulation.World.Tick.Value);
        }

        var request = new TaskCompletionSource<(byte[] Snapshot, int Tick)>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _snapshotRequest, request, null) is not null)
        {
            throw new InvalidOperationException("Another snapshot request is pending.");
        }

        return request.Task.Wait(TimeSpan.FromSeconds(10)) ? request.Task.Result : throw new TimeoutException("The sim thread did not take the snapshot.");
    }

    /// <summary>Stops the sim thread (the match stays readable for the campaign layer; ticks only through <see cref="Step"/>).</summary>
    public void Stop()
    {
        if (_thread is null)
        {
            return;
        }

        _stop = true;
        if (_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _thread = null;
            _stop = false;
        }
    }

    // Answers a pending snapshot request from the sim thread (between ticks).
    private void ServeSnapshot()
    {
        if (Interlocked.Exchange(ref _snapshotRequest, null) is { } request)
        {
            try
            {
                request.SetResult((Simulation.CaptureSnapshot(), Simulation.World.Tick.Value));
            }
#pragma warning disable CA1031 // The requester gets the failure.
            catch (Exception e)
#pragma warning restore CA1031
            {
                request.SetException(e);
            }
        }
    }

    /// <summary>A snapshot of the match between ticks (not while the sim thread runs).</summary>
    /// <exception cref="InvalidOperationException">The sim thread is running.</exception>
    public byte[] CaptureSnapshot()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Pause and stop the sim thread before capturing a snapshot.");
        }

        return Simulation.CaptureSnapshot();
    }

    /// <summary>What the peers agreed on (ruleset, map, settings, AI programs).</summary>
    public MatchFingerprint Fingerprint { get; }

    /// <summary>The replay header of this match (with the AI program of every computer kingdom).</summary>
    internal ReplayHeader ReplayHeader() => Tzar2.Replay.ReplayHeader.For(Simulation, GameInfo.Version) with { AiPrograms = Setup.AiPrograms };

    private static SimulationConfig Config(MatchSetup setup) => new()
    {
        MatchSeed = setup.Seed,
        PlayerCount = setup.Players.Count,
        StartingResources = setup.StartingResources,
        PopulationLimit = setup.PopulationLimit,
        Victory = setup.Victory,
        GameType = setup.GameType,
        ScoreLimit = setup.ScoreLimit,
        TimeLimitMinutes = setup.TimeLimitMinutes,
        NoFog = setup.NoFog,
        Explored = setup.Explored,
        DayNight = setup.DayNight,
        Weather = setup.Weather,
        Teams = setup.Teams,
        CheatsAllowed = setup.CheatsAllowed,
        Ranked = setup.Ranked,
    };

    /// <summary>The setup.</summary>
    public MatchSetup Setup { get; }

    /// <summary>Whether this is a local single-player match (no peers): scenario prompts pause it (REQ-SCR-03).</summary>
    public bool SinglePlayer { get; private init; } = true;

    /// <summary>The scenario presentation (quests, conversations, story screens), or null without a scenario.</summary>
    public ScenarioPresenter? Scenario { get; }

    /// <summary>
    /// Single player pauses while a conversation waits for the player's choice (Appendix C.8; ADR 0131): the sim thread
    /// holds the clock until a reply is submitted, then runs the tick that carries it.
    /// </summary>
    /// <remarks>
    /// Two volatile flags read without a common lock: the worst case of a stale read is one more 5 ms sleep of the sim
    /// thread before the reply's tick (review of #32; accepted).
    /// </remarks>
    public bool HoldsForScenario => _holdForScenario && !_replyQueued;

    private volatile bool _holdForScenario;
    private volatile bool _replyQueued;
    private readonly List<ScenarioNotice> _notices = [];

    /// <summary>Moves the scenario notices of every tick since the last call into <paramref name="into"/> (main thread).</summary>
    public void DrainNotices(List<ScenarioNotice> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        lock (_gate)
        {
            into.AddRange(_notices);
            _notices.Clear();
        }
    }

    /// <summary>Answers the waiting conversation with candidate <paramref name="position"/> (main thread).</summary>
    public void ReplyToConversation(int serial, int position)
    {
        var batch = new CommandList();
        ScenarioPresenter.Reply(batch, serial, position);
        Submit(batch);
    }

    /// <summary>The AI of the computer kingdoms, or null when there are none (diagnostics; owned by the sim thread).</summary>
    internal LegacyAiSystem? Ai { get; }

    /// <summary>The ruleset.</summary>
    public Ruleset Ruleset { get; }

    /// <summary>The map.</summary>
    public MapData Map { get; }

    /// <summary>The local slot.</summary>
    public byte LocalPlayer { get; }

    /// <summary>The simulation. Owned by the sim thread while <see cref="IsRunning"/>; tests read it after <see cref="Step"/>.</summary>
    internal Simulation Simulation { get; }

    /// <summary>Whether the sim thread runs.</summary>
    public bool IsRunning => _thread is not null;

    /// <summary>Ticks run so far (any thread).</summary>
    public int TicksRun => Volatile.Read(ref _ticksRun);

    private int _ticksRun;

    /// <summary>The exception that stopped the sim thread, or null.</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>Index into <see cref="SpeedSteps"/> (fixed by the lobby in multiplayer: changes are ignored).</summary>
    public int SpeedIndex
    {
        get => _speedIndex;
        set
        {
            if (_driver is null)
            {
                _speedIndex = Math.Clamp(value, 0, SpeedSteps.Length - 1);
                TraceSession(LogLevel.Info, $"speed {SpeedSteps[_speedIndex]} %");
            }
        }
    }

    /// <summary>Game speed in percent.</summary>
    public int SpeedPercent => _driver?.SpeedPercent ?? SpeedSteps[_speedIndex];

    /// <summary>Whether the match is paused (multiplayer: asks the relay, which pauses every peer; REQ-NET-07 pauses).</summary>
    public bool Paused
    {
        get => _driver?.Paused ?? _paused;
        set
        {
            if (_driver is not null)
            {
                _driver.RequestPause(value);
            }
            else
            {
                _paused = value;
            }

            TraceSession(LogLevel.Info, value ? "paused" : "resumed");
        }
    }

    /// <summary>The commands of the latest tick; read it on the sim thread in <see cref="AfterTick"/> only (ADR 0176).</summary>
    public CommandList LastTickCommands => _tickSet;

    /// <summary>The result (tick and hashes) of the latest tick; read it on the sim thread in <see cref="AfterTick"/>.</summary>
    public TickResult LastTickResult { get; private set; }

    /// <summary>How long each tick took on this machine (ms), for the render bench (ADR 0244); never part of the match.</summary>
    public Diagnostics.SampleRing TickTimes { get; } = new();

    /// <summary>
    /// Per-section tick times (ADR 0244; null: off). Set it before <see cref="Start"/> or between ticks: the sim thread
    /// feeds it, and it never changes the match.
    /// </summary>
    public Diagnostics.PhaseTimes? Phases
    {
        get => _phases;
        set
        {
            _phases = value;
            Simulation.Profiler = value;
        }
    }

    private Diagnostics.PhaseTimes? _phases;

    private void TraceSession(LogLevel level, string message)
    {
        ITraceSink trace = Simulation.World.Trace;
        if (trace.IsEnabled(TraceArea.Session, level))
        {
            trace.Write(TraceArea.Session, level, Simulation.World.Tick.Value, message);
        }
    }

    /// <summary>
    /// Runs one tick while paused (the editor's trigger debugger "step", ADR 0153); ignored when the session is not
    /// paused or has no sim thread.
    /// </summary>
    public void StepPaused() => Interlocked.Increment(ref _stepRequests);

    /// <summary>
    /// Raised on the sim thread after every tick, before the frame is published (the trigger debugger reads the trigger
    /// state there, ADR 0153). Handlers must not change the simulation.
    /// </summary>
    public event Action<GameSession>? AfterTick;

    /// <summary>
    /// Queues the local player's commands for the next tick (main thread). The list is copied; its player fields are
    /// ignored and stamped with <see cref="LocalPlayer"/> (ADR 0009).
    /// </summary>
    public void Submit(CommandList batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (_gate)
        {
            _pending[LocalPlayer].AppendStamped(LocalPlayer, batch);
        }

        for (int i = 0; i < batch.Count; i++)
        {
            if (batch[i].Kind == CommandKind.ScenarioReply)
            {
                _replyQueued = true;
            }
        }
    }

    /// <summary>Queues commands of another slot (tests: what a remote peer would send).</summary>
    internal void SubmitAs(byte slot, CommandList batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, _slots);
        lock (_gate)
        {
            _pending[slot].AppendStamped(slot, batch);
        }
    }

    /// <summary>The newest frame (main thread).</summary>
    public GameFrame? AcquireFrame(out bool fresh) => _frames.AcquireLatest(out fresh);

    /// <summary>Moves the events of every tick since the last call into <paramref name="into"/> (main thread).</summary>
    public void DrainEvents(List<SimEvent> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        lock (_gate)
        {
            into.AddRange(_events);
            _events.Clear();
        }
    }

    /// <summary>Starts the sim thread.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "Tzar2 sim" };
        _thread.Start();
    }

    /// <summary>Runs <paramref name="ticks"/> ticks on the calling thread (without the sim thread).</summary>
    public void Step(int ticks)
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("The sim thread is running.");
        }

        for (int i = 0; i < ticks; i++)
        {
            RunTick();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop = true;
        if (_thread is not null && !_thread.Join(TimeSpan.FromSeconds(5)))
        {
            // The sim thread is stuck in a tick: leave the simulation to it rather than dispose it under its feet.
            System.Diagnostics.Trace.TraceWarning("Tzar2 sim thread did not stop within 5 s; the simulation is not disposed.");
            return;
        }

        _thread = null;
        Simulation.Dispose();
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        double due = 0;
        try
        {
            while (!_stop && _driver is not null)
            {
                // Multiplayer: the relay's closed ticks are the clock (ADR 0162).
                ServeSnapshot();
                if (Lockstep(256) == 0)
                {
                    Thread.Sleep(1);
                }
            }

            while (!_stop)
            {
                ServeSnapshot();
                double now = clock.Elapsed.TotalMilliseconds;
                if (_paused && Volatile.Read(ref _stepRequests) > 0 && !HoldsForScenario)
                {
                    Interlocked.Decrement(ref _stepRequests);
                    RunTick();
                    due = now;
                    continue;
                }

                if (_paused || HoldsForScenario)
                {
                    due = now;
                    Thread.Sleep(5);
                    continue;
                }

                double interval = Simulation.World.Config.TickLengthMilliseconds * 100.0 / SpeedPercent;
                if (now < due)
                {
                    Thread.Sleep(Math.Max(0, Math.Min(5, (int)(due - now))));
                    continue;
                }

                RunTick();

                // Do not try to catch up more than a few ticks after a stall.
                due = Math.Max(due + interval, now - (interval * 4));
            }
        }
#pragma warning disable CA1031 // The sim thread reports any fault to the main thread (ADR 0019: a faulted sim stops).
        catch (Exception e)
#pragma warning restore CA1031
        {
            Volatile.Write(ref _fault, e);
        }
    }

    private void RunTick()
    {
        if (_driver is not null)
        {
            // Headless multiplayer (Step): wait briefly for the relay's closure instead of making one up.
            var wait = Stopwatch.StartNew();
            while (Lockstep(1) == 0 && wait.ElapsedMilliseconds < 5_000 && !_stop)
            {
                Thread.Sleep(1);
            }

            return;
        }

        _tickSet.Clear();
        lock (_gate)
        {
            // Per slot, like the relay's closed command set; the AI's commands are issued inside the tick.
            for (int slot = 0; slot < _slots; slot++)
            {
                _tickSet.AppendStamped((byte)slot, _pending[slot]);
                _pending[slot].Clear();
            }
        }

        ExecuteTick();
    }

    // Runs one tick with the command set in _tickSet (local scheduler or lockstep closure), records it and publishes.
    private void ExecuteTick()
    {
        World world = Simulation.World;
        if (_recorder is not null && _recorder.IsKeyframeDue(world.Tick))
        {
            byte[] keyframe = Simulation.CaptureSnapshot();
            _recorder.RecordKeyframe(world.Tick, keyframe);
            KeepKeyframe(world.Tick.Value, keyframe);
        }
        else if (_recorder is null && _driver is not null && world.Tick.Value % ReplayKeyframeInterval == 0)
        {
            KeepKeyframe(world.Tick.Value, Simulation.CaptureSnapshot()); // desync reports need one even unrecorded
        }

        bool replied = _replyQueued;
        long started = Stopwatch.GetTimestamp();
        TickResult result = Simulation.Tick(_tickSet);
        double tickMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        TickTimes.Add(tickMs);
        _phases?.EndTick(tickMs);
        LastTickResult = result;
        _recorder?.RecordTick(_tickSet, result);
        Interlocked.Increment(ref _ticksRun);
        if (_recorder is not null)
        {
            lock (_gate)
            {
                foreach ((int tick, ChatLine line) in _pendingChatRecords)
                {
                    _recorder.RecordChat(tick, line.From, line.Recipients, line.Text);
                }

                _pendingChatRecords.Clear();
            }
        }

        lock (_gate)
        {
            // Bounded: a reader that stops draining (minimised window) must not grow the list forever.
            if (_events.Count < 65536)
            {
                _events.AddRange(world.Events.Events);
            }

            if (Scenario is { } scenario && _notices.Count < 4096)
            {
                scenario.Update(world.Events.Events, _notices);
            }
        }

        if (replied)
        {
            _replyQueued = false;
        }

        _holdForScenario = Scenario is { AwaitingReply: true } && SinglePlayer;
        AfterTick?.Invoke(this);
        Publish();
        _driver?.AfterTick(this, result);
    }

    /// <summary>Ticks between keyframes (REQ-NET-09: 60 s of game time).</summary>
    internal int ReplayKeyframeInterval => _recorder?.Header.KeyframeIntervalTicks ?? 1200;

    // The two latest keyframes (desync reports send the last one every peer agreed on).
    private void KeepKeyframe(int tick, byte[] snapshot)
    {
        _keyframes = _keyframes.Length < 2 ? [.. _keyframes, (tick, snapshot)] : [_keyframes[1], (tick, snapshot)];
    }

    /// <summary>
    /// Submits a chat line typed by the local player: an <c>hm*</c> code becomes its cheat command (GAME-19; the sim refuses
    /// it unless the match allows cheats). Other text is chat, which needs multiplayer (M6).
    /// </summary>
    /// <param name="text">The line.</param>
    /// <param name="x">Where <c>hmspawn</c> spawns (world units; e.g. the view centre).</param>
    /// <param name="y">Where <c>hmspawn</c> spawns.</param>
    public bool SubmitChat(string text, int x = 0, int y = 0) => SubmitChat(text, ChatTarget.All, 0, x, y);

    /// <summary>
    /// Submits a chat line: an <c>hm*</c> code becomes its cheat command (GAME-19; the sim refuses it unless the lobby
    /// allowed cheats, never in a ranked match); in multiplayer any other text goes to <paramref name="target"/> through
    /// the relay's chat channel (REQ-NET-03). Returns false for plain text in single player.
    /// </summary>
    /// <param name="text">The line.</param>
    /// <param name="target">Everyone, the allies, the enemies, or the slots in <paramref name="customMask"/>.</param>
    /// <param name="customMask">Recipient slots for <see cref="ChatTarget.Custom"/>.</param>
    /// <param name="x">Where <c>hmspawn</c> spawns (world units; e.g. the view centre).</param>
    /// <param name="y">Where <c>hmspawn</c> spawns.</param>
    public bool SubmitChat(string text, ChatTarget target, byte customMask = 0, int x = 0, int y = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        var batch = new CommandList();
        if (Input.CheatCodes.TryParse(text, Ruleset, batch, x, y))
        {
            Submit(batch);
            return true;
        }

        if (_driver is null || text.Trim().Length == 0)
        {
            return false;
        }

        _driver.SendChat(target, customMask, text);
        return true;
    }

    // GAME-17: the outcome follows the sim's victory evaluation (VictorySystem).
    private MatchOutcome Outcome()
    {
        PlayerFlags flags = Simulation.World.Players[LocalPlayer].Flags;
        return (flags & PlayerFlags.Victorious) != 0 ? MatchOutcome.Victory
            : (flags & PlayerFlags.Defeated) != 0 ? MatchOutcome.Defeat
            : MatchOutcome.None;
    }

    private void Publish()
    {
        GameFrame frame = _frames.Back;
        int view = _view;
        byte? fog = view == 255 ? null : view >= 0 ? (byte)view : _fog ? LocalPlayer : null;
        FrameBuilder.Fill(frame, Simulation.World, fog, Outcome(), LocalPlayer);
        frame.SpeedPercent = SpeedPercent;
        frame.Paused = Paused;
        frame.Scenario = Scenario?.View;
        _frames.Publish();
    }
}
