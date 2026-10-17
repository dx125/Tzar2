using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using Tzar2.Sim;
using Tzar2.Sim.Scenarios;

namespace Tzar2.Benchmarks;

/// <summary>
/// One sim tick of the synthetic scenario: 4,000 units on a 512² map, 8 players issuing move orders (B3-like), and
/// the same with delete/respawn churn (B4-like mass death). Budget: p99 ≤ 12 ms at 4,000 units (TDD §21).
/// MemoryDiagnoser shows the per-tick allocation, which must be 0 B (§8.9).
/// </summary>
[MemoryDiagnoser]
[Config(typeof(PercentileConfig))]
public class SimTickBenchmarks
{
    private SyntheticScenario _scenario = null!;

    /// <summary>Sim worker threads (never changes the result).</summary>
    [Params(1, 4)]
    public int Workers { get; set; }

    /// <summary>Units deleted and respawned per player per tick.</summary>
    [Params(0, 4)]
    public int Churn { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _scenario = new SyntheticScenario(new SyntheticScenarioOptions { ChurnPerPlayerPerTick = Churn }, Workers);
        for (int t = 0; t < 200; t++)
        {
            _scenario.RunTick();
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _scenario.Dispose();

    // IterationSetup makes every iteration a single tick, so the percentile columns are per-tick times.
    [IterationSetup]
    public void GenerateCommands() => _scenario.GenerateCommands();

    [Benchmark]
    public TickResult Tick() => _scenario.Simulation.Tick(_scenario.LastCommands);

    private sealed class PercentileConfig : ManualConfig
    {
        public PercentileConfig()
        {
            AddColumn(StatisticColumn.P50, StatisticColumn.P95, StatisticColumn.Max);
            SummaryStyle = SummaryStyle.Default.WithMaxParameterColumnWidth(20);
        }
    }
}
