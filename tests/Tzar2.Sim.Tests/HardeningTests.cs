using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Ecs;
using Tzar2.Sim.Events;
using Tzar2.Sim.Systems;
using Tzar2.Sim.Threading;
using Xunit;

namespace Tzar2.Sim.Tests;

/// <summary>
/// Regression tests for the M2 part A review (ADR 0019). R1-R10 are the reviewer's adversarial repros, inverted to
/// assert the fixed behaviour.
/// </summary>
public class HardeningTests
{
    [Fact]
    public void R1_a_slot_is_retired_before_its_generation_wraps()
    {
        var registry = new EntityRegistry();
        EntityId first = registry.Create();
        EntityId current = first;
        for (int i = 1; i < EntityId.MaxGeneration; i++)
        {
            registry.Destroy(current);
            registry.CommitDestroyed();
            current = registry.Create();
            Assert.Equal(first.Index, current.Index);
        }

        Assert.Equal(EntityId.MaxGeneration, current.Generation);
        registry.Destroy(current);
        registry.CommitDestroyed();
        EntityId next = registry.Create();
        Assert.NotEqual(first.Index, next.Index); // slot 0 is retired, not recycled to generation 1
        Assert.False(registry.IsAlive(first));
        Assert.Equal(EntityId.MaxIndex + 1 - 2, registry.RemainingCapacity);
    }

