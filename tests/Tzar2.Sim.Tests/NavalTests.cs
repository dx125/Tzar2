using System;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Xunit;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// Ships (REQ-PATH-09, GAME-02/03/07/09; ADRs 0140-0143): docking to board, unload and be repaired, fishing, harbor
/// production, naval combat and exploring.
/// </summary>
public class NavalTests
{
    // Land (columns 0-11), a sea (12-27), land again (28-39).
    internal static readonly string[] Bay = [.. Enumerable.Repeat("............~~~~~~~~~~~~~~~~............", 16)];

    private static bool OnLand(SimDriver d, EntityId e) => d.World.Map.IsPassableAt(Locomotor.Foot, d.PositionOf(e).X, d.PositionOf(e).Y);

    [Fact]
    public void A_ship_at_sea_sails_to_the_shore_to_take_boarders()
    {
        using var d = new SimDriver(AsciiMap(Bay, [Unit("comm.galleon", 0, 20, 12), .. Enumerable.Range(0, 3).Select(i => Unit("euro.footman", 0, 4, 2 + i))]), ruleset: Classic);
        EntityId ship = OfType(d.World, "comm.galleon").Single();
        EntityId[] footmen = [.. OfType(d.World, "euro.footman")];
        d.Order(0, CommandKind.Board, footmen, target: ship);
        d.Step();
        Assert.Equal(DockPurpose.Board, d.World.Dockings.Get(ship).Purpose);
        d.RunUntil(() => d.World.Holders.Get(ship).Count == 3, 1500);
        Assert.Equal(3, d.World.Holders.Get(ship).Count);
        Assert.InRange(d.PositionOf(ship).X / Tile, 12, 15); // it came to the west shore, next to them
        Assert.False(d.World.Dockings.Has(ship));
    }

    [Fact]
    public void Unloading_at_a_point_across_the_sea_sails_there_first()
    {
        using var d = new SimDriver(AsciiMap(Bay, [Unit("comm.galleon", 0, 13, 4), .. Enumerable.Range(0, 3).Select(i => Unit("euro.footman", 0, 10, 3 + i))]), ruleset: Classic);
        EntityId ship = OfType(d.World, "comm.galleon").Single();
        EntityId[] footmen = [.. OfType(d.World, "euro.footman")];
        d.Order(0, CommandKind.Board, footmen, target: ship);
        d.RunUntil(() => d.World.Holders.Get(ship).Count == 3, 800);
        d.Order(0, CommandKind.Unload, [ship], 34 * Tile, 10 * Tile);
        d.Step();
        Assert.Equal(3, d.World.Holders.Get(ship).Count); // not here: there is no land of that side next to it
        Assert.Equal(DockPurpose.UnloadTo, d.World.Dockings.Get(ship).Purpose);
        d.RunUntil(() => d.World.Holders.Get(ship).Count == 0, 2000);
        Assert.InRange(d.PositionOf(ship).X / Tile, 24, 27); // the east shore
        d.RunUntil(() => !d.AnyMoving(footmen), 800);
        Assert.All(footmen, f => Assert.True(OnLand(d, f) && d.PositionOf(f).X / Tile >= 28, $"footman at {d.PositionOf(f).X / Tile}"));
        Assert.All(footmen, f => Assert.InRange(d.PositionOf(f).X / Tile, 31, 37));
    }

    [Fact]
    public void Unloading_at_sea_without_a_point_goes_to_the_nearest_shore()
    {
        using var d = new SimDriver(AsciiMap(Bay, [Unit("comm.galleon", 0, 13, 4), Unit("euro.footman", 0, 10, 4)]), ruleset: Classic);
        EntityId ship = OfType(d.World, "comm.galleon").Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        d.Order(0, CommandKind.Board, [footman], target: ship);
        d.RunUntil(() => d.World.IsHeld(footman), 800);
        d.Order(0, CommandKind.Move, [ship], 25 * Tile, 8 * Tile);
        d.RunUntil(() => !d.IsMoving(ship), 1200);
        d.Order(0, CommandKind.Unload, [ship]);
        d.RunUntil(() => !d.World.IsHeld(footman), 1200);
        Assert.False(d.World.IsHeld(footman));
        Assert.True(OnLand(d, footman));
        Assert.True(d.PositionOf(footman).X / Tile >= 28); // the east shore is the nearer one
    }

