using System;
using System.Collections.Generic;
using System.IO;
using Tzar2.Sim.Scenarios;
using Xunit;

namespace Tzar2.Sim.Tests;

/// <summary>REQ-SIM-13 (save → load → continue) and TDD §7.3/§22.2 (worker-count independence).</summary>
public class DeterminismTests
{
    private const int Ticks = 120;

    [Fact]
    public void Worker_count_never_affects_the_hash_stream()
    {
        List<ulong> reference = Run(TestWorlds.SmallScenario, workers: 1, Ticks);
        foreach (int workers in new[] { 2, 3, 4, 8 })
        {
            Assert.Equal(reference, Run(TestWorlds.SmallScenario, workers, Ticks));
        }
    }

    [Fact]
    public void Worker_count_never_affects_long_routes_and_staggered_orders()
    {
        // A map large enough for route plans (the parallel route pre-pass, ADR 0080), with AI-like staggered orders.
        SyntheticScenarioOptions options = TestWorlds.SmallScenario with
        {
            MapSize = 256,
            EntityCount = 1200,
            OrdersPerPlayerPerTick = 16,
            ChurnPerPlayerPerTick = 1,
            OrderIntervalTicks = 3,
        };
        List<ulong> reference = Run(options, workers: 1, Ticks);
        Assert.Equal(reference, Run(options, workers: 4, Ticks));
        Assert.NotEqual(reference, Run(options with { OrderIntervalTicks = 1 }, workers: 1, Ticks));
    }

    [Fact]
    public void Runs_are_repeatable_and_the_seed_matters()
    {
        Assert.Equal(Run(TestWorlds.SmallScenario, 1, 40), Run(TestWorlds.SmallScenario, 1, 40));
        Assert.NotEqual(Run(TestWorlds.SmallScenario, 1, 40), Run(TestWorlds.SmallScenario with { Seed = 100 }, 1, 40));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(60)]
    public void Snapshot_restore_continues_with_an_identical_hash_stream(int splitTick)
    {
        using var uninterrupted = new SyntheticScenario(TestWorlds.SmallScenario, 1);
        using var interrupted = new SyntheticScenario(TestWorlds.SmallScenario, 1);
        for (int t = 0; t < splitTick; t++)
        {
            Assert.Equal(uninterrupted.RunTick(), interrupted.RunTick());
        }

        byte[] snapshot = interrupted.Simulation.CaptureSnapshot();
        using Simulation restored = Simulation.Restore(snapshot, interrupted.Ruleset, interrupted.Map, workerCount: 3);
        Assert.Equal(interrupted.Simulation.ComputeFullHash(), restored.ComputeFullHash());
        Assert.Equal(interrupted.Simulation.ComputeStateHash(), restored.ComputeStateHash());

        // Byte-identical re-capture: the snapshot is canonical.
        Assert.Equal(snapshot, restored.CaptureSnapshot());

        // Continue: feed the restored sim the same commands the scenario bot generates for the original.
        for (int t = splitTick; t < splitTick + 60; t++)
        {
            TickResult expected = uninterrupted.RunTick();
            interrupted.GenerateCommands();
            TickResult actual = restored.Tick(interrupted.LastCommands);
            interrupted.Simulation.Tick(interrupted.LastCommands);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Corrupted_or_mismatched_snapshots_are_rejected()
    {
        using var scenario = new SyntheticScenario(TestWorlds.SmallScenario, 1);
        for (int t = 0; t < 5; t++)
        {
            scenario.RunTick();
        }

        byte[] snapshot = scenario.Simulation.CaptureSnapshot();
        byte[] flipped = (byte[])snapshot.Clone();
        flipped[flipped.Length / 2] ^= 0x40;
        Assert.ThrowsAny<InvalidDataException>(() => Simulation.Restore(flipped, scenario.Ruleset, scenario.Map).Dispose());
        Assert.ThrowsAny<InvalidDataException>(() => Simulation.Restore(snapshot.AsSpan(0, snapshot.Length - 3), scenario.Ruleset, scenario.Map).Dispose());
        Assert.ThrowsAny<InvalidDataException>(() => Simulation.Restore(snapshot, scenario.Ruleset, TestWorlds.Flat(64)).Dispose());
    }

    [Fact]
    public void Full_hash_is_reported_every_twenty_ticks()
    {
        using var scenario = new SyntheticScenario(TestWorlds.SmallScenario, 1);
        for (int t = 0; t < 45; t++)
        {
            TickResult result = scenario.RunTick();
            Assert.Equal((t + 1) % 20 == 0, result.FullHash.HasValue);
        }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 9)]
    [InlineData(20, 5)]
    [InlineData(20, 31)]
    public void Maze_moves_are_worker_independent_and_survive_a_snapshot_mid_path(int groupSize, int splitTick)
    {
        // Paths, routes, flow fields and arrival slots in flight: 1 vs 4 workers, and restore mid-path.
        var options = new MazeMoveScenarioOptions { MapSize = 96, Units = 120, GroupSize = groupSize, Bystanders = 40, Seed = 77, PathBudgetPerTick = 20_000 };
        using var single = new MazeMoveScenario(options, workerCount: 1);
        using var multi = new MazeMoveScenario(options, workerCount: 4);
        for (int t = 0; t < splitTick; t++)
        {
            Assert.Equal(single.RunTick(), multi.RunTick());
        }

        byte[] snapshot = multi.Simulation.CaptureSnapshot();
        using Simulation restored = Simulation.Restore(snapshot, multi.Ruleset, multi.Map, workerCount: 2);
        Assert.Equal(snapshot, restored.CaptureSnapshot());
        var none = new Tzar2.Sim.Commands.CommandList();
        for (int t = splitTick; t < splitTick + 150; t++)
        {
            TickResult expected = single.RunTick();
            Assert.Equal(expected, multi.RunTick());
            Assert.Equal(expected, restored.Tick(t == 0 ? multi.LastCommands : none));
        }

        Assert.Equal(single.Simulation.ComputeFullHash(), restored.ComputeFullHash());
    }

    private static List<ulong> Run(SyntheticScenarioOptions options, int workers, int ticks)
    {
        using var scenario = new SyntheticScenario(options, workers);
        var hashes = new List<ulong>(ticks);
        for (int t = 0; t < ticks; t++)
        {
            TickResult r = scenario.RunTick();
            hashes.Add(r.StateHash);
            if (r.FullHash is { } full)
            {
                hashes.Add(full);
            }
        }

        return hashes;
    }
}
