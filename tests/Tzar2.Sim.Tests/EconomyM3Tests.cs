using System;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// GAME-01/02 (ADRs 0040, 0042) on synthetic maps: mana, fishing boats and harbors, cows (milk, meat, poaching),
/// neglected fields, auto-gathering after building a drop-off, and the HUD's resources.
/// </summary>
public class EconomyM3Tests
{
    [Fact]
    [Trait("Rule", "TECH-010")]
    public void Ore_flotation_makes_a_mine_lose_half_of_each_load()
    {
        // ReTzar's reading of the original: the mine loses desired x 50 / 100 of a full load.
        using var d = new SimDriver(TestMap(40, 20, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 14, 6), Structure("comm.res_gold", MapObject.Nature, 18, 6)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        EntityId mine = OfType(d.World, "comm.res_gold").Single();
        Grant(d.World, 0, "te_orefloatation");
        d.Step();
        int before = d.World.ResourceStocks.Get(mine).Amount;
        d.Order(0, CommandKind.Harvest, [peon], target: mine);
        d.RunUntil(() => d.World.Workers.Get(peon).CarryAmount >= 10, 3000);
        Assert.Equal(10, d.World.Workers.Get(peon).CarryAmount);
        Assert.Equal(before - 5, d.World.ResourceStocks.Get(mine).Amount);
    }

    [Fact]
    [Trait("Rule", "TECH-010")]
    [Trait("Rule", "TECH-006")]
    public void Gathering_technologies_raise_what_a_peasant_carries_from_their_resource_only()
    {
        // 10 + 2 for the resource's "plus" technology, + 4 of Irrigation for food (ReTzar, 0x4ae720).
        using var d = new SimDriver(TestMap(40, 20, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 14, 6), Tree(20, 12)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        EntityTypeDefinition tree = Classic.EntityTypes[Id("comm.tree")], gold = Classic.EntityTypes[Id("comm.res_gold")], field = Classic.EntityTypes[Id("comm.field")];
        Assert.Equal(10, GameplayCommands.CarryCapacity(d.World, peon, tree));
        ResearchTests.Grant(d, 0, Classic, "te_woodplus");
        ResearchTests.Grant(d, 0, Classic, "te_fields");
        ResearchTests.Grant(d, 0, Classic, "te_irrigation");
        d.Step();
        Assert.Equal(12, GameplayCommands.CarryCapacity(d.World, peon, tree));
        Assert.Equal(10, GameplayCommands.CarryCapacity(d.World, peon, gold));
        Assert.Equal(16, GameplayCommands.CarryCapacity(d.World, peon, field));
        Assert.Equal(50, GameplayCommands.CarryCapacity(d.World, peon, Classic.EntityTypes[Id("comm.cow")])); // cows keep theirs

        d.Order(0, CommandKind.Harvest, [peon], target: OfType(d.World, "comm.tree").Single());
        d.RunUntil(() => d.World.Workers.Get(peon).CarryAmount >= 12, 3000);
        Assert.Equal(12, d.World.Workers.Get(peon).CarryAmount);
    }

    private static int Food => Resource("food");

    private static readonly string[] Coast =
    [
        "..........~~~~~~~~~~",
        "..........~~~~~~~~~~",
        "..........~~~~~~~~~~",
        "..........~~~~~~~~~~",
        "..........~~~~~~~~~~",
        "..........~~~~~~~~~~",
        "..........~~~~~~~~~~",
        "..........~~~~~~~~~~",
    ];

    [Fact]
    [Trait("Rule", "MAG-001")]
    public void Iron_is_a_resource_but_not_in_the_hud_and_casters_regain_mana()
    {
        Assert.Equal(5, Classic.ResourceTypes.Count);
        Assert.Equal(0, Classic.HudResourceMask & (1 << Resource("iron")));
        using var d = new SimDriver(TestMap(16, 16, Unit("euro.mage", 0, 4, 4)), ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        Assert.Equal(90_000, d.World.Manas.Get(mage).MilliMana); // starts full (max_manna 90, synthetic)
        d.World.Manas.Get(mage).MilliMana = 0;
        d.RunUntil(() => false, 600); // 30 s at 60 mana per minute (UNVERIFIED)
        Assert.Equal(30_000, d.World.Manas.Get(mage).MilliMana);
        d.RunUntil(() => false, 2400);
        Assert.Equal(90_000, d.World.Manas.Get(mage).MilliMana); // capped
    }

    [Fact]
    [Trait("Rule", "ECO-010")]
    public void Fishing_boats_fish_and_deliver_at_the_harbor_not_on_land()
    {
        using var d = new SimDriver(
            AsciiMap(Coast, Structure("euro.castle", 0, 0, 0), Structure("euro.harbor", 0, 7, 3), Unit("comm.fishing_boat", 0, 12, 4), new MapObject("comm.fishpatch", MapObject.Nature, (16 * Tile) + (Tile / 2), (4 * Tile) + (Tile / 2))),
            ruleset: Classic);
        EntityId boat = OfType(d.World, "comm.fishing_boat").Single();
        EntityId fish = OfType(d.World, "comm.fishpatch").Single();
        Assert.False(d.World.Footprints.Has(fish));
        Assert.False(d.World.OccupancyCells.Has(fish)); // takes no room (ORIG-01)
        d.Order(0, CommandKind.Harvest, [boat], target: fish);
        d.RunUntil(() => d.Count(SimEventKind.ResourceDelivered) >= 2, 3000);
        SimEvent delivery = d.Events.First(e => e.Event.Kind == SimEventKind.ResourceDelivered).Event;
        Assert.Equal(Food, delivery.X);
        Assert.Equal(20, delivery.Arg); // a boat carries 20 (UNVERIFIED)
        Assert.Equal(OfType(d.World, "euro.harbor").Single().Index, delivery.Y);
        Assert.Equal(333 - d.World.Workers.Get(boat).CarryAmount - 40, d.World.ResourceStocks.Get(fish).Amount);
    }

    [Fact]
    public void A_harbor_needs_water_next_to_it()
    {
        using var d = new SimDriver(AsciiMap(Coast, Structure("euro.castle", 0, 0, 0), Unit("euro.peon", 0, 6, 6)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Build, [peon], 1 * Tile, 5 * Tile, arg0: Id("euro.harbor")); // inland
        d.Step();
        Assert.Equal(CommandRejection.IllegalPlacement, d.Rejections().Last());
        d.Order(0, CommandKind.Build, [peon], 7 * Tile, 5 * Tile, arg0: Id("euro.harbor")); // on the shore
        d.Step();
        Assert.Single(OfType(d.World, "euro.harbor"));
    }

    [Theory]
    [Trait("Rule", "ECO-018")]
    [InlineData("euro")]
    [InlineData("arab")]
    public void Peasants_of_every_civilization_milk_cows(string civ)
    {
        // The owner (2026-10-21): milking is the same for every civilization's peasants (the rules give it to every
        // ^class.peon; the synthetic tables have no Asian peasant).
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Unit("comm.cow", 0, 12, 4), Unit(civ + ".peon", 0, 10, 4)), ruleset: Classic);
        d.Order(0, CommandKind.Harvest, [OfType(d.World, civ + ".peon").Single()], target: OfType(d.World, "comm.cow").Single());
        d.RunUntil(() => d.Count(SimEventKind.ResourceDelivered) >= 1, 2000);
        Assert.Equal(50, d.Events.First(e => e.Event.Kind == SimEventKind.ResourceDelivered).Event.Arg);
    }

    [Fact]
    [Trait("Rule", "ECO-017")]
    public void Peasants_milk_cows_which_regrow_and_butchered_cows_leave_rotting_meat()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Unit("comm.cow", 0, 12, 4), Unit("euro.peon", 0, 10, 4)), ruleset: Classic);
        EntityId cow = OfType(d.World, "comm.cow").Single();
        EntityId peon = OfType(d.World, "euro.peon").Single();
        Assert.Equal(40, d.World.ResourceStocks.Get(cow).Amount); // max_manna 40 (synthetic) is the milk
        d.Order(0, CommandKind.Harvest, [peon], target: cow);
        d.RunUntil(() => d.Count(SimEventKind.ResourceDelivered) >= 1, 2000);
        // A cow's load is 50 (ADR 0170): the peasant milks this cow dry and waits by it for the milk to regrow until the
        // pail is full (play-test 2026-10-20), instead of carrying home a unit or two at a time.
        Assert.Equal(50, d.Events.First(e => e.Event.Kind == SimEventKind.ResourceDelivered).Event.Arg);
        Assert.True(d.World.Entities.IsAlive(cow));
        d.RunUntil(() => d.Count(SimEventKind.ResourceDelivered) >= 2, 4000);
        Assert.Equal(50, d.Events.Where(e => e.Event.Kind == SimEventKind.ResourceDelivered).ElementAt(1).Event.Arg);

