using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;
using Tzar2.Sim.Scenarios;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.SimDriver;

namespace Tzar2.Sim.Tests;

/// <summary>Movement orders, avoidance, obstacles and the path budget (TDD §9.2/§9.3).</summary>
public class MovementTests
{
    private static MapData Open(int size) => ScenarioMaps.FromAscii("open", Enumerable.Repeat(new string('.', size), size).ToArray());

    private static long Distance(Position p, int x, int y)
    {
        long dx = p.X - (long)x;
        long dy = p.Y - (long)y;
        return (long)Math.Sqrt((dx * dx) + (dy * dy));
    }

    [Fact]
    public void A_single_unit_walks_around_a_wall_to_its_goal()
    {
        MapData map = ScenarioMaps.FromAscii(
            "wall",
            "....................",
            "....................",
            "..........#.........",
            "..........#.........",
            "..........#.........",
            "..........#.........",
            "..........#.........",
            "..........#.........",
            "....................",
            "....................");
        using var d = new SimDriver(map);
        EntityId u = d.Spawn(SyntheticScenario.Swordsman, 0, 5, 5);
        d.MoveToTile(0, [u], 15, 5);
        d.RunUntil(() => !d.IsMoving(u), 2000);
        Position p = d.PositionOf(u);
        Assert.Equal((15, 5), (p.X / Tile, p.Y / Tile));
        Assert.Equal(1, d.Count(SimEventKind.Arrived));
    }

    [Fact]
    public void The_render_snapshot_shows_who_is_walking()
    {
        using var d = new SimDriver(Open(20));
        var exchange = new Snapshots.RenderSnapshotExchange();
        d.Sim.RenderOutput = exchange;
        EntityId walker = d.Spawn(SyntheticScenario.Swordsman, 0, 2, 2);
        EntityId idler = d.Spawn(SyntheticScenario.Swordsman, 0, 10, 15);
        d.MoveToTile(0, [walker], 15, 2);
        for (int t = 0; t < 5; t++)
        {
            d.Step();
        }

        Snapshots.RenderSnapshot snapshot = exchange.AcquireLatest()!;
        Assert.True(snapshot.Entities.ToArray().Single(e => e.Id == walker).IsMoving);
        Assert.False(snapshot.Entities.ToArray().Single(e => e.Id == idler).IsMoving);
        Assert.Equal(0, snapshot.Entities.ToArray().Single(e => e.Id == walker).Facing); // east
    }

    [Fact]
    [Trait("Rule", "ORD-008")]
    public void Stop_halts_a_moving_unit_and_drops_its_plan()
    {
        using var d = new SimDriver(Open(40));
        EntityId u = d.Spawn(SyntheticScenario.Swordsman, 0, 5, 5);
        d.MoveToTile(0, [u], 35, 35);
        for (int t = 0; t < 20; t++)
        {
            d.Step();
        }

        d.Order(0, CommandKind.Stop, [u]);
        d.Step();
        Position stopped = d.PositionOf(u);
        for (int t = 0; t < 20; t++)
        {
            d.Step();
        }

        Assert.Equal(MotionState.Idle, d.MotionOf(u).State);
        Assert.Equal(stopped, d.PositionOf(u));
        Assert.Equal(0, d.World.Paths.Count(u.Index));
    }

    [Fact]
    public void Patrol_goes_back_and_forth()
    {
        using var d = new SimDriver(Open(40));
        EntityId u = d.Spawn(SyntheticScenario.Knight, 0, 5, 20);
        d.Order(0, CommandKind.Patrol, [u], TileCenter(25), TileCenter(20));
        d.RunUntil(() => d.Count(SimEventKind.Arrived) >= 3, 3000);
        Assert.True(d.Count(SimEventKind.Arrived) >= 3);
        Assert.Equal(MotionState.Moving, d.MotionOf(u).State);
        Assert.Equal(MotionOrder.Patrol, d.MotionOf(u).Order);
    }

