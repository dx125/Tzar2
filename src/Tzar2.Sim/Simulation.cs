using System;
using System.Threading;
using Tzar2.Content.Compiled;
using Tzar2.Core.Hashing;
using Tzar2.Core.Ids;
using Tzar2.Core.Serialization;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Snapshots;
using Tzar2.Sim.Systems;
using Tzar2.Sim.Threading;

namespace Tzar2.Sim;

/// <summary>The outcome of one tick.</summary>
/// <param name="Tick">The tick that ran.</param>
/// <param name="StateHash">Per-tick state hash after the tick (REQ-SIM-12).</param>
/// <param name="FullHash">Full-state hash after the tick, on ticks where <c>(Tick + 1) % FullHashIntervalTicks == 0</c>; otherwise <c>null</c>.</param>
public readonly record struct TickResult(Tick Tick, ulong StateHash, ulong? FullHash);

/// <summary>
/// The deterministic simulation of one match (TDD §8): a <see cref="World"/>, the fixed system pipeline and a worker
/// pool. <see cref="Tick"/> is the only way time advances: <c>Sim.Tick(T, commands)</c> of TDD §7.4.
/// </summary>
/// <remarks>
/// <para><b>Thread affinity.</b> A simulation belongs to one thread (the sim thread, §7.3). <see cref="Tick"/>,
/// <see cref="ComputeStateHash"/>, <see cref="ComputeFullHash"/> and <see cref="CaptureSnapshot"/> share scratch
/// buffers; calling one while another runs throws <see cref="InvalidOperationException"/> instead of corrupting a hash
/// or capturing a half-applied tick. Other threads read the sim only through <see cref="RenderOutput"/>.</para>
/// <para><b>Faults.</b> If a tick throws (a bug: commands are validated so that ticks do not fail, ADR 0019), the
/// world may be half-applied. The simulation is then <see cref="IsFaulted"/> and refuses further ticks and snapshots;
/// the session ends the match with a repro bundle instead of continuing from a corrupt state.</para>
/// <para><b>System state.</b> Systems keep no state across ticks outside <see cref="World"/> (ADR 0019), so a restored
/// simulation only needs the same pipeline: pass the same <c>configureSystems</c> to <see cref="Restore"/>.</para>
/// </remarks>
public sealed class Simulation : IDisposable
{
    private readonly SystemScheduler _scheduler = new();
    private readonly SimContext _context;
    private readonly StateHasher _hasher = new();
    private readonly BulkHash _bulk = new();
    private readonly BinaryBufferWriter _snapshotWriter = new(64 * 1024);
    private int _busy;

    /// <summary>Creates a match at tick 0 with the default pipeline.</summary>
    /// <param name="ruleset">The compiled ruleset.</param>
    /// <param name="map">The compiled map.</param>
    /// <param name="config">Match settings.</param>
    /// <param name="workerCount">Threads for fork/join jobs (never affects results).</param>
    public Simulation(Ruleset ruleset, MapData map, SimulationConfig config, int workerCount = 1)
        : this(NewMatch(ruleset, map, config), workerCount)
    {
    }

    private Simulation(World world, int workerCount)
    {
        World = world;

        // Match load: build the navigation layers now rather than in the first tick (TDD §21: load <= 10 s).
        world.Navigation.EnsureUpToDate();
        Workers = new DeterministicWorkerPool(workerCount);
        _context = new SimContext(world, Workers);
        _scheduler.Add(new CommandSystem());
        _scheduler.Add(new ProductionSystem());
        _scheduler.Add(new EconomySystem());
        _scheduler.Add(new TradeSystem());
        _scheduler.Add(new WorkerSystem());
        _scheduler.Add(new NatureSystem());
        _scheduler.Add(new PathRequestSystem());
        _scheduler.Add(new MovementSystem());
        _scheduler.Add(new CombatSystem());
        _scheduler.Add(new StructureSystem());
        _scheduler.Add(new CaptureSystem());
        AddCombatMagicSystems(_scheduler);
        _scheduler.Add(new VisibilitySystem());
        _scheduler.Add(new ClockSystem());
        _scheduler.Add(new VictorySystem());
        _scheduler.Add(new CleanupSystem());
    }

