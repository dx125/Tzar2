using System;
using System.Linq;
using FsCheck.Xunit;
using Tzar2.Content.Compiled;
using Tzar2.Core.Rng;
using Tzar2.Sim.Map;
using Tzar2.Sim.Navigation;
using Tzar2.Sim.Scenarios;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Sim.Tests.Navigation;

/// <summary>
/// Pathfinding invariants (TDD §22 item 1): connectivity is consistent with plain A*, HPA* paths are valid and within
/// a fixed bound of optimal, incremental updates equal rebuilds, and there is no depth limit (§9.2 acceptance 6).
/// </summary>
public class NavLayerTests(ITestOutputHelper output)
{
    /// <summary>
    /// Per-query bound on HPA* tile-path cost vs. the optimum for random maps and queries (ADR 0022). Short queries
    /// (≤ 48 tiles per axis) are solved optimally in a window; the abstract graph's single transition per entrance
    /// costs up to ~30% on unlucky medium-range queries.
    /// </summary>
    private const double OptimalityBound = 1.35;

    /// <summary>Worst ratio over the fixed 1,000-query sample (measured 1.11).</summary>
    private const double SampleWorstBound = 1.16;

    /// <summary>Mean ratio over the fixed sample (measured 1.015).</summary>
    private const double SampleMeanBound = 1.03;

    private static MapData RandomMap(int size, int obstacleBasisPoints, ulong seed)
    {
        var rng = Xoshiro256StarStar.FromSeed(seed);
        byte[] cliffs = new byte[size * size];
        for (int i = 0; i < cliffs.Length; i++)
        {
            cliffs[i] = rng.NextChance(obstacleBasisPoints) ? (byte)1 : (byte)0;
        }

        // Some wall segments so that paths have to detour.
        for (int w = 0; w < size / 6; w++)
        {
            int x = rng.NextInt(size);
            int y = rng.NextInt(size);
            bool horizontal = rng.NextBool();
            int length = rng.NextInt(4, size / 2);
            for (int k = 0; k < length; k++)
            {
                int tx = horizontal ? x + k : x;
                int ty = horizontal ? y : y + k;
                if (tx < size && ty < size)
                {
                    cliffs[(ty * size) + tx] = 1;
                }
            }
        }

        return ScenarioMaps.Build($"random-{seed}", size, size, new byte[size * size], cliffs);
    }

    private static NavLayer LandLayer(World world)
    {
        world.Navigation.EnsureUpToDate();
        return world.Navigation.ForType(SyntheticScenario.Swordsman)!;
    }

    private static World NewWorld(MapData map) => new(TestWorlds.Ruleset, map, new SimulationConfig { PlayerCount = 2 });

    [Property(MaxTest = 60)]
    public void HPA_paths_are_valid_connected_and_near_optimal(ulong seed)
    {
        var rng = Xoshiro256StarStar.FromSeed(seed);
        int size = rng.NextInt(20, 90);
        World world = NewWorld(RandomMap(size, rng.NextInt(500, 3000), seed));
        NavLayer layer = LandLayer(world);
        var search = new PathSearch();
        for (int q = 0; q < 12; q++)
        {
            int start = rng.NextInt(size * size);
            int goal = rng.NextInt(size * size);
            int optimal = ReferenceAStar.Cost(layer, start, goal, out _);
            bool connected = layer.AreConnected(start, goal);
            if (connected != (optimal >= 0))
            {
                Assert.Fail($"connectivity mismatch {start}->{goal}: components say {connected}, A* cost {optimal}");
            }

            if (!connected)
            {
                continue;
            }

            if (!search.FindTilePath(layer, start, goal))
            {
                Assert.Fail($"HPA* found no path {start}->{goal}");
            }

            ReadOnlySpan<int> tiles = search.Tiles;
            if (tiles[0] != start || tiles[^1] != goal)
            {
                Assert.Fail("path does not join start and goal");
            }

            int cost = 0;
            for (int k = 1; k < tiles.Length; k++)
            {
                int a = tiles[k - 1];
                int b = tiles[k];
                int dx = (b % size) - (a % size);
                int dy = (b / size) - (a / size);
                int dir = Array.FindIndex([(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)], d => d == (dx, dy));
                if (dir < 0 || !layer.CanStep(a % size, a / size, dir))
                {
                    Assert.Fail($"illegal step {a}->{b}");
                }

                cost += (dir & 1) == 0 ? NavLayer.StraightCost : NavLayer.DiagonalCost;
            }

            if (cost != search.Cost || cost < optimal || cost > (optimal * OptimalityBound) + (2 * NavLayer.StraightCost))
            {
                Assert.Fail($"cost {cost} (reported {search.Cost}) vs optimal {optimal}");
            }

            // Smoothed waypoints: every segment is walkable.
            search.Smooth(layer, layer.CenterX(start), layer.CenterY(start), layer.CenterX(goal), layer.CenterY(goal), margin: 0);
            ReadOnlySpan<int> w = search.Waypoints;
            int px = layer.CenterX(start);
            int py = layer.CenterY(start);
            for (int k = 0; k < w.Length; k += 2)
            {
                if (!PathSearch.LineWalkable(layer, px, py, w[k], w[k + 1], out _))
                {
                    Assert.Fail("smoothed segment crosses an obstacle");
                }

                px = w[k];
                py = w[k + 1];
            }
        }

    }

