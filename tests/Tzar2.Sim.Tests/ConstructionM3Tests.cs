using System;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// GAME-03 (ADR 0043) on synthetic maps: repair of buildings and ships, fire, ruins, walls dragged as lines, gate
/// orientation and opening, bridges over water, and Architecture's build rate.
/// </summary>
public class ConstructionM3Tests
{
    [Fact]
    [Trait("Rule", "BLD-007")]
    public void Peasants_repair_a_damaged_building_back_to_full_health()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Structure("euro.house", 0, 12, 4), Unit("euro.peon", 0, 11, 7)), ruleset: Classic);
        EntityId house = OfType(d.World, "euro.house").Single();
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.World.Healths.Get(house).Current = 315; // 105 of 420 missing: still above the fire threshold
        d.Order(0, CommandKind.Repair, [peon], target: house);
        int ticks = d.RunUntil(() => d.Count(SimEventKind.Repaired) == 1, 2000);
        Assert.Equal(420, d.World.Healths.Get(house).Current);
        // Blows of 420 / (4 x TIME 16) = 6 hit points every 1.25 s (BLD-022): 18 blows, 22.5 s, plus the walk.
        Assert.InRange(ticks, 450, 490);
        d.Step();
        Assert.Equal(WorkerTask.None, d.World.Workers.Get(peon).Task);

        // Whole buildings, enemy buildings and sites are not repair targets.
        d.Order(0, CommandKind.Repair, [peon], target: house);
        d.Step();
        Assert.Equal(CommandRejection.InvalidArgument, d.Rejections().Last());
    }

    [Fact]
    [Trait("Rule", "BLD-007")]
    public void Peasants_repair_ships()
    {
        using var d = new SimDriver(
            AsciiMap(["..........~~~~~~", "..........~~~~~~", "..........~~~~~~", "..........~~~~~~", "..........~~~~~~", "..........~~~~~~"], Structure("euro.castle", 0, 0, 0), Unit("comm.galleon", 0, 11, 3), Unit("euro.peon", 0, 8, 3)),
            ruleset: Classic);
        EntityId ship = OfType(d.World, "comm.galleon").Single();
        d.World.Healths.Get(ship).Current = 1000;
        d.Order(0, CommandKind.Repair, [OfType(d.World, "euro.peon").Single()], target: ship);
        d.RunUntil(() => d.Count(SimEventKind.Repaired) == 1, 3000);
        Assert.Equal(1150, d.World.Healths.Get(ship).Current);
    }

    [Fact]
    [Trait("Rule", "BLD-006")]
    public void Badly_damaged_buildings_burn_down_unless_repaired_and_leave_a_ruin()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Structure("euro.house", 0, 12, 4), Structure("euro.house", 0, 20, 4), Unit("euro.peon", 0, 11, 7)), ruleset: Classic);
        EntityId[] houses = [.. OfType(d.World, "euro.house")];
        d.World.Healths.Get(houses[0]).Current = 100; // below 50% (UNVERIFIED): burns at 60 HP per minute (UNVERIFIED)
        d.World.Healths.Get(houses[1]).Current = 30;
        d.Step();
        Assert.True(d.World.Burnings.Has(houses[0]));
        Assert.Equal(2, d.Count(SimEventKind.FireStarted));
        d.Order(0, CommandKind.Repair, [OfType(d.World, "euro.peon").Single()], target: houses[0]);
        d.RunUntil(() => !d.World.Entities.IsAlive(houses[1]), 1000);
        Assert.InRange(d.World.Tick.Value, 590, 610); // 30 HP at 1 HP per second
        Assert.Contains(d.World.Decals[0].Kind, new[] { DecalKind.Ruin });
        Assert.True(d.World.Map.IsPassable(Locomotor.Foot, 20, 4)); // a ruin never blocks (ORIG-01)
        d.RunUntil(() => d.World.Healths.Get(houses[0]).Current > 210, 2000);
        d.Step();
        Assert.False(d.World.Burnings.Has(houses[0])); // repaired above the threshold: the fire is out
    }

    [Fact]
    [Trait("Rule", "BLD-018")]
    public void A_gate_placed_over_the_kingdoms_own_wall_replaces_its_pieces_but_not_another_kingdoms()
    {
        MapObject[] walls =
        [
            .. Enumerable.Range(10, 6).Select(x => Structure("comm.wall_wood", 0, x, 6)),
            .. Enumerable.Range(20, 4).Select(x => Structure("comm.wall_wood", 1, x, 6)),
        ];
        using var d = new SimDriver(TestMap(40, 16, [Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 12, 8), .. walls]), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Build, [peon], 11 * Tile, 6 * Tile, arg0: Id("comm.gate_wood")); // 7 x 1 on tiles 11..17
        d.Step();
        Assert.Empty(d.Rejections());
        Assert.Single(OfType(d.World, "comm.gate_wood"));
        Assert.Single(OfType(d.World, "comm.wall_wood", 0)); // 10 is left
        Assert.DoesNotContain(OfType(d.World, "comm.wall_wood", 0), w => d.World.Footprints.Get(w).TileX is >= 11 and <= 17);

        d.Order(0, CommandKind.Build, [peon], 20 * Tile, 6 * Tile, arg0: Id("comm.gate_wood"));
        d.Step();
        Assert.Equal(CommandRejection.IllegalPlacement, d.Rejections().Last());
        Assert.Equal(4, OfType(d.World, "comm.wall_wood", 1).Count());
    }

    [Fact]
    [Trait("Rule", "WORLD-018")]
    public void A_ninja_climbs_over_a_wall_that_stops_everyone_else()
    {
        // A wooden wall across the map, column 10; a climber (the ninja's locomotor) and a footman west of it.
        Ruleset rules = ClassicWith("euro.archer: { Mobile: { locomotor: climber } }");
        using var d = new SimDriver(TestMap(20, 8, Structure("euro.castle", 0, 1, 1), Unit("euro.archer", 0, 6, 4), Unit("euro.footman", 0, 6, 5)), ruleset: rules);
        EntityId peon = d.Spawn(Id(rules, "euro.peon"), 0, 8, 1);
        d.World.Players[0].Resources[Resource("wood")] = 5000;
        d.Order(0, CommandKind.BuildWall, [peon], 10 * Tile, 0, arg0: Id(rules, "comm.wall_wood"), arg1: (7 << 16) | 10);
        d.Step();
        d.RunUntil(() => d.Count(SimEventKind.ConstructionCompleted) == 8, 12000);
        Assert.Equal(8, d.Count(SimEventKind.ConstructionCompleted));

        EntityId ninja = OfType(d.World, "euro.archer").Single(), footman = OfType(d.World, "euro.footman").Single();
        d.MoveToTile(0, [ninja], 14, 4);
        d.MoveToTile(0, [footman], 14, 5);
        d.RunUntil(() => d.PositionOf(ninja).X / Tile >= 13, 3000);
        Assert.InRange(d.PositionOf(ninja).X / Tile, 13, 15);
        Assert.True(d.PositionOf(footman).X / Tile < 10);
    }

    [Fact]
    [Trait("Rule", "BLD-011")]
    public void A_dragged_wall_places_one_site_per_tile_and_the_builder_moves_along_it()
    {
        using var d = new SimDriver(TestMap(40, 16, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 12, 8)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.World.Map.AddBlocker(15, 6); // something in the way: that tile is skipped
        d.Order(0, CommandKind.BuildWall, [peon], 12 * Tile, 6 * Tile, arg0: Id("comm.wall_wood"), arg1: (6 << 16) | 19);
        d.Step();
        EntityId[] segments = [.. OfType(d.World, "comm.wall_wood")];
        Assert.Equal(7, segments.Length); // tiles 12..19 without 15
        Assert.Equal(500 - (7 * 23), d.World.Players[0].Resources[Resource("wood")]);
        d.RunUntil(() => d.Count(SimEventKind.ConstructionCompleted) == 7, 6000);
        Assert.All(segments, s => Assert.False(d.World.Constructions.Has(s)));
        Assert.Empty(d.Rejections());

        // A diagonal line, and a line nobody can pay.
        d.Order(0, CommandKind.BuildWall, [peon], 22 * Tile, 2 * Tile, arg0: Id("comm.wall_wood"), arg1: (5 << 16) | 25);
        d.Step();
        Assert.Equal(7 + 4, OfType(d.World, "comm.wall_wood").Count());
        d.Order(0, CommandKind.BuildWall, [peon], 2 * Tile, 12 * Tile, arg0: Id("euro.house"), arg1: (12 << 16) | 8);
        d.Step();
        Assert.Equal(CommandRejection.InvalidArgument, d.Rejections().Last()); // only walls are dragged
    }

    [Fact]
    public void Gates_can_be_turned_and_open_for_their_owner_but_not_for_enemies()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 12, 10), Unit("euro.footman", 1, 26, 6)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Build, [peon], 16 * Tile, 5 * Tile, arg0: Id("comm.gate_wood"), arg1: 1); // 1 x 6 when turned (gw2_anim)
        d.Step();
        EntityId gate = OfType(d.World, "comm.gate_wood").Single();
        Assert.Equal((16, 5, 1, 6), d.World.FootprintRect(gate));
        d.RunUntil(() => d.Count(SimEventKind.ConstructionCompleted) == 1, 2000);
        Assert.True(d.World.Gates.Has(gate));
        Assert.Equal(GateMode.Automatic, d.World.Gates.Get(gate).Mode);

        // The peasant is next to the gate: it opens (its door, the second to fourth tiles down), the rest stays blocked.
        d.Step();
        Assert.Equal(1, d.World.Gates.Get(gate).Open);
        Assert.True(d.World.Map.IsPassable(Locomotor.Foot, 16, 6));
        Assert.True(d.World.Map.IsPassable(Locomotor.Foot, 16, 8));
        Assert.False(d.World.Map.IsPassable(Locomotor.Foot, 16, 5));
        Assert.False(d.World.Map.IsPassable(Locomotor.Foot, 16, 9));

        // The peasant walks away: it closes; an enemy coming close does not open it.
        d.MoveToTile(0, [peon], 4, 12);
        d.MoveToTile(1, [OfType(d.World, "euro.footman").Single()], 18, 6);
        d.RunUntil(() => false, 300);
        Assert.Equal(0, d.World.Gates.Get(gate).Open);
        Assert.False(d.World.Map.IsPassable(Locomotor.Foot, 16, 6));

        // Manual control.
        d.Order(0, CommandKind.SetGate, [gate], arg0: 1);
        d.Step();
        Assert.Equal((1, GateMode.Manual), (d.World.Gates.Get(gate).Open, d.World.Gates.Get(gate).Mode));
        d.Order(0, CommandKind.SetGate, [gate], arg0: 0); // within 1 s of the last change: rejected (owner decision)
        d.Step();
        Assert.Equal(CommandRejection.GateBusy, d.Rejections().Last());
        Assert.Equal(1, d.World.Gates.Get(gate).Open);
        d.RunUntil(() => false, 20);
        d.Order(0, CommandKind.SetGate, [gate], arg0: 0);
        d.Step();
        Assert.Equal(0, d.World.Gates.Get(gate).Open);
    }

    [Fact]
    [Trait("Rule", "TECH-004")]
    [Trait("Rule", "WORLD-006")]
    public void A_bridge_needs_architecture_and_carries_land_units_over_water()
    {
        // Columns 6-8 are a river (6 and 8 are shore tiles that carry neither land units nor ships on test maps).
        string[] river =
        [
            "................",
            "................",
            "......~~~.......",
            "......~~~.......",
            "......~~~.......",
            "......~~~.......",
            "......~~~.......",
            "......~~~.......",
            "......~~~.......",
            "................",
            "................",
        ];
        using var d = new SimDriver(AsciiMap(river, Unit("euro.peon", 0, 3, 4)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        Assert.True(d.World.Map.IsPassable(Locomotor.Ship, 7, 4));
        d.Order(0, CommandKind.Build, [peon], 5 * Tile, 4 * Tile, arg0: Id("comm.bridge"));
        d.Step();
        Assert.Equal(CommandRejection.RequirementNotMet, d.Rejections().Last());
        ResearchTests.Grant(d, 0, Classic, "te_architecture");

        // 5 x 2 (synthetic): both ends on walkable land; from x = 4 the far end would be a shore tile.
        d.Order(0, CommandKind.Build, [peon], 4 * Tile, 4 * Tile, arg0: Id("comm.bridge"));
        d.Step();
        Assert.Equal(CommandRejection.IllegalPlacement, d.Rejections().Last());
        d.Order(0, CommandKind.Build, [peon], 5 * Tile, 4 * Tile, arg0: Id("comm.bridge"));
        d.Step();
        EntityId bridge = OfType(d.World, "comm.bridge").Single();
        Assert.False(d.World.Map.IsPassable(Locomotor.Foot, 7, 4)); // a site carries nobody yet
        Assert.True(d.World.Map.IsPassable(Locomotor.Ship, 7, 4));
        d.RunUntil(() => d.Count(SimEventKind.ConstructionCompleted) == 1, 3000);
        Assert.True(d.World.Map.IsPassable(Locomotor.Foot, 7, 4));
        Assert.True(d.World.Map.IsPassable(Locomotor.Mounted, 6, 5));
        Assert.False(d.World.Map.IsPassable(Locomotor.Ship, 7, 4));
        Assert.True(d.World.Map.IsPassable(Locomotor.Ship, 7, 6)); // beside the bridge, still water

        // The peasant crosses on the bridge, and destroying it takes the deck away.
        d.MoveToTile(0, [peon], 13, 4);
        d.RunUntil(() => !d.IsMoving(peon), 2000);
        Assert.True(d.PositionOf(peon).X > 10 * Tile, $"peasant at {d.PositionOf(peon).X / Tile}");
        d.Order(0, CommandKind.Delete, [bridge]);
        d.Step();
        Assert.False(d.World.Map.IsPassable(Locomotor.Foot, 7, 4));
        Assert.Equal(0, d.World.Map.Bridges[(4 * d.World.Map.Width) + 7]);
    }

    [Fact]
    [Trait("Rule", "TECH-004")]
    public void Architecture_speeds_up_construction()
    {
        Ruleset rules = ClassicWith("te_architecture: { Technology: { effects: [{ stat: buildRate, percent: 100, category: building }] } }");
        int Build(bool researched)
        {
            using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 12, 8)), ruleset: rules);
            if (researched)
            {
                ResearchTests.Grant(d, 0, rules, "te_architecture");
            }

            d.Order(0, CommandKind.Build, [OfType(d.World, "euro.peon").Single()], 13 * Tile, 8 * Tile, arg0: Id(rules, "euro.house"));
            return d.RunUntil(() => d.Count(SimEventKind.ConstructionCompleted) == 1, 2000);
        }

        int normal = Build(false);
        int fast = Build(true);
        Assert.InRange(normal - fast, 190, 210); // TIME 16: 20 s → 10 s with Architecture
    }
}
