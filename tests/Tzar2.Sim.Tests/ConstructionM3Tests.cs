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
    public void A_gate_takes_the_place_of_any_walls_under_it_and_needs_the_way_before_its_door_clear()
    {
        // ReTzar's reading of the original (0x456b50, 0x455e20): walls never stop a gate's site, whoever's they are, and
        // the gate replaces those under it; the tiles before its door, where the drawbridge comes down, hold no building
        // or tree but walls.
        MapObject[] walls =
        [
            .. Enumerable.Range(10, 6).Select(x => Structure("comm.wall_wood", 0, x, 6)),
            Structure("comm.wall_wood", 0, 14, 7), // a wall across the way before the door stays
            .. Enumerable.Range(20, 4).Select(x => Structure("comm.wall_wood", 1, x, 6)),
        ];
        using var d = new SimDriver(TestMap(40, 16, [Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 12, 9), Tree(27, 12), .. walls]), ruleset: Classic);
        d.World.Players[0].Resources[Resource("wood")] = 5000;
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Build, [peon], 11 * Tile, 6 * Tile, arg0: Id("comm.gate_wood")); // 7 x 1 on tiles 11..17
        d.Step();
        Assert.Empty(d.Rejections());
        Assert.Equal(2, OfType(d.World, "comm.wall_wood", 0).Count()); // 10, and 14 before the door
        Assert.DoesNotContain(OfType(d.World, "comm.wall_wood", 0), w => d.World.Footprints.Get(w).TileY == 6 && d.World.Footprints.Get(w).TileX >= 11);

        // Another kingdom's wall is no obstacle either.
        d.Order(0, CommandKind.Build, [peon], 20 * Tile, 6 * Tile, arg0: Id("comm.gate_wood"));
        d.Step();
        Assert.Empty(d.Rejections());
        Assert.Empty(OfType(d.World, "comm.wall_wood", 1));

        // Nor a wall still being built.
        d.Order(0, CommandKind.BuildWall, [peon], 30 * Tile, 2 * Tile, arg0: Id("comm.wall_wood"), arg1: (2 << 16) | 33);
        d.Step();
        Assert.Equal(4, OfType(d.World, "comm.wall_wood", 0).Count(w => d.World.Constructions.Has(w)));
        d.Order(0, CommandKind.Build, [peon], 29 * Tile, 2 * Tile, arg0: Id("comm.gate_wood"));
        d.Step();
        Assert.Empty(d.Rejections());
        Assert.DoesNotContain(OfType(d.World, "comm.wall_wood", 0), w => d.World.Footprints.Get(w).TileY == 2);

        // A tree two tiles before the door stops it (door 26..28 at row 10, its way down rows 11 and 12); facing the other
        // way (Build's argument 1 bit 1), the way is the one tile above, and it fits.
        d.Order(0, CommandKind.Build, [peon], 24 * Tile, 10 * Tile, arg0: Id("comm.gate_wood"));
        d.Step();
        Assert.Equal(CommandRejection.IllegalPlacement, d.Rejections().Last());
        Assert.False(Systems.PlacementQueries.CanPlaceFor(d.World, Id("comm.gate_wood"), 24, 10));
        Assert.True(Systems.PlacementQueries.CanPlaceFor(d.World, Id("comm.gate_wood"), 24, 10, mirrored: true));
        int before = d.Rejections().Count;
        d.Order(0, CommandKind.Build, [peon], 24 * Tile, 10 * Tile, arg0: Id("comm.gate_wood"), arg1: 2);
        d.Step();
        Assert.Equal([], d.Rejections().Skip(before));
        Assert.Equal(4, OfType(d.World, "comm.gate_wood").Count());
    }

    [Fact]
    [Trait("Rule", "BLD-028")]
    public void A_gate_destroyed_closed_leaves_its_rubble_and_lets_everyone_through()
    {
        // A wooden wall across the map, row 6, with a gate in it (tiles 8..14, the door 10..12), closed by hand.
        MapObject[] walls = [.. Enumerable.Range(0, 24).Where(x => x is < 8 or > 14).Select(x => Structure("comm.wall_wood", 0, x, 6))];
        using var d = new SimDriver(TestMap(24, 14, [Structure("euro.castle", 0, 1, 1), Structure("comm.gate_wood", 0, 8, 6), Unit("euro.footman", 1, 11, 10), .. walls]), ruleset: Classic);
        EntityId gate = OfType(d.World, "comm.gate_wood").Single();
        d.Order(0, CommandKind.SetGate, [gate], arg0: 3); // its automatism off
        d.RunUntil(() => false, 30);
        d.Order(0, CommandKind.SetGate, [gate], arg0: 0); // closed
        d.RunUntil(() => false, 30);
        Assert.False(d.World.Map.IsPassable(Locomotor.Foot, 11, 6));

        EntityId footman = OfType(d.World, "euro.footman").Single();
        d.MoveToTile(1, [footman], 11, 2);
        d.RunUntil(() => false, 200);
        Assert.True(d.PositionOf(footman).Y / Tile > 6); // the gate holds it

        d.Order(0, CommandKind.DebugKill, [gate]);
        d.Step();
        Assert.True(d.World.Map.IsPassable(Locomotor.Foot, 11, 6));
        Assert.Equal(1, d.World.Decals.LastingCount);
        Decal rubble = d.World.Decals.Lasting(0);
        Assert.Equal((DecalKind.Rubble, Id("comm.gate_wood"), 0), (rubble.Kind, rubble.TypeId, (int)rubble.Look));
        d.MoveToTile(1, [footman], 11, 2);
        d.RunUntil(() => d.PositionOf(footman).Y / Tile <= 3, 1500);
        Assert.True(d.PositionOf(footman).Y / Tile <= 3); // through the rubble

        // A wall leaves its rubble too, and it stays (a snapshot keeps it).
        EntityId wall = OfType(d.World, "comm.wall_wood").First(w => d.World.Footprints.Get(w).TileX == 3);
        d.Order(0, CommandKind.DebugKill, [wall]);
        d.Step();
        Assert.Equal(2, d.World.Decals.LastingCount);
        d.RunUntil(() => false, 2000);
        Assert.Equal(2, d.World.Decals.LastingCount);
        Simulation restored = Simulation.Restore(Snapshots.WorldSnapshot.Capture(d.World), Classic, d.World.Map.Data);
        Assert.Equal(d.Sim.ComputeFullHash(), restored.ComputeFullHash());
        Assert.Equal(2, restored.World.Decals.LastingCount);

        // A wall built again there and destroyed again leaves one piece, not two.
        var index = new DecalIndex(4);
        index.AddLasting(new Decal { X = 100, Y = 100, Kind = DecalKind.Rubble, ExpiresTick = int.MaxValue, TypeId = 1 });
        index.AddLasting(new Decal { X = 100, Y = 100, Kind = DecalKind.Rubble, ExpiresTick = int.MaxValue, TypeId = 2 });
        Assert.Equal((1, (ushort)2), (index.LastingCount, index.Lasting(0).TypeId));
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

        // A slanted drag lays an L (the side along x, then down: 7 tiles), and a line nobody can pay.
        d.Order(0, CommandKind.BuildWall, [peon], 22 * Tile, 2 * Tile, arg0: Id("comm.wall_wood"), arg1: (5 << 16) | 25);
        d.Step();
        Assert.Equal(7 + 7, OfType(d.World, "comm.wall_wood").Count());
        d.Order(0, CommandKind.BuildWall, [peon], 2 * Tile, 12 * Tile, arg0: Id("euro.house"), arg1: (12 << 16) | 8);
        d.Step();
        Assert.Equal(CommandRejection.InvalidArgument, d.Rejections().Last()); // only walls are dragged
    }

    [Fact]
    [Trait("Rule", "ORD-013")]
    public void Gates_can_be_turned_stand_open_and_close_for_a_stranger_near_the_door()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 12, 10), Unit("euro.footman", 1, 28, 6)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        EntityId stranger = OfType(d.World, "euro.footman").Single();
        d.Order(0, CommandKind.Build, [peon], 16 * Tile, 5 * Tile, arg0: Id("comm.gate_wood"), arg1: 1); // 1 x 6 when turned (gw2_anim)
        d.Step();
        EntityId gate = OfType(d.World, "comm.gate_wood").Single();
        Assert.Equal((16, 5, 1, 6), d.World.FootprintRect(gate));
        d.RunUntil(() => d.Count(SimEventKind.ConstructionCompleted) == 1, 2000);
        Assert.True(d.World.Gates.Has(gate));
        Assert.Equal(GateMode.Automatic, d.World.Gates.Get(gate).Mode);

        // Nobody of another kingdom near: it stands open (its door, the second to fourth tiles down), the rest stays blocked.
        d.Step();
        Assert.Equal(1, d.World.Gates.Get(gate).Open);
        Assert.True(d.World.Map.IsPassable(Locomotor.Foot, 16, 6));
        Assert.True(d.World.Map.IsPassable(Locomotor.Foot, 16, 8));
        Assert.False(d.World.Map.IsPassable(Locomotor.Foot, 16, 5));
        Assert.False(d.World.Map.IsPassable(Locomotor.Foot, 16, 9));

        // The peasant walks away: it stays open. A stranger comes within four tiles of the door: it closes, and opens
        // again once the stranger has gone (ReTzar 0x4571a0).
        d.MoveToTile(0, [peon], 4, 12);
        d.RunUntil(() => false, 100);
        Assert.Equal(1, d.World.Gates.Get(gate).Open);
        d.MoveToTile(1, [stranger], 20, 7);
        d.RunUntil(() => d.World.Gates.Get(gate).Open == 0, 300);
        Assert.False(d.World.Map.IsPassable(Locomotor.Foot, 16, 6));
        d.MoveToTile(1, [stranger], 29, 13);
        d.RunUntil(() => d.World.Gates.Get(gate).Open == 1, 400);
        Assert.Equal(1, d.World.Gates.Get(gate).Open);

        // Close gate: it closes and stays closed while the order holds the automatism away (10 s), then stands open again.
        d.RunUntil(() => false, 40);
        d.Order(0, CommandKind.SetGate, [gate], arg0: 0);
        d.Step();
        Assert.Equal((0, GateMode.Automatic), (d.World.Gates.Get(gate).Open, d.World.Gates.Get(gate).Mode));
        d.Order(0, CommandKind.SetGate, [gate], arg0: 1); // within 1 s of the last change: rejected (owner decision)
        d.Step();
        Assert.Equal(CommandRejection.GateBusy, d.Rejections().Last());
        int hold = Tzar2.Sim.Systems.GameplayCommands.GateHoldTicks(d.World);
        d.RunUntil(() => false, hold - 10);
        Assert.Equal(0, d.World.Gates.Get(gate).Open);
        d.RunUntil(() => d.World.Gates.Get(gate).Open == 1, 40);
        Assert.Equal(1, d.World.Gates.Get(gate).Open);

        // Gate AI off: the gate keeps its state, a stranger at the door changes nothing; orders open and close it.
        d.Order(0, CommandKind.SetGate, [gate], arg0: 3);
        d.Step();
        Assert.Equal((1, GateMode.Manual), (d.World.Gates.Get(gate).Open, d.World.Gates.Get(gate).Mode));
        d.MoveToTile(1, [stranger], 20, 7);
        d.RunUntil(() => false, 300);
        Assert.Equal(1, d.World.Gates.Get(gate).Open);
        d.Order(0, CommandKind.SetGate, [gate], arg0: 0);
        d.Step();
        Assert.Equal(0, d.World.Gates.Get(gate).Open);
        d.RunUntil(() => false, 300);
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
