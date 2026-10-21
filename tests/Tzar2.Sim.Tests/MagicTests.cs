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

    [Fact]
    [Trait("Rule", "MAG-001")]
    [Trait("Rule", "MAG-002")]
    public void Lightning_costs_mana_has_a_cooldown_and_needs_enough_mana()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.mage", 0, 4, 4), Unit("euro.footman", 1, 10, 4), Unit("euro.footman", 1, 10, 7)), ruleset: NoRegen);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId[] enemies = [.. OfType(d.World, "euro.footman")];
        d.Order(1, CommandKind.SetStance, enemies, arg0: (int)CombatStance.HoldFire);
        d.Order(0, CommandKind.SetStance, [mage], arg0: (int)CombatStance.HoldFire);
        Cast(d, mage, "spell_lightning", enemies[0]);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 1, 200);
        Assert.Equal(70 - 40, Hp(d, enemies[0]));
        Assert.Equal(90 - 30, Mana(d, mage));
        Cast(d, mage, "spell_lightning", enemies[1]);
        d.Step();
        Assert.Contains(CommandRejection.SpellNotReady, d.Rejections());
        d.RunUntil(() => false, 60);
        Cast(d, mage, "spell_lightning", enemies[1]);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 2, 200);
        d.RunUntil(() => false, 60);
        Cast(d, mage, "spell_lightning", enemies[0]);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 3, 200);
        Assert.False(d.World.Entities.IsAlive(enemies[0]));
        Assert.Equal(0, Mana(d, mage));
        d.RunUntil(() => false, 60);
        Cast(d, mage, "spell_lightning", enemies[1]);
        d.Step();
        Assert.Contains(CommandRejection.NotEnoughMana, d.Rejections());
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
        Cast(d, mage, "spell_freeze", footman);
        d.Step();
        Assert.Contains(CommandRejection.SpellUnknown, d.Rejections()); // TECHTREE [orders] spell_freeze = te_freeze
        Grant(d.World, 0, "te_freeze");
        Cast(d, mage, "spell_freeze", footman);
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
        Assert.Equal(60, Hp(d, footman));

        d.RunUntil(() => false, 60);
        Cast(d, priest, "spell_bless", footman);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 2, 200);
        d.Order(0, CommandKind.Attack, [footman], target: enemy);
        d.RunUntil(() => d.Count(SimEventKind.Damaged) > 0, 200);
        Assert.Equal((9 * 125 / 100) - 2, Of(d, SimEventKind.Damaged).First().Arg); // +25% attack (UNVERIFIED)
    }

    [Fact]
    [Trait("Rule", "MAG-002")]
    [Trait("Rule", "PROD-010")]
    public void Summons_cost_the_casters_mana_and_population()
    {
        using var d = new SimDriver(TestMap(32, 12, Structure("euro.castle", 0, 20, 4), Unit("euro.mage", 0, 4, 4)), ruleset: NoRegen);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        Cast(d, mage, "spell_spawnbat");
        d.RunUntil(() => d.Count(SimEventKind.Summoned) == 1, 200);
        EntityId bat = OfType(d.World, "comm.bat").Single();
        Assert.Equal(0, d.World.Identities.Get(bat).Owner);
        Assert.Equal(90 - 30, Mana(d, mage)); // the bat's "30m" (synthetic)
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
                Unit("euro.mage", 0, 1, 1), Unit("euro.footman", 0, 6, 2), Unit("euro.footman", 1, 6, 3), Unit("comm.galleon", 1, 12, 3), Unit("euro.footman", 1, 9, 3)),
            ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId own = OfType(d.World, "euro.footman", 0).Single();
        d.Order(0, CommandKind.SetStance, [mage, own], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [.. OfType(d.World, "euro.footman", 1), .. OfType(d.World, "comm.galleon")], arg0: (int)CombatStance.HoldFire);
        Cast(d, mage, "spell_firerain", default, (6 * Tile) + 1024, 3 * Tile);
        d.RunUntil(() => d.World.AreaEffects.Count == 0 && d.Count(SimEventKind.SpellCast) == 1, 400);
        Assert.True(Hp(d, own) < 70); // the manual: "including your own"
        Assert.True(Hp(d, OfType(d.World, "euro.footman", 1).First()) < 70);

        EntityId ship = OfType(d.World, "comm.galleon").Single();
        EntityId shore = OfType(d.World, "euro.footman", 1).Single(f => d.PositionOf(f).X / Tile == 9);
        d.RunUntil(() => false, 120);
        Cast(d, mage, "spell_whirlpool", default, 11 * Tile, (3 * Tile) + 1024);
        d.RunUntil(() => d.World.AreaEffects.Count == 0 && d.Count(SimEventKind.SpellCast) == 2, 600);
        Assert.True(Hp(d, ship) < 1150);
        Assert.Equal(70, Hp(d, shore));
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
        d.RunUntil(() => !d.World.IsStone(enemies[0]), 400);
        Cast(d, mage, "spell_berserk", enemies[0]);
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
    public void A_crusade_raises_crusaders_once_and_waits_its_cooldown()
    {
        using var d = new SimDriver(TestMap(40, 20, Structure("euro.cathedral", 0, 4, 4)), ruleset: Classic);
        d.World.Players[0].Resources[Resource("gold")] = 5000;
        d.World.Players[0].Resources[Resource("food")] = 5000;
        EntityId cathedral = OfType(d.World, "euro.cathedral").Single();
        d.Order(0, CommandKind.PreachCrusade, [cathedral], 11 * Tile, 6 * Tile);
        d.RunUntil(() => false, 10);
        Assert.Empty(d.Rejections());
        Assert.Equal(5000 - 1100, d.World.Players[0].Resources[Resource("gold")]); // CRUSADE_ANCHOR.INI cost (synthetic)
        Assert.Equal(5, OfType(d.World, "euro.crusader").Count());
        d.Order(0, CommandKind.PreachCrusade, [cathedral], 11 * Tile, 6 * Tile);
        d.Step();
        Assert.Contains(CommandRejection.CrusadeActive, d.Rejections());

        d.Order(0, CommandKind.DebugKill, [.. OfType(d.World, "euro.crusader")]);
        d.RunUntil(() => d.World.Crusades.Count == 0, 2000);
        Assert.InRange(d.World.Tick.Value, 1200, 1300); // 60 s cooldown (UNVERIFIED)
        d.Order(0, CommandKind.PreachCrusade, [cathedral], 11 * Tile, 6 * Tile);
        d.Step();
        Assert.Equal(1, d.World.Crusades.Count);
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
        Assert.Equal(gold - (66 * 2), d.World.Players[0].Resources[Resource("gold")]); // 200% of 66 food (UNVERIFIED)
    }

    [Fact]
    [Trait("Rule", "MAG-014")]
    public void A_soul_kills_a_unit_but_not_a_hero()
    {
        using var d = new SimDriver(
            TestMap(24, 12, Unit("euro.footman", 0, 4, 4), Unit("comm.item_soul", MapObject.Nature, 5, 4), Unit("comm.item_soul", MapObject.Nature, 5, 5), Unit("euro.footman", 1, 6, 4), Unit("euro.knight", 1, 6, 6)),
            ruleset: Classic);
        EntityId footman = OfType(d.World, "euro.footman", 0).Single();
        EntityId victim = OfType(d.World, "euro.footman", 1).Single();
        EntityId hero = OfType(d.World, "euro.knight").Single();
        d.Order(0, CommandKind.SetStance, [footman], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [victim, hero], arg0: (int)CombatStance.HoldFire);
        d.World.Experiences.Get(hero).Level = (ushort)d.World.Ruleset.Combat.HeroLevel;
        d.World.Experiences.Get(hero).Flags = ExperienceFlags.Hero;
        foreach (EntityId soul in OfType(d.World, "comm.item_soul").ToArray())
        {
            d.Order(0, CommandKind.PickItem, [footman], target: soul);
            d.RunUntil(() => d.World.IsHeld(soul), 200);
        }

        d.Order(0, CommandKind.UseItem, [footman], target: hero, arg0: 0);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 1, 200);
        Assert.True(d.World.Entities.IsAlive(hero)); // heroes are immune to murder (UNVERIFIED)
        Assert.Equal(1, d.World.Inventories.Get(footman).Count); // a soul is used once
        d.RunUntil(() => false, 200); // murder's cooldown
        d.Order(0, CommandKind.UseItem, [footman], target: victim, arg0: 0);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 2, 400);
        Assert.False(d.World.Entities.IsAlive(victim));
    }
}
