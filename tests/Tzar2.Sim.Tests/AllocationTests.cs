using System;
using System.Threading;
using Tzar2.Sim.Scenarios;
using Xunit;

namespace Tzar2.Sim.Tests;

/// <summary>TDD §8.9 / §21: zero heap allocations per tick in steady state.</summary>
public class AllocationTests
{
#if DEBUG
    [Theory(Skip = "The allocation gate runs on optimized (Release) builds; Debug code is JIT-compiled without boxing elision.")]
#else
    [Theory]
#endif
    [InlineData(0)]
    [InlineData(3)]
    public void Steady_state_ticks_do_not_allocate(int churn)
    {
        using var scenario = new SyntheticScenario(TestWorlds.SmallScenario with { ChurnPerPlayerPerTick = churn }, workerCount: 1);
        // Tier-0 and OSR code may allocate (e.g. unelided boxing) until tiered compilation has optimized the hot
        // methods in the background; with DOTNET_TieredCompilation=0 the first measured tick is already at 0 B.
        for (int attempt = 0; attempt < 60 && MeasureTicks(scenario, 50) != 0; attempt++)
        {
            Thread.Sleep(50);
        }

        Assert.Equal(0, MeasureTicks(scenario, 200));
    }

    private static long MeasureTicks(SyntheticScenario scenario, int ticks)
    {
        long allocated = 0;
        for (int t = 0; t < ticks; t++)
        {
            scenario.GenerateCommands();
            long before = GC.GetAllocatedBytesForCurrentThread();
            scenario.Simulation.Tick(scenario.LastCommands);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        return allocated;
    }
}
