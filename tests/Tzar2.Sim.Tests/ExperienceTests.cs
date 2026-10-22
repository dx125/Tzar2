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
    // A strong footman (attack 100) against an enemy castle: 100 x 1000 / 1700 = 58 damage and experience per hit.
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
    [Trait("Rule", "EXP-001")]
    [Trait("Rule", "EXP-002")]
    public void Experience_follows_the_damage_dealt_and_levels_cost_more_each_time()
    {
        using SimDriver d = FootmanAtCastle(Strong, "te_learning");
        EntityId footman = OfType(d.World, "euro.footman").Single();
        d.RunUntil(() => d.World.Experiences.Get(footman).Level >= 2, 2000);
        Experience x = d.World.Experiences.Get(footman);
        Assert.Equal(2, x.Level);
        Assert.InRange(x.Xp, 300, 599); // level 1 at 100, 2 at 300, 3 at 600
        int[] levels = [.. Of(d, SimEventKind.LevelUp).Where(e => e.Entity == footman).Select(e => e.X)];
        Assert.Equal([1, 2], levels);

        // +1 attack per level: 102 x 1000 / 1700 = 60 after level 2 (58 at level 0); +1/2 defence per level.
        d.RunUntil(() => Of(d, SimEventKind.Damaged).Any(e => e.Arg == 60), 400);
        Assert.Contains(Of(d, SimEventKind.Damaged), e => e.Arg == 60);
        Assert.Equal(2 + 1, CombatStats.Defence(d.World, footman, d.World.TypeOf(footman)));
    }

    [Fact]
    [Trait("Rule", "TECH-014")]
    public void The_level_cap_holds_until_Heroism()
    {
        using SimDriver d = FootmanAtCastle(Strong, "te_learning");
        EntityId footman = OfType(d.World, "euro.footman").Single();
        d.RunUntil(() => false, 3000);
        Assert.Equal(4, d.World.Experiences.Get(footman).Level); // te_learning: +4 levels (UNVERIFIED)
        Assert.Equal(CombatStats.XpForLevel(d.World.Ruleset.Combat, 4), d.World.Experiences.Get(footman).Xp);
    }

    [Fact]
    [Trait("Rule", "TECH-017")]
    public void Warfare_speeds_up_learning()
    {
        using SimDriver slow = FootmanAtCastle(Strong, "te_learning");
        using SimDriver fast = FootmanAtCastle(Strong, "te_learning", "te_warfare");
        slow.RunUntil(() => slow.Count(SimEventKind.Damaged) == 1, 1000);
        fast.RunUntil(() => fast.Count(SimEventKind.Damaged) == 1, 1000);
        slow.Step();
        fast.Step();
        int a = slow.World.Experiences.Get(OfType(slow.World, "euro.footman").Single()).Xp;
        int b = fast.World.Experiences.Get(OfType(fast.World, "euro.footman").Single()).Xp;
        Assert.Equal(58, a);
        Assert.Equal(58 * 125 / 100, b);
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
        Assert.Equal(3, d.World.Experiences.Get(footman).Level); // combat.dummyMaxLevel
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
        d.Order(0, CommandKind.Attack, footmen, target: OfType(d.World, "euro.castle").Single());
        d.RunUntil(() => footmen.All(f => d.World.IsHero(f)), 3000);
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
        d.Order(0, CommandKind.Attack, [footman], target: OfType(d.World, "euro.castle").Single());
        d.RunUntil(() => d.World.IsHero(footman), 2000);
        int before = d.World.Experiences.Get(archer).Xp;
        d.RunUntil(() => d.World.Experiences.Get(archer).Xp > before, 200);
        Assert.Equal(before + 29, d.World.Experiences.Get(archer).Xp); // 50% of 58 (UNVERIFIED share)
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
        Assert.Equal(300, d.World.Experiences.Get(footman).Xp);
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
        d.Order(0, CommandKind.Attack, [footman], target: OfType(d.World, "euro.castle").Single());
        d.RunUntil(() => d.World.IsHero(footman), 3000);

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
