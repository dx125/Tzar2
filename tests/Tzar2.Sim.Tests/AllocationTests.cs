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

        // Tier-0 and OSR code may allocate (e.g. unelided boxing) until tiered compilation has optimized the hot
        // methods in the background; with DOTNET_TieredCompilation=0 the first measured tick is already at 0 B.
        for (int attempt = 0; attempt < 60 && MeasureTicks(scenario, 50).SimThread != 0; attempt++)
        {
            Thread.Sleep(50);
        }

        (long simThread, long allThreads) = MeasureTicks(scenario, 200);
        Assert.Equal(0, simThread);

        // The process-wide counter also sees the runtime's own background threads (tiering, timers), so it gets a
        // small allowance; a per-tick allocation on a worker would be at least ~24 B x 200 ticks.
        Assert.True(allThreads < 1024, $"{allThreads} B allocated on all threads over 200 ticks");
    }

    private static (long SimThread, long AllThreads) MeasureTicks(SyntheticScenario scenario, int ticks)
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
