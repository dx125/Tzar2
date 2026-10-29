using System;
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
    [Trait("Rule", "NAT-001")]
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
    [Trait("Rule", "NAT-003")]
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
    [Trait("Rule", "NAT-002")]
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
        Assert.NotEqual(MotionState.Moving, w.Motions.Get(cow).State); // a claimed cow stops its walk (play-test 2026-10-20)
        d.RunUntil(() => !d.IsMoving(cow), 40); // it ends the step it began onto its tile (ADR 0270)
        Position claimedAt = d.PositionOf(cow);
        d.Order(0, CommandKind.Stop, [peon]);
        d.RunUntil(() => false, 400);
        Assert.Equal(claimedAt, d.PositionOf(cow)); // it grazes there a while (NAT-007)
    }

    [Fact]
    [Trait("Rule", "NAT-007")]
    public void A_cow_grazes_and_walks_only_near_where_it_was_sent_and_stands_while_it_is_milked()
    {
        using var d = new SimDriver(
            TestMap(48, 32, Unit("comm.cow", 0, 10, 10), Unit("comm.cow", 0, 30, 10), Unit("euro.peon", 0, 30, 12), Structure("euro.castle", 0, 36, 20)),
            ruleset: Classic);
        World w = d.World;
        EntityId[] cows = [.. OfType(w, "comm.cow").OrderBy(c => d.PositionOf(c).X)];
        (EntityId free, EntityId milked) = (cows[0], cows[1]);
        d.Order(0, CommandKind.Harvest, [OfType(w, "euro.peon").Single()], target: milked);
        Position home = d.PositionOf(free);
        Position milkedAt = d.PositionOf(milked);

        // Five minutes: the free cow walks now and then, never more than two tiles from its home; the milked one stands.
        bool walked = false;
        for (int t = 0; t < 6000; t++)
        {
            d.Step();
            Position p = d.PositionOf(free);
            walked |= !p.Equals(home);
            Assert.True(Math.Abs(p.X - home.X) <= 2 * SimDriver.Tile && Math.Abs(p.Y - home.Y) <= 2 * SimDriver.Tile, $"tick {t}: {p}");
            Assert.Equal(milkedAt, d.PositionOf(milked));
        }

        Assert.True(walked);

        // An order moves its home: it grazes where it was sent.
        d.MoveToTile(0, [free], 20, 20);
        d.RunUntil(() => !d.IsMoving(free) && w.Tick.Value > 6010, 600);
        Position sent = d.PositionOf(free);
        Assert.Equal((20, 20), (sent.X / SimDriver.Tile, sent.Y / SimDriver.Tile));
        for (int t = 0; t < 3000; t++)
        {
            d.Step();
            Position p = d.PositionOf(free);
            Assert.True(Math.Abs(p.X - sent.X) <= 2 * SimDriver.Tile && Math.Abs(p.Y - sent.Y) <= 2 * SimDriver.Tile, $"tick {t}: {p}");
        }
    }

    [Fact]
    [Trait("Rule", "NAT-007")]
    public void Cows_by_a_busy_farm_stay_by_it()
    {
        // The sixth play-test: cows walked off far from the farms. Peasants milking them and carrying the milk home push
        // them aside; a cow that could not get back to its tile used to stay where it was pushed, and drifted away.
        var objects = new System.Collections.Generic.List<Content.Compiled.MapObject> { Structure("euro.castle", 0, 20, 20), Structure("euro.farm", 0, 34, 20) };
        for (int i = 0; i < 8; i++)
        {
            objects.Add(Unit("comm.cow", 0, 28 + (i % 4), 28 + (i / 4)));
        }

        for (int i = 0; i < 16; i++)
        {
            objects.Add(Unit("euro.peon", 0, 22 + (i % 8), 32 + (i / 8)));
        }

        using var d = new SimDriver(TestMap(64, 64, [.. objects]), ruleset: Classic);
        World w = d.World;
        EntityId[] cows = [.. OfType(w, "comm.cow")];
        EntityId[] peons = [.. OfType(w, "euro.peon")];
        var start = cows.ToDictionary(c => c, d.PositionOf);
        for (int i = 0; i < peons.Length; i++)
        {
            d.Order(0, CommandKind.Harvest, [peons[i]], target: cows[i % 4]); // half the cows milked, half not
        }

        d.RunUntil(() => false, 12_000); // ten minutes
        foreach (EntityId c in cows)
        {
            // The tile it stands on or steps into: a cow that makes way for a peasant may be in the middle of a step.
            int width = w.Map.Width;
            int to = w.CellSteps.IndexOf(c) is >= 0 and var s && w.CellSteps.At(s).To >= 0 ? w.CellSteps.At(s).To : -1;
            (int X, int Y) tile = to >= 0 ? (to % width, to / width) : (d.PositionOf(c).X / SimDriver.Tile, d.PositionOf(c).Y / SimDriver.Tile);
            (int X, int Y) first = (start[c].X / SimDriver.Tile, start[c].Y / SimDriver.Tile);
            (int X, int Y) home = (w.NatureHomes.Get(c).X / SimDriver.Tile, w.NatureHomes.Get(c).Y / SimDriver.Tile);
            Assert.True(Math.Max(Math.Abs(home.X - first.X), Math.Abs(home.Y - first.Y)) <= 2, $"home {first} -> {home}"); // no drift
            // Two tiles of its grazing, and up to three steps aside for a peasant passing by (WORLD-020).
            Assert.True(Math.Max(Math.Abs(tile.X - home.X), Math.Abs(tile.Y - home.Y)) <= 5, $"{home} -> {tile}");
        }
    }
}
