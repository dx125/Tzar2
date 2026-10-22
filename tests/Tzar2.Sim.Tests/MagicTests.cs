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
        Assert.Equal(90 - 50, Mana(d, mage)); // 50 mana (ReTzar, 0x4723e0)
        Cast(d, mage, "spell_lightning", enemies[1]);
        d.Step();
        Assert.Contains(CommandRejection.SpellNotReady, d.Rejections());
        d.RunUntil(() => false, 60);
        Cast(d, mage, "spell_lightning", enemies[1]);
        d.Step();
        Assert.Contains(CommandRejection.NotEnoughMana, d.Rejections()); // 40 left
        d.World.Manas.Get(mage).MilliMana = 90_000;
        Cast(d, mage, "spell_lightning", enemies[0]);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 2, 200);
        Assert.False(d.World.Entities.IsAlive(enemies[0]));
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
            ruleset: ClassicWith("euro.mage: { ManaPool: { max: 200 } }")); // fire rain costs 100 (ReTzar), above the synthetic mage's 90
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId own = OfType(d.World, "euro.footman", 0).Single();
        d.Order(0, CommandKind.SetStance, [mage, own], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [.. OfType(d.World, "euro.footman", 1), .. OfType(d.World, "comm.galleon")], arg0: (int)CombatStance.HoldFire);
        d.World.Manas.Get(mage).MilliMana = 200_000;
        Cast(d, mage, "spell_firerain", default, (6 * Tile) + 1024, 3 * Tile);
        d.RunUntil(() => d.World.AreaEffects.Count == 0 && d.Count(SimEventKind.SpellCast) == 1, 400);
        Assert.True(Hp(d, own) < 70); // the manual: "including your own"
        Assert.True(Hp(d, OfType(d.World, "euro.footman", 1).First()) < 70);

        EntityId ship = OfType(d.World, "comm.galleon").Single();
        EntityId shore = OfType(d.World, "euro.footman", 1).Single(f => d.PositionOf(f).X / Tile == 9);
        d.RunUntil(() => false, 120);
        d.World.Manas.Get(mage).MilliMana = 90_000;
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
        d.World.Manas.Get(mage).MilliMana = 90_000; // berserk costs 70 (ReTzar)
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
