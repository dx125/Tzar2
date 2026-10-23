using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// The curse, hypnosis, the ghost's banshee scream and the Messiah's immunity, as ReTzar reads them from the original's code
/// (ADR 0250).
/// </summary>
public class CurseHypnosisTests
{
    private const int Tile = 2048;

    // A footman of kingdom 0 holding a spell book of <paramref name="spell"/>.
    private static EntityId Reader(SimDriver d, string spell)
    {
        EntityId footman = OfType(d.World, "euro.footman", 0).Single();
        EntityId book = OfType(d.World, "comm.item_magic").Single();
        d.Order(0, CommandKind.PickItem, [footman], target: book);
        d.RunUntil(() => d.World.IsHeld(book), 400);
        d.World.Items.Get(book).Spell = (ushort)(Spell(d.World.Ruleset, spell) + 1);
        d.Order(0, CommandKind.SetStance, [footman], arg0: (int)CombatStance.HoldFire);
        return footman;
    }

    private static void Read(SimDriver d, EntityId reader, EntityId target)
    {
        Position at = d.PositionOf(target);
        d.Order(0, CommandKind.UseItem, [reader], at.X, at.Y, target, arg0: 0);
    }

    private static int Defence(SimDriver d, EntityId e) => CombatStats.Defence(d.World, e, d.World.TypeOf(e));

    private static int Attack(SimDriver d, EntityId e) => CombatStats.Attack(d.World, e, d.World.TypeOf(e), d.World.TypeOf(e).Traits.Armament!);

    [Fact]
    [Trait("Rule", "MAG-008")]
    public void Hypnosis_hands_the_unit_to_the_casters_kingdom_for_30_seconds()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.footman", 0, 4, 4), Unit("comm.item_magic", MapObject.Nature, 5, 4), Unit("euro.archer", 1, 10, 4)), ruleset: Classic);
        EntityId reader = Reader(d, "spell_hypnotize");
        EntityId archer = OfType(d.World, "euro.archer").Single();
        d.Order(1, CommandKind.SetStance, [archer], arg0: (int)CombatStance.HoldFire);
        d.Step();
        int population = d.World.Players[1].Population;
        Read(d, reader, archer);
        d.RunUntil(() => d.World.Identities.Get(archer).Owner == 0, 400);
        Assert.Equal(0, d.World.Identities.Get(archer).Owner);
        Assert.Equal(1, d.World.HypnotizedFrom(archer)); // its colours
        d.Step();
        Assert.Equal(population, d.World.Players[1].Population); // still its kingdom's
        int start = d.World.Tick.Value - 1;

        // Kingdom 0 commands it; kingdom 1 no longer does.
        Position before = d.PositionOf(archer);
        d.MoveToTile(1, [archer], 20, 4);
        d.RunUntil(() => false, 20);
        Assert.Equal(before, d.PositionOf(archer));
        d.MoveToTile(0, [archer], 10, 8);
        d.RunUntil(() => false, 40);
        Assert.NotEqual(before, d.PositionOf(archer));