    // GAME-07…11 (ADRs 0091-0095): errands (step 6), spells (10, after mana regeneration), experience (11), holders (13).
    private static void AddCombatMagicSystems(SystemScheduler scheduler)
    {
        scheduler.Add(new ErrandSystem());
        scheduler.Add(new NavalSystem()); // ships to the shore, explorers (ADRs 0140, 0143)
        scheduler.Add(new MagicSystem());
        scheduler.Add(new ExperienceSystem());
        scheduler.Add(new HolderSystem());
    }

    // Match start: the map's objects, starting resources and the population limit (never on restore).
    private static World NewMatch(Ruleset ruleset, MapData map, SimulationConfig config)
    {
        var world = new World(ruleset, map, config);
        world.LoadMap();
        ProductionSystem.UpdatePopulation(world);
        world.StartKingdoms();
        return world;
    }

    /// <summary>The state.</summary>
    public World World { get; }

    /// <summary><see cref="Ruleset.ContentHash"/> of the ruleset (for replay headers and lobby checks).</summary>
    public ulong RulesetHash => World.Ruleset.ContentHash;

    /// <summary><see cref="MapData.ContentHash"/> of the map.</summary>
    public ulong MapHash => World.Map.Data.ContentHash;

    /// <summary>The worker pool.</summary>
    public DeterministicWorkerPool Workers { get; }

    /// <summary>The system pipeline. Add systems before the first tick.</summary>
    public SystemScheduler Systems => _scheduler;

    /// <summary>Optional render output; when set, a <see cref="RenderSnapshot"/> is published after every tick.</summary>
    public RenderSnapshotExchange? RenderOutput { get; set; }

    /// <summary>Optional profiler for the sections of every tick (diagnostics only; never affects results).</summary>
    public ITickProfiler? Profiler
    {
        get => _context.Profiler;
        set => _context.Profiler = value;
    }

    /// <summary>Whether a tick threw; the simulation then refuses to continue.</summary>
    public bool IsFaulted { get; private set; }

    /// <summary>
    /// Restores a match from a snapshot (save/load, reconnect, replay seek; REQ-SIM-13). The snapshot is untrusted
    /// input: its structure and all world invariants are validated (ADR 0019). Whether it is the <i>right</i> state is
    /// established by the lockstep hash agreement: a rejoining peer compares its restored hash with the others'.
    /// </summary>
    /// <param name="snapshot">The snapshot bytes.</param>
    /// <param name="ruleset">The ruleset (must match the snapshot's ruleset hash).</param>
    /// <param name="map">The map (must match the snapshot's map hash).</param>
    /// <param name="workerCount">Threads for fork/join jobs.</param>
    /// <param name="configureSystems">Adds the same extra systems the original simulation had.</param>
    /// <exception cref="System.IO.InvalidDataException">The snapshot is malformed, inconsistent, or does not match the ruleset/map.</exception>
    /// <param name="tolerance">
    /// <see cref="SnapshotTolerance.SaveGame"/>: a player's save, read from an older format or under other rules with the
    /// same tables (ADR 0271); replays, rejoins and desync reports stay <see cref="SnapshotTolerance.Exact"/>.
    /// </param>
    /// <param name="allowCheats">
    /// A player's save continued in a game alone, where the original's cheat codes work (HUD-036): cheats are allowed
    /// though the save was made without them.
    /// </param>
    public static Simulation Restore(ReadOnlySpan<byte> snapshot, Ruleset ruleset, MapData map, int workerCount = 1, Action<SystemScheduler>? configureSystems = null, SnapshotTolerance tolerance = SnapshotTolerance.Exact, bool allowCheats = false)
    {
        SimulationConfig config = WorldSnapshot.ReadConfig(snapshot, ruleset, map, tolerance);
        if (allowCheats && tolerance == SnapshotTolerance.SaveGame && !config.Ranked)
        {
            config = config with { CheatsAllowed = true };
        }

        var world = new World(ruleset, map, config);
        bool exact = WorldSnapshot.Restore(world, snapshot, tolerance);
        world.IsLocked = true;
        var simulation = new Simulation(world, workerCount) { RestoredExactly = exact };
        configureSystems?.Invoke(simulation.Systems);
        return simulation;
    }

