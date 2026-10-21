using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Xunit;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>GAME-07 (ADR 0091): projectiles, splash, minimum range, damage rolls, stances, attack ground, holder shares.</summary>
public class CombatM3eTests
{
    [Fact]
    public void The_killed_event_names_the_killers_owner()
    {
        using var d = new SimDriver(TestMap(16, 8, Unit("euro.footman", 1, 4, 4), Unit("euro.peon", 0, 5, 4)), ruleset: Classic);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(1, CommandKind.Attack, [footman], target: peon);
        d.RunUntil(() => !d.World.Entities.IsAlive(peon), 400);
        SimEvent killed = Of(d, SimEventKind.Killed).Single();
        Assert.Equal(footman.Index, killed.X);
        Assert.Equal(1, killed.Y); // the client counts kills by this (backlog: no inference by index)
        Assert.Equal(0, killed.Player);
    }

    [Fact]
    [Trait("Rule", "WAR-003")]
    public void Arrows_fly_and_hit_after_their_flight_time_with_the_same_damage()
    {
        // archer (7) vs footman (defence 2), arrow_unit vs footman -25%: 7 x 0.75 = 5 - 2 = 3 (CombatTests); the arrow
        // flies 400 px/s over about 4 tiles (~6 ticks).
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.archer", 0, 4, 4), Unit("euro.footman", 1, 8, 4)), ruleset: Classic);
        EntityId archer = OfType(d.World, "euro.archer").Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        d.Order(0, CommandKind.Attack, [archer], target: footman);
        d.RunUntil(() => d.Count(SimEventKind.Damaged) > 0, 400);
        int launched = TimedOf(d, SimEventKind.ProjectileLaunched).First().Tick;
        (int hitTick, SimEvent hit) = TimedOf(d, SimEventKind.Damaged).First();
        Assert.InRange(hitTick - launched, 3, 12);
        Assert.Equal(3, hit.Arg);
        Assert.Equal(archer.Index, hit.X);
    }