    [Fact]
    [Trait("Rule", "BLD-008")]
    public void A_ship_comes_to_its_repairers()
    {
        // Patch notes 1.05-1.10: ship repair behaviour (TDD §5.1).
        using var d = new SimDriver(AsciiMap(Bay, Unit("comm.galleon", 0, 20, 8), Unit("euro.peon", 0, 6, 8)), ruleset: Classic);
        EntityId ship = OfType(d.World, "comm.galleon").Single();
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.World.Players[0].Resources[Resource("wood")] = 5000;
        d.World.Players[0].Resources[Resource("food")] = 5000;
        ref Health h = ref d.World.Healths.Get(ship);
        h.Current = h.Max / 2;
        d.Order(0, CommandKind.Repair, [peon], target: ship);
        d.Step();
        Assert.Equal(DockPurpose.Repair, d.World.Dockings.Get(ship).Purpose);
        d.RunUntil(() => d.World.Healths.Get(ship).Current == d.World.Healths.Get(ship).Max, 6000);
        Assert.Equal(d.World.Healths.Get(ship).Max, d.World.Healths.Get(ship).Current);
        Assert.InRange(d.PositionOf(ship).X / Tile, 12, 15);
    }

    [Fact]
    [Trait("Rule", "ECO-010")]
    public void Boats_fish_from_patch_to_patch_and_peasants_never_fish()
    {
        using var d = new SimDriver(
            AsciiMap(Bay, Structure("euro.castle", 0, 0, 0), Structure("euro.harbor", 0, 9, 6), Unit("comm.fishing_boat", 0, 14, 7), Unit("euro.peon", 0, 6, 12),
                new MapObject("comm.fishpatch", MapObject.Nature, TileCenterOf(17), TileCenterOf(7)), new MapObject("comm.fishpatch", MapObject.Nature, TileCenterOf(19), TileCenterOf(10)),
                Unit("comm.cow", 0, 8, 12)),
            ruleset: Classic);
        EntityId boat = OfType(d.World, "comm.fishing_boat").Single();
        EntityId peon = OfType(d.World, "euro.peon").Single();
        EntityId[] fish = [.. OfType(d.World, "comm.fishpatch")];
        d.Order(0, CommandKind.Harvest, [peon], target: fish[0]);
        d.Step();
        Assert.Contains(CommandRejection.NoValidEntities, d.Rejections()); // a peasant cannot reach the sea

        d.World.ResourceStocks.Get(fish[0]).Amount = 25; // the first patch runs out after the first load
        d.Order(0, CommandKind.Harvest, [boat], target: fish[0]);
        d.RunUntil(() => d.Count(SimEventKind.ResourceDelivered) >= 3, 6000);
        Assert.Equal(3, d.Count(SimEventKind.ResourceDelivered));
        Assert.False(d.World.Entities.IsAlive(fish[0]) && d.World.ResourceStocks.Get(fish[0]).Amount > 0);
        Assert.True(d.World.ResourceStocks.Get(fish[1]).Amount < 333); // it went on to the next patch, not to the cow
        Assert.Equal(fish[1].Value, d.World.Workers.Get(boat).Target);
    }

    [Fact]
    public void A_harbor_trains_ships_onto_the_water()
    {
        using var d = new SimDriver(AsciiMap(Bay, Structure("euro.castle", 0, 0, 0), Structure("euro.harbor", 0, 9, 6)), ruleset: Classic);
        Grant(d.World, 0, "te_mapmaking");
        Grant(d.World, 0, "te_usury");
        foreach (string r in new[] { "food", "wood", "gold", "stone" })
        {
            d.World.Players[0].Resources[Resource(r)] = 20_000;
        }

        EntityId harbor = OfType(d.World, "euro.harbor").Single();
        foreach (string ship in new[] { "comm.fishing_boat", "comm.galleon", "comm.xebec", "comm.hulk" })
        {
            d.Order(0, CommandKind.Train, [harbor], arg0: Id(ship), arg1: 1);
        }

        d.Step();
        Assert.Empty(d.Rejections());
        d.RunUntil(() => OfType(d.World, "comm.hulk").Any(), 12_000);
        foreach (string ship in new[] { "comm.fishing_boat", "comm.galleon", "comm.xebec", "comm.hulk" })
        {
            EntityId e = OfType(d.World, ship).Single();
            Assert.True(d.World.Map.IsPassableAt(Locomotor.Ship, d.PositionOf(e).X, d.PositionOf(e).Y), ship);
        }
    }

