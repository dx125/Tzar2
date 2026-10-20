using System;
using System.Threading;
using Tzar2.Sim.Scenarios;
using Xunit;

namespace Tzar2.Sim.Tests;

/// <summary>Runs alone, so process-wide allocation counters see only the simulation.</summary>
[CollectionDefinition(nameof(AllocationCollection), DisableParallelization = true)]
public sealed class AllocationCollection
{
}

/// <summary>TDD §8.9 / §21: zero heap allocations per tick in steady state, on the sim thread and on the workers.</summary>
[Collection(nameof(AllocationCollection))]
public class AllocationTests
{
#if DEBUG
    [Theory(Skip = "The allocation gate runs on optimized (Release) builds; Debug code is JIT-compiled without boxing elision.")]
#else
    [Theory]
#endif
    [InlineData(1, 0)]
    [InlineData(1, 3)]
    [InlineData(4, 0)]
    [InlineData(4, 3)]
    public void Steady_state_ticks_do_not_allocate(int workers, int churn)
    {
        using var scenario = new SyntheticScenario(TestWorlds.SmallScenario with { ChurnPerPlayerPerTick = churn }, workers);
        AssertSteadyStateDoesNotAllocate(scenario, workers);
    }

    /// <summary>
    /// Periodic group move orders to new goals on a map large enough for routes, flow fields and long plans (AI-like
    /// order rate, ADR 0080): path requests, plan buffers and the flow-field cache must reach a steady state.
    /// </summary>
#if DEBUG
    [Theory(Skip = "The allocation gate runs on optimized (Release) builds; Debug code is JIT-compiled without boxing elision.")]
#else
    [Theory]
#endif
    [InlineData(1)]
    [InlineData(4)]
    public void Periodic_move_orders_to_new_goals_do_not_allocate(int workers)
    {
        SyntheticScenarioOptions options = TestWorlds.SmallScenario with
        {
            MapSize = 256,
            EntityCount = 1600,
            OrdersPerPlayerPerTick = 24,
            ChurnPerPlayerPerTick = 0,
            OrderIntervalTicks = 10,
        };
        using var scenario = new SyntheticScenario(options, workers);
        World world = scenario.Simulation.World;
        for (int t = 0; t < 600 || (world.FlowFields.FieldsAllocated < Tzar2.Sim.Navigation.FlowFieldCache.Capacity && t < 20_000); t++)
        {
            scenario.RunTick();
        }

        AssertSteadyStateDoesNotAllocate(scenario, workers);
    }

    /// <summary>The gameplay systems (production, economy, workers, combat) under the economy-and-combat scenario.</summary>
#if DEBUG
    [Theory(Skip = "The allocation gate runs on optimized (Release) builds; Debug code is JIT-compiled without boxing elision.")]
#else
    [Theory]
#endif
    [InlineData(1)]
    [InlineData(4)]
    public void Gameplay_ticks_do_not_allocate(int workers)
    {
        using var scenario = new GameplayScenario(GameplayFixtures.Classic, new GameplayScenarioOptions { MapSize = 96, Players = 2, SoldiersPerPlayer = 150, WorkersPerPlayer = 30, TreesPerPlayer = 120, BotIntervalTicks = 4 }, workers);

        // Into the fight (soldiers meet in the middle after ~30 s), and until the flow-field cache stops growing: group
        // orders to new goals allocate a field object until then (bounded pool growth, not per tick). It stops either at
        // its 64-field capacity or below it, when no more goals are live at once: since units settle next to a taken goal
        // (ADR 0146) this scenario levels off at about half the capacity.
        const int Quiet = 2_000;
        World world = scenario.Simulation.World;
        int fields = -1;
        int sinceGrowth = 0;
        int t = 0;
        for (; t < 700 || (world.FlowFields.FieldsAllocated < Tzar2.Sim.Navigation.FlowFieldCache.Capacity && sinceGrowth < Quiet && t < 20_000); t++)
        {
            scenario.RunTick();
            sinceGrowth = world.FlowFields.FieldsAllocated == fields ? sinceGrowth + 1 : 0;
            fields = world.FlowFields.FieldsAllocated;
        }

        Assert.True(
            world.FlowFields.FieldsAllocated == Tzar2.Sim.Navigation.FlowFieldCache.Capacity || sinceGrowth >= Quiet,
            $"the flow-field cache still grew after {t} ticks ({world.FlowFields.FieldsAllocated} fields)");
        AssertSteadyStateDoesNotAllocate(scenario, workers);
    }

