using System.Collections.Generic;
using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Tzar2.Sim.Kingdoms;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// GAME-13 (ADR 0102): asymmetric stances (enemy / neutral attacks only military units / ally), consented sharing of
/// control and resources, tribute, anti flip-flop, and gates that let only their owner and its allies through.
/// </summary>
public class DiplomacyTests
{
    private static int Damage(SimDriver d, EntityId target) => d.Events.Count(e => e.Event.Kind == SimEventKind.Damaged && e.Event.Entity == target);

    private static void Stance(SimDriver d, int from, int to, Stance stance, Sharing offers = Sharing.None)
        => d.Order(from, CommandKind.SetDiplomacy, [], x: to, arg0: (int)stance, arg1: (int)offers);

    [Theory]
    [InlineData(Kingdoms.Stance.Enemy, true, true)]
    [InlineData(Kingdoms.Stance.Neutral, false, true)]
    [InlineData(Kingdoms.Stance.Ally, false, false)]
    public void Stances_decide_what_military_units_attack(Stance stance, bool peasantHit, bool soldierHit)
    {
        // Player 0's footman between player 1's peasant and footman; only player 0's stance changes.
        using var d = new SimDriver(TestMap(48, 16, Unit("euro.footman", 0, 20, 8), Unit("euro.peon", 1, 22, 8), Unit("euro.footman", 1, 18, 4)), ruleset: Classic);
        EntityId peasant = OfType(d.World, "euro.peon", 1).Single();
        EntityId soldier = OfType(d.World, "euro.footman", 1).Single();
        Stance(d, 0, 1, stance);
        Stance(d, 1, 0, Kingdoms.Stance.Ally); // player 1 never strikes first
        for (int t = 0; t < 200; t++)
        {
            d.Step();
        }

        Assert.Equal(peasantHit, Damage(d, peasant) > 0);
        Assert.Equal(soldierHit, Damage(d, soldier) > 0);
    }

    [Fact]
    public void Stances_are_asymmetric_an_ally_does_not_strike_back_and_allies_cannot_be_attacked_on_order()
    {
        using var d = new SimDriver(TestMap(48, 16, Unit("euro.footman", 0, 20, 8), Unit("euro.footman", 1, 21, 8)), ruleset: Classic);
        EntityId mine = OfType(d.World, "euro.footman", 0).Single();
        EntityId theirs = OfType(d.World, "euro.footman", 1).Single();
        Stance(d, 0, 1, Kingdoms.Stance.Ally);
        d.Step();
        d.Order(0, CommandKind.Attack, [mine], target: theirs);
        for (int t = 0; t < 100; t++)
        {
            d.Step();
        }

        Assert.Contains(CommandRejection.NotAllied, d.Rejections());
        Assert.True(Damage(d, mine) > 0); // player 1 still treats player 0 as an enemy
        Assert.Equal(0, Damage(d, theirs)); // and player 0's footman does not answer (manual ch. 5 §8)
    }

    [Fact]
    public void Unit_control_and_resources_are_shared_only_when_both_allies_offer_them()
    {
        using var d = new SimDriver(TestMap(48, 16, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 20, 10)), ruleset: Classic);
        World w = d.World;
        EntityId peon = OfType(w, "euro.peon").Single();
        int food = Resource("food");
        d.Order(1, CommandKind.Move, [peon], SimDriver.TileCenter(30), SimDriver.TileCenter(10));
        Stance(d, 0, 1, Kingdoms.Stance.Ally, Sharing.UnitControl | Sharing.Resources);
        d.Step();
        Assert.Contains(CommandRejection.NoValidEntities, d.Rejections()); // not yet consented

        Stance(d, 1, 0, Kingdoms.Stance.Ally, Sharing.UnitControl | Sharing.Resources);
        d.Step();
        d.Order(1, CommandKind.Move, [peon], SimDriver.TileCenter(30), SimDriver.TileCenter(10));
        d.Order(1, CommandKind.Delete, [peon]); // control covers orders, never deleting or cancelling the ally's things
        d.Step();
        Assert.True(d.IsMoving(peon));
        Assert.True(w.Entities.IsAlive(peon));

