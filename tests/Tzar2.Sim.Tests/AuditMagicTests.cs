using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Xunit;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// The 2026-11-07 audit's magic and experience rules (ADR 0280): Leadership's reach, mana that does not come back, and the
/// ninja's cloak kept on its own mana.
/// </summary>
public class AuditMagicTests
{
    // A footman made a ninja for the test: the ninja's cloak and a pool of 100 (NINJA.INI max_manna).
    private static readonly Ruleset Ninjas = ClassicWith("euro.footman: { Spellcaster: { spells: [cloak] }, ManaPool: { max: 100 } }");

    private static int Mana(SimDriver d, EntityId e) => d.World.Manas.Get(e).MilliMana / 1000;

    private static void Cast(SimDriver d, EntityId caster, string spell, EntityId target = default)
        => d.Order(d.World.Identities.Get(caster).Owner, CommandKind.CastSpell, [caster], 0, 0, target, arg0: Spell(d.World.Ruleset, spell));

    [Fact]
    [Trait("Rule", "MAG-040")]
    public void A_ninja_hides_for_50_mana_and_stays_hidden_paying_one_every_2_s_while_it_can()
    {
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.footman", 0, 4, 4)), ruleset: Ninjas);
        EntityId ninja = OfType(d.World, "euro.footman").Single();
        d.World.Manas.Get(ninja).MilliMana = 54_000;
        Cast(d, ninja, "cloak");
        d.RunUntil(() => d.World.IsCloaked(ninja), 40);
        Assert.Equal(4, Mana(d, ninja)); // 50 paid; no mana comes back while it hides (0x4ebb91)
        d.RunUntil(() => false, 4 * 40); // 8 s: four more payments
        Assert.True(d.World.IsCloaked(ninja));
        Assert.InRange(Mana(d, ninja), 0, 1);
        d.RunUntil(() => !d.World.IsCloaked(ninja), 200); // out of mana: seen again
        d.Step();
        Assert.Equal(0, d.World.Statuses.Has(ninja) ? d.World.Statuses.Get(ninja).CloakDrain : 0);
        int seen = Mana(d, ninja);
        d.RunUntil(() => false, 40);
        Assert.True(Mana(d, ninja) > seen); // and it regains mana again
    }

    [Fact]
    [Trait("Rule", "MAG-039")]
    public void Frozen_or_stone_casters_and_a_healer_at_work_regain_no_mana()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.mage", 0, 4, 4), Unit("euro.priest", 0, 4, 8), Unit("euro.footman", 0, 6, 8)), ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId priest = OfType(d.World, "euro.priest").Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        d.World.Manas.Get(mage).MilliMana = 10_000;
        d.World.Statuses.Set(mage, new StatusEffects { FrozenUntil = 100_000 });
        d.RunUntil(() => false, 100);
        Assert.Equal(10, Mana(d, mage)); // frozen: none back (0x504e30)

        Grant(d.World, 0, "te_healing");
        d.World.Manas.Get(priest).MilliMana = 50_000;
        d.World.Healths.Get(footman).Current = 1;
        d.Order(0, CommandKind.SetStance, [footman], arg0: (int)CombatStance.HoldFire);
        Cast(d, priest, "spell_heal", footman);
        d.RunUntil(() => d.World.Casters.Get(priest).Phase != CastPhase.None, 40);
        int before = d.World.Manas.Get(priest).MilliMana;
        d.Step();
        Assert.True(d.World.Manas.Get(priest).MilliMana <= before); // healing with the mana for it: nothing comes back
    }

    [Fact]
    [Trait("Rule", "EXP-008")]
    public void Leadership_reaches_units_below_level_11_closer_than_the_heros_sight()
    {
        Ruleset rules = ClassicWith("euro.footman: { Armament: { attack: 100 } }\ncombat: { heroLevel: 1 }");
        using var d = new SimDriver(
            TestMap(64, 16, Structure("euro.castle", 1, 14, 5), Unit("euro.footman", 0, 12, 7), Unit("euro.archer", 0, 11, 7), Unit("euro.archer", 0, 11, 9), Unit("euro.archer", 0, 50, 7)),
            ruleset: rules);
        Grant(d.World, 0, "te_learning");
        Grant(d.World, 0, "te_heroism");
        Grant(d.World, 0, "te_nobility");
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId[] archers = [.. OfType(d.World, "euro.archer").OrderBy(a => d.PositionOf(a).X).ThenBy(a => d.PositionOf(a).Y)];
        d.Order(0, CommandKind.SetStance, archers, arg0: (int)CombatStance.HoldFire);
        d.World.Experiences.Get(archers[1]).Level = 11; // a veteran learns nothing from it
        ExperienceTests.Kills(d, footman, 1);
        Assert.True(d.World.IsHero(footman));
        int[] before = [.. archers.Select(a => d.World.Experiences.Get(a).Xp)];
        ExperienceTests.Kills(d, footman, 1);
        Assert.Equal(1, d.World.Experiences.Get(archers[0]).Xp - before[0]); // near, below level 11
        Assert.Equal(0, d.World.Experiences.Get(archers[1]).Xp - before[1]); // level 11
        Assert.Equal(0, d.World.Experiences.Get(archers[2]).Xp - before[2]); // beyond the hero's sight
    }
}
