using System;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// TRADE-009 (ADR 0108, ADR 0280; ReTzar's reading of the original, 0x4d24c0, 0x4d2030, 0x4d23b0): each tavern's lists
/// of mercenaries and heroes, their growth a minute apart, Fame, the price and hiring.
/// </summary>
public class TavernHiringTests
{
    private static MapData Town() => TestMap(48, 16, Structure("euro.castle", 0, 2, 2), Structure("euro.tavern", 0, 20, 4), Structure("euro.castle", 1, 40, 2));

    private static SimDriver Driver(ulong seed = 7)
    {
        var d = new SimDriver(Town(), ruleset: Classic, configure: c => c with { MatchSeed = seed });
        for (int r = 0; r < Classic.ResourceTypes.Count; r++)
        {
            d.World.Players[0].Resources[r] = 100_000;
        }

        d.World.Players[0].PopulationCap = 200;
        return d;
    }

    private static EntityId Tavern(SimDriver d) => OfType(d.World, "euro.tavern").Single();

    private static (int Slot, TavernOffer Offer)[] Offers(SimDriver d)
    {
        var list = new (int, TavernOffer)[TavernStock.Capacity];
        int n = TavernOffers.Offers(d.World, Tavern(d), list);
        return list[..n];
    }

    private static int Minutes(SimDriver d, int m) => m * 60_000 / d.World.Config.TickLengthMilliseconds;

    [Fact]
    [Trait("Rule", "TRADE-009")]
    public void A_tavern_gains_a_mercenary_and_a_hero_offer_a_second_after_it_stands_and_then_every_minute()
    {
        using SimDriver d = Driver();
        World w = d.World;
        d.RunUntil(() => false, 60);
        Assert.Empty(Offers(d)); // nothing without Mercenaries or Heroes' Quest
        ResearchTests.Grant(d, 0, Classic, Classic.World.MercenariesTechnology, Classic.World.HeroesTechnology);
        d.RunUntil(() => Offers(d).Length == 2, Minutes(d, 1) + 40);
        (int slot, TavernOffer merc) = Offers(d)[0];
        (int heroSlot, TavernOffer hero) = Offers(d)[1];
        Assert.Equal(0, slot);
        Assert.Equal(TavernStock.ListSize, heroSlot);
        Assert.InRange(merc.Count, 8, 12);
        Assert.Equal(0, merc.Level);
        Assert.Equal(1, hero.Count);
        Assert.InRange(hero.Level, 10, 30);
        Assert.Contains(merc.TypeId, w.BarracksPool(Classic.EntityTypes[merc.TypeId].Traits.Civilization).ToArray());

        // A minute later another of each, until each list holds 8: then the oldest goes.
        d.RunUntil(() => false, Minutes(d, 1));
        Assert.Equal(4, Offers(d).Length);
        d.RunUntil(() => false, Minutes(d, 8));
        Assert.Equal(2 * TavernStock.ListSize, Offers(d).Length);
        Assert.NotEqual(merc, Offers(d)[0].Offer);
    }

    [Fact]
    [Trait("Rule", "TRADE-009")]
    public void Hiring_takes_the_offer_off_its_list_and_brings_its_units_at_their_level_for_the_originals_price()
    {
        using SimDriver d = Driver();
        World w = d.World;
        ResearchTests.Grant(d, 0, Classic, Classic.World.MercenariesTechnology, Classic.World.HeroesTechnology);
        d.RunUntil(() => Offers(d).Length == 2, 80);
        TavernOffer merc = Offers(d)[0].Offer;
        TavernOffer hero = Offers(d)[1].Offer;
        Span<int> cost = stackalloc int[Classic.ResourceTypes.Count];
        TavernOffers.CostOf(w, 0, merc, cost);
        EntityTypeDefinition type = Classic.EntityTypes[merc.TypeId];
        int gold = Classic.ResourceId("gold");
        for (int r = 0; r < cost.Length; r++)
        {
            long expected = (long)type.Traits.Cost[r] * merc.Count * 160 / 100;
            Assert.Equal(expected, cost[r]); // level 0: nothing more (0x4d23b0)
        }

        TavernOffers.CostOf(w, 0, hero, cost);
        Assert.True(cost[gold] >= 30 * hero.Level); // 30 gold a level

        int before = w.Players[0].Resources[gold];
        d.Order(0, CommandKind.Hire, [Tavern(d)], arg0: 0);
        d.Order(0, CommandKind.Hire, [Tavern(d)], arg0: TavernStock.ListSize);
        d.Step();
        Assert.Empty(d.Rejections());
        Assert.Empty(Offers(d)); // both taken off their lists
        Assert.Equal(merc.Count, OfType(w, Classic.EntityTypes[merc.TypeId].Key, 0).Count());
        SimEvent hired = d.Events.Last(e => e.Event.Kind == SimEventKind.Hired).Event;
        Assert.True(w.IsHero(hired.Entity));
        Assert.Equal(hero.Level, w.Experiences.Get(hired.Entity).Level);
        Assert.True(w.Players[0].Resources[gold] < before);

        d.Order(0, CommandKind.Hire, [Tavern(d)], arg0: 0);
        d.Step();
        Assert.Contains(CommandRejection.OfferTaken, d.Rejections());
    }