        d.RunUntil(() => d.World.Identities.Get(archer).Owner == 1, 2000);
        int ticks = d.World.Tick.Value - start;
        int expected = 30_000 / d.World.Config.TickLengthMilliseconds;
        Assert.InRange(ticks, expected - 2, expected + 2);
        Assert.False(d.World.Statuses.Has(archer));
    }

    [Fact]
    [Trait("Rule", "WAR-009")]
    [Trait("Rule", "MAG-008")]
    public void The_curse_halves_attack_and_takes_all_defence_of_the_enemies_around_its_point_but_not_machines()
    {
        using var d = new SimDriver(
            TestMap(40, 16, Unit("euro.footman", 0, 4, 4), Unit("comm.item_magic", MapObject.Nature, 5, 4), Unit("euro.footman", 1, 10, 4), Unit("euro.footman", 1, 11, 6),
                Unit("comm.catapult", 1, 10, 8), Unit("euro.footman", 1, 30, 4)),
            ruleset: Classic);
        EntityId reader = Reader(d, "spell_curse");
        EntityId[] enemies = [.. OfType(d.World, "euro.footman", 1)];
        EntityId catapult = OfType(d.World, "comm.catapult").Single();
        d.Order(1, CommandKind.SetStance, [.. enemies, catapult], arg0: (int)CombatStance.HoldFire);
        int attack = Attack(d, enemies[0]);
        Assert.True(Defence(d, enemies[0]) > 0);
        Read(d, reader, enemies[0]);
        d.RunUntil(() => d.World.Statuses.Has(enemies[0]), 400);

        EntityId near = enemies[0], second = enemies.Single(e => d.PositionOf(e).Y > 5 * Tile && d.PositionOf(e).X < 20 * Tile), far = enemies.Single(e => d.PositionOf(e).X > 20 * Tile);
        Assert.Equal(attack / 2, Attack(d, near));
        Assert.Equal(0, Defence(d, near));
        Assert.Equal(0, Defence(d, second));
        Assert.False(d.World.Statuses.Has(far)); // beyond the caster's sight of the point
        Assert.False(d.World.Statuses.Has(catapult)); // a machine
        Assert.False(d.World.Statuses.Has(reader)); // not the caster's own

        d.RunUntil(() => !d.World.Statuses.Has(near), 1000); // 30 s
        Assert.Equal(attack, Attack(d, near));
    }

    [Theory]
    [InlineData(1001, true)]
    [InlineData(0, false)]
    [Trait("Rule", "WAR-009")]
    public void A_ghosts_blow_may_curse_the_enemies_around_it(int chance, bool screams)
    {
        Ruleset ruleset = ClassicWith($"combat: {{ bansheeChance: {chance} }}");
        using var d = new SimDriver(TestMap(32, 12, Unit("comm.ghost", 0, 8, 4), Unit("euro.footman", 1, 9, 4), Unit("euro.footman", 1, 9, 6)), ruleset: ruleset);
        EntityId ghost = OfType(d.World, "comm.ghost").Single();
        EntityId[] enemies = [.. OfType(d.World, "euro.footman", 1)];
        d.Order(1, CommandKind.SetStance, enemies, arg0: (int)CombatStance.HoldFire);
        d.Order(0, CommandKind.Attack, [ghost], target: enemies[0]);
        d.RunUntil(() => Hp(d, enemies[0]) < d.World.Healths.Get(enemies[0]).Max, 400);
        d.Step();
        Assert.Equal(screams, d.World.Statuses.Has(enemies[0]));
        Assert.Equal(screams, d.World.Statuses.Has(enemies[1]));
        Assert.False(d.World.Statuses.Has(ghost));
    }

    [Fact]
    [Trait("Rule", "WAR-010")]
    public void Lasting_spells_do_not_take_on_the_messiah()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.footman", 0, 4, 4), Unit("comm.item_magic", MapObject.Nature, 5, 4), Unit("comm.messiah", 1, 10, 4)), ruleset: Classic);
        EntityId reader = Reader(d, "spell_hypnotize");
        EntityId messiah = OfType(d.World, "comm.messiah").Single();
        d.Order(1, CommandKind.SetStance, [messiah], arg0: (int)CombatStance.HoldFire);
        Read(d, reader, messiah);
        d.RunUntil(() => d.Count(Events.SimEventKind.SpellCast) == 1, 400);
        Assert.Equal(1, d.World.Identities.Get(messiah).Owner);

        foreach (string spell in new[] { "spell_freeze", "spell_turntostone", "spell_berserk", "spell_curse" })
        {
            MagicSystem.ApplyEffect(d.World, Spell(d.World.Ruleset, spell), reader, 0, messiah, d.PositionOf(messiah).X, d.PositionOf(messiah).Y, []);
        }

        Assert.False(d.World.Statuses.Has(messiah));
        Assert.True(d.World.CanAct(messiah));
        int hp = Hp(d, messiah);
        MagicSystem.ApplyEffect(d.World, Spell(d.World.Ruleset, "spell_lightning"), reader, 0, messiah, 0, 0, []);
        Assert.Equal(hp, Hp(d, messiah)); // spells are magic: no damage either
    }

    [Fact]
    [Trait("Rule", "WAR-010")]
    public void The_messiah_takes_no_damage_from_a_mages_attack_but_does_from_a_sword()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.mage", 0, 6, 4), Unit("euro.footman", 0, 9, 6), Unit("comm.messiah", 1, 9, 4)), ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId messiah = OfType(d.World, "comm.messiah").Single();
        d.Order(1, CommandKind.SetStance, [messiah], arg0: (int)CombatStance.HoldFire);
        d.Order(0, CommandKind.SetStance, [footman], arg0: (int)CombatStance.HoldFire);
        int hp = Hp(d, messiah);
        d.Order(0, CommandKind.Attack, [mage], target: messiah);
        d.RunUntil(() => false, 200);
        Assert.Equal(hp, Hp(d, messiah)); // magic (ReTzar: damage category 3)
        d.Order(0, CommandKind.Stop, [mage]);
        d.Order(0, CommandKind.Attack, [footman], target: messiah);
        d.RunUntil(() => Hp(d, messiah) < hp, 400);
        Assert.True(Hp(d, messiah) < hp);
    }
}
