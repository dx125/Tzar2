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
/// The 2026-11-07 audit's war and score rules (ADR 0280): the pikeman's counter-blow, the peasant that fights back or
/// runs, and the original's score.
/// </summary>
public class AuditWarTests
{
    private static int Hp(SimDriver d, EntityId e) => d.World.Healths.Get(e).Current;

    [Fact]
    [Trait("Rule", "WAR-021")]
    public void A_pikeman_left_standing_by_a_hand_to_hand_blow_strikes_back_at_once_but_not_at_a_pikeman_or_an_arrow()
    {
        using var d = new SimDriver(
            TestMap(32, 12, Unit("euro.footman", 0, 4, 2), Unit("euro.pikeman", 1, 5, 2), Unit("euro.pikeman", 0, 4, 6), Unit("euro.pikeman", 1, 5, 6), Unit("euro.archer", 0, 4, 10), Unit("euro.pikeman", 1, 8, 10)),
            ruleset: Classic);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId archer = OfType(d.World, "euro.archer").Single();
        EntityId[] enemies = [.. OfType(d.World, "euro.pikeman", 1).OrderBy(p => d.PositionOf(p).Y)];
        EntityId ownPikeman = OfType(d.World, "euro.pikeman", 0).Single();
        d.Order(1, CommandKind.SetStance, enemies, arg0: (int)CombatStance.HoldFire);
        d.Order(0, CommandKind.Attack, [footman], target: enemies[0]);
        d.Order(0, CommandKind.Attack, [ownPikeman], target: enemies[1]);
        d.Order(0, CommandKind.Attack, [archer], target: enemies[2]);
        int footmanHp = Hp(d, footman), pikemanHp = Hp(d, ownPikeman), archerHp = Hp(d, archer);
        d.RunUntil(() => Hp(d, enemies[0]) < d.World.Healths.Get(enemies[0]).Max && Hp(d, enemies[2]) < d.World.Healths.Get(enemies[2]).Max, 200);
        Assert.True(Hp(d, footman) < footmanHp); // struck back the moment the blow landed, though on hold fire
        d.RunUntil(() => Hp(d, enemies[1]) < d.World.Healths.Get(enemies[1]).Max, 200);
        Assert.Equal(pikemanHp, Hp(d, ownPikeman)); // a pikeman's blow is not answered
        Assert.Equal(archerHp, Hp(d, archer)); // nor an arrow
    }

    [Fact]
    [Trait("Rule", "WAR-022")]
    public void A_peasant_hit_at_work_runs_from_a_stronger_attacker_rests_and_goes_back_to_its_tree()
    {
        using var d = new SimDriver(
            TestMap(40, 16, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 20, 8), Tree(21, 8), Unit("euro.footman", 1, 24, 8)),
            ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        d.Order(1, CommandKind.SetStance, [footman], arg0: (int)CombatStance.HoldFire);
        d.Order(0, CommandKind.Harvest, [peon], target: OfType(d.World, "comm.tree").Single());
        d.RunUntil(() => d.World.Workers.Get(peon).Phase == WorkerPhase.Gathering, 200);
        d.Order(1, CommandKind.Attack, [footman], target: peon);
        d.RunUntil(() => Hp(d, peon) < d.World.Healths.Get(peon).Max, 200);
        d.Order(1, CommandKind.Stop, [footman]);
        d.Step();
        Assert.Equal(CombatMode.Flee, d.World.Combats.Get(peon).Mode);
        Assert.Equal(WorkerTask.Harvest, d.World.Workers.Get(peon).Task); // the work waits
        Assert.Equal(WorkerPhase.ToNode, d.World.Workers.Get(peon).Phase); // the load half cut is lost
        d.RunUntil(() => d.World.Combats.Get(peon).Mode == CombatMode.Flee && d.World.Combats.Get(peon).ChaseTick >= 0, 200);
        Position rest = d.PositionOf(peon);
        Assert.Equal(15, rest.X / Tile); // five tiles straight away from the footman (0x430be0)
        int rested = d.World.Tick.Value;
        d.RunUntil(() => d.World.Combats.Get(peon).Mode == CombatMode.None, 200);
        Assert.InRange(d.World.Tick.Value - rested, 59, 61); // 3 s of rest
        d.RunUntil(() => d.World.Workers.Get(peon).Phase == WorkerPhase.Gathering, 400); // back at its tree
    }

    [Fact]
    [Trait("Rule", "WAR-022")]
    public void A_peasant_fights_back_an_attacker_no_stronger_than_itself()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.peon", 0, 10, 6), Unit("euro.peon", 1, 11, 6)), ruleset: Classic);
        EntityId mine = OfType(d.World, "euro.peon", 0).Single();
        EntityId theirs = OfType(d.World, "euro.peon", 1).Single();
        Assert.Equal(CombatSystem.Strength(d.World, mine), CombatSystem.Strength(d.World, theirs));
        d.Order(1, CommandKind.Attack, [theirs], target: mine);
        d.RunUntil(() => Hp(d, mine) < d.World.Healths.Get(mine).Max, 200);
        d.Step();
        Assert.Equal(theirs.Value, d.World.Combats.Get(mine).Target);
        Assert.Equal(CombatMode.Auto, d.World.Combats.Get(mine).Mode);
        d.RunUntil(() => Hp(d, theirs) < d.World.Healths.Get(theirs).Max, 200);
    }

    [Fact]
    [Trait("Rule", "DIP-011")]
    public void A_kingdom_scores_for_what_it_kills_and_for_the_gold_it_takes_from_a_mine()
    {
        using var d = new SimDriver(
            TestMap(40, 20, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 14, 6), Structure("comm.res_gold", MapObject.Nature, 18, 6), Unit("euro.footman", 0, 29, 14), Unit("euro.footman", 1, 30, 14), Structure("euro.house", 1, 30, 16)),
            ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        EntityId soldier = OfType(d.World, "euro.footman", 0).Single();
        EntityId enemy = OfType(d.World, "euro.footman", 1).Single();
        EntityId house = OfType(d.World, "euro.house", 1).Single();
        d.Order(0, CommandKind.SetStance, [soldier], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [enemy], arg0: (int)CombatStance.HoldFire);
        d.Order(0, CommandKind.Harvest, [peon], target: OfType(d.World, "comm.res_gold").Single());
        d.RunUntil(() => d.World.Workers.Get(peon).CarryAmount > 0, 600);
        int load = d.World.Workers.Get(peon).CarryAmount;
        Assert.Equal(load, d.World.Kingdoms[0].Score); // as the load is taken, before it is delivered
        d.World.Healths.Get(enemy).Current = 1;
        d.World.Healths.Get(house).Current = 1;
        d.Order(0, CommandKind.Attack, [soldier], target: enemy);
        d.Order(0, CommandKind.Attack, [soldier], target: house, flags: CommandFlags.Queue);
        d.RunUntil(() => d.Count(SimEventKind.Killed) == 2, 600);
        Assert.Equal(load + 200 + 500, d.World.Kingdoms[0].Score); // 200 for the footman, 500 for the house (0x504cbe)
        Assert.Equal((load + 700 + 50) / 100, VictorySystem.Score(d.World.Kingdoms[0]));
    }
}