    [Fact]
    public void Follow_keeps_up_with_a_moving_target()
    {
        using var d = new SimDriver(Open(64));
        EntityId leader = d.Spawn(SyntheticScenario.Swordsman, 0, 10, 10);
        EntityId follower = d.Spawn(SyntheticScenario.Knight, 0, 4, 4);
        d.MoveToTile(0, [leader], 50, 50);
        d.Order(0, CommandKind.Follow, [follower], target: leader);
        // A guard keeps within 3 tiles of what it guards (ORD-001) and walks back from further away.
        int worst = 0;
        for (int t = 0; t < 900; t++)
        {
            d.Step();
            if (t > 200)
            {
                worst = Math.Max(worst, Tiles(d.PositionOf(follower), d.PositionOf(leader)));
            }
        }

        Assert.True(worst <= 5, $"follower fell {worst} tiles behind");
        Assert.True(Tiles(d.PositionOf(follower), d.PositionOf(leader)) <= 3);

        static int Tiles(Position a, Position b) => Math.Max(Math.Abs((a.X / Tile) - (b.X / Tile)), Math.Abs((a.Y / Tile) - (b.Y / Tile)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_mover_goes_around_an_enemy_dead_ahead_on_its_right_side(bool westward)
    {
        // Owner decision (ADR 0025 amendment): side-step right first (keep right), then left.
        using var d = new SimDriver(Open(32));
        EntityId mover = d.Spawn(SyntheticScenario.Swordsman, 0, westward ? 20 : 4, 10);
        d.Spawn(SyntheticScenario.Swordsman, 1, 12, 10);
        int startY = d.PositionOf(mover).Y;
        d.MoveToTile(0, [mover], westward ? 4 : 20, 10);
        int passingY = startY;
        d.RunUntil(() =>
        {
            Position p = d.PositionOf(mover);
            if (Math.Abs(p.X - ((12 * Tile) + (Tile / 2))) < Tile / 4)
            {
                passingY = p.Y;
            }

            return !d.IsMoving(mover);
        }, 800);
        Assert.InRange(d.PositionOf(mover).X / Tile, westward ? 3 : 19, westward ? 5 : 21);

        // East-bound, right is south (+Y, screen coordinates); west-bound, right is north.
        Assert.True(westward ? passingY < startY : passingY > startY, $"passed at y {passingY}, started at {startY}");
        Assert.Equal(0, d.Count(SimEventKind.Blocked));
    }

    [Fact]
    public void A_follower_pushed_out_of_range_of_an_idle_target_returns()
    {
        using var d = new SimDriver(Open(64));
        EntityId leader = d.Spawn(SyntheticScenario.Swordsman, 0, 20, 20);
        EntityId follower = d.Spawn(SyntheticScenario.Swordsman, 0, 24, 20);
        d.Order(0, CommandKind.Follow, [follower], target: leader);
        d.RunUntil(() => false, 100);
        Assert.False(d.IsMoving(follower));

        // Shove it 5 tiles away (as a crowd would) while the leader stands still.
        World w = d.World;
        w.IsLocked = false;
        ref Position p = ref w.Positions.Get(follower);
        ref OccupancyCell cell = ref w.OccupancyCells.Get(follower);
        w.Map.RemoveOccupant(cell.Cell);
        p = new Position(p.X + (5 * Tile), p.Y);
        cell.Cell = w.Map.CellIndexAt(p.X, p.Y);
        w.Map.AddOccupant(cell.Cell);
        w.IsLocked = true;

        d.RunUntil(() => false, 200);
        Position l = d.PositionOf(leader);
        Assert.True(Distance(d.PositionOf(follower), l.X, l.Y) < 4 * Tile, "the follower did not come back");
    }

    [Fact]
    public void A_building_placed_across_a_path_makes_the_unit_route_around_it()
    {
        // A corridor 3 tiles high; a 3×3 house (plus-shaped mask) placed later in its middle, with a detour above.
        string[] rows =
        [
            "..............................",
            "..............................",
            "..............................",
            "##########...........#########",
            "..............................",
            "..............................",
            "..............................",
            "##############################",
        ];
        using var d = new SimDriver(ScenarioMaps.FromAscii("corridor", rows));
        EntityId u = d.Spawn(SyntheticScenario.Swordsman, 0, 1, 5);
        d.MoveToTile(0, [u], 28, 5);
        d.Step();
        d.Step();

        // Block the corridor with a footprint: place through a debug-free path, as a building system would.
        d.World.IsLocked = false;
        d.World.PlaceStructure(SyntheticScenario.House, 1, 14, 4);
        d.World.PlaceStructure(SyntheticScenario.Tree, 1, 14, 4);
        d.World.PlaceStructure(SyntheticScenario.Tree, 1, 16, 4);
        d.World.PlaceStructure(SyntheticScenario.Tree, 1, 14, 6);
        d.World.PlaceStructure(SyntheticScenario.Tree, 1, 16, 6);
        d.World.IsLocked = true;
        int version = d.World.Map.ObstacleVersion;
        Assert.True(version > 0);
        d.RunUntil(() => !d.IsMoving(u), 3000);
        Position p = d.PositionOf(u);
        Assert.Equal((28, 5), (p.X / Tile, p.Y / Tile));
        Assert.Equal(0, d.Count(SimEventKind.Blocked));
    }

    [Fact]
    [Trait("Rule", "BLD-015")]
    public void A_unit_inside_a_new_building_is_pushed_out()
    {
        using var d = new SimDriver(Open(20));
        EntityId u = d.Spawn(SyntheticScenario.Swordsman, 0, 10, 10);
        d.Step();
        d.World.IsLocked = false;
        d.World.PlaceStructure(SyntheticScenario.House, 1, 9, 9);
        d.World.IsLocked = true;
        for (int t = 0; t < 60; t++)
        {
            d.Step();
        }

        Position p = d.PositionOf(u);
        Assert.True(d.World.Navigation.ForType(SyntheticScenario.Swordsman)!.IsWalkablePoint(p.X, p.Y));
    }

    [Fact]
    [Trait("Rule", "WORLD-001")]
    public void Air_units_fly_straight_over_cliffs_and_ships_sail_around_land()
    {
        string[] rows = new string[24];
        for (int y = 0; y < 24; y++)
        {
            char[] row = new char[24];
            for (int x = 0; x < 24; x++)
            {
                row[x] = x == 12 && y < 20 ? '#' : y >= 18 ? '~' : '~';
            }

            rows[y] = new string(row);
        }

        // Water everywhere except a cliff spit at x = 12 from the top; ships must go around its end.
        using var d = new SimDriver(ScenarioMaps.FromAscii("sea", rows));
        EntityId dragon = d.Spawn(SyntheticScenario.Dragon, 0, 3, 3);
        EntityId boat = d.Spawn(SyntheticScenario.Boat, 0, 4, 6);
        d.MoveToTile(0, [dragon], 20, 3);
        d.MoveToTile(0, [boat], 20, 6);
        d.RunUntil(() => !d.IsMoving(dragon) && !d.IsMoving(boat), 4000);
        Assert.Equal((20, 3), (d.PositionOf(dragon).X / Tile, d.PositionOf(dragon).Y / Tile));
        Assert.Equal((20, 6), (d.PositionOf(boat).X / Tile, d.PositionOf(boat).Y / Tile));
        Assert.Equal(0, d.Count(SimEventKind.Blocked));
    }

    [Fact]
    public void The_path_budget_limits_the_requests_accepted_per_tick()
    {
        using var d = new SimDriver(Open(64), pathBudget: 1);
        var units = new List<EntityId>();
        for (int i = 0; i < 10; i++)
        {
            units.Add(d.Spawn(SyntheticScenario.Swordsman, 0, 5 + i, 5));
        }

        foreach (EntityId u in units)
        {
            d.MoveToTile(0, [u], 60, 60);
        }

        d.Step();
        int planned = units.Count(u => d.MotionOf(u).Mode != MotionMode.Waiting);
        Assert.Equal(1, planned); // at least one per tick, whatever its cost
        for (int t = 0; t < 9; t++)
        {
            d.Step();
        }

        Assert.All(units, u => Assert.NotEqual(MotionMode.Waiting, d.MotionOf(u).Mode));
    }

    [Fact]
    public void Corpses_expire_after_their_ttl_and_the_cap_drops_the_oldest()
    {
        var ruleset = new Ruleset("decals", TestWorlds.Ruleset.TerrainTypes, TestWorlds.Ruleset.EntityTypes, corpseTtlMilliseconds: 1000, decalCap: 5);
        using var d = new SimDriver(Open(16), ruleset: ruleset);
        var units = new List<EntityId>();
        for (int i = 0; i < 8; i++)
        {
            units.Add(d.Spawn(SyntheticScenario.Swordsman, 1, 2 + i, 2));
        }

        d.Order(1, CommandKind.DebugKill, units.ToArray());
        d.Step();
        Assert.Equal(5, d.World.Decals.Count);
        Assert.Equal(TileCenter(5), d.World.Decals[0].X); // the three oldest were dropped
        for (int t = 0; t < 19; t++)
        {
            d.Step();
        }

        Assert.Equal(5, d.World.Decals.Count);
        d.Step();
        Assert.Equal(0, d.World.Decals.Count); // 1,000 ms = 20 ticks
        Assert.Equal(0, d.World.Map.ObstacleVersion);
    }

    [Fact]
    public void A_group_split_by_a_wall_plans_each_side_once()
    {
        string[] rows = new string[20];
        for (int y = 0; y < 20; y++)
        {
            rows[y] = new string('.', 10) + '#' + new string('.', 19);
        }

        using var d = new SimDriver(ScenarioMaps.FromAscii("split", rows));
        var units = new List<EntityId>();
        for (int i = 0; i < 12; i++)
        {
            // Interleaved in entity order: even on the west side, odd on the east side.
            units.Add(i % 2 == 0 ? d.Spawn(SyntheticScenario.Swordsman, 0, 2 + (i % 3), 3 + i) : d.Spawn(SyntheticScenario.Swordsman, 0, 20 + (i % 3), 3 + i));
        }

        d.MoveToTile(0, units.ToArray(), 25, 10);
        d.Step();
        d.Step();
        Assert.All(units, u => Assert.NotEqual(MotionMode.Waiting, d.MotionOf(u).Mode));
        Assert.Equal(12, units.Select(u => (d.MotionOf(u).TargetX, d.MotionOf(u).TargetY)).Distinct().Count());
        Assert.Equal(6, d.Count(SimEventKind.GoalUnreachable)); // the west side goes to the wall instead
        d.RunUntil(() => !d.AnyMoving(units), 3000);
        Assert.All(units.Where((_, i) => i % 2 == 1), u => Assert.True(d.PositionOf(u).X > 11 * Tile));
        Assert.All(units.Where((_, i) => i % 2 == 0), u => Assert.InRange(d.PositionOf(u).X / Tile, 5, 9));
    }

    [Fact]
    public void Group_moves_assign_distinct_slots_and_share_one_flow_field()
    {
        // A wall between the group and its goal, so the shared field is actually descended.
        string[] rows = new string[64];
        for (int y = 0; y < 64; y++)
        {
            rows[y] = string.Concat(Enumerable.Range(0, 64).Select(x => x == 30 && y < 56 ? '#' : '.'));
        }

        using var d = new SimDriver(ScenarioMaps.FromAscii("wall", rows));
        var units = new List<EntityId>();
        for (int i = 0; i < 30; i++)
        {
            units.Add(d.Spawn(SyntheticScenario.Swordsman, 0, 5 + (i % 6), 5 + (i / 6)));
        }

        d.MoveToTile(0, units.ToArray(), 50, 10);
        d.Step();
        Assert.Equal(1, d.World.FlowFields.Builds);
        Assert.All(units, u => Assert.Equal(MotionMode.Flow, d.MotionOf(u).Mode));
        Assert.Single(units.Select(u => d.MotionOf(u).GoalTile).Distinct());
        Assert.Equal(30, units.Select(u => (d.MotionOf(u).TargetX, d.MotionOf(u).TargetY)).Distinct().Count());
        Assert.Single(units.Select(u => d.MotionOf(u).GroupId).Distinct());
        d.RunUntil(() => !d.AnyMoving(units), 3000);
        Assert.Equal(1, d.World.FlowFields.Builds);
        Assert.All(units, u => Assert.True(d.PositionOf(u).X > 31 * Tile));
        Assert.Equal(0, d.Count(SimEventKind.Blocked));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Rule", "WORLD-016")]
    public void An_idle_unit_of_a_mutual_ally_is_shoved_aside_like_a_friend_an_enemy_is_not(bool allied)
    {
        // ADR 0220: allies are friends for movement (heroes walking through an allied village); enemies stay hard
        // obstacles (ADR 0025).
        using var d = new SimDriver(Open(24));
        if (allied)
        {
            d.World.Diplomacy.Set(0, 1, Kingdoms.Stance.Ally, Kingdoms.Sharing.None, 0);
            d.World.Diplomacy.Set(1, 0, Kingdoms.Stance.Ally, Kingdoms.Sharing.None, 0);
        }

        EntityId mover = d.Spawn(SyntheticScenario.Swordsman, 0, 4, 10);
        EntityId stander = d.Spawn(SyntheticScenario.Swordsman, 1, 12, 10);
        Position before = d.PositionOf(stander);
        d.MoveToTile(0, [mover], 12, 10);
        d.RunUntil(() => !d.IsMoving(mover), 800);
        long shoved = Distance(d.PositionOf(stander), before.X, before.Y);
        if (allied)
        {
            Assert.True(shoved > 0, "the idle ally steps aside");
        }
        else
        {
            Assert.Equal(0, shoved);
        }
    }

    [Fact]
    [Trait("Rule", "WORLD-017")]
    public void A_unit_meeting_a_blocked_corner_almost_head_on_slides_past_it()
    {
        // ADR 0220: the step along the free axis takes the step's full length, not only its tiny sideways part.
        MapData map = ScenarioMaps.FromAscii(
            "corner",
            "..........",
            "..........",
            "..........",
            "....#.....",
            "....#.....",
            "....#.....",
            "..........",
            "..........");
        using var d = new SimDriver(map);
        EntityId u = d.Spawn(SyntheticScenario.Swordsman, 0, 5, 1);
        d.MoveToTile(0, [u], 5, 7);
        d.RunUntil(() => !d.IsMoving(u), 600);
        Position p = d.PositionOf(u);
        Assert.Equal((5, 7), (p.X / Tile, p.Y / Tile));
    }
}
