using System;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// GAME-01/02 (ADRs 0040, 0042) on synthetic maps: mana, fishing boats and harbors, cows (milk, meat, poaching),
/// neglected fields, auto-gathering after building a drop-off, and the HUD's resources.
/// </summary>
public class EconomyM3Tests
{
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

    [Fact]
    public void Peasants_milk_cows_which_regrow_and_butchered_cows_leave_rotting_meat()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Unit("comm.cow", 0, 12, 4), Unit("euro.peon", 0, 10, 4)), ruleset: Classic);
        EntityId cow = OfType(d.World, "comm.cow").Single();
        EntityId peon = OfType(d.World, "euro.peon").Single();
        Assert.Equal(40, d.World.ResourceStocks.Get(cow).Amount); // max_manna 40 (synthetic) is the milk
        d.Order(0, CommandKind.Harvest, [peon], target: cow);
        d.RunUntil(() => d.Count(SimEventKind.ResourceDelivered) >= 1, 2000);
        // A cow's load is 50 (ADR 0170): the peasant milks this cow dry, 40 plus the 2 that regrew while it was milking.
        Assert.Equal(42, d.Events.First(e => e.Event.Kind == SimEventKind.ResourceDelivered).Event.Arg);
        Assert.True(d.World.Entities.IsAlive(cow));

        // Milk regrows (4 s per unit, UNVERIFIED) up to the full 40.
        d.Order(0, CommandKind.Stop, [peon]);
        d.Step();
        int left = d.World.ResourceStocks.Get(cow).Amount;
        d.RunUntil(() => false, 160);
        Assert.Equal(Math.Min(40, left + 2), d.World.ResourceStocks.Get(cow).Amount);

        // Butchering an own cow: meat (a Nature node without footprint) that anyone may take, which rots.
        d.Order(0, CommandKind.Attack, [peon], target: cow);
        d.RunUntil(() => !d.World.Entities.IsAlive(cow), 3000);
        EntityId meat = OfType(d.World, "comm.meat").Single();
        Assert.Equal(MapObject.Nature, d.World.Identities.Get(meat).Owner);
        Assert.Equal(100, d.World.ResourceStocks.Get(meat).Amount);
        Assert.False(d.World.OccupancyCells.Has(meat));
        d.Order(0, CommandKind.Harvest, [peon], target: meat);
        d.RunUntil(() => d.Count(SimEventKind.ResourceDelivered) >= 2, 2000);
        int expires = d.World.Lifetimes.Get(meat).ExpiresTick;
        d.Order(0, CommandKind.Stop, [peon]);
        d.RunUntil(() => !d.World.Entities.IsAlive(meat), 3000);
        Assert.Equal(expires, d.World.Tick.Value - 1);
    }

    [Fact]
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
}