    [Fact]
    [Trait("Rule", "TRADE-009")]
    public void Fame_adds_four_levels_to_every_offer()
    {
        using SimDriver plain = Driver(9);
        using SimDriver famous = Driver(9);
        ResearchTests.Grant(plain, 0, Classic, Classic.World.MercenariesTechnology);
        ResearchTests.Grant(famous, 0, Classic, Classic.World.MercenariesTechnology, Classic.World.FameTechnology);
        plain.RunUntil(() => Offers(plain).Length == 1, 80);
        famous.RunUntil(() => Offers(famous).Length == 1, 80);
        Assert.Equal(Offers(plain)[0].Offer.Level + 4, Offers(famous)[0].Offer.Level);
        Assert.Equal(Offers(plain)[0].Offer.Count, Offers(famous)[0].Offer.Count); // Fame brings no more soldiers
    }

    [Fact]
    public void Hiring_needs_room_an_own_tavern_and_a_valid_slot()
    {
        using SimDriver a = Driver(7);
        World w = a.World;
        ResearchTests.Grant(a, 0, Classic, Classic.World.MercenariesTechnology);
        a.RunUntil(() => Offers(a).Length == 1, 80);
        a.Order(0, CommandKind.Hire, [Tavern(a)], arg0: -1);
        a.Order(1, CommandKind.Hire, [Tavern(a)], arg0: 0); // not its tavern
        w.Players[0].PopulationCap = w.Players[0].Population; // no room
        a.Order(0, CommandKind.Hire, [Tavern(a)], arg0: 0);
        a.Step();
        Assert.Contains(CommandRejection.InvalidArgument, a.Rejections());
        Assert.Contains(CommandRejection.NoValidEntities, a.Rejections());
        Assert.Contains(CommandRejection.NoRoom, a.Rejections());
        Assert.Equal(0, a.Count(SimEventKind.Hired));
        Assert.Single(Offers(a)); // a refused hire changes nothing
    }

    [Theory]
    [InlineData("too many offers")]
    [InlineData("an empty offer")]
    public void Invalid_tavern_state_is_rejected(string corruption)
    {
        var world = new World(Classic, Town(), new SimulationConfig { PlayerCount = 2 });
        world.LoadMap();
        EntityId tavern = OfType(world, "euro.tavern").Single();
        var stock = new TavernStock { NextTick = 20 };
        if (corruption == "too many offers")
        {
            stock.Mercenaries = TavernStock.ListSize + 1;
        }
        else
        {
            stock.Mercenaries = 1; // its slot holds nothing
        }

        world.TavernStocks.Set(tavern, stock);
        byte[] snapshot = Snapshots.WorldSnapshot.Capture(world);
        Assert.Throws<InvalidDataException>(() => Simulation.Restore(snapshot, Classic, world.Map.Data));
    }

    [Fact]
    public void A_taverns_offers_survive_a_snapshot_and_continue_identically()
    {
        using SimDriver d = Driver();
        ResearchTests.Grant(d, 0, Classic, Classic.World.MercenariesTechnology, Classic.World.HeroesTechnology);
        d.RunUntil(() => Offers(d).Length == 2, 80);
        d.Order(0, CommandKind.Hire, [Tavern(d)], arg0: 0);
        d.Step();
        byte[] snapshot = d.Sim.CaptureSnapshot();
        using Simulation restored = Simulation.Restore(snapshot, Classic, d.World.Map.Data);
        Assert.Equal(snapshot, restored.CaptureSnapshot());
        var empty = new CommandList();
        for (int t = 0; t < 1300; t++)
        {
            Assert.Equal(d.Step().StateHash, restored.Tick(empty).StateHash);
        }
    }
}
