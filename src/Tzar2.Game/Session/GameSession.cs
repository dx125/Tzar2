using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Tzar2.AI;
using Tzar2.AI.Runtime;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Tzar2.Game.Scenario;
using Tzar2.Replay;
using Tzar2.Scripting.Legacy;

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
    public static readonly int[] SpeedSteps = [25, 50, 75, 100, 150, 200, 300];

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
        : this(setup, ruleset, fog, peers, snapshot: null)
    {
    }

    private GameSession(MatchSetup setup, Ruleset ruleset, bool fog, IReadOnlyList<MatchFingerprint>? peers, byte[]? snapshot)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(ruleset);
        setup.Validate(ruleset);
        Setup = setup;
        Ruleset = ruleset;
        LocalPlayer = setup.LocalPlayer;
        Map = setup.Map.Create(ruleset, setup);
        Map.Validate(ruleset);
        Fingerprint = MatchFingerprint.Of(setup, ruleset, Map);
        SinglePlayer = peers is null || peers.Count == 0;
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
            Simulation = Simulation.Restore(snapshot, ruleset, Map);
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
                if (p.Controller == ControllerKind.Computer)
                {
                    computers.Add(new AiPlayerSettings { Slot = p.Slot, Program = setup.Ai.Program, Civilization = p.Civilization, Difficulty = SkirmishAi.ToAi(p.Difficulty) });
                }
            }
        }

        Ai = computers.Count > 0 ? LegacyAiSystem.Attach(Simulation, computers) : null;

        // The scenario's triggers (ADR 0130) join the pipeline the same way; their state is in the snapshot too.
        if (setup.Scenario is { } scenario)
        {
            Scenario = new ScenarioPresenter(LegacyTriggerSystem.Attach(Simulation, scenario, setup.EffectiveTriggerOptions), Simulation.World, LocalPlayer);
        }

        _fog = fog;
        Publish();
    }

    /// <summary>
    /// Continues a match from a snapshot taken by <see cref="CaptureSnapshot"/> (saves, reconnects): the same setup
    /// rebuilds the map and re-attaches the AI, whose state comes from the snapshot.
    /// </summary>
    /// <exception cref="System.IO.InvalidDataException">The snapshot does not belong to this setup (map, ruleset, AI script).</exception>
    public static GameSession Restore(MatchSetup setup, Ruleset ruleset, byte[] snapshot, bool fog = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new GameSession(setup, ruleset, fog, peers: null, snapshot);
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
    public ReplayHeader ReplayHeader() => Tzar2.Replay.ReplayHeader.For(Simulation, GameInfo.Version) with { AiPrograms = Setup.AiPrograms };

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
    public LegacyAiSystem? Ai { get; }

    /// <summary>The ruleset.</summary>
    public Ruleset Ruleset { get; }

    /// <summary>The map.</summary>
    public MapData Map { get; }

    /// <summary>The local slot.</summary>
    public byte LocalPlayer { get; }

    /// <summary>The simulation. Owned by the sim thread while <see cref="IsRunning"/>; tests read it after <see cref="Step"/>.</summary>
    public Simulation Simulation { get; }

    /// <summary>Whether the sim thread runs.</summary>
    public bool IsRunning => _thread is not null;

    /// <summary>Ticks run so far (any thread).</summary>
    public int TicksRun => Volatile.Read(ref _ticksRun);

    private int _ticksRun;

    /// <summary>The exception that stopped the sim thread, or null.</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>Index into <see cref="SpeedSteps"/>.</summary>
    public int SpeedIndex
    {
        get => _speedIndex;
        set => _speedIndex = Math.Clamp(value, 0, SpeedSteps.Length - 1);
    }

    /// <summary>Game speed in percent.</summary>
    public int SpeedPercent => SpeedSteps[_speedIndex];

    /// <summary>Whether the match is paused.</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

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
            while (!_stop)
            {
                double now = clock.Elapsed.TotalMilliseconds;
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
        World world = Simulation.World;
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

        bool replied = _replyQueued;
        Simulation.Tick(_tickSet);
        Interlocked.Increment(ref _ticksRun);

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

        Publish();
    }

    /// <summary>
    /// Submits a chat line typed by the local player: an <c>hm*</c> code becomes its cheat command (GAME-19; the sim refuses
    /// it unless the match allows cheats). Other text is chat, which needs multiplayer (M6).
    /// </summary>
    /// <param name="text">The line.</param>
    /// <param name="x">Where <c>hmspawn</c> spawns (world units; e.g. the view centre).</param>
    /// <param name="y">Where <c>hmspawn</c> spawns.</param>
    public bool SubmitChat(string text, int x = 0, int y = 0)
    {
        var batch = new CommandList();
        if (!Input.CheatCodes.TryParse(text, Ruleset, batch, x, y))
        {
            return false;
        }

        Submit(batch);
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
        FrameBuilder.Fill(frame, Simulation.World, _fog ? LocalPlayer : null, Outcome());
        frame.SpeedPercent = SpeedPercent;
        frame.Paused = _paused;
        frame.Scenario = Scenario?.View;
        _frames.Publish();
    }
}
