using System;
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
/// GAME-04 (ADR 0044): capture by peasants, resistance and EMIT influence, Fanaticism, protected deletes. GAME-05:
/// rally modes, population techs, queues of research and units together.
/// </summary>
public class CaptureAndRallyTests
{
    [Fact]
    [Trait("Rule", "CAP-001")]
    public void Peasants_dig_to_capture_an_enemy_house_and_take_it_over()
    {
        using var d = new SimDriver(
            TestMap(48, 16, Structure("euro.castle", 1, 40, 2), Structure("euro.house", 1, 14, 4), Structure("euro.barrack", 1, 24, 10), Unit("euro.peon", 0, 12, 4), Unit("euro.peon", 0, 12, 5)),
            ruleset: Classic);
        EntityId house = OfType(d.World, "euro.house").Single();
        EntityId[] peons = [.. OfType(d.World, "euro.peon")];

        // RESISTANCE 40; the castle is 28 tiles away (beyond the influence radius of 16, UNVERIFIED).
        Assert.Equal(40, CaptureSystem.CaptureResistance(d.World, house));
        d.Order(0, CommandKind.Capture, peons, target: house);
        int ticks = d.RunUntil(() => d.Count(SimEventKind.Captured) == 1, 2000);
        Assert.Equal(0, d.World.Identities.Get(house).Owner);
        // Two peasants at 10 points per second each (UNVERIFIED): 2 s of digging after the walk.
        Assert.InRange(ticks, 40, 80);
        Assert.False(d.World.CaptureStates.Has(house));
        d.Step();
        Assert.All(peons, p => Assert.Equal(WorkerTask.None, d.World.Workers.Get(p).Task));
        Assert.Equal(12, d.World.Players[0].PopulationCap); // the house now houses player 0
    }

    [Fact]
    [Trait("Rule", "CAP-002")]
    public void Influence_makes_buildings_near_their_town_harder_to_capture_and_near_the_enemy_easier()
    {
        using var d = new SimDriver(
            TestMap(64, 16, Structure("euro.castle", 1, 2, 2), Structure("euro.house", 1, 9, 4), Structure("euro.house", 1, 30, 4), Structure("euro.castle", 0, 50, 2), Structure("euro.house", 1, 46, 4)),
            ruleset: Classic);
        EntityId[] houses = [.. OfType(d.World, "euro.house").OrderBy(h => d.World.Positions.Get(h).X)];
        int nearOwn = CaptureSystem.CaptureResistance(d.World, houses[0]);
        int alone = CaptureSystem.CaptureResistance(d.World, houses[1]);
        int nearEnemy = CaptureSystem.CaptureResistance(d.World, houses[2]);
        Assert.Equal(40, alone);
        Assert.True(nearOwn > alone, $"{nearOwn}"); // castle EMIT +300 (synthetic) fading with distance
        Assert.True(nearEnemy < alone, $"{nearEnemy}"); // enemy castle EMIT -150

        // Fanaticism raises its owner's resistance (+20%, UNVERIFIED).
        Ruleset fanatic = ClassicWith("te_fanaticism: { Technology: { effects: [{ stat: resistance, percent: 100, category: building }] } }");
        using var f = new SimDriver(TestMap(64, 16, Structure("euro.house", 1, 30, 4)), ruleset: fanatic);
        ResearchTests.Grant(f, 1, fanatic, "te_fanaticism");
        Assert.Equal(80, CaptureSystem.CaptureResistance(f.World, OfType(f.World, "euro.house").Single()));
    }