    /// <summary>Structures appearing and disappearing (incremental navigation updates) while units move.</summary>
#if DEBUG
    [Theory(Skip = "The allocation gate runs on optimized (Release) builds; Debug code is JIT-compiled without boxing elision.")]
#else
    [Theory]
#endif
    [InlineData(1)]
    [InlineData(4)]
    public void Passability_churn_does_not_allocate(int workers)
    {
        using var scenario = new PassabilityChurnScenario(workers);
        for (int t = 0; t < 400; t++)
        {
            scenario.RunTick();
        }

        int version = scenario.Simulation.World.Map.PassabilityVersion;
        AssertSteadyStateDoesNotAllocate(scenario, workers);
        Assert.True(scenario.Simulation.World.Map.PassabilityVersion > version + 100, "the scenario did not change passability");
    }

    private static void AssertSteadyStateDoesNotAllocate(IBenchScenario scenario, int workers)
    {
        // Tier-0 and OSR code may allocate (e.g. unelided boxing) until tiered compilation has optimized the hot
        // methods in the background; with DOTNET_TieredCompilation=0 the first measured tick is already at 0 B.
        // Pools (plan buffers, flow-field cluster fields) also grow to their steady-state size during the first few
        // hundred ticks, so require four clean windows in a row.
        int clean = 0;
        for (int attempt = 0; attempt < 120 && clean < 4; attempt++)
        {
            (long sim, long all) = MeasureTicks(scenario, 50);
            if (sim == 0 && (all == 0 || workers == 1))
            {
                clean++;
            }
            else
            {
                clean = 0;
                Thread.Sleep(50);
            }
        }

        // The process-wide counter also sees the runtime's own background threads (tiering, timers), which are noisy
        // on CI runners. A per-tick allocation in the sim or its jobs shows up in every window (>= ~24 B x 200 ticks),
        // background noise does not, so the quietest of six windows must be (almost) clean.
        long simThread = long.MaxValue;
        long allThreads = long.MaxValue;
        for (int window = 0; window < 6; window++)
        {
            (long sim, long all) = MeasureTicks(scenario, 200);
            simThread = Math.Min(simThread, sim);
            allThreads = Math.Min(allThreads, all);
        }

        Assert.Equal(0, simThread);

        // With one worker every job runs inline on this thread, so other threads' allocations are the runtime's alone
        // (ubuntu x64 runners showed a steady ~1.3 KB per window there); with workers, they may be the jobs'. The runtime's
        // own share also appears with workers (1,176 B per window on ubuntu x64 runners, M2 part C; 2,184 B in every one of
        // the six windows from October 2026, issue #68, with no sim change), so the bound is 4 KB: a per-tick allocation in
        // a job costs at least ~24 B x 200 ticks = 4.8 KB and still fails it.
        if (workers > 1)
        {
            Assert.True(allThreads < 4096, $"{allThreads} B allocated on all threads over 200 ticks");
        }
    }

    private static (long SimThread, long AllThreads) MeasureTicks(IBenchScenario scenario, int ticks)
    {
        long simThread = 0;
        long allThreads = 0;
        for (int t = 0; t < ticks; t++)
        {
            scenario.GenerateCommands();
            long total = GC.GetTotalAllocatedBytes(precise: true);
            long current = GC.GetAllocatedBytesForCurrentThread();
            scenario.Simulation.Tick(scenario.LastCommands);
            simThread += GC.GetAllocatedBytesForCurrentThread() - current;
            allThreads += GC.GetTotalAllocatedBytes(precise: true) - total;
        }

        return (simThread, allThreads);
    }
}