    [Fact]
    public void Soldiers_on_land_and_ships_do_not_chase_each_other_across_the_shore()
    {
        using var d = new SimDriver(AsciiMap(Bay, Unit("comm.galleon", 1, 14, 8), Unit("euro.footman", 0, 10, 8), Unit("euro.archer", 0, 9, 3)), ruleset: Classic);
        EntityId ship = OfType(d.World, "comm.galleon").Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId archer = OfType(d.World, "euro.archer").Single();
        d.Order(1, CommandKind.SetStance, [ship], arg0: (int)CombatStance.HoldFire);
        d.RunUntil(() => false, 200);
        Assert.Equal(0u, d.World.Combats.Get(footman).Target); // a swordsman cannot reach a ship
        Assert.False(d.IsMoving(footman));
        Assert.True(d.World.Healths.Get(ship).Current < d.World.Healths.Get(ship).Max); // the archer in range shoots it

        d.Order(1, CommandKind.SetStance, [ship], arg0: (int)CombatStance.Aggressive);
        d.Order(0, CommandKind.Move, [archer], 2 * Tile, 12 * Tile);
        d.RunUntil(() => false, 300);
        Assert.True(d.World.Map.IsPassableAt(Locomotor.Ship, d.PositionOf(ship).X, d.PositionOf(ship).Y));
        Assert.InRange(d.PositionOf(ship).X / Tile, 12, 16); // it fires at what it reaches but sails after nothing inland
    }

    [Theory]
    [InlineData(CombatStance.StandGround, false)]
    [InlineData(CombatStance.Aggressive, true)]
    [Trait("Rule", "ORD-017")]
    public void A_ship_on_stand_ground_does_not_sail_after_enemies(CombatStance stance, bool chases)
    {
        // Patch notes: stand-ground/explore for ships (TDD §5.1). The enemy is in sight but out of range.
        EntityTypeDefinition galleon = Classic.EntityTypes[Id("comm.galleon")];
        int gap = (galleon.Traits.Armament!.RangeUnits / Tile) + 3;
        Assert.True(gap * Tile < galleon.Traits.SightUnits);
        string[] sea = [.. Enumerable.Repeat("........" + new string('~', 48) + "........", 12)];
        using var d = new SimDriver(AsciiMap(sea, Unit("comm.galleon", 0, 12, 6), Unit("comm.xebec", 1, 12 + gap, 6)), ruleset: Classic);
        EntityId ship = OfType(d.World, "comm.galleon").Single();
        EntityId enemy = OfType(d.World, "comm.xebec").Single();
        d.Order(1, CommandKind.SetStance, [enemy], arg0: (int)CombatStance.HoldFire);
        d.Order(0, CommandKind.SetStance, [ship], arg0: (int)stance);
        d.Step();
        Position start = d.PositionOf(ship);
        d.RunUntil(() => false, 300);
        Assert.Equal(chases, d.PositionOf(ship).X != start.X);
        Assert.Equal(chases, d.World.Healths.Get(enemy).Current < d.World.Healths.Get(enemy).Max);
    }

    [Fact]
    [Trait("Rule", "ORD-017")]
    public void Ships_explore_the_water_and_soldiers_the_land()
    {
        // Patch notes: stand-ground/explore for ships (TDD §5.1). A long sea between two shores.
        string[] sea = [.. Enumerable.Repeat("........" + new string('~', 104) + "........", 16)];
        using var d = new SimDriver(AsciiMap(sea, Unit("comm.xebec", 0, 10, 2), Unit("euro.footman", 0, 2, 2)), ruleset: Classic);
        EntityId ship = OfType(d.World, "comm.xebec").Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        d.Order(0, CommandKind.Explore, [ship, footman]);
        d.Step();
        Assert.Equal(2, d.World.Explorers.Count);
        bool shipOnWater = true;
        bool footmanOnLand = true;
        d.RunUntil(
            () =>
            {
                shipOnWater &= d.World.Map.IsPassableAt(Locomotor.Ship, d.PositionOf(ship).X, d.PositionOf(ship).Y);
                footmanOnLand &= OnLand(d, footman);
                return d.World.Explorers.Count == 0;
            },
            20_000);
        Assert.True(shipOnWater && footmanOnLand);
        Assert.Equal(0, d.World.Explorers.Count); // done: nothing left either can reach
        for (int x = 9; x < 111; x++)
        {
            Assert.True(d.World.Visibility.IsExplored(0, x, 8), $"sea tile {x} unexplored");
        }

        Assert.True(d.PositionOf(ship).X / Tile > 40); // it sailed out to see
    }