        // Milk regrows (4 s per unit, UNVERIFIED) up to the full 40.
        d.Order(0, CommandKind.Stop, [peon]);
        d.Step();
        int left = d.World.ResourceStocks.Get(cow).Amount;
        d.RunUntil(() => false, 160);
        Assert.Equal(Math.Min(40, left + 2), d.World.ResourceStocks.Get(cow).Amount);

        // Butchering an own cow: its meat falls as Nature's food items without footprint, which rot (NAT-004).
        d.Order(0, CommandKind.Attack, [peon], target: cow);
        d.RunUntil(() => !d.World.Entities.IsAlive(cow), 3000);
        EntityId[] meat = [.. OfType(d.World, "comm.meat")];
        Assert.True(meat.Length >= 3);
        Assert.All(meat, m => Assert.Equal(MapObject.Nature, d.World.Identities.Get(m).Owner));
        Assert.All(meat, m => Assert.InRange(d.World.Items.Get(m).Power, 1, 100));
        Assert.All(meat, m => Assert.False(d.World.OccupancyCells.Has(m)));
        int expires = d.World.Lifetimes.Get(meat[0]).ExpiresTick;
        d.RunUntil(() => !d.World.Entities.IsAlive(meat[0]), 3000);
        Assert.Equal(expires, d.World.Tick.Value - 1);
    }

    [Fact]
    [Trait("Rule", "NAT-004")]
    public void A_cow_grows_meat_until_it_dies_and_any_unit_can_take_the_pieces()
    {
        // ReTzar's reading of the original: 300 at the start, 2 a second up to 600, items of at most 100.
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Unit("comm.cow", 0, 12, 4), Unit("euro.footman", 0, 10, 4)), ruleset: Classic);
        EntityId cow = OfType(d.World, "comm.cow").Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        Assert.Equal(300, d.World.MeatStocks.Get(cow).Amount);
        d.RunUntil(() => false, 10 * 1000 / d.World.Config.TickLengthMilliseconds);
        Assert.Equal(320, d.World.MeatStocks.Get(cow).Amount);
        d.World.MeatStocks.Get(cow).Amount = 250;
        d.Order(0, CommandKind.DebugKill, [cow]);
        d.Step();
        int[] pieces = [.. OfType(d.World, "comm.meat").Select(m => d.World.Items.Get(m).Power).OrderBy(p => p)];
        Assert.Equal([50, 100, 100], pieces);
        int food = d.World.Players[0].Resources[Resource("food")];
        EntityId first = OfType(d.World, "comm.meat").First();
        int power = d.World.Items.Get(first).Power;
        d.Order(0, CommandKind.PickItem, [footman], target: first);
        d.RunUntil(() => !d.World.Entities.IsAlive(first), 400);
        Assert.Equal(food + power, d.World.Players[0].Resources[Resource("food")]); // a soldier brings it in at once
    }

    [Fact]
    [Trait("Rule", "ECO-019")]
    public void Cow_poaching_steals_enemy_cows_at_once_once_researched()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Unit("comm.cow", 1, 14, 4), Unit("euro.peon", 0, 10, 4)), ruleset: Classic);
        EntityId cow = OfType(d.World, "comm.cow").Single();
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Capture, [peon], target: cow);
        d.Step();
        Assert.Equal(CommandRejection.RequirementNotMet, d.Rejections().Last());
        ResearchTests.Grant(d, 0, Classic, "te_cowpoaching");
        d.Order(0, CommandKind.Capture, [peon], target: cow);
        d.RunUntil(() => d.Count(SimEventKind.Captured) == 1, 1000);
        Assert.Equal(0, d.World.Identities.Get(cow).Owner);
        Assert.Equal(WorkerTask.None, d.World.Workers.Get(peon).Task);
    }

    [Fact]
    [Trait("Rule", "ECO-012")]
    public void Neglected_fields_decay_and_disappear_but_tended_ones_do_not()
    {
        using var d = new SimDriver(
            TestMap(40, 16, Structure("euro.castle", 0, 2, 2), Structure("comm.field", 0, 12, 2), Structure("comm.field", 0, 20, 2), Unit("euro.peon", 0, 13, 6)),
            ruleset: Classic);
        EntityId[] fields = [.. OfType(d.World, "comm.field")];
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Harvest, [peon], target: fields[0]);
        d.RunUntil(() => false, 1200 + 40); // 60 s grace (UNVERIFIED) + 2 s
        Assert.True(d.World.Healths.Get(fields[1]).Current < 130); // neglected: decaying at 30 HP/min (UNVERIFIED)
        Assert.Equal(130, d.World.Healths.Get(fields[0]).Current); // tended
        int ticks = d.RunUntil(() => !d.World.Entities.IsAlive(fields[1]), 6000);
        Assert.InRange(ticks, 4500, 5300); // 130 HP at 30 per minute ~ 260 s
        Assert.Contains(d.Events, e => e.Event.Kind == SimEventKind.Decayed && e.Event.Entity == fields[1]);
        Assert.True(d.World.Entities.IsAlive(fields[0]));
    }

    [Fact]
    [Trait("Rule", "ECO-008")]
    public void Fields_are_walked_over_and_farmed_from_inside_but_nothing_is_built_on_them()
    {
        // A field (3 x 3 tiles at 12, 2) between a footman and his goal, and a peasant sent to farm it.
        using var d = new SimDriver(
            TestMap(40, 16, Structure("euro.castle", 0, 30, 10), Structure("comm.field", 0, 12, 2), Unit("euro.footman", 0, 9, 3), Unit("euro.peon", 0, 13, 8)),
            ruleset: Classic);
        World w = d.World;
        EntityId field = OfType(w, "comm.field").Single();
        EntityId footman = OfType(w, "euro.footman").Single();
        EntityId peon = OfType(w, "euro.peon").Single();
        Assert.True(w.IsUnderWalkableFootprint(13, 3));
        Assert.True(w.Map.IsPassable(Locomotor.Foot, 13, 3)); // no blocker
        Assert.False(PlacementQueries.CanPlace(w, (ushort)Id("euro.house"), 12, 2)); // nothing is built on it
        Assert.False(PlacementQueries.IsBuildableTile(w, 13, 3));

        // The footman walks straight across the field.
        d.Order(0, CommandKind.Move, [footman], (17 * Tile) + (Tile / 2), (3 * Tile) + (Tile / 2));
        bool crossed = false;
        for (int t = 0; t < 400 && !crossed; t++)
        {
            d.Step();
            Position p = d.PositionOf(footman);
            crossed |= w.IsUnderWalkableFootprint(p.X / Tile, p.Y / Tile);
        }

        Assert.True(crossed);

        // The farmer works on the field, not beside it.
        d.Order(0, CommandKind.Harvest, [peon], target: field);
        d.RunUntil(() => w.Workers.Get(peon).Phase == WorkerPhase.Gathering, 600);
        Position at = d.PositionOf(peon);
        Assert.True(w.IsUnderWalkableFootprint(at.X / Tile, at.Y / Tile), $"farmer at {at.X / (double)Tile:0.0},{at.Y / (double)Tile:0.0}");

        // The cover survives a snapshot round trip.
        using Simulation restored = Simulation.Restore(d.Sim.CaptureSnapshot(), Classic, w.Map.Data);
        Assert.True(restored.World.IsUnderWalkableFootprint(13, 3));
        Assert.Equal(d.Sim.ComputeFullHash(), restored.ComputeFullHash());
    }

    [Fact]
    [Trait("Rule", "BLD-012")]
    public void Builders_of_a_lumber_mill_start_chopping_and_a_field_builder_farms_it()
    {
        using var d = new SimDriver(
            TestMap(40, 20, [Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 14, 6), Unit("euro.peon", 0, 24, 6), .. Enumerable.Range(0, 4).Select(i => Tree(20 + i, 12))]),
            ruleset: Classic);
        EntityId[] peons = [.. OfType(d.World, "euro.peon")];
        d.Order(0, CommandKind.Build, [peons[0]], 15 * Tile, 7 * Tile, arg0: Id("euro.lumber"));
        d.Order(0, CommandKind.Build, [peons[1]], 25 * Tile, 7 * Tile, arg0: Id("comm.field"));
        d.RunUntil(() => d.Count(SimEventKind.ConstructionCompleted) == 2, 3000);
        d.Step();
        Assert.Equal(WorkerTask.Harvest, d.World.Workers.Get(peons[0]).Task);
        Assert.Equal(Resource("wood"), d.World.Workers.Get(peons[0]).NodeResource);
        Assert.Equal(WorkerTask.Harvest, d.World.Workers.Get(peons[1]).Task);
        Assert.Equal(OfType(d.World, "comm.field").Single().Value, d.World.Workers.Get(peons[1]).Target);
    }

    [Fact]
    [Trait("Rule", "ECO-021")]
    public void Only_one_peasant_tends_a_field_and_another_goes_to_a_free_one_or_waits()
    {
        // The help (HT_TEND_FIELDS): only one peasant can tend each field.
        using var d = new SimDriver(
            TestMap(40, 16, Structure("euro.castle", 0, 2, 2), Structure("comm.field", 0, 12, 2), Structure("comm.field", 0, 20, 2), Unit("euro.peon", 0, 13, 6), Unit("euro.peon", 0, 14, 6), Unit("euro.peon", 0, 15, 6)),
            ruleset: Classic);
        EntityId[] fields = [.. OfType(d.World, "comm.field")];
        EntityId[] peons = [.. OfType(d.World, "euro.peon")];
        Assert.Equal(1, d.World.TypeOf(fields[0]).Traits.ResourceNode!.MaxWorkers);
        d.Order(0, CommandKind.Harvest, peons, target: fields[0]);
        d.RunUntil(() => false, 400);

        // One on each field; the third has none left and stands idle.
        var targets = peons.Where(p => d.World.Workers.Get(p).Task == WorkerTask.Harvest).Select(p => d.World.Workers.Get(p).Target).ToArray();
        Assert.Equal(fields.Select(f => f.Value).OrderBy(v => v), targets.OrderBy(v => v));
        Assert.Single(peons, p => d.World.Workers.Get(p).Task == WorkerTask.None);
    }
}