    [Fact]
    public void R2_restore_rejects_a_duplicated_free_slot()
    {
        using var sim = new Simulation(TestWorlds.Ruleset, TestWorlds.Flat(), new SimulationConfig { PlayerCount = 2 });
        EntityId a = sim.World.Spawn(0, 0, 1000, 1000);
        sim.World.Spawn(0, 0, 3000, 3000);
        sim.World.Destroy(a);
        sim.World.Entities.CommitDestroyed();
        EntityRegistry registry = sim.World.Entities;
        FieldInfo freeField = typeof(EntityRegistry).GetField("_free", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo countField = typeof(EntityRegistry).GetField("_freeCount", BindingFlags.NonPublic | BindingFlags.Instance)!;
        int[] free = (int[])freeField.GetValue(registry)!;
        int count = (int)countField.GetValue(registry)!;
        free[count] = free[0];
        countField.SetValue(registry, count + 1);
        byte[] snapshot = sim.CaptureSnapshot(); // the trailing hash covers the corrupt state: it is not a MAC

        Assert.Throws<InvalidDataException>(() => Simulation.Restore(snapshot, TestWorlds.Ruleset, TestWorlds.Flat()).Dispose());
    }

    [Fact]
    public void R2_restore_rejects_a_leaked_dead_slot()
    {
        using var sim = new Simulation(TestWorlds.Ruleset, TestWorlds.Flat(), new SimulationConfig { PlayerCount = 2 });
        EntityId a = sim.World.Spawn(0, 0, 1000, 1000);
        sim.World.Destroy(a);
        sim.World.Entities.CommitDestroyed();
        typeof(EntityRegistry).GetField("_freeCount", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(sim.World.Entities, 0);
        byte[] snapshot = sim.CaptureSnapshot();
        Assert.Throws<InvalidDataException>(() => Simulation.Restore(snapshot, TestWorlds.Ruleset, TestWorlds.Flat()).Dispose());
    }

    public static TheoryData<string> Corruptions() => new() { "cell-off-grid", "cell-mismatch", "owner", "type", "facing", "motion-state", "health" };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void Restore_rejects_snapshots_that_violate_world_invariants(string corruption)
    {
        using var sim = new Simulation(TestWorlds.Ruleset, TestWorlds.Flat(), new SimulationConfig { PlayerCount = 2 });
        World w = sim.World;
        EntityId unit = w.Spawn(0, 1, 1000, 1000);
        switch (corruption)
        {
            case "cell-off-grid": w.OccupancyCells.Get(unit).Cell = 1 << 30; break;
            case "cell-mismatch": w.OccupancyCells.Get(unit).Cell = 5; break;
            case "owner": w.Identities.Get(unit).Owner = 7; break;
            case "type": w.Identities.Get(unit).TypeId = 99; break;
            case "facing": w.Identities.Get(unit).Facing = 8; break;
            case "motion-state": w.Motions.Get(unit).State = (MotionState)9; break;
            case "health": w.Healths.Get(unit).Current = w.Healths.Get(unit).Max + 1; break;
        }

        byte[] snapshot;
        try
        {
            snapshot = sim.CaptureSnapshot();
        }
        catch (IndexOutOfRangeException)
        {
            return; // an off-grid cell cannot even be hashed
        }

        Assert.Throws<InvalidDataException>(() => Simulation.Restore(snapshot, TestWorlds.Ruleset, TestWorlds.Flat()).Dispose());
    }

    [Fact]
    public void R3_names_longer_than_the_snapshot_bound_are_rejected_up_front()
    {
        Assert.Throws<ArgumentException>(() => new EntityName(new string('x', EntityName.MaxBytes + 1)));
        Assert.Throws<ArgumentException>(() => new EntityName(new string('ж', (EntityName.MaxBytes / 2) + 1))); // 2 bytes each
        Assert.Throws<ArgumentException>(() => new EntityName("\uD800"));
        Assert.Equal(EntityName.MaxBytes, new EntityName(new string('x', EntityName.MaxBytes)).Value.Length);
    }

    [Fact]
    public void R4_mass_spawns_never_abort_a_tick()
    {
        using var sim = new Simulation(TestWorlds.Ruleset, TestWorlds.Flat(), new SimulationConfig { PlayerCount = 2, CheatsAllowed = true });
        var batch = new CommandList();
        for (int i = 0; i < 1100; i++)
        {
            batch.Add(CommandKind.DebugSpawn, default, 1000, 1000, arg0: 0, arg1: 64);
        }

        var set = new CommandList();
        set.AppendStamped(0, batch);
        TickResult result = sim.Tick(set); // 70,400 units spawned on one point: counts are int, nothing overflows
        Assert.Equal(0, result.Tick.Value);
        Assert.Equal(70_400, sim.World.Entities.AliveCount);
        long occupants = 0;
        foreach (int count in sim.World.Map.Occupancy)
        {
            occupants += count;
        }

        // Movement starts pushing the stack apart in the same tick; every unit is still counted exactly once.
        Assert.Equal(70_400, occupants);
    }

    [Fact]
    public void R4_spawns_beyond_the_entity_cap_are_rejected_deterministically()
    {
        using var sim = new Simulation(TestWorlds.Ruleset, TestWorlds.Flat(), new SimulationConfig { PlayerCount = 2, CheatsAllowed = true });
        World w = sim.World;
        while (w.Entities.RemainingCapacity > 10)
        {
            w.Entities.Create();
        }

        var batch = new CommandList();
        batch.Add(CommandKind.DebugSpawn, default, 1000, 1000, arg0: 0, arg1: 8);
        batch.Add(CommandKind.DebugSpawn, default, 1000, 1000, arg0: 0, arg1: 8);
        var set = new CommandList();
        set.AppendStamped(1, batch);
        var output = new Snapshots.RenderSnapshotExchange();
        sim.RenderOutput = output;
        sim.Tick(set);
        Assert.Equal(2, w.Entities.RemainingCapacity);
        SimEvent rejection = Assert.Single(output.AcquireLatest()!.Events.ToArray(), e => e.Kind == SimEventKind.CommandRejected);
        Assert.Equal((1, CommandRejection.EntityLimit), (rejection.X, (CommandRejection)rejection.Arg));
        Assert.False(sim.IsFaulted);
    }

    [Fact]
    public void A_throwing_tick_faults_the_simulation()
    {
        using var sim = new Simulation(TestWorlds.Ruleset, TestWorlds.Flat(), new SimulationConfig { PlayerCount = 2 });
        sim.Systems.Add(new ThrowingSystem(throwAtTick: 1));
        var empty = new CommandList();
        sim.Tick(empty);
        Assert.Throws<InvalidOperationException>(() => sim.Tick(empty));
        Assert.True(sim.IsFaulted);
        var ex = Assert.Throws<InvalidOperationException>(() => sim.Tick(empty));
        Assert.Contains("faulted", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => sim.CaptureSnapshot());
        _ = sim.ComputeStateHash(); // diagnostics still work for the repro bundle
    }

    [Fact]
    public void Hashing_or_capturing_during_a_tick_is_refused()
    {
        using var sim = new Simulation(TestWorlds.Ruleset, TestWorlds.Flat(), new SimulationConfig { PlayerCount = 2 });
        Exception? inner = null;
        sim.Systems.Add(new CallbackSystem(() => inner = Record.Exception(() => sim.ComputeStateHash())));
        sim.Tick(new CommandList());
        Assert.IsType<InvalidOperationException>(inner);
    }

    [Fact]
    public void The_world_is_locked_between_ticks()
    {
        using var sim = new Simulation(TestWorlds.Ruleset, TestWorlds.Flat(), new SimulationConfig { PlayerCount = 2 });
        EntityId unit = sim.World.Spawn(0, 0, 1000, 1000); // setup before the first tick is allowed
        sim.Tick(new CommandList());
        Assert.True(sim.World.IsLocked);
        Assert.Throws<InvalidOperationException>(() => sim.World.Spawn(0, 0, 1000, 1000));
        Assert.Throws<InvalidOperationException>(() => sim.World.Destroy(unit));
        Assert.Throws<InvalidOperationException>(() => sim.World.Set(unit, new Position(1, 1)));
        Assert.Throws<InvalidOperationException>(() => sim.World.Remove<Position>(unit));
        using Simulation restored = Simulation.Restore(sim.CaptureSnapshot(), TestWorlds.Ruleset, TestWorlds.Flat());
        Assert.True(restored.World.IsLocked);
    }

    [Fact]
    public void Restore_can_rebuild_a_custom_pipeline()
    {
        using var sim = new Simulation(TestWorlds.Ruleset, TestWorlds.Flat(), new SimulationConfig { PlayerCount = 2 });
        int calls = 0;
        using Simulation restored = Simulation.Restore(
            sim.CaptureSnapshot(), TestWorlds.Ruleset, TestWorlds.Flat(), configureSystems: s => s.Add(new CallbackSystem(() => calls++)));
        restored.Tick(new CommandList());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void R5_deduplication_keeps_the_newer_handle()
    {
        using var sim = new Simulation(TestWorlds.Ruleset, TestWorlds.Flat(), new SimulationConfig { PlayerCount = 2 });
        World w = sim.World;
        EntityId old = w.Spawn(0, 0, 1000, 1000);
        w.Destroy(old);
        w.Entities.CommitDestroyed();
        EntityId live = w.Spawn(0, 0, 1000, 1000);
        Assert.Equal(old.Index, live.Index);
        var batch = new CommandList();
        batch.Add(CommandKind.Move, [live, old], 5000, 5000);
        Assert.Equal([live], batch.EntitiesOf(batch[0]).ToArray());
        var set = new CommandList();
        set.AppendStamped(0, batch);
        sim.Tick(set);
        Assert.Equal(MotionState.Moving, w.Motions.Get(live).State);
    }

    [Fact]
    public void Command_lists_reject_what_the_codec_rejects()
    {
        var list = new CommandList();
        Assert.Throws<ArgumentException>(() => list.Add(CommandKind.None));
        Assert.Throws<ArgumentException>(() => list.Add(CommandKind.Move, flags: (CommandFlags)0x80));
        EntityId[] many = Enumerable.Range(0, CommandList.MaxEntitiesPerCommand).Select(i => EntityId.Create(i, 1)).ToArray();
        for (int i = 0; i < CommandList.MaxEntitiesPerList / CommandList.MaxEntitiesPerCommand; i++)
        {
            list.Add(CommandKind.Move, many);
        }

        Assert.Throws<InvalidOperationException>(() => list.Add(CommandKind.Move, many.AsSpan(0, 1)));
    }

    [Fact]
    public void R6_snapshots_do_not_store_static_map_layers()
    {
        using var sim = new Simulation(TestWorlds.Ruleset, TestWorlds.Flat(1024), new SimulationConfig { PlayerCount = 2 });
        sim.World.Spawn(0, 0, 1000, 1000);
        sim.World.Map.AddBlocker(3, 4);
        byte[] snapshot = sim.CaptureSnapshot();
        Assert.True(snapshot.Length < 2048, $"{snapshot.Length} bytes for a 1024² map with one unit"); // + per-kingdom world state (ADR 0101) and the stream E store headers
        using Simulation restored = Simulation.Restore(snapshot, TestWorlds.Ruleset, TestWorlds.Flat(1024));
        Assert.Equal(sim.ComputeFullHash(), restored.ComputeFullHash());
        Assert.Equal(1, restored.World.Map.Blockers[(4 * 1024) + 3]);
        Assert.Equal(1, restored.World.Map.Occupancy[restored.World.Map.CellIndexAt(1000, 1000)]);
    }

    [Fact]
    public void R10_occupancy_cells_must_divide_the_tile()
    {
        foreach (int cells in new[] { 0, 3, 5, 8 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new Ruleset(
                "r",
                [new TerrainTypeDefinition(0, "grass", LocomotorMask.Land, isWater: false)],
                [],
                occupancyCellsPerTileAxis: cells));
        }

        foreach (int cells in new[] { 1, 2, 4 })
        {
            var rules = new Ruleset("r", [new TerrainTypeDefinition(0, "grass", LocomotorMask.Land, isWater: false)], [], occupancyCellsPerTileAxis: cells);
            var map = new Map.MapState(new MapData("m", 4, 4, new byte[25], [], []), rules);
            int last = (4 * Map.MapState.UnitsPerTile) - 1;
            Assert.Equal(map.Occupancy.Length - 1, map.CellIndexAt(last, last));
            Assert.Equal(cells - 1, map.CellIndexAt(Map.MapState.UnitsPerTile - 1, 0)); // the last cell of tile 0
        }
    }

    [Fact]
    public void Worker_pool_refuses_nested_runs()
    {
        using var pool = new DeterministicWorkerPool(2);
        var job = new NestingJob(pool);
        var ex = Assert.Throws<InvalidOperationException>(() => pool.Run(job, 4));
        Assert.Contains("re-entrant", ex.Message, StringComparison.Ordinal);
        pool.Run(new NestingJob(null), 4); // still usable afterwards
    }

    private sealed class ThrowingSystem(int throwAtTick) : ISimSystem
    {
        public SimPhase Phase => SimPhase.Victory;

        public string Name => "throwing";

        public void Execute(SimContext context)
        {
            if (context.World.Tick.Value == throwAtTick)
            {
                throw new InvalidOperationException("boom");
            }
        }
    }

    private sealed class CallbackSystem(Action callback) : ISimSystem
    {
        public SimPhase Phase => SimPhase.Victory;

        public string Name => "callback";

        public void Execute(SimContext context) => callback();
    }

    private sealed class NestingJob(DeterministicWorkerPool? pool) : IParallelJob
    {
        public void Execute(int jobIndex) => pool?.Run(new NestingJob(null), 1);
    }
}