    [Fact]
    public void HPA_cost_ratio_over_many_queries()
    {
        double worst = 1;
        double sum = 0;
        int n = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            World world = NewWorld(RandomMap(96, 1500, seed));
            NavLayer layer = LandLayer(world);
            var search = new PathSearch();
            var rng = Xoshiro256StarStar.FromSeed(seed * 31);
            for (int q = 0; q < 50; q++)
            {
                int start = rng.NextInt(96 * 96);
                int goal = rng.NextInt(96 * 96);
                if (!layer.AreConnected(start, goal) || start == goal)
                {
                    continue;
                }

                int optimal = ReferenceAStar.Cost(layer, start, goal, out _);
                Assert.True(search.FindTilePath(layer, start, goal));
                double ratio = (double)search.Cost / optimal;
                worst = Math.Max(worst, ratio);
                sum += ratio;
                n++;
            }
        }

        output.WriteLine($"{n} queries: mean cost ratio {sum / n:F4}, worst {worst:F4}");
        Assert.True(worst <= SampleWorstBound, $"worst ratio {worst}");
        Assert.True(sum / n <= SampleMeanBound, $"mean ratio {sum / n}");
    }

    [Property(MaxTest = 40)]
    public void Incremental_updates_equal_a_rebuild(ulong seed)
    {
        var rng = Xoshiro256StarStar.FromSeed(seed);
        int size = rng.NextInt(16, 70);
        var ruleset = new Ruleset(
            "nav",
            TestWorlds.Ruleset.TerrainTypes,
            [
                new EntityTypeDefinition(0, "small", Locomotor.Foot, 64 * 48, 10),
                new EntityTypeDefinition(1, "big", Locomotor.Large, 64 * 24, 10, clearanceTiles: 2),
                new EntityTypeDefinition(2, "rock", Locomotor.Foot, 0, 10, footprintWidth: 1, footprintHeight: 1),
                new EntityTypeDefinition(3, "house", Locomotor.Foot, 0, 10, footprintWidth: 3, footprintHeight: 3, footprint: [0, 1, 0, 1, 1, 1, 0, 1, 0]),
                new EntityTypeDefinition(4, "hall", Locomotor.Foot, 0, 10, footprintWidth: 5, footprintHeight: 4, footprint: [0, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 0, 1, 1, 1, 0]),
            ]);
        MapData map = RandomMap(size, 1000, seed);
        var world = new World(ruleset, map, new SimulationConfig { PlayerCount = 1 });
        world.Navigation.EnsureUpToDate();
        var placed = new System.Collections.Generic.List<Tzar2.Core.Ids.EntityId>();
        for (int edit = 0; edit < 40; edit++)
        {
            if (placed.Count > 0 && rng.NextChance(3500))
            {
                int k = rng.NextInt(placed.Count);
                world.Destroy(placed[k]);
                placed.RemoveAt(k);
            }
            else
            {
                // Rocks, and masked multi-tile footprints (which may overlap other structures).
                ushort type = (ushort)(2 + rng.NextInt(3));
                int w = type == 2 ? 1 : type == 3 ? 3 : 5;
                int h = type == 2 ? 1 : type == 3 ? 3 : 4;
                placed.Add(world.PlaceStructure(type, 0, rng.NextInt(size - w + 1), rng.NextInt(size - h + 1)));
            }

            if (rng.NextChance(4000))
            {
                world.Navigation.EnsureUpToDate();
            }
        }

        world.Navigation.EnsureUpToDate();
        var fresh = new World(ruleset, map, new SimulationConfig { PlayerCount = 1 });
        for (int t = 0; t < size * size; t++)
        {
            for (int b = 0; b < world.Map.Blockers[t]; b++)
            {
                fresh.Map.AddBlocker(t % size, t / size);
            }
        }

        fresh.Navigation.EnsureUpToDate();
        Assert.Equal(2, world.Navigation.LayerCount);
        for (int l = 0; l < world.Navigation.LayerCount; l++)
        {
            NavLayer incremental = world.Navigation.Layers[l];
            NavLayer rebuilt = fresh.Navigation.Layers[l];
            if (incremental.Digest() != rebuilt.Digest())
            {
                Assert.Fail($"layer {l} differs after incremental updates");
            }
        }

    }

    [Fact]
    public void Clearance_two_units_do_not_fit_through_one_tile_gaps()
    {
        MapData map = ScenarioMaps.FromAscii(
            "gap",
            "..........",
            "..........",
            "#####.####",
            "..........",
            "..........");
        var ruleset = new Ruleset(
            "nav",
            TestWorlds.Ruleset.TerrainTypes,
            [
                new EntityTypeDefinition(0, "small", Locomotor.Foot, 64 * 48, 10),
                new EntityTypeDefinition(1, "big", Locomotor.Large, 64 * 24, 10, clearanceTiles: 2),
            ]);
        var world = new World(ruleset, map, new SimulationConfig { PlayerCount = 1 });
        NavLayer small = world.Navigation.ForType(0)!;
        NavLayer big = world.Navigation.ForType(1)!;
        Assert.NotSame(small, big);
        Assert.True(small.AreConnected(0, (4 * 10) + 9));
        Assert.False(big.AreConnected(0, (3 * 10) + 8));
        Assert.True(big.IsWalkable(0, 0));
        Assert.False(big.IsWalkable(9, 0)); // the 2×2 square would leave the map
    }

    [Fact]
    public void Locomotors_with_identical_passability_share_a_layer_and_ships_get_their_own()
    {
        World world = NewWorld(SyntheticScenario.CreateMap(64, 5));
        world.Navigation.EnsureUpToDate();
        NavigationGrid nav = world.Navigation;
        Assert.Equal(2, nav.LayerCount);
        Assert.Same(nav.ForType(SyntheticScenario.Swordsman), nav.ForType(SyntheticScenario.Knight));
        Assert.Same(nav.ForType(SyntheticScenario.Swordsman), nav.ForType(SyntheticScenario.Catapult));
        Assert.NotSame(nav.ForType(SyntheticScenario.Swordsman), nav.ForType(SyntheticScenario.Boat));
        Assert.Null(nav.ForType(SyntheticScenario.Dragon));
        Assert.Null(nav.ForType(SyntheticScenario.House));
    }

    [Property(MaxTest = 30)]
    public void Route_queries_cost_and_charge_exactly_what_the_cluster_searches_give(ulong seed)
    {
        // FindRoute reads the start and goal costs off the portal trees (ADR 0080); FindTilePath still runs the two
        // cluster searches. Same abstract search, so the cost and the charged expansions must be identical.
        var rng = Xoshiro256StarStar.FromSeed(seed);
        int size = rng.NextInt(70, 160);
        World world = NewWorld(RandomMap(size, rng.NextInt(300, 2500), seed));
        NavLayer layer = LandLayer(world);
        var route = new PathSearch();
        var tiles = new PathSearch();
        int compared = 0;
        for (int q = 0; q < 40; q++)
        {
            int start = rng.NextInt(size * size);
            int goal = rng.NextInt(size * size);
            if (!layer.IsWalkable(start) || !layer.IsWalkable(goal) || !layer.AreConnected(start, goal))
            {
                continue;
            }

            Assert.True(route.FindRoute(layer, start, goal));
            Assert.True(tiles.FindTilePath(layer, start, goal));
            Assert.Equal(tiles.Cost, route.Cost);
            Assert.Equal(tiles.Expanded, route.Expanded);
            compared++;
        }

        Assert.True(compared > 0);
    }

    [Fact]
    public void Buildings_block_and_unblock_incrementally()
    {
        World world = NewWorld(TestWorlds.Flat(48));
        NavLayer layer = LandLayer(world);
        int center = (20 * 48) + 21;
        Assert.True(layer.IsWalkable(center));
        int components = layer.ComponentCount;
        var house = world.PlaceStructure(SyntheticScenario.House, 0, 20, 19);
        world.Navigation.EnsureUpToDate();
        Assert.False(layer.IsWalkable(center));
        Assert.True(layer.IsWalkable((19 * 48) + 20)); // a free corner of the footprint mask
        Assert.Equal(components, layer.ComponentCount);
        world.Destroy(house);
        world.Navigation.EnsureUpToDate();
        Assert.True(layer.IsWalkable(center));
        Assert.Equal(0, world.Map.Blockers.ToArray().Sum(b => b));
    }

    [Fact]
    public void A_long_path_across_a_512_maze_is_found_without_a_depth_limit()
    {
        (MapData map, (int X, int Y) first, (int X, int Y) last) = ScenarioMaps.Maze(512, corridor: 3, wall: 1, seed: 2026);
        World world = NewWorld(map);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        NavLayer layer = LandLayer(world);
        long build = watch.ElapsedMilliseconds;
        int start = (first.Y * 512) + first.X;
        int goal = (last.Y * 512) + last.X;
        Assert.True(layer.AreConnected(start, goal));
        var search = new PathSearch();
        watch.Restart();
        Assert.True(search.FindTilePath(layer, start, goal));
        long query = watch.ElapsedMilliseconds;
        int optimal = ReferenceAStar.Cost(layer, start, goal, out int referenceExpanded);
        output.WriteLine($"layer build {build} ms; HPA* {query} ms, {search.Expanded} expansions, {search.Tiles.Length} tiles, cost {search.Cost}; plain A* cost {optimal} with {referenceExpanded} expansions; portals {layer.PortalCount}");
        Assert.InRange(search.Cost, optimal, (int)(optimal * SampleWorstBound));
        Assert.True(search.Tiles.Length > 1000);
    }
}
