using System;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>GAME-14 tavern hiring (ADR 0108): mercenary groups and heroes, Mercenaries / Heroes' Quest / Fame, periods, room.</summary>
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

        return d;
    }

    private static EntityId Tavern(SimDriver d) => OfType(d.World, "euro.tavern").Single();

    [Fact]
    [Trait("Rule", "TRADE-009")]
    public void Mercenaries_are_hired_in_groups_at_their_level_once_per_period()
    {
        using SimDriver d = Driver();
        World w = d.World;
        d.Order(0, CommandKind.Hire, [Tavern(d)], arg0: 0);
        d.Step();
        Assert.Contains(CommandRejection.RequirementNotMet, d.Rejections()); // Mercenaries first

        ResearchTests.Grant(d, 0, Classic, Classic.World.MercenariesTechnology);
        TavernOffer offer = TavernOffers.Offer(w, 0, 0)!.Value;
        Assert.False(offer.IsHero);
        Assert.InRange(offer.Count, w.Ruleset.World.MercenaryGroupMin, w.Ruleset.World.MercenaryGroupMax);
        Assert.Equal("euro", w.Ruleset.EntityTypes[offer.TypeId].Traits.Civilization);
        Assert.True(w.Ruleset.EntityTypes[offer.TypeId].Traits.World.IsMilitary);
        var cost = new int[Classic.ResourceTypes.Count];
        TavernOffers.CostOf(w, 0, offer, cost);
        int[] before = Enumerable.Range(0, cost.Length).Select(r => w.Players[0].Resources[r]).ToArray();
        int units = w.Identities.Count;

        d.Order(0, CommandKind.Hire, [Tavern(d)], arg0: 0);
        d.Step();
        Assert.Equal(units + offer.Count, w.Identities.Count);
        SimEvent hired = d.Events.Single(e => e.Event.Kind == SimEventKind.Hired).Event;
        Assert.Equal((0, offer.Count, (int)offer.TypeId), (hired.X, hired.Y, hired.Arg));
        for (int r = 0; r < cost.Length; r++)
        {
            Assert.Equal(before[r] - cost[r], w.Players[0].Resources[r]);
            Assert.Equal((int)((long)w.Ruleset.EntityTypes[offer.TypeId].Traits.Cost[r] * offer.Count * 150 / 100), cost[r]); // UNVERIFIED 150%
        }

        EntityId[] mercs = [.. OfType(w, w.Ruleset.EntityTypes[offer.TypeId].Key, 0)];
        Assert.Equal(offer.Count, mercs.Length);
        Assert.All(mercs, m => Assert.Equal(w.Ruleset.World.MercenaryLevel, w.Experiences.Get(m).Level));

        // Once per period; the next period offers again.
        d.Order(0, CommandKind.Hire, [Tavern(d)], arg0: 0);
        d.Step();
        Assert.Contains(CommandRejection.OfferTaken, d.Rejections());
        int periodTicks = w.Ruleset.World.TavernRefreshMs / w.Config.TickLengthMilliseconds;
        d.RunUntil(() => TavernOffers.PeriodAt(w, w.Tick.Value) > 0, periodTicks + 5);
        d.Order(0, CommandKind.Hire, [Tavern(d)], arg0: 0);
        d.Step();
        Assert.Equal(2, d.Count(SimEventKind.Hired));
    }

    [Fact]
    [Trait("Rule", "TRADE-009")]
    public void Heroes_need_heroes_quest_and_come_named_at_the_hero_level_and_fame_makes_better_cheaper_groups()
    {
        using SimDriver d = Driver();
        World w = d.World;
        int heroSlot = w.Ruleset.World.MercenaryOffers;
        ResearchTests.Grant(d, 0, Classic, Classic.World.MercenariesTechnology);
        d.Order(0, CommandKind.Hire, [Tavern(d)], arg0: heroSlot);
        d.Step();
        Assert.Contains(CommandRejection.RequirementNotMet, d.Rejections());

        TavernOffer plain = TavernOffers.Offer(w, 0, 1)!.Value;
        var plainCost = new int[Classic.ResourceTypes.Count];
        TavernOffers.CostOf(w, 0, plain, plainCost);
        ResearchTests.Grant(d, 0, Classic, Classic.World.HeroesTechnology, Classic.World.FameTechnology);
        TavernOffer famous = TavernOffers.Offer(w, 0, 1)!.Value;
        var famousCost = new int[Classic.ResourceTypes.Count];
        TavernOffers.CostOf(w, 0, famous, famousCost);
        Assert.Equal(plain.TypeId, famous.TypeId);
        Assert.Equal(plain.Count + w.Ruleset.World.FameGroupBonus, famous.Count);
        Assert.Equal(plain.Level + w.Ruleset.World.FameLevelBonus, famous.Level);
        Assert.True(famousCost.Sum() * plain.Count < plainCost.Sum() * famous.Count); // cheaper per soldier

        d.Order(0, CommandKind.Hire, [Tavern(d)], arg0: heroSlot);
        d.Step();
        SimEvent hired = d.Events.Single(e => e.Event.Kind == SimEventKind.Hired).Event;
        EntityId hero = hired.Entity;
        Assert.Equal(1, hired.Y);
        Assert.True(w.IsHero(hero));
        Assert.Equal(w.Ruleset.Combat.HeroLevel, w.Experiences.Get(hero).Level);
        Assert.Contains(d.Events, e => e.Event.Kind == SimEventKind.HeroPromoted && e.Event.Entity == hero);
        Assert.Equal(w.Healths.Get(hero).Max, w.Healths.Get(hero).Current);
    }

    [Fact]
    public void Offers_follow_the_seed_and_hiring_needs_room_an_own_tavern_and_a_valid_slot()
    {
        using SimDriver a = Driver(7);
        using SimDriver b = Driver(7);
        using SimDriver c = Driver(8);
        int slots = TavernOffers.SlotCount(a.World);
        TavernOffer?[] Offers(SimDriver d, int p) => [.. Enumerable.Range(0, slots).Select(s => TavernOffers.Offer(d.World, p, s))];
        Assert.Equal(Offers(a, 0), Offers(b, 0));
        Assert.True(!Offers(a, 0).SequenceEqual(Offers(c, 0)) || !Offers(a, 1).SequenceEqual(Offers(c, 1)));
        Assert.Null(TavernOffers.Offer(a.World, 0, slots));

        World w = a.World;
        ResearchTests.Grant(a, 0, Classic, Classic.World.MercenariesTechnology);
        a.Order(0, CommandKind.Hire, [Tavern(a)], arg0: -1);
        a.Order(1, CommandKind.Hire, [Tavern(a)], arg0: 0); // not its tavern
        w.Players[0].PopulationCap = w.Players[0].Population; // no room
        a.Order(0, CommandKind.Hire, [Tavern(a)], arg0: 0);
        a.Step();
        Assert.Contains(CommandRejection.InvalidArgument, a.Rejections());
        Assert.Contains(CommandRejection.NoValidEntities, a.Rejections());
        Assert.Contains(CommandRejection.NoRoom, a.Rejections());
        Assert.Equal(0, a.Count(SimEventKind.Hired));
        Assert.Equal(0, w.Kingdoms[0].TavernTaken); // a refused hire changes nothing
    }

    [Theory]
    [InlineData("taken beyond the slots")]
    [InlineData("period in the future")]
    public void Invalid_tavern_state_is_rejected(string corruption)
    {
        var world = new World(Classic, Town(), new SimulationConfig { PlayerCount = 2 });
        world.LoadMap();
        if (corruption == "taken beyond the slots")
        {
            world.Kingdoms[0].TavernTaken = (byte)(1 << TavernOffers.SlotCount(world));
        }
        else
        {
            world.Kingdoms[0].TavernPeriod = 5;
        }

        byte[] snapshot = Snapshots.WorldSnapshot.Capture(world);
        Assert.Throws<InvalidDataException>(() => Simulation.Restore(snapshot, Classic, world.Map.Data));
    }

    [Fact]
    public void Hired_offers_survive_a_snapshot_and_continue_identically()
    {
        using SimDriver d = Driver();
        ResearchTests.Grant(d, 0, Classic, Classic.World.MercenariesTechnology, Classic.World.HeroesTechnology);
        d.Order(0, CommandKind.Hire, [Tavern(d)], arg0: 0);
        d.Order(0, CommandKind.Hire, [Tavern(d)], arg0: d.World.Ruleset.World.MercenaryOffers);
        d.Step();
        Assert.NotEqual(0, d.World.Kingdoms[0].TavernTaken);
        byte[] snapshot = d.Sim.CaptureSnapshot();
        using Simulation restored = Simulation.Restore(snapshot, Classic, d.World.Map.Data);
        Assert.Equal(snapshot, restored.CaptureSnapshot());
        Assert.Equal(d.World.Kingdoms[0].TavernTaken, restored.World.Kingdoms[0].TavernTaken);
        var empty = new CommandList();
        for (int t = 0; t < 100; t++)
        {
            Assert.Equal(d.Step().StateHash, restored.Tick(empty).StateHash);
        }
    }
}
