using BenchmarkDotNet.Attributes;
using Tzar2.Content.Compiled;
using Tzar2.Core.Rng;
using Tzar2.Sim;
using Tzar2.Sim.Navigation;
using Tzar2.Sim.Scenarios;

namespace Tzar2.Benchmarks;

/// <summary>
/// Pathfinding micro-benchmarks on the B3 maze (TDD §21): one HPA* route query between random corridor tiles, one
/// shared flow-field build, and the navigation build at match load. Each invocation runs <see cref="Queries"/>
/// queries, so the per-query time is the reported mean / <see cref="Queries"/>.
/// </summary>
[MemoryDiagnoser]
public class PathfindingBenchmarks
{
    /// <summary>Queries per invocation.</summary>
    public const int Queries = 100;

    private readonly PathSearch _search = new();
    private readonly int[] _starts = new int[Queries];
    private readonly int[] _goals = new int[Queries];
    private World _world = null!;
    private NavLayer _layer = null!;
    private MapData _map = null!;

    [GlobalSetup]
    public void Setup()
    {
        (_map, _, _) = ScenarioMaps.Maze(512, corridor: 3, wall: 1, seed: 0xB3_2026UL);
        _world = new World(SyntheticScenario.CreateRuleset(), _map, new SimulationConfig { PlayerCount = 2 });
        _world.Navigation.EnsureUpToDate();
        _layer = _world.Navigation.ForType(SyntheticScenario.Swordsman)!;
        var rng = Xoshiro256StarStar.FromSeed(7);
        for (int i = 0; i < Queries; i++)
        {
            _starts[i] = RandomWalkable(ref rng);
            _goals[i] = RandomWalkable(ref rng);
        }
    }

    /// <summary>100 route queries (abstract A* only, as the planner issues them for long paths).</summary>
    [Benchmark]
    public int RouteQueries()
    {
        int expanded = 0;
        for (int i = 0; i < Queries; i++)
        {
            _search.FindRoute(_layer, _starts[i], _goals[i]);
            expanded += _search.Expanded;
        }

        return expanded;
    }

    /// <summary>100 fully refined tile paths (for comparison; the planner refines only short paths).</summary>
    [Benchmark]
    public int TilePathQueries()
    {
        int tiles = 0;
        for (int i = 0; i < Queries; i++)
        {
            _search.FindTilePath(_layer, _starts[i], _goals[i]);
            tiles += _search.Tiles.Length;
        }

        return tiles;
    }

    /// <summary>One flow field over the whole maze (abstract Dijkstra from the goal).</summary>
    [Benchmark]
    public int FlowFieldBuild()
    {
        var cache = new FlowFieldCache();
        return cache.Get(_layer, _goals[0], _world.Navigation.Version, 0).Cost;
    }

    /// <summary>Navigation layers built from scratch at match load (512² maze).</summary>
    [Benchmark]
    public int NavigationBuild()
    {
        var world = new World(SyntheticScenario.CreateRuleset(), _map, new SimulationConfig { PlayerCount = 2 });
        world.Navigation.EnsureUpToDate();
        return world.Navigation.Layers[0].PortalCount;
    }

    private int RandomWalkable(ref Xoshiro256StarStar rng)
    {
        while (true)
        {
            int t = rng.NextInt(512 * 512);
            if (_layer.IsWalkable(t))
            {
                return t;
            }
        }
    }
}
