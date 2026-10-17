using System;
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
public sealed class Simulation : IDisposable
{
    private readonly SystemScheduler _scheduler = new();
    private readonly SimContext _context;
    private readonly StateHasher _hasher = new();
    private readonly BinaryBufferWriter _snapshotWriter = new(64 * 1024);

    /// <summary>Creates a match at tick 0 with the default pipeline.</summary>
    /// <param name="ruleset">The compiled ruleset.</param>
    /// <param name="map">The compiled map.</param>
    /// <param name="config">Match settings.</param>
    /// <param name="workerCount">Threads for fork/join jobs (never affects results).</param>
    public Simulation(Ruleset ruleset, MapData map, SimulationConfig config, int workerCount = 1)
        : this(new World(ruleset, map, config), workerCount)
    {
    }

    private Simulation(World world, int workerCount)
    {
        World = world;
        Workers = new DeterministicWorkerPool(workerCount);
        _context = new SimContext(world, Workers);
        _scheduler.Add(new CommandSystem());
        _scheduler.Add(new MovementSystem());
        _scheduler.Add(new CleanupSystem());
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

    /// <summary>Restores a match from a snapshot (save/load, reconnect, replay seek; REQ-SIM-13).</summary>
    /// <exception cref="System.IO.InvalidDataException">The snapshot is malformed or does not match the ruleset/map.</exception>
    public static Simulation Restore(ReadOnlySpan<byte> snapshot, Ruleset ruleset, MapData map, int workerCount = 1)
    {
        SimulationConfig config = WorldSnapshot.ReadConfig(snapshot, ruleset, map);
        var world = new World(ruleset, map, config);
        WorldSnapshot.Restore(world, snapshot);
        return new Simulation(world, workerCount);
    }

    /// <summary>
    /// Runs tick <see cref="World.Tick"/> with its closed command set (pipeline §8.5), then hashes the state, publishes
    /// the render snapshot and advances the tick counter.
    /// </summary>
    public TickResult Tick(CommandList commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        World.Components.Seal();
        Tick tick = World.Tick;
        World.Events.Clear();
        _context.Commands = commands;
        _scheduler.RunTick(_context);

        // Step 18: hashes describe the state after tick T, i.e. the state in which tick T+1 begins.
        World.Tick = tick + 1;
        ulong hash = StateHashing.ComputeTickHash(World, _hasher);
        ulong? full = World.Tick.Value % World.Config.FullHashIntervalTicks == 0
            ? StateHashing.ComputeFullHash(World, _hasher)
            : null;
        RenderOutput?.Publish(World, tick, hash);
        return new TickResult(tick, hash, full);
    }

    /// <summary>The per-tick hash of the current state.</summary>
    public ulong ComputeStateHash() => StateHashing.ComputeTickHash(World, _hasher);

    /// <summary>The full-state hash of the current state.</summary>
    public ulong ComputeFullHash() => StateHashing.ComputeFullHash(World, _hasher);

    /// <summary>Captures a snapshot of the current state (between ticks).</summary>
    public byte[] CaptureSnapshot()
    {
        _snapshotWriter.Clear();
        WorldSnapshot.Capture(World, _snapshotWriter, _hasher);
        return _snapshotWriter.ToArray();
    }

    /// <inheritdoc />
    public void Dispose() => Workers.Dispose();
}
