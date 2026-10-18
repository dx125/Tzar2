using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;

namespace Tzar2.Game.Session;

/// <summary>
/// A running single-player match (TDD §7.3/§7.4, ADR 0070): the simulation on its own thread at the classic tick rate
/// (scaled by the game speed), a local scheduler that turns the player's batches into the tick's closed command set
/// (stamped with the local slot, like the relay), computer players through <see cref="IPlayerController"/>, a
/// triple-buffered <see cref="GameFrame"/> per tick and the sim event stream for audio and effects.
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
    private readonly CommandList _pending = new();
    private readonly CommandList _batch = new();
    private readonly CommandList _tickSet = new();
    private readonly List<SimEvent> _events = [];
    private readonly IPlayerController?[] _controllers;
    private readonly FrameExchange _frames = new();
    private readonly bool _fog;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile int _speedIndex = 3;
    private volatile bool _paused;
    private Exception? _fault;

    /// <summary>Creates the match at tick 0.</summary>
    /// <param name="setup">Players and settings.</param>
    /// <param name="ruleset">The compiled ruleset named by <see cref="MatchSetup.Ruleset"/>.</param>
    /// <param name="controllers">Controller factory for computer slots (null = <see cref="PlaceholderAi"/>).</param>
    /// <param name="fog">Whether to compute fog for the local player.</param>
    public GameSession(MatchSetup setup, Ruleset ruleset, Func<PlayerSetup, IPlayerController>? controllers = null, bool fog = true)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(ruleset);
        setup.Validate(ruleset);
        Setup = setup;
        Ruleset = ruleset;
        LocalPlayer = setup.LocalPlayer;
        Map = setup.Map.Create(ruleset, setup);
        Map.Validate(ruleset);
        Simulation = new Simulation(
            ruleset,
            Map,
            new SimulationConfig
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
            });
        _controllers = new IPlayerController?[setup.Players.Count];
        foreach (PlayerSetup p in setup.Players)
        {
            if (p.Controller == ControllerKind.Computer)
            {
                _controllers[p.Slot] = controllers?.Invoke(p) ?? new PlaceholderAi(p.Difficulty);
            }
        }

        _fog = fog;
        Publish();
    }

    /// <summary>The setup.</summary>
    public MatchSetup Setup { get; }

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
            _pending.AppendStamped(LocalPlayer, batch);
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
                if (_paused)
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
        for (int slot = 0; slot < _controllers.Length; slot++)
        {
            if (slot == LocalPlayer)
            {
                lock (_gate)
                {
                    _tickSet.AppendStamped(LocalPlayer, _pending);
                    _pending.Clear();
                }
            }

            if (_controllers[slot] is { } controller && (world.Players[slot].Flags & (PlayerFlags.Defeated | PlayerFlags.Victorious)) == 0)
            {
                _batch.Clear();
                controller.Think(world, (byte)slot, _batch);
                _tickSet.AppendStamped((byte)slot, _batch);
            }
        }

        Simulation.Tick(_tickSet);
        Interlocked.Increment(ref _ticksRun);

        lock (_gate)
        {
            // Bounded: a reader that stops draining (minimised window) must not grow the list forever.
            if (_events.Count < 65536)
            {
                _events.AddRange(world.Events.Events);
            }
        }

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
        _frames.Publish();
    }
}
