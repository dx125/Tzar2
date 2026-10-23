using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Serialization;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Persistence;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>GAME-08 (ADR 0092): experience from combat, levels and their effects, learning technologies, heroes, persistence.</summary>
public class ExperienceTests
{
    // A strong footman (attack 100): one blow kills a peasant.
    private static readonly Ruleset Strong = ClassicWith("euro.footman: { Armament: { attack: 100 } }");

    private static SimDriver FootmanAtCastle(Ruleset rules, params string[] technologies)
    {
        var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 1, 14, 5), Unit("euro.footman", 0, 12, 7)), ruleset: rules);
        foreach (string t in technologies)
        {
            Grant(d.World, 0, t);
        }

        d.Order(0, CommandKind.Attack, [.. OfType(d.World, "euro.footman")], target: OfType(d.World, "euro.castle").Single());
        return d;
    }

    // Credits <paramref name="n"/> kills of enemy peasants to <paramref name="killer"/> through the death step, then lets the
    // experience step run (a kill is one point: ReTzar's reading of the original, 0x504f20).
    private static void Kills(SimDriver d, EntityId killer, int n, int victimOwner = 1)
    {
        Position at = d.PositionOf(killer);
        var deaths = new System.Collections.Generic.List<Death>();
        for (int k = 0; k < n; k++)
        {
            bool locked = d.World.IsLocked;
            d.World.IsLocked = false;
            EntityId victim = d.Spawn(Id(d.World.Ruleset, "euro.peon"), (byte)victimOwner, (at.X / Tile) + 1, (at.Y / Tile) + 1);
            d.World.Healths.Get(victim).Current = 0;
            deaths.Add(new Death(victim, killer, d.World.Identities.Get(killer).Owner));
            CombatSystem.ProcessDeaths(d.World, deaths);
            d.World.IsLocked = locked;
            d.Step();
        }
    }

    private const int Tile = Map.MapState.UnitsPerTile;

    [Fact]
    [Trait("Rule", "TECH-005")]
    public void Without_Learning_units_gain_no_levels()
    {
        // Strategy guide: "Researching Learning allows your units to gain experience" (combat.baseLevelCap 0).
        using SimDriver d = FootmanAtCastle(Strong);
        d.RunUntil(() => d.Count(SimEventKind.Damaged) >= 5, 1000);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        Assert.Equal(0, d.World.Experiences.Get(footman).Level);
        Assert.Equal(0, d.World.Experiences.Get(footman).Xp);
    }

    [Fact]
    [Trait("Rule", "EXP-003")]
    public void Kills_of_allies_give_no_experience_but_kills_of_enemies_do()
    {
        // 1.05: units cannot gain experience from ally units; damage gives none at all (ReTzar, 0x504f20).
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.footman", 0, 4, 4), Unit("euro.footman", 1, 8, 4), Unit("euro.footman", 2, 12, 4)), players: 3, ruleset: Classic);
        Grant(d.World, 0, "te_learning");
        d.Order(0, CommandKind.SetDiplomacy, [], x: 1, arg0: (int)Kingdoms.Stance.Ally);
        d.Step();
        EntityId attacker = OfType(d.World, "euro.footman", 0).Single();
        ushort type = d.World.Identities.Get(attacker).TypeId;
        var hit = new HitRecord(attacker, OfType(d.World, "euro.footman", 2).Single(), 0, type, 10, 10, 100, DamageClass.Sword, false);
        CombatSystem.Credit(d.World, hit, 2, 10);
        Assert.Equal(0, d.World.Experiences.Get(attacker).PendingXp); // a blow teaches nothing
        Kills(d, attacker, 1, victimOwner: 1);
        Assert.Equal(0, d.World.Experiences.Get(attacker).Xp);
        Kills(d, attacker, 1, victimOwner: 2);
        Assert.Equal(1, d.World.Experiences.Get(attacker).Xp);
    }

    [Fact]
    [Trait("Rule", "EXP-002")]
    public void The_experience_curve_is_data_and_classic_keeps_the_originals()
    {
        // Code review 2026-10 (item 16, for a fork): the curve's shape was code (level k costs base × k). It is
        // levelBaseXp + levelStepXp × (k − 1) now, -1 for "as base"; classic gives the original's numbers exactly, at
        // every experience speed.
        CombatSettings classic = Classic.Combat;
        Assert.Equal((1, -1, 1), (classic.LevelBaseXp, classic.LevelStepXp, classic.LevelStep));
        foreach (int b in new[] { 1, 3, 100 })
        {
            CombatSettings c = classic with { LevelBaseXp = b };
            foreach (int speed in new[] { 100, 70, 50, 35 })
            {
                for (int n = 0; n <= 200; n++)
                {
                    long old = 0;
                    if (speed == 100)
                    {
                        old = n <= 0 ? 0 : 1 + ((long)b * n * (n - 1) / 2);
                    }
                    else
                    {
                        for (int k = 0; k < n; k++)
                        {
                            old += System.Math.Max(1, (long)b * speed * k / 100);
                        }
                    }

                    Assert.Equal(old, CombatStats.XpForLevel(c, n, speed));
                }
            }
        }

        // A fork with flat levels (every level costs 5) and one that grows faster (2, then 3 more per level).
        Ruleset flat = ClassicWith("combat: { levelBaseXp: 5, levelStepXp: 0 }");
        Assert.Equal([0, 1, 6, 11, 16], Enumerable.Range(0, 5).Select(n => CombatStats.XpForLevel(flat.Combat, n)));
        Ruleset steep = ClassicWith("combat: { levelBaseXp: 2, levelStepXp: 3 }");
        Assert.Equal([0, 1, 3, 8, 16], Enumerable.Range(0, 5).Select(n => CombatStats.XpForLevel(steep.Combat, n)));
        Assert.Equal(CombatStats.XpForLevel(steep.Combat, 30), CombatStats.XpForLevel(steep.Combat, 30, 100));
    }

    [Fact]
    [Trait("Rule", "EXP-001")]
    [Trait("Rule", "EXP-002")]
    public void Each_kill_is_a_point_and_each_level_costs_as_many_points_as_it_is_high()
    {
        // ReTzar's reading of the original (0x5022e0): the first point gives level 1, level k needs k more.
        using var d = new SimDriver(
            TestMap(32, 16, [Unit("euro.footman", 0, 6, 7), .. Enumerable.Range(0, 7).Select(i => Unit("euro.peon", 1, 8, 4 + i))]),
            ruleset: Strong);
        Grant(d.World, 0, "te_learning");
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId[] peons = [.. OfType(d.World, "euro.peon")];
        d.Order(1, CommandKind.SetStance, peons, arg0: (int)CombatStance.HoldFire);
        int attack = CombatStats.Attack(d.World, footman, d.World.TypeOf(footman), d.World.TypeOf(footman).Traits.Armament!);
        foreach (EntityId peon in peons)
        {
            d.Order(0, CommandKind.Attack, [footman], target: peon);
            d.RunUntil(() => !d.World.Entities.IsAlive(peon), 2000);
            d.Step();
        }

        Experience x = d.World.Experiences.Get(footman);
        Assert.Equal(7, x.Xp);
        int[] levels = [.. Of(d, SimEventKind.LevelUp).Where(e => e.Entity == footman).Select(e => e.X)];
        Assert.Equal([1, 2, 3, 4], levels); // after 1, 2, 4 and 7 kills
        Assert.Equal([0, 1, 2, 4, 7], Enumerable.Range(0, 5).Select(n => CombatStats.XpForLevel(d.World.Ruleset.Combat, n)));

        // +1 attack per level, +1/2 defence per level.
        Assert.Equal(attack + 4, CombatStats.Attack(d.World, footman, d.World.TypeOf(footman), d.World.TypeOf(footman).Traits.Armament!));
        Assert.Equal(2 + 2, CombatStats.Defence(d.World, footman, d.World.TypeOf(footman)));
    }

    [Fact]
    [Trait("Rule", "TECH-014")]
    public void The_level_cap_holds_until_Heroism()
    {
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.footman", 0, 4, 4)), ruleset: Classic);
        Grant(d.World, 0, "te_learning");
        EntityId footman = OfType(d.World, "euro.footman").Single();
        Kills(d, footman, 20);
        Assert.Equal(4, d.World.Experiences.Get(footman).Level); // te_learning: +4 levels (ReTzar, ADR 0191)
        Assert.Equal(CombatStats.XpForLevel(d.World.Ruleset.Combat, 4), d.World.Experiences.Get(footman).Xp);
        Grant(d.World, 0, "te_heroism");
        Kills(d, footman, 4);
        Assert.Equal(5, d.World.Experiences.Get(footman).Level);
    }

    [Fact]
    [Trait("Rule", "EXP-001")]
    public void Each_academy_raises_the_level_cap_by_ten()
    {
        // ReTzar's reading of the original (0x401850): a finished academy adds 10 to its kingdom's cap.
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.academy", 0, 10, 4), Unit("euro.footman", 0, 4, 4)), ruleset: Classic);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        Assert.Equal(10, CombatStats.LevelCap(d.World, footman));
        Grant(d.World, 0, "te_learning");
        Assert.Equal(14, CombatStats.LevelCap(d.World, footman));
    }

    [Fact]
    [Trait("Rule", "TECH-017")]
    public void Warfare_speeds_up_learning()
    {
        // A level costs 70 % with Warfare (ReTzar, ADR 0191): level 4 after 5 kills, not 7.
        using var slow = new SimDriver(TestMap(24, 12, Unit("euro.footman", 0, 4, 4)), ruleset: Classic);
        using var fast = new SimDriver(TestMap(24, 12, Unit("euro.footman", 0, 4, 4)), ruleset: Classic);
        Grant(slow.World, 0, "te_learning");
        Grant(fast.World, 0, "te_learning");
        Grant(fast.World, 0, "te_warfare");
        Kills(slow, OfType(slow.World, "euro.footman").Single(), 5);
        Kills(fast, OfType(fast.World, "euro.footman").Single(), 5);
        Assert.Equal(3, slow.World.Experiences.Get(OfType(slow.World, "euro.footman").Single()).Level);
        Assert.Equal(4, fast.World.Experiences.Get(OfType(fast.World, "euro.footman").Single()).Level);
        Assert.Equal(70, CombatStats.ExperienceSpeed(fast.World, 0));
    }

    [Fact]
    [Trait("Rule", "EXP-004")]
    public void Own_units_teach_nothing_but_training_dummies_do_up_to_their_level()
    {
        using var d = new SimDriver(TestMap(24, 12, Structure("comm.dummy", 0, 10, 4), Unit("euro.footman", 0, 8, 4), Unit("euro.peon", 0, 8, 8)), ruleset: Strong);
        Grant(d.World, 0, "te_learning");
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId dummy = OfType(d.World, "comm.dummy").Single();
        d.Order(0, CommandKind.Attack, [footman], target: OfType(d.World, "euro.peon").Single());
        d.Step();
        Assert.Contains(CommandRejection.InvalidArgument, d.Rejections()); // own peasants are no target

        d.Order(0, CommandKind.Attack, [footman], target: dummy);
        d.RunUntil(() => false, 2000);
        Assert.Equal(5100, Hp(d, dummy)); // dummies take no damage
        Assert.Equal(4, d.World.Experiences.Get(footman).Level); // Learning's cap before the dummies' level 8
        Grant(d.World, 0, "te_heroism");
        d.Order(0, CommandKind.Attack, [footman], target: dummy);
        d.RunUntil(() => false, 4000);
        Assert.Equal(8, d.World.Experiences.Get(footman).Level); // a point per 75 of damage, up to level 8 (ReTzar, 0x4df884)
    }

    [Fact]
    [Trait("Rule", "TECH-018")]
    public void Level_twelve_makes_a_named_hero_and_Body_Building_adds_hit_points()
    {
        Ruleset rules = ClassicWith("euro.footman: { Armament: { attack: 100 } }\ncombat: { heroLevel: 2 }");
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 1, 14, 5), Unit("euro.footman", 0, 12, 7), Unit("euro.footman", 0, 12, 9)), ruleset: rules);
        Grant(d.World, 0, "te_learning");
        Grant(d.World, 0, "te_bodybuilding");
        EntityId[] footmen = [.. OfType(d.World, "euro.footman")];
        foreach (EntityId f in footmen)
        {
            Kills(d, f, 2);
        }

        Assert.All(footmen, f => Assert.True(d.World.IsHero(f)));
        Assert.Equal(["Synthia", "Testor"], footmen.Select(f => d.World.Names.Get(f).Value).OrderBy(n => n));
        Assert.Equal(2, d.Count(SimEventKind.HeroPromoted));
        int level = d.World.Experiences.Get(footmen[0]).Level;
        Assert.Equal(70 + (5 * level), d.World.Healths.Get(footmen[0]).Max); // te_bodybuilding: +5 HP per level (UNVERIFIED)
    }

    [Fact]
    [Trait("Rule", "TECH-015")]
    public void Leadership_shares_a_heroes_experience_with_the_units_around_it()
    {
        Ruleset rules = ClassicWith("euro.footman: { Armament: { attack: 100 } }\ncombat: { heroLevel: 1 }");
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 1, 14, 5), Unit("euro.footman", 0, 12, 7), Unit("euro.archer", 0, 11, 7)), ruleset: rules);
        Grant(d.World, 0, "te_learning");
        Grant(d.World, 0, "te_nobility");
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId archer = OfType(d.World, "euro.archer").Single();
        d.Order(0, CommandKind.SetStance, [archer], arg0: (int)CombatStance.HoldFire);
        Kills(d, footman, 1);
        Assert.True(d.World.IsHero(footman));
        int before = d.World.Experiences.Get(archer).Xp;
        Kills(d, footman, 1);
        Assert.Equal(1, d.World.Experiences.Get(archer).Xp - before); // the whole of the hero's point (ReTzar, ADR 0191), not half
    }

    [Fact]
    [Trait("Rule", "TECH-027")]
    public void Philosophy_trains_units_at_a_higher_level()
    {
        Ruleset rules = ClassicWith("te_philosophy: { Technology: { effects: [{ stat: startingLevel, add: 2, classes: [footman] }] } }");
        using var d = new SimDriver(TestMap(24, 12), ruleset: rules);
        Grant(d.World, 0, "te_philosophy");
        EntityId footman = d.Spawn(Id(rules, "euro.footman"), 0, 4, 4);
        Assert.Equal(2, d.World.Experiences.Get(footman).Level);
        Assert.Equal(2, d.World.Experiences.Get(footman).Xp); // XpForLevel(2)
    }

    [Fact]
    [Trait("Rule", "EXP-006")]
    public void Persistent_heroes_round_trip_and_apply_to_a_new_map()
    {
        Ruleset rules = ClassicWith("euro.footman: { Armament: { attack: 100 } }\ncombat: { heroLevel: 2 }");
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 1, 14, 5), Unit("euro.footman", 0, 12, 7), Unit("comm.item", 255, 12, 6)), ruleset: rules);
        Grant(d.World, 0, "te_learning");
        EntityId footman = OfType(d.World, "euro.footman").Single();
        d.Order(0, CommandKind.PickItem, [footman], target: OfType(d.World, "comm.item").Single());
        d.RunUntil(() => d.World.Inventories.Get(footman).Count == 1, 400);
        Kills(d, footman, 2);
        Assert.True(d.World.IsHero(footman));

        var records = PersistentHeroes.Capture(d.World, 0);
        PersistentHero hero = Assert.Single(records);
        Assert.Equal("Synthia", hero.Name);
        var writer = new BinaryBufferWriter();
        PersistentHeroes.Write(writer, records);
        PersistentHero back = Assert.Single(PersistentHeroes.Read(writer.WrittenSpan));
        Assert.Equal(hero.Name, back.Name);
        Assert.Equal(hero.Level, back.Level);
        Assert.Equal(hero.Items.Select(i => i.TypeKey), back.Items.Select(i => i.TypeKey));
        PersistentHero bookHero = hero with { Items = [new PersistentItem("comm.item_magic", 0, 2, "spell_lightning", ItemEffect.Default, 2)] };
        var bookWriter = new BinaryBufferWriter();
        PersistentHeroes.Write(bookWriter, [bookHero]);
        Assert.Equal(bookHero.Items[0], Assert.Single(PersistentHeroes.Read(bookWriter.WrittenSpan)).Items[0]); // ITEM-016: its own spell and look
        PersistentHero poolHero = hero with { Items = [new PersistentItem("comm.item_magic", 0, 2, "spell_lightning", ItemEffect.Default, 2, 7_000, ItemStateFlags.OwnCooldown | ItemStateFlags.Endless)] };
        var poolWriter = new BinaryBufferWriter();
        PersistentHeroes.Write(poolWriter, [poolHero]);
        Assert.Equal(poolHero.Items[0], Assert.Single(PersistentHeroes.Read(poolWriter.WrittenSpan)).Items[0]); // ADR 0265: its own pool, endless
        Assert.Throws<System.IO.InvalidDataException>(() => PersistentHeroes.Read(writer.WrittenSpan[..^1]));

        // The next map: a unit of the same name takes the record (applied at match setup).
        using var next = new SimDriver(TestMap(16, 8, Unit("euro.footman", 0, 4, 4)), ruleset: rules);
        EntityId again = OfType(next.World, "euro.footman").Single();
        PersistentHeroes.Apply(next.World, again, back);
        Assert.True(next.World.IsHero(again));
        Assert.Equal(back.Level, next.World.Experiences.Get(again).Level);
        Assert.Equal(1, next.World.Inventories.Get(again).Count);
        next.Step();
        Assert.Throws<System.InvalidOperationException>(() => PersistentHeroes.Apply(next.World, again, back));
    }
}
