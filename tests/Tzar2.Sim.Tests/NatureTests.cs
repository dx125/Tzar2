using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>GAME-15 (ADR 0104): magic gateways claimed by a military unit's touch and summoning; animals wander and guard; ambient birds.</summary>
public class NatureTests
{
    private const byte Nature = Content.Compiled.MapObject.Nature;

    [Fact]
    public void A_gateway_is_claimed_by_the_touch_of_a_military_unit_and_then_summons()
    {
        using var d = new SimDriver(
            TestMap(48, 16, Structure("comm.magicgateway", Nature, 20, 6), Unit("euro.peon", 0, 14, 7), Unit("euro.footman", 1, 30, 7), Structure("euro.castle", 1, 40, 9)),
            ruleset: Classic);
        World w = d.World;
        EntityId gateway = OfType(w, "comm.magicgateway").Single();
        Assert.Equal(Nature, w.Identities.Get(gateway).Owner);
        Assert.False(w.Productions.Has(gateway));

        // A peasant is not a military unit: touching does nothing.
        d.MoveToTile(0, [OfType(w, "euro.peon").Single()], 19, 7);
        d.RunUntil(() => !d.IsMoving(OfType(w, "euro.peon").Single()) && w.Tick.Value > 5, 400);
        for (int t = 0; t < 20; t++)
        {
            d.Step();
        }

        Assert.Equal(Nature, w.Identities.Get(gateway).Owner);

        // Nobody's units attack an unclaimed gateway; player 1's footman walks up and takes it.
        d.MoveToTile(1, [OfType(w, "euro.footman").Single()], 23, 7);
        d.RunUntil(() => d.Count(SimEventKind.Claimed) == 1, 600);
        Assert.Equal(1, w.Identities.Get(gateway).Owner);
        Assert.True(w.Productions.Has(gateway));
        Assert.Equal(0, d.Events.Count(e => e.Event.Kind == SimEventKind.Damaged && e.Event.Entity == gateway));

        // It summons orcs, dwarves, ghosts and skeletons (MENU.INI [magic_gateway]) for their table cost.
        ushort ork = Id("comm.ork");
        Assert.True(w.TypeOf(gateway).Traits.Producer!.CanProduce(ork));
        Assert.True(w.TypeOf(gateway).Traits.Producer!.CanProduce(Id("comm.skeleton")));
        d.Order(1, CommandKind.Train, [gateway], arg0: ork, arg1: 1);
        d.RunUntil(() => d.Count(SimEventKind.UnitTrained) == 1, 600);
        Assert.Single(OfType(w, "comm.ork", 1));
    }

    [Fact]
    public void A_bear_fights_near_home_and_walks_back_when_lured_away()
    {
        using var d = new SimDriver(TestMap(64, 16, Unit("comm.bear", Nature, 20, 8), Unit("euro.footman", 0, 24, 8)), ruleset: Classic);
        World w = d.World;
        EntityId bear = OfType(w, "comm.bear").Single();
        EntityId footman = OfType(w, "euro.footman").Single();
        Assert.True(w.NatureHomes.Has(bear));
        Position home = new(w.NatureHomes.Get(bear).X, w.NatureHomes.Get(bear).Y);
        d.RunUntil(() => d.Events.Any(e => e.Event.Kind == SimEventKind.Damaged && e.Event.Entity == footman), 300);
        Assert.Contains(d.Events, e => e.Event.Kind == SimEventKind.Damaged && e.Event.Entity == footman);

        // Run far away: the bear gives up beyond 10 tiles (UNVERIFIED) and goes home.
        d.MoveToTile(0, [footman], 60, 8);
        bool returned = false;
        for (int t = 0; t < 1200 && !returned; t++)
        {
            d.Step();
            returned |= w.NatureHomes.Get(bear).Returning != 0;
        }

        Assert.True(returned);
        d.RunUntil(() => w.NatureHomes.Get(bear).Returning == 0, 800);
        Position at = d.PositionOf(bear);
        Assert.True(IntMath.DistanceSquared(at.X, at.Y, home.X, home.Y) <= (long)(3 * SimDriver.Tile) * (3 * SimDriver.Tile));
    }

    [Fact]
    public void Wild_cows_wander_and_join_the_kingdom_that_touches_them_and_birds_are_never_attacked()
    {
        using var d = new SimDriver(
            TestMap(48, 16, Unit("comm.cow", Nature, 20, 8), Unit("comm.bird", Nature, 30, 8), Unit("euro.footman", 0, 30, 9), Unit("euro.peon", 0, 5, 8)),
            ruleset: Classic);
        World w = d.World;
        EntityId cow = OfType(w, "comm.cow").Single();
        Position start = d.PositionOf(cow);
        bool wandered = false;
        for (int t = 0; t < 600 && !wandered; t++)
        {
            d.Step();
            wandered |= !d.PositionOf(cow).Equals(start);
        }

        Assert.True(wandered);
        Assert.Equal(0, d.Events.Count(e => e.Event.Kind == SimEventKind.Damaged)); // the footman ignores bird and cow

        EntityId peon = OfType(w, "euro.peon").Single();
        for (int round = 0; round < 10 && w.Identities.Get(cow).Owner == Nature; round++)
        {
            Position c = d.PositionOf(cow);
            d.Order(0, CommandKind.Move, [peon], c.X, c.Y);
            d.RunUntil(() => w.Identities.Get(cow).Owner != Nature, 200);
        }

        Assert.Equal(0, w.Identities.Get(cow).Owner);
        Assert.False(w.NatureHomes.Has(cow));
    }
}
