using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Tzar2.Sim;
using Tzar2.Sim.Scenarios;

namespace Tzar2.Benchmarks;

/// <summary>
/// TDD §21 <b>B3</b> "500 units mass move on 512² maze": one invocation = the first three ticks after 500 move orders
/// (individual, or in groups of 50), i.e. the whole planning window of the budget "500 move orders planned within 3
/// ticks" (REQ-PATH-12). Each iteration starts from a fresh scenario.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, iterationCount: 10)]
public class MazeMoveBenchmarks
{
    private MazeMoveScenario _scenario = null!;

    /// <summary>Units per order (1 = 500 individual orders).</summary>
    [Params(1, 50)]
    public int GroupSize { get; set; }

    /// <summary>Sim worker threads.</summary>
    [Params(1, 4)]
    public int Workers { get; set; }

    [IterationSetup]
    public void Setup() => _scenario = new MazeMoveScenario(new MazeMoveScenarioOptions { GroupSize = GroupSize }, Workers);

    [IterationCleanup]
    public void Cleanup() => _scenario.Dispose();

    /// <summary>Ticks 0-2: all 500 orders planned (checked).</summary>
    [Benchmark]
    public int PlanWithinThreeTicks()
    {
        for (int t = 0; t < 3; t++)
        {
            _scenario.RunTick();
        }

        return _scenario.PlannedAfterTick is >= 0 and <= 2
            ? _scenario.PlannedAfterTick
            : throw new System.InvalidOperationException($"B3: orders not planned within 3 ticks ({_scenario.Describe()})");
    }
}

/// <summary>
/// TDD §21 <b>B4</b> "mass death: 2,000 kills in 60 s then traffic through the area": one invocation = one tick of
/// bridge traffic among 2,000 fresh corpses and 4,000 units in total (setup runs the 60 s of killing first).
/// </summary>
[MemoryDiagnoser]
public class MassDeathBenchmarks
{
    private MassDeathScenario _scenario = null!;

    /// <summary>Sim worker threads.</summary>
    [Params(1, 4)]
    public int Workers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _scenario = new MassDeathScenario(new MassDeathScenarioOptions(), Workers);
        for (int t = 0; t < 1300; t++)
        {
            _scenario.RunTick();
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _scenario.Dispose();

    [IterationSetup]
    public void GenerateCommands() => _scenario.GenerateCommands();

    [Benchmark]
    public TickResult TrafficTick()
    {
        TickResult result = _scenario.Simulation.Tick(_scenario.LastCommands);
        _scenario.AfterTick();
        return result;
    }
}