    [Fact]
    public void An_arrow_is_lost_when_its_target_dies_in_flight()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.archer", 0, 2, 4), Unit("euro.peon", 1, 9, 4)), ruleset: Classic);
        EntityId archer = OfType(d.World, "euro.archer").Single();
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Attack, [archer], target: peon);
        d.RunUntil(() => d.World.Projectiles.Count > 0, 400);
        d.Order(1, CommandKind.DebugKill, [peon]);
        d.Step();
        Assert.False(d.World.Entities.IsAlive(peon));
        d.RunUntil(() => d.World.Projectiles.Count == 0, 40);
        Assert.Equal(0, d.World.Projectiles.Count);
        Assert.Empty(Of(d, SimEventKind.Damaged));
    }

    [Fact]
    [Trait("Rule", "WAR-004")]
    public void Catapult_stones_land_where_the_target_stood_and_splash_its_neighbours()
    {
        // Catapult attack 11 - footman defence 2 = 9; the neighbour 1/4 tile away takes the 50% splash: 4.
        using var d = new SimDriver(TestMap(40, 20, Unit("comm.catapult", 0, 2, 10), Unit("euro.footman", 1, 14, 10), Unit("euro.footman", 1, 14, 11) with { Y = (10 * Tile) + 1500 }), ruleset: Classic);
        EntityId catapult = OfType(d.World, "comm.catapult").Single();
        EntityId[] footmen = [.. OfType(d.World, "euro.footman")];
        d.Order(0, CommandKind.Attack, [catapult], target: footmen[0]);
        d.RunUntil(() => Of(d, SimEventKind.Damaged).Any(e => e.X == catapult.Index), 600);
        SimEvent[] hits = [.. Of(d, SimEventKind.Damaged).Where(e => e.X == catapult.Index)];
        Assert.Contains(hits, h => h.Entity == footmen[0] && h.Arg == 9);
        Assert.Contains(hits, h => h.Entity == footmen[1] && h.Arg == 4);
    }

    [Fact]
    [Trait("Rule", "WAR-004")]
    public void A_unit_that_steps_away_from_a_falling_stone_is_not_hit()
    {
        // Strategy guide: peasants step away "just as the catapult projectiles are about to land".
        using var d = new SimDriver(TestMap(40, 20, Unit("comm.catapult", 0, 2, 10), Unit("euro.footman", 1, 16, 10)), ruleset: Classic);
        EntityId catapult = OfType(d.World, "comm.catapult").Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        d.Order(1, CommandKind.Stop, [footman]);
        d.Order(0, CommandKind.Attack, [catapult], target: footman);
        d.RunUntil(() => d.World.Projectiles.Count > 0, 600);
        d.MoveToTile(1, [footman], 16, 18);
        d.RunUntil(() => d.World.Projectiles.Count == 0, 200);
        Assert.DoesNotContain(Of(d, SimEventKind.Damaged), e => e.X == catapult.Index);
    }

    [Fact]
    public void A_catapult_cannot_hit_inside_its_minimum_range()
    {
        using var d = new SimDriver(TestMap(24, 12, Unit("comm.catapult", 0, 4, 4), Unit("euro.footman", 1, 5, 4)), ruleset: Classic);
        EntityId catapult = OfType(d.World, "comm.catapult").Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        d.Order(1, CommandKind.Stop, [footman]);
        d.Order(0, CommandKind.Attack, [catapult], target: footman);
        d.Order(1, CommandKind.SetStance, [footman], arg0: (int)CombatStance.HoldFire);
        d.RunUntil(() => false, 200);
        Assert.DoesNotContain(Of(d, SimEventKind.ProjectileLaunched), e => e.Entity == catapult);
        Assert.Equal(70, Hp(d, footman));
    }

    [Fact]
    public void Cannon_towers_roll_their_damage_up_to_max_damage()
    {
        // Cannon tower (synthetic): attack 39, max_damage 45 (⚠ HYPOTHESIS: a roll in [attack, max]); footman defence 2.
        using var d = new SimDriver(
            TestMap(40, 20, [Structure("comm.tower_stone2", 0, 4, 9), .. Enumerable.Range(0, 12).Select(i => Unit("euro.footman", 1, 10 + (i % 4) * 3, 4 + (i / 4) * 5))]),
            ruleset: Classic);
        foreach (EntityId f in OfType(d.World, "euro.footman"))
        {
            d.Order(1, CommandKind.SetStance, [f], arg0: (int)CombatStance.HoldFire);
        }

        d.RunUntil(() => Of(d, SimEventKind.Damaged).Count(e => e.Arg >= 30) >= 8, 3000);
        int[] direct = [.. Of(d, SimEventKind.Damaged).Where(e => e.Arg >= 30).Select(e => e.Arg)];
        Assert.True(direct.Length >= 8);
        Assert.All(direct, v => Assert.InRange(v, 37, 43));
        Assert.True(direct.Distinct().Count() > 1, "the rolls should vary");
    }

    [Fact]
    [Trait("Rule", "ORD-010")]
    public void Hold_fire_never_starts_a_fight_and_stand_ground_never_chases()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.footman", 0, 4, 4), Unit("euro.peon", 1, 7, 4)), ruleset: Classic);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.SetStance, [footman], arg0: (int)CombatStance.HoldFire);
        d.RunUntil(() => false, 40);
        Assert.Equal(0u, d.World.Combats.Get(footman).Target);

        // Stand ground: the peasant 3 tiles away is in sight but not in reach: no chase.
        d.Order(0, CommandKind.SetStance, [footman], arg0: (int)CombatStance.StandGround);
        Position before = d.PositionOf(footman);
        d.RunUntil(() => false, 60);
        Assert.Equal(before, d.PositionOf(footman));
        Assert.Equal(44, Hp(d, peon));

        // Back to aggressive: it acquires the peasant and walks over.
        d.Order(0, CommandKind.SetStance, [footman], arg0: (int)CombatStance.Aggressive);
        d.RunUntil(() => Hp(d, peon) < 44, 400);
        Assert.True(Hp(d, peon) < 44);
        Assert.False(d.World.Stances.Has(footman));
    }

    [Fact]
    [Trait("Rule", "ORD-012")]
    public void Attack_ground_hits_what_stands_at_the_point()
    {
        using var d = new SimDriver(TestMap(40, 20, Unit("comm.catapult", 0, 2, 10), Unit("euro.footman", 1, 14, 10), Unit("euro.footman", 1, 14, 10) with { X = (14 * Tile) + 600 }), ruleset: Classic);
        EntityId catapult = OfType(d.World, "comm.catapult").Single();
        EntityId[] footmen = [.. OfType(d.World, "euro.footman")];
        d.Order(1, CommandKind.SetStance, footmen, arg0: (int)CombatStance.HoldFire);
        d.Order(0, CommandKind.AttackGround, [catapult], (14 * Tile) + 1324, (10 * Tile) + 1024);
        d.RunUntil(() => Of(d, SimEventKind.Damaged).Count() >= 2, 600);
        Assert.Equal(CombatMode.Ground, d.World.Combats.Get(catapult).Mode);
        Assert.Equal(2, Of(d, SimEventKind.Damaged).Select(e => e.Entity).Distinct().Count());
    }

    [Fact]
    [Trait("Rule", "HOLD-004")]
    public void A_holder_spreads_its_damage_share_over_the_units_inside()
    {
        // Castle (synthetic): cannon damage 85% taken, 15% spread; a 200-attack catapult: 200 x 1000 / 1700 = 117 ->
        // the castle takes 99, the four footmen inside share 17 (5, 4, 4, 4).
        Ruleset rules = ClassicWith("comm.catapult: { Armament: { attack: 200 } }\neuro.castle: { Holder: { healPerMinute: 0 } }");
        using var d = new SimDriver(
            TestMap(48, 20, [Structure("euro.castle", 0, 20, 8), Unit("comm.catapult", 1, 6, 10), .. Enumerable.Range(0, 4).Select(i => Unit("euro.footman", 0, 18, 8 + i))]),
            ruleset: rules);
        EntityId castle = OfType(d.World, "euro.castle").Single();
        EntityId catapult = OfType(d.World, "comm.catapult").Single();
        EntityId[] footmen = [.. OfType(d.World, "euro.footman")];
        d.Order(0, CommandKind.Board, footmen, target: castle);
        d.RunUntil(() => d.World.Holders.Get(castle).Count == 4, 400);
        d.Order(1, CommandKind.Attack, [catapult], target: castle);
        d.RunUntil(() => d.Count(SimEventKind.DamageShared) > 0, 800);
        SimEvent shared = Of(d, SimEventKind.DamageShared).First();
        Assert.Equal(4, shared.X);
        Assert.Equal(17, shared.Arg);
        Assert.Contains(Of(d, SimEventKind.Damaged), e => e.Entity == castle && e.Arg == 99);
        Assert.Equal(70 * 4 - 17, footmen.Sum(f => Hp(d, f)));
    }
}
