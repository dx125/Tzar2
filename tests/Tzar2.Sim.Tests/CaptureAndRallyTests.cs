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
    [Trait("Rule", "CAP-005")]
    public void Peasants_dig_to_capture_an_enemy_house_take_it_over_and_mend_it_whole()
    {
        using var d = new SimDriver(
            TestMap(48, 16, Structure("euro.castle", 1, 40, 2), Structure("euro.house", 1, 14, 4), Structure("euro.barrack", 1, 24, 10), Unit("euro.peon", 0, 12, 4), Unit("euro.peon", 0, 12, 5)),
            ruleset: Classic);
        EntityId house = OfType(d.World, "euro.house").Single();
        EntityId[] peons = [.. OfType(d.World, "euro.peon")];

        // RESISTANCE 40, no EMIT round it. Each 1196 ms cycle of a peasant takes 5, the house gets 10 back every 2 s, and it
        // goes at the cycle that finds no more than 5 (ReTzar's reading of the original, 0x4dfe52, 0x505040, 0x41f271):
        // two peasants take 16.7 every 2 s against the 10, so it falls in about 12 s.
        Assert.Equal(40, CaptureSystem.CaptureResistance(d.World, house));
        d.Order(0, CommandKind.Capture, peons, target: house);
        d.RunUntil(() => peons.All(p => d.World.Workers.Get(p).Phase == WorkerPhase.Capturing), 200);
        int start = d.World.Tick.Value;
        d.RunUntil(() => d.Count(SimEventKind.Captured) == 1, 600);
        Assert.Equal(0, d.World.Identities.Get(house).Owner);
        Assert.InRange(d.World.Tick.Value - start, 8 * 1196 / 50, 16 * 1196 / 50);
        Assert.Equal(0, CaptureSystem.CaptureResistance(d.World, house));

        // They go on, giving 5 a cycle back until it is whole, then stop (0x41fab0).
        d.RunUntil(() => peons.All(p => d.World.Workers.Get(p).Task == WorkerTask.None), 400);
        Assert.False(d.World.CaptureStates.Has(house));
        Assert.Equal(40, CaptureSystem.CaptureResistance(d.World, house));
        Assert.Equal(12, d.World.Players[0].PopulationCap); // the house now houses player 0
    }

    [Fact]
    [Trait("Rule", "CAP-002")]
    public void The_emit_of_buildings_round_a_building_makes_it_harder_to_capture_for_its_allies_and_easier_for_others()
    {
        using var d = new SimDriver(
            TestMap(64, 16, Structure("euro.castle", 1, 2, 2), Structure("euro.house", 1, 9, 4), Structure("euro.house", 1, 30, 4), Structure("euro.castle", 0, 50, 2), Structure("euro.house", 1, 46, 4)),
            ruleset: Classic);
        EntityId[] houses = [.. OfType(d.World, "euro.house").OrderBy(h => d.World.Positions.Get(h).X)];
        int nearOwn = CaptureSystem.ResistanceTarget(d.World, houses[0]);
        int alone = CaptureSystem.ResistanceTarget(d.World, houses[1]);
        int nearEnemy = CaptureSystem.ResistanceTarget(d.World, houses[2]);
        Assert.Equal(40, alone);
        Assert.True(nearOwn > alone, $"{nearOwn}"); // its own castle's EMIT (own) within the castle's sight
        Assert.True(nearEnemy < alone, $"{nearEnemy}"); // the enemy castle's EMIT (other)
        Assert.Equal(Math.Max(0, nearEnemy), CaptureSystem.CaptureResistance(d.World, houses[2]));

        // Fanaticism raises its owner's resistance.
        Ruleset fanatic = ClassicWith("te_fanaticism: { Technology: { effects: [{ stat: resistance, percent: 100, category: building }] } }");
        using var f = new SimDriver(TestMap(64, 16, Structure("euro.house", 1, 30, 4)), ruleset: fanatic);
        ResearchTests.Grant(f, 1, fanatic, "te_fanaticism");
        Assert.Equal(80, CaptureSystem.CaptureResistance(f.World, OfType(f.World, "euro.house").Single()));
    }

    [Fact]
    [Trait("Rule", "CAP-006")]
    [Trait("Rule", "CAP-001")]
    public void A_lowered_resistance_comes_back_10_every_2_s_so_one_peasant_alone_never_takes_a_house()
    {
        using var d = new SimDriver(
            TestMap(48, 16, Structure("euro.castle", 1, 2, 2), Structure("euro.house", 1, 20, 4), Structure("euro.house", 1, 30, 10), Structure("comm.field", 1, 30, 4), Unit("euro.peon", 0, 18, 4)),
            ruleset: Classic);
        EntityId house = OfType(d.World, "euro.house").Single(h => d.PositionOf(h).Y < 8 * Tile);
        EntityId lowered = OfType(d.World, "euro.house").Single(h => d.PositionOf(h).Y > 8 * Tile);
        EntityId peon = OfType(d.World, "euro.peon").Single();

        // 10 back every 2 s (0x41f271): from 10 to 40 in three pulses.
        d.World.CaptureStates.Set(lowered, new CaptureState { Resistance = 10 });
        int start = d.World.Tick.Value;
        d.RunUntil(() => !d.World.CaptureStates.Has(lowered), 400);
        Assert.InRange(d.World.Tick.Value - start, 81, 120);
        Assert.Equal(40, CaptureSystem.CaptureResistance(d.World, lowered));

        // One peasant takes 8.4 every 2 s against those 10: the house stands (as ReTzar's own test says).
        d.Order(0, CommandKind.Capture, [peon], target: house);
        d.RunUntil(() => false, 1200);
        Assert.Equal(1, d.World.Identities.Get(house).Owner);
        Assert.True(CaptureSystem.CaptureResistance(d.World, house) > 25);

        d.Order(0, CommandKind.Capture, [peon], target: OfType(d.World, "comm.field").Single());
        d.Step();
        Assert.Equal(CommandRejection.InvalidArgument, d.Rejections().Last());
    }

    [Fact]
    [Trait("Rule", "CAP-004")]
    public void Towers_are_taken_from_any_kingdom_not_an_ally_and_a_friendly_building_is_only_mended()
    {
        using var d = new SimDriver(
            TestMap(48, 16, Structure("euro.castle", 1, 2, 2), Structure("comm.tower_wood", 1, 20, 4), Structure("euro.house", 1, 30, 4), Structure("euro.house", 0, 36, 4), Unit("euro.peon", 0, 18, 4), Unit("euro.peon", 0, 34, 8)),
            players: 2,
            ruleset: Classic,
            configure: c => c);
        d.Order(0, CommandKind.SetDiplomacy, [], x: 1, arg0: (int)Kingdoms.Stance.Neutral);
        d.Order(1, CommandKind.SetDiplomacy, [], x: 0, arg0: (int)Kingdoms.Stance.Neutral);
        d.Step();
        EntityId tower = OfType(d.World, "comm.tower_wood").Single();
        EntityId theirs = OfType(d.World, "euro.house", 1).Single();
        EntityId mine = OfType(d.World, "euro.house", 0).Single();
        EntityId[] peons = [.. OfType(d.World, "euro.peon").OrderBy(p => d.PositionOf(p).X)];
        Assert.True(CaptureSystem.MayCapture(d.World, 0, tower)); // a tower of a kingdom that is not an ally
        Assert.False(CaptureSystem.MayCapture(d.World, 0, theirs)); // a neutral's house is not
        Assert.False(CaptureSystem.MayCapture(d.World, 0, mine)); // its own, whole
        d.World.CaptureStates.Set(mine, new CaptureState { Resistance = 10 });
        Assert.True(CaptureSystem.MayCapture(d.World, 0, mine)); // lowered: to mend
        d.Order(0, CommandKind.Capture, [peons[1]], target: mine);
        d.Order(0, CommandKind.Capture, [peons[0]], target: tower);
        d.RunUntil(() => !d.World.CaptureStates.Has(mine) && d.World.Workers.Get(peons[1]).Task == WorkerTask.None, 600);
        Assert.Equal(0, d.World.Identities.Get(mine).Owner);
        d.RunUntil(() => d.World.Identities.Get(tower).Owner == 0, 1200);
    }

    [Fact]
    [Trait("Rule", "MAG-038")]
    public void A_bribe_costs_the_level_plus_20_times_ten_quarters_in_gold_whatever_the_unit()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.footman", 1, 10, 6), Unit("euro.knight", 1, 14, 6)), players: 2, ruleset: Classic);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId knight = OfType(d.World, "euro.knight").Single();
        Assert.Equal(50, CombatMagicCommands.BribeCost(d.World, footman)); // (0 + 20) x 10 / 4 (0x4ac62f)
        Assert.Equal(50, CombatMagicCommands.BribeCost(d.World, knight));
        d.World.Experiences.Get(knight).Level = 7;
        Assert.Equal(67, CombatMagicCommands.BribeCost(d.World, knight)); // 27 x 10 / 4
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
    public void Supply_storage_raises_the_population_limit_by_a_quarter()
    {
        // ReTzar's reading of the original (0x4d3819): each of Supply Storage and Seafood adds a quarter of the limit.
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Structure("euro.house", 0, 12, 4), Structure("euro.house", 0, 16, 4)), ruleset: Classic, configure: c => c with { PopulationLimit = 40 });
        d.Step();
        Assert.Equal(40, d.World.Players[0].PopulationCap); // the houses give 48, the limit holds at 40
        ResearchTests.Grant(d, 0, Classic, "te_supplystorage");
        d.Step();
        Assert.Equal(48, d.World.Players[0].PopulationCap); // the limit is 50 now
    }
}
