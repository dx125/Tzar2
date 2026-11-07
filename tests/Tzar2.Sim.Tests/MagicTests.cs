using System;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>GAME-11 (ADR 0095): spells, mana and cooldowns, summons, priests, crusades, conversions, bribes.</summary>
public class MagicTests
{
    // No mana regeneration, so mana sums are exact.
    private static readonly Ruleset NoRegen = ClassicWith("gameplay: { manaRegenPerMinute: 0 }");

    private static void Cast(SimDriver d, EntityId caster, string spell, EntityId target = default, int x = 0, int y = 0)
        => d.Order(d.World.Identities.Get(caster).Owner, CommandKind.CastSpell, [caster], x, y, target, arg0: Spell(d.World.Ruleset, spell));

    private static int Mana(SimDriver d, EntityId e) => d.World.Manas.Get(e).MilliMana / 1000;

    // A spell cast at a point: where the unit stands (the freeze, confusion and the blessing; MAG-032).
    private static void CastAt(SimDriver d, EntityId caster, string spell, EntityId at)
        => Cast(d, caster, spell, default, d.PositionOf(at).X, d.PositionOf(at).Y);

    [Fact]
    [Trait("Rule", "MAG-001")]
    [Trait("Rule", "MAG-002")]
    [Trait("Rule", "MAG-025")]
    [Trait("Rule", "MAG-030")]
    public void Lightning_costs_mana_and_waits_for_enough_mana_but_for_nothing_else()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.mage", 0, 4, 4), Unit("euro.footman", 1, 10, 4), Unit("euro.footman", 1, 10, 7)), ruleset: NoRegen);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId[] enemies = [.. OfType(d.World, "euro.footman")];
        d.Order(1, CommandKind.SetStance, enemies, arg0: (int)CombatStance.HoldFire);
        d.Order(0, CommandKind.SetStance, [mage], arg0: (int)CombatStance.HoldFire);
        d.World.Healths.Get(enemies[0]).Current = d.World.Healths.Get(enemies[0]).Max = 400;
        Cast(d, mage, "spell_lightning", enemies[0]);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 1, 200);
        Assert.Equal(400 - 150, Hp(d, enemies[0])); // 150 damage (ReTzar, 0x472e83)
        Assert.Equal(90 - 50, Mana(d, mage)); // 50 mana (ReTzar, 0x4723e0)
        Cast(d, mage, "spell_lightning", enemies[1]);
        d.RunUntil(() => false, 60);
        Assert.Empty(d.Rejections()); // 40 left: the order is kept, the mage waits for its mana (MAG-030)
        Assert.Equal(1, d.Count(SimEventKind.SpellCast));
        d.World.Manas.Get(mage).MilliMana = 90_000;
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 2, 60); // it looks again within 2 s and casts
        Assert.Equal(2, d.Count(SimEventKind.SpellCast));
        Assert.False(d.World.Entities.IsAlive(enemies[1]));
    }

    [Fact]
    [Trait("Rule", "MAG-026")]
    public void A_kingdoms_casters_regain_two_mana_a_second_natures_one_and_concentration_adds_four()
    {
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.mage", 0, 4, 4), Unit("euro.mage", 1, 12, 4), Unit("euro.mage", MapObject.Nature, 20, 4)), ruleset: Classic);
        EntityId own = OfType(d.World, "euro.mage", 0).Single();
        EntityId other = OfType(d.World, "euro.mage", 1).Single();
        d.Order(0, CommandKind.SetStance, [own], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [other], arg0: (int)CombatStance.HoldFire);
        EntityId wild = OfType(d.World, "euro.mage", MapObject.Nature).Single();
        Grant(d.World, 1, "te_concentration");
        foreach (EntityId mage in new[] { own, other, wild })
        {
            d.World.Manas.Get(mage).MilliMana = 0;
        }

        d.RunUntil(() => false, 200); // 10 s
        Assert.Equal((20, 60, 10), (Mana(d, own), Mana(d, other), Mana(d, wild)));
    }

    [Fact]
    [Trait("Rule", "MAG-027")]
    [Trait("Rule", "MAG-030")]
    public void A_summon_above_the_casters_pool_raises_the_pool_to_its_cost_and_casts_once_it_fills()
    {
        // ReTzar 0x4e1c30: the order finds too little mana; the caster's most mana grows to the cost, here the golem's whole
        // mana (the tables' 600m against a pool of 90), and the spell is cast once the pool has filled.
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.mage", 0, 4, 4), Structure("euro.castle", 0, 14, 2)), ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        int spell = Spell(d.World.Ruleset, "spell_spawnstonegolem");
        int cost = d.World.Ruleset.EntityTypes[d.World.Ruleset.Spells[spell].Summon].Traits.ManaCost;
        Assert.True(cost > d.World.MaxMana(mage));
        for (int r = 0; r < d.World.Ruleset.ResourceTypes.Count; r++)
        {
            d.World.Players[0].Resources[r] = 100_000;
        }

        Cast(d, mage, "spell_spawnstonegolem", default, 6 * Tile, 4 * Tile);
        d.RunUntil(() => d.Count(SimEventKind.ManaRaised) > 0, 100);
        Assert.Empty(d.Rejections()); // the order is kept (MAG-030)
        Assert.Equal(cost, d.World.MaxMana(mage)); // the pool now holds the cost
        Assert.Equal(cost, Of(d, SimEventKind.ManaRaised).Single().Arg); // the pump picture plays over it (ReTzar 0x4e1d0a)
        d.RunUntil(() => false, 100);
        Assert.Equal(0, d.Count(SimEventKind.SpellCast)); // still filling
        d.World.Manas.Get(mage).MilliMana = cost * 1000;
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 1, 100); // the golem comes by itself once the mana is there
        Assert.Equal(1, d.Count(SimEventKind.SpellCast));
        Assert.Single(OfType(d.World, d.World.Ruleset.EntityTypes[d.World.Ruleset.Spells[spell].Summon].Key, 0));
        Assert.True(Mana(d, mage) < 50); // the whole cost was paid
    }

    [Fact]
    [Trait("Rule", "MAG-028")]
    [Trait("Rule", "MAG-014")]
    public void A_ninjas_murder_is_one_blow_of_fifteen_times_its_attack_spends_its_mana_and_takes_the_messiah()
    {
        // ReTzar 0x4e076a; the guide's mission 18: "8-10 cloaked Ninja set to murder him" (not magic, so the Messiah takes it).
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.footman", 0, 4, 4), Unit("comm.messiah", 1, 6, 4)), ruleset: Ninjas);
        Ruleset rules = d.World.Ruleset;
        EntityId ninja = OfType(d.World, "euro.footman").Single();
        EntityId messiah = OfType(d.World, "comm.messiah").Single();
        GrantOrder(d, "murder", "euro");
        d.Order(1, CommandKind.SetStance, [messiah], arg0: (int)CombatStance.HoldFire);
        d.Order(0, CommandKind.SetStance, [ninja], arg0: (int)CombatStance.HoldFire);
        d.World.Manas.Get(ninja).MilliMana = 100_000;
        EntityTypeDefinition killer = d.World.TypeOf(ninja), victim = d.World.TypeOf(messiah);
        long blow = (long)CombatStats.Attack(d.World, ninja, killer, killer.Traits.Armament!) * 15;
        int expected = (int)((blow * (10_000 + rules.BonusBasisPoints(killer.Id, victim.Id)) / 10_000) - CombatStats.Defence(d.World, messiah, victim));
        int before = Hp(d, messiah);
        Cast(d, ninja, "murder", messiah);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 1, 400);
        Assert.Equal(1, d.Count(SimEventKind.SpellCast));
        Assert.Equal(before - Math.Max(expected, rules.Gameplay.MinimumDamage), Hp(d, messiah));
        Assert.Equal(0, Mana(d, ninja)); // all of it
    }

    [Fact]
    [Trait("Rule", "MAG-028")]
    public void A_ninja_with_sixty_mana_ordered_to_attack_a_unit_murders_it_with_its_first_blow()
    {
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.footman", 0, 4, 4), Unit("euro.knight", 1, 6, 4), Unit("euro.knight", 1, 6, 8)), ruleset: Ninjas);
        EntityId ninja = OfType(d.World, "euro.footman").Single();
        EntityId[] knights = [.. OfType(d.World, "euro.knight").OrderBy(k => d.PositionOf(k).Y)];
        GrantOrder(d, "murder", "euro");
        d.Order(1, CommandKind.SetStance, knights, arg0: (int)CombatStance.HoldFire);
        d.World.Manas.Get(ninja).MilliMana = 60_000;
        d.Order(0, CommandKind.Attack, [ninja], target: knights[0]);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 1, 400);
        Assert.Equal(Spell(d.World.Ruleset, "murder"), Of(d, SimEventKind.SpellCast).Single().Arg);
        Assert.Equal(0, Mana(d, ninja));

        // Below 60 the attack is a plain one.
        d.World.Manas.Get(ninja).MilliMana = 59_000;
        int hp = Hp(d, knights[1]);
        d.Order(0, CommandKind.Attack, [ninja], target: knights[1]);
        d.RunUntil(() => Hp(d, knights[1]) < hp, 600);
        Assert.True(Hp(d, knights[1]) < hp);
        Assert.Equal(1, d.Count(SimEventKind.SpellCast));
    }

    [Fact]
    [Trait("Rule", "MAG-029")]
    public void A_flame_bolt_burns_its_way_the_first_enemy_on_its_first_tiles_then_all_round_it()
    {
        // ReTzar's reading of the original (0x431f40): on its first three tiles the first unit there that is no friend of
        // the caster takes twice the spell's 60; from the fourth on, everything but the caster meeting the 3 x 3 tiles
        // round it takes 60 on its own tile and 30 beside it, the caster's own side too; nothing more where it lands.
        Ruleset rules = ClassicWith("euro.mage: { Spellcaster: { spells+=: [spell_flamebolt] } }");
        using var d = new SimDriver(
            TestMap(32, 16,
            [
                Unit("euro.mage", 0, 2, 8),
                Unit("euro.footman", 1, 3, 8), // its first tile
                Unit("euro.footman", 0, 4, 8), // its second tile: a friend, spared
                Unit("euro.footman", 0, 9, 7), // beside its way further on: the caster's own, burnt
                Unit("euro.footman", 1, 16, 8), // the target
            ]),
            ruleset: rules);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId[] enemies = [.. OfType(d.World, "euro.footman", 1)];
        EntityId[] friends = [.. OfType(d.World, "euro.footman", 0)];
        EntityId first = enemies.Single(e => d.PositionOf(e).X / Tile == 3), target = enemies.Single(e => e != first);
        EntityId spared = friends.Single(e => d.PositionOf(e).X / Tile == 4), beside = friends.Single(e => e != spared);
        d.Order(1, CommandKind.SetStance, enemies, arg0: (int)CombatStance.HoldFire);
        d.Order(0, CommandKind.SetStance, [mage, .. friends], arg0: (int)CombatStance.HoldFire);
        foreach (EntityId e in (EntityId[])[.. enemies, .. friends])
        {
            d.World.Healths.Get(e).Current = d.World.Healths.Get(e).Max = 1000;
        }

        GrantOrder(d, "spell_flamebolt", "euro");
        d.World.Manas.Get(mage).MilliMana = 90_000;
        Cast(d, mage, "spell_flamebolt", target);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 1 && d.World.Projectiles.Count == 0, 400);
        Assert.Equal(0, d.World.Projectiles.Count);
        Assert.Equal(1000 - 120, Hp(d, first));
        Assert.Equal(1000, Hp(d, spared));
        Assert.True(Hp(d, beside) < 1000);
        Assert.True(Hp(d, target) < 1000);
        Assert.Equal(0, (1000 - Hp(d, beside)) % 30);
        Assert.False(d.World.Trails.Count > 0);
    }

    // The synthetic tables have no ninja: a footman that can murder, with a pool.
    private static readonly Ruleset Ninjas = ClassicWith("euro.footman: { Spellcaster: { spells: [murder] }, ManaPool: { max: 100 } }");

    private static void GrantOrder(SimDriver d, string order, string civilization)
    {
        if (d.World.Ruleset.OrderTechnology(order, civilization) is >= 0 and var gate)
        {
            Grant(d.World, 0, d.World.Ruleset.Technologies[gate].Key);
        }
    }

    [Theory]
    [InlineData("spell_firerain", 14, 100)]
    [InlineData("spell_lightning", 12, 50)]
    [InlineData("spell_freeze", 12, 60)]
    [InlineData("spell_berserk", 12, 70)]
    [InlineData("spell_heal", 3, 4)]
    [InlineData("spell_spawnbat", 16, 0)]
    [InlineData("spell_spawndragon", 4, 0)]
    [InlineData("spell_whirlpool", 16, 50)]
    [InlineData("spell_magicvision", 1024, 30)]
    [InlineData("spell_turntostone", 20, 50)]
    [InlineData("spell_flamebolt", 18, 50)]
    [InlineData("spell_raiseexp", 1, 10)]
    [InlineData("raisedead", 8, 70)]
    [InlineData("spell_bless", 8, 50)]
    [InlineData("spell_curse", 8, 50)]
    [Trait("Rule", "MAG-023")]
    public void Each_spell_has_the_originals_range_and_mana(string key, int rangeTiles, int mana)
    {
        // ReTzar's reading of the original's spell dispatch (0x4721e0, 0x4723e0); summons pay their creature's mana.
        SpellDefinition s = Classic.Spells[Classic.SpellId(key)];
        Assert.Equal((rangeTiles * Tile, mana), (s.RangeUnits, s.ManaCost));
    }

    [Fact]
    [Trait("Rule", "MAG-023")]
    [Trait("Rule", "MAG-009")]
    public void Medicine_lets_priests_heal_from_five_tiles_instead_of_three()
    {
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.priest", 0, 4, 4)), ruleset: Classic);
        EntityId priest = OfType(d.World, "euro.priest").Single();
        SpellDefinition heal = d.World.Ruleset.Spells[Spell(d.World.Ruleset, "spell_heal")];
        Assert.Equal(3 * Tile, MagicSystem.RangeOf(d.World, priest, heal));
        Grant(d.World, 0, "te_medicine");
        Assert.Equal(5 * Tile, MagicSystem.RangeOf(d.World, priest, heal));
    }

    [Fact]
    [Trait("Rule", "MAG-002")]
    [Trait("Rule", "TECH-033")]
    public void Freeze_needs_its_technology_and_stops_the_target()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.mage", 0, 4, 4), Unit("euro.footman", 1, 10, 4)), ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        d.Order(0, CommandKind.SetStance, [mage], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [footman], arg0: (int)CombatStance.HoldFire);
        CastAt(d, mage, "spell_freeze", footman);
        d.Step();
        Assert.Contains(CommandRejection.SpellUnknown, d.Rejections()); // TECHTREE [orders] spell_freeze = te_freeze
        Grant(d.World, 0, "te_freeze");
        CastAt(d, mage, "spell_freeze", footman); // at a point (MAG-032)
        d.RunUntil(() => d.World.Statuses.Has(footman), 200); // the freeze bolt flies first
        Assert.False(d.World.CanAct(footman));
        Position at = d.PositionOf(footman);
        d.MoveToTile(1, [footman], 20, 4);
        d.RunUntil(() => false, 100);
        Assert.Equal(at, d.PositionOf(footman));
        d.RunUntil(() => d.World.CanAct(footman), 300); // 10 s (UNVERIFIED)
        d.MoveToTile(1, [footman], 20, 4);
        d.RunUntil(() => false, 40);
        Assert.NotEqual(at, d.PositionOf(footman));
    }

    [Fact]
    [Trait("Rule", "MAG-009")]
    [Trait("Rule", "MAG-033")]
    public void Priests_heal_and_bless()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.priest", 0, 4, 4), Unit("euro.footman", 0, 6, 4), Unit("euro.footman", 1, 8, 4)), ruleset: Classic);
        EntityId priest = OfType(d.World, "euro.priest").Single();
        EntityId footman = OfType(d.World, "euro.footman", 0).Single();
        EntityId enemy = OfType(d.World, "euro.footman", 1).Single();
        d.Order(0, CommandKind.SetStance, [priest, footman], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [enemy], arg0: (int)CombatStance.HoldFire);
        Grant(d.World, 0, "te_healing");
        Grant(d.World, 0, "te_bless");
        d.World.Healths.Get(footman).Current = 20;
        Cast(d, priest, "spell_heal", footman);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 1, 200);
        Assert.Equal(26, Hp(d, footman)); // +6 a cast (ReTzar, 0x472ff1)
        d.RunUntil(() => Hp(d, footman) == d.World.Healths.Get(footman).Max, 400); // MAG-033: it goes on until healed
        Assert.True(d.Count(SimEventKind.SpellCast) > 1);
        int casts = d.Count(SimEventKind.SpellCast);
        d.RunUntil(() => false, 60);
        Assert.Equal(casts, d.Count(SimEventKind.SpellCast)); // and stops there
        CastAt(d, priest, "spell_bless", footman); // at a point: those not hostile within 4 (MAG-032)
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == casts + 1, 200);
        d.Order(0, CommandKind.Attack, [footman], target: enemy);
        d.RunUntil(() => d.Count(SimEventKind.Damaged) > 0, 200);
        Assert.Equal(9 + 5 - 2, Of(d, SimEventKind.Damaged).First().Arg); // +5 attack (ReTzar, 0x4e5809)
    }

    [Fact]
    [Trait("Rule", "MAG-002")]
    [Trait("Rule", "PROD-010")]
    [Trait("Rule", "MAG-031")]
    public void A_summoning_spends_the_mana_in_multiples_of_the_creatures_cost_one_creature_each()
    {
        // ReTzar 0x472409: 90 mana against the bat's "30m" (synthetic) calls three bats and spends all of it.
        using var d = new SimDriver(TestMap(32, 12, Structure("euro.castle", 0, 20, 4), Unit("euro.mage", 0, 4, 4)), ruleset: NoRegen);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        Cast(d, mage, "spell_spawnbat");
        d.RunUntil(() => d.Count(SimEventKind.Summoned) == 3, 200);
        EntityId[] bats = [.. OfType(d.World, "comm.bat")];
        Assert.Equal(3, bats.Length);
        Assert.All(bats, bat => Assert.Equal(0, d.World.Identities.Get(bat).Owner));
        Assert.Equal(0, Mana(d, mage));
        Assert.Equal(1, d.Count(SimEventKind.SpellCast));
    }

    [Fact]
    [Trait("Rule", "MAG-032")]
    public void A_freeze_flies_to_a_point_and_freezes_every_unit_within_three_tiles_whoever_it_belongs_to()
    {
        // ReTzar 0x432590: the ball lands; every unit within 3 tiles, own ones too, is frozen and hurt short of death.
        using var d = new SimDriver(TestMap(32, 16, Unit("euro.mage", 0, 4, 4), Unit("euro.footman", 1, 12, 4), Unit("euro.footman", 0, 14, 5), Unit("euro.footman", 1, 20, 4)), ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId enemy = OfType(d.World, "euro.footman", 1).First(f => d.PositionOf(f).X / Tile == 12);
        EntityId far = OfType(d.World, "euro.footman", 1).First(f => d.PositionOf(f).X / Tile == 20);
        EntityId own = OfType(d.World, "euro.footman", 0).Single();
        d.Order(0, CommandKind.SetStance, [mage, own], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [enemy, far], arg0: (int)CombatStance.HoldFire);
        Grant(d.World, 0, "te_freeze");
        Cast(d, mage, "spell_freeze", default, (13 * Tile) + (Tile / 2), (4 * Tile) + (Tile / 2)); // open ground between them
        d.RunUntil(() => d.World.Statuses.Has(enemy), 200);
        Assert.Empty(d.Rejections());
        Assert.False(d.World.CanAct(enemy));
        Assert.False(d.World.CanAct(own)); // the caster's own unit too
        Assert.True(d.World.CanAct(far));
        Assert.True(Hp(d, enemy) < d.World.Healths.Get(enemy).Max);
    }

    [Fact]
    [Trait("Rule", "MAG-034")]
    public void A_freeze_ball_falls_onto_its_point_from_500_pixels_above_wherever_the_caster_stands()
    {
        using var d = new SimDriver(TestMap(32, 24, Unit("euro.mage", 0, 4, 20), Unit("euro.footman", 1, 12, 20)), ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId enemy = OfType(d.World, "euro.footman", 1).Single();
        d.Order(0, CommandKind.SetStance, [mage], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [enemy], arg0: (int)CombatStance.HoldFire);
        Grant(d.World, 0, "te_freeze");
        int px = (13 * Tile) + (Tile / 2), py = (20 * Tile) + (Tile / 2);
        Cast(d, mage, "spell_freeze", default, px, py);
        d.RunUntil(() => d.World.Projectiles.Count > 0, 200);
        Projectile ball = d.World.Projectiles.Components[0];
        Assert.Equal(px, ball.X); // above the point, not at the caster (ReTzar 0x432350)
        Assert.InRange(py - ball.Y, 32_000 - 2_240, 32_000); // 500 pixels up, at most one 50 ms step of 700 px/s down already
        int flight = d.RunUntil(() => d.World.Statuses.Has(enemy), 40);
        Assert.InRange(flight, 13, 15); // 500 pixels at 700 px/s: 714 ms
    }

    [Fact]
    [Trait("Rule", "MAG-035")]
    public void A_spell_cast_far_beyond_the_casters_sight_lets_its_kingdom_see_round_the_point_for_four_seconds()
    {
        using var d = new SimDriver(TestMap(48, 12, Unit("euro.mage", 0, 2, 5), Unit("euro.footman", 1, 24, 5)), ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId enemy = OfType(d.World, "euro.footman", 1).Single();
        d.Order(0, CommandKind.SetStance, [mage], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [enemy], arg0: (int)CombatStance.HoldFire);
        Cast(d, mage, "spell_turntostone", enemy); // reaches 20 tiles, beyond the mage's sight less 3
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 1, 400);
        Position at = d.PositionOf(enemy);
        Assert.Contains(d.World.Visibility.TemporaryReveals, r => r.Player == 0 && r.Radius == 3 && r.Tile == ((at.Y / Tile) * 48) + (at.X / Tile));
        d.RunUntil(() => false, (4000 / 50) + 1);
        Assert.DoesNotContain(d.World.Visibility.TemporaryReveals, r => r.Player == 0 && r.Radius == 3);
    }

    [Fact]
    [Trait("Rule", "MAG-036")]
    public void Magic_vision_on_a_unit_lets_the_kingdom_see_what_it_sees_following_it_for_60_s_and_at_a_point_only_a_glimpse()
    {
        using var d = new SimDriver(TestMap(64, 16, Unit("euro.mage", 0, 2, 8), Unit("euro.footman", 1, 50, 8)), ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId enemy = OfType(d.World, "euro.footman", 1).Single();
        d.Order(0, CommandKind.SetStance, [mage], arg0: (int)CombatStance.HoldFire);
        d.Step();
        Assert.False(d.World.Visibility.IsVisible(0, 50, 8));
        Cast(d, mage, "spell_magicvision", enemy, d.PositionOf(enemy).X, d.PositionOf(enemy).Y);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 1, 200);
        d.Step();
        Assert.Empty(d.Rejections());
        Assert.True(d.World.Visibility.IsVisible(0, 50, 8)); // what the footman sees
        d.Order(1, CommandKind.Move, [enemy], 40 * Tile, 3 * Tile);
        d.RunUntil(() => d.PositionOf(enemy).X / Tile == 40 && d.PositionOf(enemy).Y / Tile == 3, 600);
        d.Step();
        Assert.Equal(1, d.World.AreaEffects.Count);
        Assert.True(d.World.Visibility.IsVisible(0, 40, 3)); // the vision went with it
        Assert.All(d.World.Visibility.TemporaryReveals.Where(r => r.Player == 0), r => Assert.Equal((3 * 64) + 40, r.Tile)); // and left where it was
        int left = d.RunUntil(() => d.World.AreaEffects.Count == 0, 1300);
        Assert.InRange(left, 1, 60_000 / 50);
        d.RunUntil(() => false, 10); // the last look fades
        Assert.False(d.World.Visibility.IsVisible(0, 40, 3)); // gone after 60 s

        // At a point with nobody there: the far cast's glimpse of 8 tiles for 4 s, and nothing that lasts.
        d.World.Manas.Get(mage).MilliMana = 100_000;
        Cast(d, mage, "spell_magicvision", default, (55 * Tile) + 32, (12 * Tile) + 32);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 2, 200);
        Assert.Equal(0, d.World.AreaEffects.Count);
        Assert.Contains(d.World.Visibility.TemporaryReveals, r => r.Player == 0 && r.Radius == 8 && r.Tile == (12 * 64) + 55);
    }

    [Fact]
    [Trait("Rule", "MAG-037")]
    [Trait("Rule", "MAG-033")]
    public void A_heal_goes_on_to_the_nearest_wounded_friend_in_sight_and_never_heals_stone()
    {
        using var d = new SimDriver(
            TestMap(32, 12, Unit("euro.priest", 0, 4, 4), Unit("euro.footman", 0, 6, 4), Unit("euro.footman", 0, 8, 4), Unit("euro.footman", 0, 5, 6), Unit("euro.footman", 1, 6, 6)),
            ruleset: Classic);
        EntityId priest = OfType(d.World, "euro.priest").Single();
        EntityId first = OfType(d.World, "euro.footman", 0).Single(f => d.PositionOf(f).X / Tile == 6);
        EntityId second = OfType(d.World, "euro.footman", 0).Single(f => d.PositionOf(f).X / Tile == 8);
        EntityId stone = OfType(d.World, "euro.footman", 0).Single(f => d.PositionOf(f).X / Tile == 5);
        EntityId enemy = OfType(d.World, "euro.footman", 1).Single();
        d.Order(0, CommandKind.SetStance, [priest, first, second, stone], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [enemy], arg0: (int)CombatStance.HoldFire);
        Grant(d.World, 0, "te_healing");
        d.World.Manas.Get(priest).RaisedMax = 1_000;
        d.World.Manas.Get(priest).MilliMana = 1_000_000;
        d.World.Healths.Get(first).Current = 40;
        d.World.Healths.Get(second).Current = 50;
        d.World.Healths.Get(stone).Current = 10;
        d.World.Healths.Get(enemy).Current = 10;
        d.World.Statuses.Set(stone, new StatusEffects { StoneUntil = 100_000 });
        Cast(d, priest, "spell_heal", stone); // stone takes no healing: the heal goes to the nearest wounded friend
        d.RunUntil(() => Hp(d, first) == d.World.Healths.Get(first).Max && Hp(d, second) == d.World.Healths.Get(second).Max, 800);
        Assert.Equal(10, Hp(d, stone));
        Assert.Equal(10, Hp(d, enemy)); // not a friend
        int casts = d.Count(SimEventKind.SpellCast);
        d.RunUntil(() => false, 60);
        Assert.Equal(casts, d.Count(SimEventKind.SpellCast)); // nobody left to heal: the order ends
        Assert.Equal(CastPhase.None, d.World.Casters.Get(priest).Phase);
    }

    [Fact]
    [Trait("Rule", "MAG-032")]
    public void A_lightning_strikes_any_object_the_kingdoms_own_buildings_too()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.mage", 0, 4, 4), Structure("euro.house", 0, 8, 4)), ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId house = OfType(d.World, "euro.house").Single();
        int before = Hp(d, house);
        Cast(d, mage, "spell_lightning", house);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 1, 200);
        Assert.Empty(d.Rejections());
        Assert.Equal(before - 150, Hp(d, house));
    }

    [Fact]
    [Trait("Rule", "MAG-004")]
    public void Raise_dead_turns_corpses_into_skeletons()
    {
        using var d = new SimDriver(TestMap(32, 12, [Unit("euro.mage", 0, 4, 4), .. Enumerable.Range(0, 3).Select(i => Unit("euro.peon", 1, 10, 3 + i))]), ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        d.Order(1, CommandKind.DebugKill, [.. OfType(d.World, "euro.peon")]);
        d.Step();
        Assert.Equal(3, d.World.Decals.Count);
        Grant(d.World, 0, "te_necromancy");
        Cast(d, mage, "raisedead", default, 10 * Tile, 4 * Tile);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 1, 200);
        Assert.Equal(3, OfType(d.World, "comm.skeleton", 0).Count());
        Assert.Equal(0, d.World.Decals.Count);
    }

    [Fact]
    [Trait("Rule", "MAG-003")]
    [Trait("Rule", "MAG-004")]
    [Trait("Rule", "MAG-005")]
    public void Rain_of_fire_burns_everyone_under_it_and_whirlpools_only_ships()
    {
        using var d = new SimDriver(
            AsciiMap(["..........~~~~~~", "..........~~~~~~", "..........~~~~~~", "..........~~~~~~", "..........~~~~~~", "..........~~~~~~"],
                Unit("euro.mage", 0, 1, 1), Unit("euro.footman", 0, 6, 2), Unit("euro.footman", 1, 6, 3), Unit("comm.galleon", 1, 12, 3), Unit("euro.footman", 1, 0, 5)),
            ruleset: ClassicWith("euro.mage: { ManaPool: { max: 200 } }")); // fire rain costs 100 (ReTzar), above the synthetic mage's 90
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId own = OfType(d.World, "euro.footman", 0).Single();
        EntityId shore = OfType(d.World, "euro.footman", 1).Single(f => d.PositionOf(f).X / Tile == 0); // out of the fireballs' reach
        EntityId enemy = OfType(d.World, "euro.footman", 1).Single(f => d.PositionOf(f).X / Tile == 6);
        d.Order(0, CommandKind.SetStance, [mage, own], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [.. OfType(d.World, "euro.footman", 1), .. OfType(d.World, "comm.galleon")], arg0: (int)CombatStance.HoldFire);
        d.World.Manas.Get(mage).MilliMana = 200_000;
        Cast(d, mage, "spell_firerain", default, (6 * Tile) + 1024, 3 * Tile);
        d.RunUntil(() => d.World.AreaEffects.Count == 0 && d.Count(SimEventKind.SpellCast) == 1, 400);
        Assert.False(d.World.Entities.IsAlive(own) && Hp(d, own) == 70); // the manual: "including your own"
        Assert.False(d.World.Entities.IsAlive(enemy) && Hp(d, enemy) == 70);

        EntityId ship = OfType(d.World, "comm.galleon").Single();
        d.RunUntil(() => false, 120);
        d.World.Manas.Get(mage).MilliMana = 90_000;
        Cast(d, mage, "spell_whirlpool", default, 12 * Tile, (3 * Tile) + 1024);
        d.RunUntil(() => d.World.AreaEffects.Count == 0 && d.Count(SimEventKind.SpellCast) == 2, 1000);
        Assert.False(d.World.Entities.IsAlive(ship) && Hp(d, ship) == 1150);
        Assert.Equal(70, Hp(d, shore));
    }

    [Fact]
    [Trait("Rule", "MAG-024")]
    [Trait("Rule", "MAG-034")]
    public void Fire_rain_is_twenty_meteors_of_40_made_every_170_ms_each_falling_onto_its_tile()
    {
        // A ship under the point: every fireball's 5 x 5 tiles meet it, wherever within 2 tiles it falls (ReTzar 0x472ddb).
        using var d = new SimDriver(
            AsciiMap(["~~~~~~~~~~~~~~~~", "~~~~~~~~~~~~~~~~", "~~~~~~~~~~~~~~~~", "~~~~~~~~~~~~~~~~", "~~~~~~~~~~~~~~~~", "~~~~~~~~~~~~~~~~"],
                Unit("comm.galleon", 0, 1, 1), Unit("comm.galleon", 1, 8, 3)),
            ruleset: Classic);
        EntityId target = OfType(d.World, "comm.galleon", 1).Single();
        d.Order(0, CommandKind.SetStance, [.. OfType(d.World, "comm.galleon", 0)], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [target], arg0: (int)CombatStance.HoldFire);
        d.Step();
        int hp = Hp(d, target);
        var s = d.World.Ruleset.Spells[Spell(d.World.Ruleset, "spell_firerain")];
        Assert.Equal((40, 170, 3400), (s.Amount, s.PulseMs, s.DurationMs));
        var p = d.PositionOf(target);
        d.World.AreaEffects.Set(d.World.Entities.Create(), new AreaEffect { X = p.X, Y = p.Y, RemainingMs = s.DurationMs, Spell = s.Id, Owner = 0 });
        d.RunUntil(() => d.World.Projectiles.Count > 0, 10); // the first meteor is made after 170 ms (ReTzar 0x472ddb)
        Projectile meteor = d.World.Projectiles.Components[0];
        Assert.Equal("meteor", d.World.Ruleset.Projectiles[meteor.Kind].Key);
        Assert.Equal(meteor.TargetX, meteor.X); // it falls straight down onto its tile (0x450620)
        Assert.True(meteor.Y < meteor.TargetY);
        d.RunUntil(() => false, 5);
        Assert.Equal(hp, Hp(d, target)); // nothing has fallen yet: a meteor takes 375 ms
        d.RunUntil(() => d.World.AreaEffects.Count == 0 && d.World.Projectiles.Count == 0, 200);
        Assert.Equal(hp - (20 * 40), Hp(d, target));
    }

    [Fact]
    [Trait("Rule", "MAG-024")]
    public void A_whirlpool_hurts_the_ships_on_its_3_by_3_tiles_wanders_on_the_water_and_lasts_30_s()
    {
        using var d = new SimDriver(
            AsciiMap(["~~~~~~~~~~~~~~~~", "~~~~~~~~~~~~~~~~", "~~~~~~~~~~~~~~~~", "~~~~~~~~~~~~~~~~", "~~~~~~~~~~~~~~~~", "~~~~~~~~~~~~~~~~"],
                Unit("comm.galleon", 0, 15, 0), Unit("comm.galleon", 1, 7, 3)),
            ruleset: Classic);
        EntityId target = OfType(d.World, "comm.galleon", 1).Single();
        d.Order(0, CommandKind.SetStance, [.. OfType(d.World, "comm.galleon", 0)], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [target], arg0: (int)CombatStance.HoldFire);
        d.Step();
        var s = d.World.Ruleset.Spells[Spell(d.World.Ruleset, "spell_whirlpool")];
        Assert.Equal((20, 250, 2000, 30_000), (s.Amount, s.PulseMs, s.MoveMs, s.DurationMs));
        var p = d.PositionOf(target);
        EntityId pool = d.World.Entities.Create();
        d.World.AreaEffects.Set(pool, new AreaEffect { X = p.X, Y = p.Y, RemainingMs = s.DurationMs, Spell = s.Id, Owner = 0 });
        int hp = Hp(d, target);
        d.RunUntil(() => false, 5); // 250 ms: the first pulse
        Assert.Equal(hp - 20, Hp(d, target));
        var moves = new System.Collections.Generic.HashSet<(int, int)>();
        int ticks = 5 + d.RunUntil(() =>
        {
            if (d.World.AreaEffects.Has(pool))
            {
                AreaEffect a = d.World.AreaEffects.Get(pool);
                moves.Add((a.X, a.Y));
                Assert.Equal(0, (a.X - p.X) % Tile); // whole tiles
            }

            return !d.World.AreaEffects.Has(pool);
        }, 700);
        Assert.Equal(30_000 / 50, ticks); // gone after 30 s
        Assert.True(moves.Count > 1); // it wandered
        Assert.True(!d.World.Entities.IsAlive(target) || (hp - Hp(d, target)) % 20 == 0);
    }

    [Fact]
    [Trait("Rule", "MAG-024")]
    public void A_whirlpool_forms_only_where_its_square_is_water()
    {
        using var d = new SimDriver(
            AsciiMap(["..........~~~~~~", "..........~~~~~~", "..........~~~~~~", "..........~~~~~~"], Unit("euro.mage", 0, 1, 1)),
            ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        d.World.Manas.Get(mage).MilliMana = 90_000;
        Cast(d, mage, "spell_whirlpool", default, (10 * Tile) + 1024, (1 * Tile) + 1024); // its 3 x 3 tiles take the shore
        d.Step();
        Assert.Contains(CommandRejection.InvalidTarget, d.Rejections());
    }

    [Fact]
    [Trait("Rule", "MAG-003")]
    [Trait("Rule", "MAG-007")]
    [Trait("Rule", "MAG-020")]
    public void Stone_takes_no_damage_and_confusion_turns_a_unit_on_its_friends()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.mage", 0, 0, 4), Unit("euro.footman", 1, 8, 4), Unit("euro.footman", 1, 8, 5), Unit("euro.footman", 0, 10, 8)), ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId[] enemies = [.. OfType(d.World, "euro.footman", 1)];
        EntityId attacker = OfType(d.World, "euro.footman", 0).Single();
        d.Order(0, CommandKind.SetStance, [mage], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [enemies[1]], arg0: (int)CombatStance.HoldFire);
        Cast(d, mage, "spell_turntostone", enemies[0]);
        d.RunUntil(() => d.World.IsStone(enemies[0]), 200);
        d.Order(0, CommandKind.Attack, [attacker], target: enemies[0]);
        d.RunUntil(() => false, 100);
        Assert.Equal(70, Hp(d, enemies[0]));

        d.Order(0, CommandKind.DebugKill, [attacker]); // only the confused unit's own friend is left in its sight
        d.RunUntil(() => !d.World.IsStone(enemies[0]), 6200); // 120 to 300 s (ReTzar, 0x473913)
        d.World.Manas.Get(mage).MilliMana = 90_000; // berserk costs 70 (ReTzar)
        CastAt(d, mage, "spell_berserk", enemies[0]);
        d.RunUntil(() => d.World.IsConfused(enemies[0]), 200);
        d.RunUntil(() => Of(d, SimEventKind.Damaged).Any(e => e.Entity == enemies[1] && e.X == enemies[0].Index), 300);
        Assert.Contains(Of(d, SimEventKind.Damaged), e => e.Entity == enemies[1] && e.X == enemies[0].Index); // it attacks its own kingdom
    }

    [Fact]
    public void Invisible_units_are_not_attacked_until_they_attack()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.mage", 0, 2, 4), Unit("euro.footman", 0, 6, 4), Unit("euro.footman", 1, 9, 4)), ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId own = OfType(d.World, "euro.footman", 0).Single();
        EntityId enemy = OfType(d.World, "euro.footman", 1).Single();
        d.Order(0, CommandKind.SetStance, [mage, own], arg0: (int)CombatStance.HoldFire);
        Cast(d, mage, "spell_cloak", own);
        d.RunUntil(() => d.World.IsCloaked(own), 100);
        d.RunUntil(() => false, 60);
        Assert.NotEqual(own.Value, d.World.Combats.Get(enemy).Target);
        d.Order(0, CommandKind.Attack, [own], target: enemy);
        d.RunUntil(() => !d.World.IsCloaked(own), 200);
        Assert.False(d.World.IsCloaked(own));
    }

    [Fact]
    [Trait("Rule", "MAG-016")]
    public void A_crusade_brings_the_gathered_crusaders_one_by_one_and_one_runs_at_a_time()
    {
        // ReTzar's reading of the original: 16 crusaders gathered at the start, one arriving every 500 ms.
        using var d = new SimDriver(TestMap(40, 20, Structure("euro.cathedral", 0, 4, 4)), ruleset: Classic);
        d.World.Players[0].Resources[Resource("gold")] = 5000;
        d.World.Players[0].Resources[Resource("food")] = 5000;
        EntityId cathedral = OfType(d.World, "euro.cathedral").Single();
        Assert.Equal(16, d.World.Players[0].Crusaders);
        d.Order(0, CommandKind.PreachCrusade, [cathedral], 11 * Tile, 6 * Tile);
        d.RunUntil(() => false, 30);
        Assert.Empty(d.Rejections());
        Assert.Equal(5000 - 1100, d.World.Players[0].Resources[Resource("gold")]); // CRUSADE_ANCHOR.INI cost (synthetic)
        Assert.InRange(OfType(d.World, "euro.crusader").Count(), 2, 4); // one every 10 ticks
        d.RunUntil(() => OfType(d.World, "euro.crusader").Count() == 16, 400);
        Assert.Equal(16, OfType(d.World, "euro.crusader").Count());
        Assert.Equal(1, d.World.Players[0].FullCrusades); // a full crusade raises the ceiling to 20
        d.Order(0, CommandKind.PreachCrusade, [cathedral], 11 * Tile, 6 * Tile);
        d.Step();
        Assert.Contains(CommandRejection.CrusadeActive, d.Rejections());

        d.Order(0, CommandKind.DebugKill, [.. OfType(d.World, "euro.crusader")]);
        d.RunUntil(() => d.World.Crusades.Count == 0, 20);
        Assert.Equal(0, d.World.Crusades.Count); // the anchor goes with its last crusader
        Assert.Equal(4, d.World.Players[0].Crusaders); // and the kingdom starts again from 4
    }

    [Fact]
    [Trait("Rule", "MAG-017")]
    public void Cathedrals_gather_crusaders_over_time_and_a_crusade_preached_early_brings_fewer_for_the_same_price()
    {
        using var d = new SimDriver(TestMap(40, 20, Structure("euro.cathedral", 0, 4, 4), Structure("euro.cathedral", 0, 4, 12)), ruleset: Classic);
        d.World.Players[0].Resources[Resource("gold")] = 5000;
        d.World.Players[0].Resources[Resource("food")] = 5000;
        EntityId cathedral = OfType(d.World, "euro.cathedral").First();
        d.Order(0, CommandKind.PreachCrusade, [cathedral], 11 * Tile, 6 * Tile);
        d.RunUntil(() => OfType(d.World, "euro.crusader").Count() == 16, 400);
        d.Order(0, CommandKind.DebugKill, [.. OfType(d.World, "euro.crusader")]);
        d.RunUntil(() => d.World.Crusades.Count == 0, 20);
        Assert.Equal(4, d.World.Players[0].Crusaders);

        // Two cathedrals, one crusader each every 12 s in the first 15 minutes: 4 more in 24 s.
        d.RunUntil(() => false, 24 * 20);
        Assert.Equal(8, d.World.Players[0].Crusaders);
        int gold = d.World.Players[0].Resources[Resource("gold")];
        d.Order(0, CommandKind.PreachCrusade, [cathedral], 11 * Tile, 6 * Tile);
        d.RunUntil(() => false, 100);
        Assert.Empty(d.Rejections());
        Assert.Equal(gold - 1100, d.World.Players[0].Resources[Resource("gold")]); // the same price
        Assert.Equal(8, OfType(d.World, "euro.crusader").Count()); // fewer than a full crusade
        Assert.Equal(1, d.World.Players[0].FullCrusades); // not a full one: the ceiling stays 20
        Assert.Equal(20, Systems.MagicSystem.CrusadeCeiling(d.World, 0));
    }

    [Fact]
    [Trait("Rule", "MAG-017")]
    public void The_crusade_gathering_pace_is_data()
    {
        // Code review 2026-10 (item 16, for a fork): the steps were literals in MagicSystem. Classic keeps the original's.
        Assert.Equal(
            [new CrusadeGatherStep(0, 12), new CrusadeGatherStep(15, 8), new CrusadeGatherStep(20, 4), new CrusadeGatherStep(30, 1)],
            Classic.Combat.CrusadeGather.ToArray());
        Assert.Equal(500, Classic.Combat.CrusaderArrivalMs);

        // A fork gathers every 3 s: two cathedrals bring 4 more in 6 s instead of 24.
        Ruleset fork = ClassicWith("combat: { crusadeGather: { 0: 3 } }");
        using var d = new SimDriver(TestMap(40, 20, Structure("euro.cathedral", 0, 4, 4), Structure("euro.cathedral", 0, 4, 12)), ruleset: fork);
        d.World.Players[0].Resources[Resource("gold")] = 5000;
        d.World.Players[0].Resources[Resource("food")] = 5000;
        EntityId cathedral = OfType(d.World, "euro.cathedral").First();
        d.Order(0, CommandKind.PreachCrusade, [cathedral], 11 * Tile, 6 * Tile);
        d.RunUntil(() => OfType(d.World, "euro.crusader").Count() == 16, 400);
        d.Order(0, CommandKind.DebugKill, [.. OfType(d.World, "euro.crusader")]);
        d.RunUntil(() => d.World.Crusades.Count == 0, 20);
        Assert.Equal(4, d.World.Players[0].Crusaders);
        d.RunUntil(() => false, 6 * 20);
        Assert.Equal(8, d.World.Players[0].Crusaders);

        Assert.ThrowsAny<System.Exception>(() => ClassicWith("combat: { crusadeGather: { 5: 3 } }"));
        Assert.ThrowsAny<System.Exception>(() => ClassicWith("combat: { crusadeGather: { 0: 3, 20: 2, 10: 1 } }"));
    }

    [Fact]
    [Trait("Rule", "FOG-005")]
    public void An_invisible_unit_that_drinks_a_potion_is_seen_again()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.mage", 0, 2, 4), Unit("euro.footman", 0, 6, 4), Unit("comm.item_health", MapObject.Nature, 7, 4)), ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId potion = OfType(d.World, "comm.item_health").Single();
        d.Order(0, CommandKind.SetStance, [mage, footman], arg0: (int)CombatStance.HoldFire);
        d.Order(0, CommandKind.PickItem, [footman], target: potion);
        d.RunUntil(() => d.World.IsHeld(potion), 400);
        Cast(d, mage, "spell_cloak", footman);
        d.RunUntil(() => d.World.IsCloaked(footman), 200);
        Assert.True(d.World.IsCloaked(footman));
        d.Order(0, CommandKind.UseItem, [footman], arg0: 0);
        d.Step();
        Assert.False(d.World.IsCloaked(footman)); // 1.09: using an item reveals it
    }

    [Fact]
    [Trait("Rule", "MAG-019")]
    public void Peasants_become_Jihad_warriors_for_their_price()
    {
        using var d = new SimDriver(TestMap(24, 12, Unit("arab.peon", 0, 4, 4)), ruleset: Classic);
        EntityId peon = OfType(d.World, "arab.peon").Single();
        int food = d.World.Players[0].Resources[Resource("food")];
        d.Order(0, CommandKind.ConvertUnit, [peon], arg0: Id("arab.jihad_warrior"));
        d.Step();
        Assert.Contains(CommandRejection.RequirementNotMet, d.Rejections()); // the table: DEPENDS ON te_jihad
        Grant(d.World, 0, "te_jihad");
        d.Order(0, CommandKind.ConvertUnit, [peon], arg0: Id("arab.jihad_warrior"));
        d.Step();
        Assert.Equal(food - 105, d.World.Players[0].Resources[Resource("food")]);
        d.RunUntil(() => d.Count(SimEventKind.UnitConverted) == 1, 400);
        Assert.False(d.World.Entities.IsAlive(peon));
        Assert.Single(OfType(d.World, "arab.jihad_warrior", 0));
        Assert.InRange(TimedOf(d, SimEventKind.UnitConverted).Single().Tick, 225, 227); // TIME 9 x 1.25 s (synthetic)
    }

    [Fact]
    [Trait("Rule", "MAG-015")]
    [Trait("Rule", "MAG-038")]
    public void Peasants_bribe_enemy_soldiers_but_not_peasants()
    {
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.peon", 0, 4, 4), Unit("euro.footman", 1, 8, 4), Unit("euro.peon", 1, 8, 7)), ruleset: Classic);
        EntityId briber = OfType(d.World, "euro.peon", 0).Single();
        EntityId soldier = OfType(d.World, "euro.footman", 1).Single();
        d.Order(1, CommandKind.SetStance, [soldier], arg0: (int)CombatStance.HoldFire);
        d.Order(0, CommandKind.Bribe, [briber], target: OfType(d.World, "euro.peon", 1).Single());
        d.Step();
        Assert.Contains(CommandRejection.InvalidTarget, d.Rejections());
        int gold = d.World.Players[0].Resources[Resource("gold")];
        d.Order(0, CommandKind.Bribe, [briber], target: soldier);
        d.RunUntil(() => d.World.Identities.Get(soldier).Owner == 0, 400);
        Assert.Equal(0, d.World.Identities.Get(soldier).Owner);
        Assert.Equal(gold - 50, d.World.Players[0].Resources[Resource("gold")]); // (level 0 + 20) x 10 / 4 (0x4ac62f)
    }

    [Fact]
    [Trait("Rule", "ITEM-010")]
    public void Soul_bind_puts_a_bound_soul_in_the_bag_which_sends_its_unit_berserk_or_kills_it_and_goes_with_it()
    {
        // ReTzar's reading of the original (0x473403, 0x465340, 0x4e219f): a hero dies like any unit.
        using var d = new SimDriver(
            TestMap(24, 12, Unit("euro.footman", 0, 4, 4), Unit("comm.item_magic", MapObject.Nature, 5, 4), Unit("euro.knight", 1, 8, 4)),
            ruleset: Classic);
        EntityId footman = OfType(d.World, "euro.footman", 0).Single();
        EntityId book = OfType(d.World, "comm.item_magic").Single();
        EntityId hero = OfType(d.World, "euro.knight").Single();
        d.Order(0, CommandKind.SetStance, [footman], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [hero], arg0: (int)CombatStance.HoldFire);
        d.World.Experiences.Get(hero).Level = (ushort)d.World.Ruleset.Combat.HeroLevel;
        d.World.Experiences.Get(hero).Flags = ExperienceFlags.Hero;
        d.Order(0, CommandKind.PickItem, [footman], target: book);
        d.RunUntil(() => d.World.IsHeld(book), 200);
        d.World.Items.Get(book).Spell = (ushort)(Spell(d.World.Ruleset, "spell_soulbind") + 1);
        Position at = d.PositionOf(hero);
        d.Order(0, CommandKind.UseItem, [footman], at.X, at.Y, hero, arg0: 0);
        d.RunUntil(() => OfType(d.World, "comm.item_soul").Any(), 400);
        EntityId soul = OfType(d.World, "comm.item_soul").Single();
        Assert.Equal(hero.Value, d.World.Items.Get(soul).Bound);
        Assert.Equal(footman.Value, d.World.Helds.Get(soul).Holder);
        Assert.Equal(1, d.World.Identities.Get(hero).Owner); // it keeps its owner
        int slot = d.World.Inventories.Get(footman).Count - 1;

        d.Order(0, CommandKind.UseItem, [footman], target: footman, arg0: slot, arg1: 1); // item_berserk
        d.Step();
        Assert.True(d.World.IsConfused(hero));
        Assert.True(d.World.Entities.IsAlive(soul)); // the soul stays

        d.Order(0, CommandKind.UseItem, [footman], target: footman, arg0: slot, arg1: 0); // item_kill
        d.Step();
        Assert.False(d.World.Entities.IsAlive(hero));
        Assert.False(d.World.Entities.IsAlive(soul)); // it goes with its unit
    }

    [Fact]
    [Trait("Rule", "ITEM-010")]
    public void A_soul_bound_to_nobody_does_nothing()
    {
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.footman", 0, 4, 4), Unit("comm.item_soul", MapObject.Nature, 5, 4)), ruleset: Classic);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId soul = OfType(d.World, "comm.item_soul").Single();
        d.Order(0, CommandKind.PickItem, [footman], target: soul);
        d.RunUntil(() => d.World.IsHeld(soul), 200);
        d.Order(0, CommandKind.UseItem, [footman], target: footman, arg0: 0);
        d.Step();
        Assert.Contains(CommandRejection.InvalidArgument, d.Rejections());
        Assert.True(d.World.Entities.IsAlive(soul));
    }
}
