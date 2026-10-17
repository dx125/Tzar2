using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Rng;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;
using Tzar2.Sim.Navigation;
using Tzar2.Sim.Scenarios;
using Xunit;
using Xunit.Abstractions;
using static Tzar2.Sim.Tests.SimDriver;

namespace Tzar2.Sim.Tests.Review;

// Regression tests ported from the independent review of PR #12 (blockers B1/B2, should-fix S1-S5).


public class AdversarialTests(ITestOutputHelper output)
{
    private static CommandList Trees(MapState map, ulong seed, int count)
    {
        var rng = Xoshiro256StarStar.FromSeed(seed);
        var batch = new CommandList();
        int placed = 0;
        while (placed < count)
        {
            int tx = rng.NextInt(map.Width);
            int ty = rng.NextInt(map.Height);
            if (map.IsPassable(Locomotor.Foot, tx, ty))
            {
                batch.Add(CommandKind.DebugSpawn, default, TileCenter(tx), TileCenter(ty), arg0: SyntheticScenario.Tree, arg1: 1);
                placed++;
            }
        }

        var set = new CommandList();
        set.AppendStamped(0, batch);
        return set;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public void Restore_after_buildings_placed_mid_game_matches_uninterrupted(int group)
    {
        var options = new MazeMoveScenarioOptions { MapSize = 96, Units = 120, GroupSize = group, Bystanders = 40, Seed = 77, PathBudgetPerTick = 20_000 };
        using var a = new MazeMoveScenario(options, workerCount: 1);
        using var b = new MazeMoveScenario(options, workerCount: 4);
        var none = new CommandList();
        Assert.Equal(a.RunTick(), b.RunTick());
        for (int t = 1; t < 60; t++)
        {
            CommandList set = t is 15 or 40 ? Trees(a.Simulation.World.Map, (ulong)t, 25) : none;
            Assert.Equal(a.Simulation.Tick(set), b.Simulation.Tick(set));
        }

        byte[] snap = b.Simulation.CaptureSnapshot();
        using Simulation r = Simulation.Restore(snap, b.Ruleset, b.Map, workerCount: 3);
        int replans = 0;
        for (int t = 60; t < 500; t++)
        {
            CommandList set = t is 61 or 90 or 200 or 201 ? Trees(a.Simulation.World.Map, (ulong)t, 25) : none;
            TickResult e = a.Simulation.Tick(set);
            Assert.Equal(e, b.Simulation.Tick(set));
            Assert.Equal(e, r.Tick(set));
            replans += a.Simulation.World.PathQueue.Count;
        }

        output.WriteLine($"obstacle version {a.Simulation.World.Map.ObstacleVersion}; full hash equal");
        Assert.Equal(a.Simulation.ComputeFullHash(), r.ComputeFullHash());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    public void A_far_away_tree_makes_route_units_stop_and_replan(int group)
    {
        var options = new MazeMoveScenarioOptions { MapSize = 256, Units = 300, GroupSize = group, Seed = 5 };
        using var s = new MazeMoveScenario(options, workerCount: 4);
        for (int t = 0; t < 30; t++) { s.RunTick(); }
        World w = s.Simulation.World;
        int Count(MotionMode mode) => w.Motions.Components.ToArray().Count(m => m.Mode == mode && m.State == MotionState.Moving);
        string Modes() => string.Join(", ", Enum.GetValues<MotionMode>().Select(m => $"{m}={Count(m)}"));
        output.WriteLine($"before: {Modes()}");
        // One tree in the far bottom-right corner corridor.
        int fx = -1, fy = -1;
        for (int y = w.Map.Height - 1; y > 0 && fx < 0; y--)
        {
            for (int x = w.Map.Width - 1; x > 0; x--)
            {
                if (w.Map.IsPassable(Locomotor.Foot, x, y)) { fx = x; fy = y; break; }
            }
        }

        var batch = new CommandList();
        batch.Add(CommandKind.DebugSpawn, default, TileCenter(fx), TileCenter(fy), arg0: SyntheticScenario.Tree, arg1: 1);
        var set = new CommandList();
        set.AppendStamped(0, batch);
        int routes = Count(MotionMode.Route);
        s.Simulation.Tick(set);
        int waiting = Count(MotionMode.Waiting);
        output.WriteLine($"after one tree at ({fx},{fy}): {Modes()}");
        int stalled = 0;
        var none = new CommandList();
        var trace = new List<int>();
        for (int t = 0; t < 20 && Count(MotionMode.Waiting) > 0; t++) { s.Simulation.Tick(none); stalled++; trace.Add(Count(MotionMode.Waiting)); }
        output.WriteLine("waiting per tick: " + string.Join(" ", trace));
        using var base0 = new MazeMoveScenario(options, workerCount: 4);
        var tr0 = new List<int>();
        for (int t = 0; t < 52; t++) { base0.RunTick(); if (t >= 29) tr0.Add(base0.Simulation.World.Motions.Components.ToArray().Count(m => m.Mode == MotionMode.Waiting && m.State == MotionState.Moving)); }
        output.WriteLine("baseline waiting per tick (no tree): " + string.Join(" ", tr0));
        output.WriteLine($"extra ticks until nobody waits: {stalled}");
        Assert.True(waiting <= 5, $"{waiting} units stopped to re-plan because of one far tree ({routes} were on routes)");
    }

    private static MapData RiverMap(int width, int height, int riverX0, int riverX1, int bridgeY0, int bridgeWidth)
    {
        var rows = new string[height];
        for (int y = 0; y < height; y++)
        {
            char[] row = new char[width];
            for (int x = 0; x < width; x++)
            {
                bool river = x >= riverX0 && x <= riverX1;
                bool bridge = y >= bridgeY0 && y < bridgeY0 + bridgeWidth;
                row[x] = river && !bridge ? '~' : '.';
            }

            rows[y] = new string(row);
        }

        return ScenarioMaps.FromAscii("river", rows);
    }

    [Theory]
    [InlineData(0, 0, 30)]
    [InlineData(0, 0, 6)]
    public void Two_groups_cross_a_one_tile_bridge_both_ways(int ownerWest, int ownerEast, int n)
    {
        MapData map = RiverMap(72, 40, 33, 38, bridgeY0: 20, bridgeWidth: 1);
        using var d = new SimDriver(map, players: 2);
        var west = new List<EntityId>();
        var east = new List<EntityId>();
        for (int i = 0; i < n; i++)
        {
            west.Add(d.World.Spawn(SyntheticScenario.Swordsman, ownerWest, (8 * Tile) + ((i % 10) * Tile * 3 / 4), (14 * Tile) + ((i / 10) * Tile * 3 / 4)));
            east.Add(d.World.Spawn(SyntheticScenario.Swordsman, ownerEast, (56 * Tile) + ((i % 10) * Tile * 3 / 4), (14 * Tile) + ((i / 10) * Tile * 3 / 4)));
        }

        d.MoveToTile(ownerWest, west.ToArray(), 60, 20);
        d.MoveToTile(ownerEast, east.ToArray(), 12, 20);
        int ticks = d.RunUntil(() => !d.AnyMoving(west) && !d.AnyMoving(east), 300 * 20);
        int wc = west.Count(u => d.PositionOf(u).X > 39 * Tile);
        int ec = east.Count(u => d.PositionOf(u).X < 33 * Tile);
        int replans = d.Events.Count;
        output.WriteLine($"owners {ownerWest}/{ownerEast} n={n}: {ticks} ticks, west crossed {wc}/{n}, east crossed {ec}/{n}, blocked {d.Count(SimEventKind.Blocked)}, still moving {west.Concat(east).Count(d.IsMoving)}");
        Assert.Equal(n, wc);
        Assert.Equal(n, ec);
    }

    [Fact]
    public void Idle_enemy_on_a_one_tile_bridge_never_ends_the_order()
    {
        MapData map = RiverMap(72, 40, 33, 38, bridgeY0: 20, bridgeWidth: 1);
        using var d = new SimDriver(map, players: 2);
        EntityId enemy = d.Spawn(SyntheticScenario.Swordsman, 1, 35, 20);
        var west = new List<EntityId>();
        for (int i = 0; i < 10; i++)
        {
            west.Add(d.World.Spawn(SyntheticScenario.Swordsman, 0, (8 * Tile) + ((i % 10) * Tile * 3 / 4), 14 * Tile));
        }

        d.MoveToTile(0, west.ToArray(), 60, 20);
        long minD = long.MaxValue;
        d.RunUntil(() =>
        {
            Position e = d.PositionOf(enemy);
            if (d.World.Tick.Value % 20 == 0 || d.World.Tick.Value < 3) output.WriteLine($"t{d.World.Tick.Value} enemy ({e.X},{e.Y}) world {d.World.Map.WorldWidth}x{d.World.Map.WorldHeight} motion {d.MotionOf(enemy).State}/{d.MotionOf(enemy).Mode}");
            foreach (EntityId u in west)
            {
                Position p = d.PositionOf(u);
                minD = Math.Min(minD, (long)Math.Sqrt(((double)(p.X - e.X) * (p.X - e.X)) + ((double)(p.Y - e.Y) * (p.Y - e.Y))));
            }

            return !d.AnyMoving(west);
        }, 120 * 20);
        output.WriteLine($"min distance mover-enemy {minD} (radii sum 896); enemy at {d.PositionOf(enemy).X},{d.PositionOf(enemy).Y}");
        output.WriteLine(string.Join(",", d.Events.GroupBy(e => e.Event.Kind).Select(g => $"{g.Key}={g.Count()}")));
        output.WriteLine("arrived ticks: " + string.Join(" ", d.Events.Where(e => e.Event.Kind == SimEventKind.Arrived).Select(e => e.Tick)));
        output.WriteLine("final tiles: " + string.Join(" ", west.Select(u => $"({d.PositionOf(u).X / Tile},{d.PositionOf(u).Y / Tile})")));
        output.WriteLine($"after 120 s: moving {west.Count(d.IsMoving)}, blocked {d.Count(SimEventKind.Blocked)}, queue {d.World.PathQueue.Count}");

        // Enemies are hard blockers (ADR 0025): nobody walks through the enemy, and the order ends instead of looping.
        Assert.True(minD >= 896 - 64, $"a mover overlapped the enemy: distance {minD}");
        Assert.False(d.AnyMoving(west), "units keep re-planning forever against an idle enemy on the bridge");
        Assert.All(west, u => Assert.True(d.PositionOf(u).X < 39 * Tile, "a unit got past the enemy on the bridge"));
    }

    [Fact]
    public void Enemy_columns_meeting_on_a_one_tile_bridge_stop_without_overlapping()
    {
        MapData map = RiverMap(72, 40, 33, 38, bridgeY0: 20, bridgeWidth: 1);
        using var d = new SimDriver(map, players: 2);
        var west = new List<EntityId>();
        var east = new List<EntityId>();
        for (int i = 0; i < 8; i++)
        {
            west.Add(d.World.Spawn(SyntheticScenario.Swordsman, 0, (8 * Tile) + ((i % 4) * Tile * 3 / 4), (18 * Tile) + ((i / 4) * Tile * 3 / 4)));
            east.Add(d.World.Spawn(SyntheticScenario.Swordsman, 1, (56 * Tile) + ((i % 4) * Tile * 3 / 4), (18 * Tile) + ((i / 4) * Tile * 3 / 4)));
        }

        d.MoveToTile(0, west.ToArray(), 60, 20);
        d.MoveToTile(1, east.ToArray(), 12, 20);
        long minD = long.MaxValue;
        d.RunUntil(
            () =>
            {
                foreach (EntityId a in west)
                {
                    foreach (EntityId b in east)
                    {
                        Position p = d.PositionOf(a);
                        Position q = d.PositionOf(b);
                        minD = Math.Min(minD, (long)Math.Sqrt(((double)(p.X - q.X) * (p.X - q.X)) + ((double)(p.Y - q.Y) * (p.Y - q.Y))));
                    }
                }

                return !d.AnyMoving(west) && !d.AnyMoving(east);
            },
            180 * 20);
        output.WriteLine($"min distance {minD}; blocked {d.Count(SimEventKind.Blocked)}");
        Assert.True(minD >= 896 - 128, $"enemies overlapped: distance {minD}");
        Assert.False(d.AnyMoving(west) || d.AnyMoving(east), "the jam never resolved into stopped units");
    }

    [Fact]
    public void Patrol_between_two_unreachable_points_spams()
    {
        string[] rows = Enumerable.Range(0, 40).Select(y => string.Concat(Enumerable.Range(0, 40).Select(x => x is >= 20 and <= 21 ? '#' : '.'))).ToArray();
        using var d = new SimDriver(ScenarioMaps.FromAscii("split", rows));
        EntityId u = d.Spawn(SyntheticScenario.Swordsman, 0, 5, 5);
        d.Order(0, CommandKind.Patrol, [u], TileCenter(30), TileCenter(10));
        for (int t = 0; t < 200; t++) { d.Step(); }
        output.WriteLine($"200 ticks: GoalUnreachable {d.Count(SimEventKind.GoalUnreachable)}, Arrived {d.Count(SimEventKind.Arrived)}");
        Assert.True(d.Count(SimEventKind.Arrived) < 20);
    }

    [Fact]
    public void Building_dropped_on_an_in_flight_path_mid_corridor()
    {
        string[] rows = Enumerable.Range(0, 30).Select(y => string.Concat(Enumerable.Range(0, 80).Select(x => y is >= 10 and <= 12 ? '.' : '#'))).ToArray();
        using var d = new SimDriver(ScenarioMaps.FromAscii("corr", rows));
        EntityId u = d.Spawn(SyntheticScenario.Swordsman, 0, 2, 11);
        d.MoveToTile(0, [u], 77, 11);
        for (int t = 0; t < 40; t++) { d.Step(); }
        // Seal the 3-tile corridor in front of the unit with three trees in a column.
        int fx = (d.PositionOf(u).X / Tile) + 6;
        for (int ty = 10; ty <= 12; ty++)
        {
            d.Order(0, CommandKind.DebugSpawn, default, TileCenter(fx), TileCenter(ty), arg0: SyntheticScenario.Tree, arg1: 1);
        }

        int ticks = d.RunUntil(() => !d.IsMoving(u), 60 * 20);
        output.WriteLine($"stopped after {ticks} ticks at tile {d.PositionOf(u).X / Tile}; unreachable {d.Count(SimEventKind.GoalUnreachable)}, blocked {d.Count(SimEventKind.Blocked)}, state {d.MotionOf(u).State}");
        Assert.False(d.IsMoving(u));
        Assert.True(d.PositionOf(u).X / Tile < fx);
    }
}

public class AdversarialOrderTests(ITestOutputHelper output)
{
    private static MapData Split() => ScenarioMaps.FromAscii("split", Enumerable.Range(0, 40).Select(y => string.Concat(Enumerable.Range(0, 40).Select(x => x is >= 20 and <= 21 ? '#' : '.'))).ToArray());

    [Fact]
    public void Patrol_to_a_point_just_across_a_wall()
    {
        using var d = new SimDriver(Split());
        EntityId u = d.Spawn(SyntheticScenario.Swordsman, 0, 19, 10);
        d.Order(0, CommandKind.Patrol, [u], TileCenter(22), TileCenter(10));
        for (int t = 0; t < 400; t++) { d.Step(); }
        output.WriteLine($"400 ticks: GoalUnreachable {d.Count(SimEventKind.GoalUnreachable)}, Arrived {d.Count(SimEventKind.Arrived)}, requests now {d.World.PathQueue.Count}");
        Assert.True(d.Count(SimEventKind.Arrived) < 20);
    }

    [Fact]
    public void Follow_an_unreachable_idle_target()
    {
        using var d = new SimDriver(Split());
        EntityId u = d.Spawn(SyntheticScenario.Swordsman, 0, 10, 10);
        EntityId target = d.Spawn(SyntheticScenario.Swordsman, 1, 30, 10);
        d.Order(0, CommandKind.Follow, [u], target: target);
        for (int t = 0; t < 2000; t++) { d.Step(); }
        output.WriteLine($"2000 ticks: GoalUnreachable {d.Count(SimEventKind.GoalUnreachable)}, Arrived {d.Count(SimEventKind.Arrived)}, state {d.MotionOf(u).State}");
        Assert.True(d.Count(SimEventKind.GoalUnreachable) < 10);
    }
}

public class AdversarialSnapshotTests(ITestOutputHelper output)
{
    [Fact]
    public void Crafted_queue_entry_with_flow_flag_and_no_goal_tile_is_rejected_on_restore()
    {
        MapData map = TestWorlds.Flat(48);
        using var d = new SimDriver(map, pathBudget: 1);
        var units = Enumerable.Range(0, 3).Select(i => d.Spawn(SyntheticScenario.Swordsman, 0, 5 + i, 5)).ToArray();
        foreach (EntityId u in units) { d.MoveToTile(0, [u], 40, 40); }
        d.Step(); // budget 1: one accepted, the rest still queued (Waiting, GoalTile -1)
        var q = d.World.PathQueue;
        Assert.True(q.Count >= 1);
        ref PathRequest r = ref q[q.Count - 1];
        r.Flags = PathRequestFlags.Prepared | PathRequestFlags.Flow | PathRequestFlags.FlowHead;
        byte[] snap = d.Sim.CaptureSnapshot(); // a consistent, correctly hashed snapshot of crafted state
        Exception? ex = Record.Exception(() =>
        {
            using Simulation s = Simulation.Restore(snap, TestWorlds.Ruleset, map);
            for (int t = 0; t < 10; t++) { s.Tick(new Tzar2.Sim.Commands.CommandList()); }
        });
        output.WriteLine(ex?.GetType().Name + ": " + ex?.Message);
        Assert.True(ex is System.IO.InvalidDataException, "restore accepted it and the tick crashed: " + ex);
    }
}

public class AdversarialFootprintTests(ITestOutputHelper output)
{
    [Fact]
    public void Unit_inside_a_large_new_building_escapes()
    {
        var types = TestWorlds.Ruleset.EntityTypes.ToList();
        ushort castle = (ushort)types.Count;
        types.Add(new EntityTypeDefinition(castle, "castle", Locomotor.Foot, 0, 400, footprintWidth: 11, footprintHeight: 11, leavesCorpse: false));
        var ruleset = new Ruleset("big", TestWorlds.Ruleset.TerrainTypes, types, maxPlayers: 16);
        using var d = new SimDriver(TestWorlds.Flat(40), ruleset: ruleset);
        EntityId u = d.Spawn(SyntheticScenario.Swordsman, 0, 20, 20);
        d.Step();
        d.World.IsLocked = false;
        d.World.PlaceStructure(castle, 1, 15, 15);
        d.World.IsLocked = true;
        for (int t = 0; t < 100; t++) { d.Step(); }
        d.MoveToTile(0, [u], 2, 2);
        for (int t = 0; t < 400; t++) { d.Step(); }
        Position p = d.PositionOf(u);
        output.WriteLine($"unit at tile ({p.X / Tile},{p.Y / Tile}), state {d.MotionOf(u).State}, unreachable {d.Count(SimEventKind.GoalUnreachable)}");
        Assert.True(d.World.Navigation.ForType(SyntheticScenario.Swordsman)!.IsWalkablePoint(p.X, p.Y));
        Assert.Equal((2, 2), (p.X / Tile, p.Y / Tile)); // and it can walk away afterwards
        Assert.Equal(0, d.Count(SimEventKind.Blocked));
    }
}

public class AdversarialChurnTests(ITestOutputHelper output)
{
    [Fact]
    public void B3_with_a_tree_chopped_every_second()
    {
        foreach (bool trees in new[] { false, true })
        {
            var options = new MazeMoveScenarioOptions { MapSize = 512, Units = 500, GroupSize = 1, Seed = 0xB3_2026UL };
            using var s = new MazeMoveScenario(options, workerCount: 4);
            World w = s.Simulation.World;
            s.RunTick();
            var none = new CommandList();
            long waitingUnitTicks = 0;
            int maxWaiting = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            double worst = 0;
            for (int t = 1; t < 600; t++)
            {
                CommandList set = none;
                if (trees && t % 20 == 0)
                {
                    // A tree in the far corner, alternately placed and removed position-wise (new tile each time).
                    var batch = new CommandList();
                    int tx = 511 - (t / 20 % 40), ty = 511;
                    for (; ty > 0 && !w.Map.IsPassable(Locomotor.Foot, tx, ty); ty--) { }
                    batch.Add(CommandKind.DebugSpawn, default, TileCenter(tx), TileCenter(ty), arg0: SyntheticScenario.Tree, arg1: 1);
                    set = new CommandList();
                    set.AppendStamped(0, batch);
                }

                var tw = System.Diagnostics.Stopwatch.StartNew();
                s.Simulation.Tick(set);
                worst = Math.Max(worst, tw.Elapsed.TotalMilliseconds);
                int waiting = 0;
                foreach (ref Motion m in w.Motions.Components) { waiting += m.State == MotionState.Moving && m.Mode == MotionMode.Waiting ? 1 : 0; }
                if (t >= 10)
                {
                    // After the initial planning window.
                    waitingUnitTicks += waiting;
                    maxWaiting = Math.Max(maxWaiting, waiting);
                }
            }

            output.WriteLine($"trees={trees}: unit-ticks spent Waiting {waitingUnitTicks} ({100.0 * waitingUnitTicks / (500 * 599):F1}%), max waiting in a tick {maxWaiting}, worst tick {worst:F1} ms");
            Assert.True(maxWaiting <= 60, $"{maxWaiting} units waited for a plan in one tick (trees={trees})");
            Assert.True(waitingUnitTicks <= 500 * 599 * 3 / 100, $"{waitingUnitTicks} unit-ticks spent waiting (trees={trees})");
        }
    }
}
