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

/// <summary>
/// Tournaments and Mastery, the Asian ways of sharing experience, as ReTzar reads them from the original's code (ADR
/// 0253). The synthetic tables have no Asian civilization, so European peasants and priests stand in for the pupils.
/// </summary>
public class TournamentsMasteryTests
{
    private static readonly Ruleset Pupils = ClassicWith(
        "combat: { tournamentPupils: { classes: [peon, priest] } }\nspell_raiseexp: { Spell: { targets: { classes: [peon, monk, ninja, priest] } } }");

    [Fact]
    [Trait("Rule", "TECH-029")]
    public void Every_75_points_the_kingdom_learns_teach_each_pupil_one_and_the_rest_is_lost()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.footman", 0, 4, 4), Unit("euro.peon", 0, 8, 4), Unit("euro.priest", 0, 10, 4), Unit("euro.peon", 1, 20, 4)), ruleset: Pupils);
        World w = d.World;
        foreach (int player in new[] { 0, 1 })
        {
            Grant(w, player, "te_learning");
        }

        EntityId footman = OfType(w, "euro.footman").Single();
        EntityId peon = OfType(w, "euro.peon", 0).Single();
        EntityId priest = OfType(w, "euro.priest").Single();
        EntityId foreign = OfType(w, "euro.peon", 1).Single();
        int Xp(EntityId e) => w.Experiences.Get(e).Xp;
        void Learn(int points)
        {
            w.Experiences.Get(footman).PendingXp += points;
            d.Step();
        }

        Learn(80); // no Tournaments yet
        Assert.Equal((0, 0), (Xp(peon), Xp(priest)));

        Grant(w, 0, "te_tournaments");
        Learn(74);
        Assert.Equal(74, w.Players[0].KungFuPool);
        Assert.Equal((0, 0), (Xp(peon), Xp(priest)));
        Learn(1);
        Assert.Equal(0, w.Players[0].KungFuPool);
        Assert.Equal((1, 1), (Xp(peon), Xp(priest)));
        Assert.Equal(0, Xp(foreign)); // another kingdom's

        Learn(300); // one lesson; what is over 75 is lost
        Assert.Equal(0, w.Players[0].KungFuPool);
        Assert.Equal((2, 2), (Xp(peon), Xp(priest)));
    }

    [Fact]
    [Trait("Rule", "TECH-028")]
    public void A_priest_teaches_only_a_pupil_of_a_lower_level_than_its_own()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.priest", 0, 4, 4), Unit("euro.peon", 0, 6, 4), Unit("euro.footman", 0, 6, 6)), ruleset: Pupils);
        World w = d.World;
        Grant(w, 0, "te_mastery");
        Grant(w, 0, "te_heroism");
        EntityId priest = OfType(w, "euro.priest").Single();
        EntityId peon = OfType(w, "euro.peon").Single();
        EntityId footman = OfType(w, "euro.footman").Single();
        d.Order(0, CommandKind.SetStance, [priest, peon, footman], arg0: (int)CombatStance.HoldFire);
        ushort spell = Spell(w.Ruleset, "spell_raiseexp");
        void Teach(EntityId pupil) => d.Order(0, CommandKind.CastSpell, [priest], 0, 0, pupil, arg0: spell);

        // A teacher of level 0 teaches nobody.
        Teach(peon);
        d.RunUntil(() => false, 40);
        Assert.Equal(0, d.Count(SimEventKind.SpellCast));

        w.Experiences.Get(priest).Level = 3;
        Teach(peon);
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 1, 200);
        Assert.Equal(1, w.Experiences.Get(peon).Xp); // one point (ReTzar, 0x473bf0)

        // Not a pupil of its kind: the footman learns nothing.
        d.RunUntil(() => false, 80);
        Teach(footman);
        d.RunUntil(() => false, 80);
        Assert.Equal(1, d.Count(SimEventKind.SpellCast));
        Assert.Equal(0, w.Experiences.Get(footman).Xp);

        // A pupil as high as its teacher is not taught.
        w.Experiences.Get(peon).Level = 3;
        Teach(peon);
        d.RunUntil(() => false, 80);
        Assert.Equal(1, d.Count(SimEventKind.SpellCast));
    }
}