    [Fact]
    [Trait("Rule", "ORD-007")]
    public void Explore_sends_the_unit_to_the_clicked_area_and_it_explores_there()
    {
        // Manual: Explore, then a click on the area to explore. A wide field: the area is the far east.
        string[] field = [.. Enumerable.Repeat(new string('.', 120), 40)];
        using var d = new SimDriver(AsciiMap(field, Unit("euro.footman", 0, 4, 20)), ruleset: Classic);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        d.Order(0, CommandKind.Explore, [footman], (100 * Tile) + (Tile / 2), (20 * Tile) + (Tile / 2), arg0: 1);
        d.RunUntil(() => d.World.Explorers.Count == 0, 20_000);
        Assert.Equal(0, d.World.Explorers.Count); // the area is explored: the order is done
        for (int x = 90; x < 110; x++)
        {
            Assert.True(d.World.Visibility.IsExplored(0, x, 20), $"tile {x} of the area unexplored");
        }

        Assert.False(d.World.Visibility.IsExplored(0, 60, 2), "it went to the area, not around itself");
        Assert.True(d.PositionOf(footman).X / Tile > 80);
    }

    [Fact]
    [Trait("Rule", "TECH-019")]
    public void Landing_Tactics_adds_ship_room_once()
    {
        // Patch notes: ship storage double upgrade (TDD §5.1). Two harbors, a ship built before and one after.
        using var d = new SimDriver(AsciiMap(Bay, Structure("euro.harbor", 0, 9, 2), Structure("euro.harbor", 0, 9, 10), Unit("comm.galleon", 0, 15, 4)), ruleset: Classic);
        EntityId before = OfType(d.World, "comm.galleon").Single();
        int room = d.World.HolderCapacity(before);
        Grant(d.World, 0, "te_landingtactics");
        EntityId after = d.Spawn(Id("comm.galleon"), 0, 15, 12);
        Assert.Equal(room * 3 / 2, d.World.HolderCapacity(before)); // 50 % more (help HT_USE_SHIPS), once (1.05)
        Assert.Equal(room * 3 / 2, d.World.HolderCapacity(after));
        byte[] snapshot = d.Sim.CaptureSnapshot();
        using Simulation restored = Simulation.Restore(snapshot, Classic, d.Sim.World.Map.Data);
        Assert.Equal(room * 3 / 2, restored.World.HolderCapacity(after));
    }

    [Fact]
    [Trait("Rule", "MAG-018")]
    public void Crusaders_who_die_inside_a_ship_end_the_crusade()
    {
        // Patch notes: crusade anchor updates when crusaders die inside ships/buildings (TDD §5.1).
        using var d = new SimDriver(AsciiMap(Bay, Structure("euro.cathedral", 0, 2, 2), Unit("comm.galleon", 0, 13, 8)), ruleset: Classic);
        d.World.Players[0].Resources[Resource("gold")] = 5000;
        d.World.Players[0].Resources[Resource("food")] = 5000;
        EntityId cathedral = OfType(d.World, "euro.cathedral").Single();
        EntityId ship = OfType(d.World, "comm.galleon").Single();
        d.World.Players[0].Crusaders = 5; // as many as the ship holds
        d.Order(0, CommandKind.PreachCrusade, [cathedral], 9 * Tile, 8 * Tile);
        d.RunUntil(() => OfType(d.World, "euro.crusader").Count() == 5, 100);
        EntityId[] crusaders = [.. OfType(d.World, "euro.crusader")];
        d.Order(0, CommandKind.Board, crusaders, target: ship);
        d.RunUntil(() => d.World.Holders.Get(ship).Count == 5, 1500);
        Assert.Equal(5, d.World.Holders.Get(ship).Count);
        d.Order(0, CommandKind.Move, [ship], 20 * Tile, 8 * Tile);
        d.RunUntil(() => !d.IsMoving(ship), 1200);
        Assert.Equal(1, d.World.Crusades.Count); // the anchor stays while its crusaders live, aboard too
        d.Order(0, CommandKind.DebugKill, [ship]);
        d.Step();
        Assert.All(crusaders, c => Assert.False(d.World.Entities.IsAlive(c)));
        Assert.Equal(0, d.World.CrusaderLinks.Count);
        d.RunUntil(() => d.World.Crusades.Count == 0, 2000);
        Assert.Equal(0, d.World.Crusades.Count); // the anchor saw them die and went
        Assert.Equal(4, d.World.Players[0].Crusaders); // the kingdom gathers again from 4
    }

    private static int TileCenterOf(int tile) => SimDriver.TileCenter(tile);
}