        // Player 0 cannot afford a peasant (45 food) alone; the ally's stockpile pays the rest.
        w.Players[0].Resources[food] = 20;
        w.Players[1].Resources[food] = 100;
        d.Order(0, CommandKind.Train, [OfType(w, "euro.castle").Single()], arg0: Id("euro.peon"), arg1: 1);
        d.Step();
        Assert.Equal(0, w.Players[0].Resources[food]);
        Assert.Equal(75, w.Players[1].Resources[food]);
    }

    [Fact]
    public void Tribute_moves_resources_once_per_interval_and_never_overflows()
    {
        using var d = new SimDriver(TestMap(32, 16), players: 3, ruleset: Classic);
        World w = d.World;
        int gold = Resource("gold");
        w.Players[0].Resources[gold] = 500;
        w.Players[1].Resources[gold] = w.Ruleset.World.StockpileCap - 10;
        w.Players[2].Resources[gold] = 0;
        d.Order(0, CommandKind.Tribute, [], x: 2, arg0: gold, arg1: 100);
        d.Order(0, CommandKind.Tribute, [], x: 2, arg0: gold, arg1: 100); // same tick: too soon
        d.Step();
        Assert.Equal(400, w.Players[0].Resources[gold]);
        Assert.Equal(100, w.Players[2].Resources[gold]);
        Assert.Contains(CommandRejection.TributeBusy, d.Rejections());

        for (int t = 0; t < 20; t++)
        {
            d.Step();
        }

        d.Order(0, CommandKind.Tribute, [], x: 1, arg0: gold, arg1: 100);
        d.Order(2, CommandKind.Tribute, [], x: 2, arg0: gold, arg1: 1); // to itself
        d.Order(2, CommandKind.Tribute, [], x: 0, arg0: gold, arg1: -5);
        d.Order(2, CommandKind.Tribute, [], x: 0, arg0: gold, arg1: 1000); // more than it has (checked after the bad ones)
        d.Step();
        Assert.Equal(w.Ruleset.World.StockpileCap, w.Players[1].Resources[gold]); // capped, not wrapped
        Assert.Equal(300, w.Players[0].Resources[gold]);
        Assert.Equal(2, d.Rejections().Count(r => r == CommandRejection.InvalidArgument));
        Assert.Contains(CommandRejection.NotAffordable, d.Rejections());
        Assert.Equal(200, w.Kingdoms[0].TributeSent);
        Assert.Equal(100, w.Kingdoms[1].TributeReceived);
    }

    [Fact]
    public void Stance_changes_are_rate_limited_against_flip_flop_spam()
    {
        using var d = new SimDriver(TestMap(32, 16), ruleset: Classic);
        Stance(d, 0, 1, Kingdoms.Stance.Ally);
        d.Step();
        Stance(d, 0, 1, Kingdoms.Stance.Enemy);
        d.Step();
        Assert.Equal(Kingdoms.Stance.Ally, d.World.Diplomacy.StanceOf(0, 1));
        Assert.Contains(CommandRejection.DiplomacyBusy, d.Rejections());
        for (int t = 0; t < 200; t++)
        {
            d.Step();
        }

        Stance(d, 0, 1, Kingdoms.Stance.Enemy);
        d.Step();
        Assert.Equal(Kingdoms.Stance.Enemy, d.World.Diplomacy.StanceOf(0, 1));
        Assert.Equal(Kingdoms.Stance.Enemy, d.World.Diplomacy.StanceOf(1, 0));
        Assert.Equal(2, d.Count(SimEventKind.DiplomacyChanged));
    }

    [Fact]
    public void Lobby_teams_start_allied_with_shared_vision_and_buildings_of_non_enemies_cannot_be_captured()
    {
        using var d = new SimDriver(
            TestMap(48, 16, Structure("euro.house", 1, 20, 4), Unit("euro.peon", 0, 17, 5)),
            players: 3,
            ruleset: Classic,
            configure: c => c with { Teams = 0x011 });
        Diplomacy diplomacy = d.World.Diplomacy;
        Assert.True(diplomacy.AreMutualAllies(0, 1));
        Assert.True(diplomacy.Shares(0, 1, Sharing.Vision | Sharing.Victory));
        Assert.Equal(Kingdoms.Stance.Enemy, diplomacy.StanceOf(0, 2));
        d.Order(0, CommandKind.Capture, [OfType(d.World, "euro.peon").Single()], target: OfType(d.World, "euro.house").Single());
        d.Step();
        Assert.Contains(CommandRejection.NotAllied, d.Rejections());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_open_gate_lets_its_owner_and_allies_through_but_never_enemies(bool allied)
    {
        // A wall across the map with player 0's gate in it; player 0's peasant keeps the automatic gate open.
        var objects = new List<Content.Compiled.MapObject>();
        for (int x = 0; x < 48; x++)
        {
            if (x < 22 || x > 24)
            {
                objects.Add(Structure("comm.wall_wood", 0, x, 8));
            }
        }

        objects.Add(Structure("comm.gate_wood", 0, 22, 8));
        objects.Add(Unit("euro.peon", 0, 25, 9));
        objects.Add(Unit("euro.peon", 1, 23, 13));
        using var d = new SimDriver(TestMap(48, 16, [.. objects]), ruleset: Classic);
        World w = d.World;
        EntityId gate = OfType(w, "comm.gate_wood").Single();
        EntityId intruder = OfType(w, "euro.peon", 1).Single();
        if (allied)
        {
            Stance(d, 0, 1, Kingdoms.Stance.Ally);
        }

        d.Step();
        d.RunUntil(() => w.Gates.Get(gate).Open != 0, 100);
        Assert.Equal(1, w.Gates.Get(gate).Open);
        d.MoveToTile(1, [intruder], 23, 3);
        d.RunUntil(() => d.PositionOf(intruder).Y < 8 * SimDriver.Tile, 600);
        Assert.Equal(allied, d.PositionOf(intruder).Y < 8 * SimDriver.Tile);
        Assert.Equal(1, w.Gates.Get(gate).Open);
    }
}