    /// <summary>
    /// Whether a restored simulation was read as it was saved; false for a save of an older format or other rules, which
    /// continues under the current ones (ADR 0271). True for a new simulation.
    /// </summary>
    public bool RestoredExactly { get; private init; } = true;

    /// <summary>
    /// Runs tick <see cref="World.Tick"/> with its closed command set (pipeline §8.5), then hashes the state, publishes
    /// the render snapshot and advances the tick counter.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called re-entrantly or concurrently, or the simulation is faulted.</exception>
    public TickResult Tick(CommandList commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        Enter();
        try
        {
            World.Components.Seal();
            Tick tick = World.Tick;
            World.Events.Clear();
            _context.Commands = commands;
            World.IsLocked = false;
            _scheduler.RunTick(_context);
            World.IsLocked = true;

            // Step 18: hashes describe the state after tick T, i.e. the state in which tick T+1 begins.
            World.Tick = tick + 1;
            ITickProfiler? profiler = _context.Profiler;
            profiler?.Begin("hash");
            ulong hash = StateHashing.ComputeTickHash(World, _hasher);
            profiler?.End();
            ulong? full = null;
            if (World.Tick.Value % World.Config.FullHashIntervalTicks == 0)
            {
                profiler?.Begin("full hash");
                full = StateHashing.ComputeFullHash(World, _hasher, _bulk, Workers);
                profiler?.End();
            }

            if (RenderOutput is { } output)
            {
                profiler?.Begin("render snapshot");
                output.Publish(World, tick, hash);
                profiler?.End();
            }
            return new TickResult(tick, hash, full);
        }
        catch
        {
            IsFaulted = true;
            World.IsLocked = true;
            throw;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>The per-tick hash of the current state.</summary>
    /// <exception cref="InvalidOperationException">A tick or another hash/snapshot call is running.</exception>
    public ulong ComputeStateHash()
    {
        Enter(allowFaulted: true);
        try
        {
            return StateHashing.ComputeTickHash(World, _hasher);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>The full-state hash of the current state.</summary>
    /// <exception cref="InvalidOperationException">A tick or another hash/snapshot call is running.</exception>
    public ulong ComputeFullHash()
    {
        Enter(allowFaulted: true);
        try
        {
            return StateHashing.ComputeFullHash(World, _hasher, _bulk, Workers);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Captures a snapshot of the current state (between ticks).</summary>
    /// <exception cref="InvalidOperationException">A tick or another hash/snapshot call is running, or the simulation is faulted.</exception>
    public byte[] CaptureSnapshot()
    {
        Enter();
        try
        {
            _snapshotWriter.Clear();
            WorldSnapshot.Capture(World, _snapshotWriter, _hasher);
            return _snapshotWriter.ToArray();
        }
        finally
        {
            Exit();
        }
    }

    private void Enter(bool allowFaulted = false)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new InvalidOperationException("The simulation is busy: Tick, hashing and snapshots must not overlap (single sim thread).");
        }

        if (IsFaulted && !allowFaulted)
        {
            Volatile.Write(ref _busy, 0);
            throw new InvalidOperationException("The simulation faulted in an earlier tick and cannot continue.");
        }
    }

    private void Exit() => Volatile.Write(ref _busy, 0);

    /// <inheritdoc />
    public void Dispose() => Workers.Dispose();
}