    [Fact]
    [Trait("Rule", "CAP-001")]
    public void Capture_progress_is_lost_when_the_diggers_stop_and_sites_and_fields_cannot_be_captured()
    {
        using var d = new SimDriver(
            TestMap(48, 16, Structure("euro.castle", 1, 2, 2), Structure("euro.house", 1, 20, 4), Structure("comm.field", 1, 30, 4), Unit("euro.peon", 0, 18, 4)),
            ruleset: Classic);
        EntityId house = OfType(d.World, "euro.house").Single();
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Capture, [peon], target: house);
        d.RunUntil(() => d.World.CaptureStates.Has(house), 400);
        d.Order(0, CommandKind.Stop, [peon]);
        d.Step();
        Assert.True(d.World.CaptureStates.Get(house).MilliPoints > 0);
        d.RunUntil(() => !d.World.CaptureStates.Has(house), 200);
        Assert.False(d.World.CaptureStates.Has(house)); // reset after 5 s (UNVERIFIED)
        Assert.Equal(1, d.World.Identities.Get(house).Owner);

        d.Order(0, CommandKind.Capture, [peon], target: OfType(d.World, "comm.field").Single());
        d.Step();
        Assert.Equal(CommandRejection.InvalidArgument, d.Rejections().Last());
    }

    [Fact]
    [Trait("Rule", "ORD-018")]
    public void The_castle_is_deleted_only_with_shift()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Structure("euro.house", 0, 12, 4)), ruleset: Classic);
        EntityId castle = OfType(d.World, "euro.castle").Single();
        d.Order(0, CommandKind.Delete, [castle]);
        d.Step();
        Assert.Equal(CommandRejection.ShiftDeleteRequired, d.Rejections().Last());
        Assert.True(d.World.Entities.IsAlive(castle));
        d.Order(0, CommandKind.Delete, [OfType(d.World, "euro.house").Single()]);
        d.Order(0, CommandKind.Delete, [castle], flags: CommandFlags.Queue);
        d.Step();
        Assert.False(d.World.Entities.IsAlive(castle));
        Assert.Empty(OfType(d.World, "euro.house"));
    }

    [Fact]
    [Trait("Rule", "PROD-011")]
    public void Rally_with_default_action_sends_new_peasants_to_harvest_and_move_advance_to_fight()
    {
        using var d = new SimDriver(TestMap(40, 16, Structure("euro.castle", 0, 2, 2), Structure("euro.barrack", 0, 2, 10), Tree(16, 4), Tree(16, 5)), ruleset: Classic);
        EntityId castle = OfType(d.World, "euro.castle").Single();
        EntityId barracks = OfType(d.World, "euro.barrack").Single();
        EntityId tree = OfType(d.World, "comm.tree").First();
        Position at = d.World.Positions.Get(tree);
        d.Order(0, CommandKind.SetRally, [castle], at.X, at.Y, target: tree, arg1: (int)RallyMode.DefaultAction);
        d.Order(0, CommandKind.SetRally, [barracks], 30 * Tile, 12 * Tile, arg1: (int)RallyMode.MoveAdvance);
        d.Order(0, CommandKind.Train, [castle], arg0: Id("euro.peon"), arg1: 1);
        d.Order(0, CommandKind.Train, [barracks], arg0: Id("euro.footman"), arg1: 1);
        d.RunUntil(() => d.Count(SimEventKind.UnitTrained) == 2, 1000);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        Assert.Equal((WorkerTask.Harvest, tree.Value), (d.World.Workers.Get(peon).Task, d.World.Workers.Get(peon).Target));
        Assert.Equal(CombatMode.AttackMove, d.World.Combats.Get(footman).Mode);
        Assert.True(d.IsMoving(footman));
        Assert.Equal(RallyMode.DefaultAction, d.World.Productions.Get(castle).RallyMode);
    }

    [Fact]
    [Trait("Rule", "PROD-007")]
    [Trait("Rule", "TECH-022")]
    public void Supply_storage_raises_the_population_of_houses()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Structure("euro.house", 0, 12, 4), Structure("euro.house", 0, 16, 4)), ruleset: Classic);
        d.Step();
        Assert.Equal(24 + 24, d.World.Players[0].PopulationCap);
        ResearchTests.Grant(d, 0, Classic, "te_supplystorage");
        d.Step();
        Assert.Equal(24 + 24 + 4, d.World.Players[0].PopulationCap); // +2 per house (UNVERIFIED)
    }
}
