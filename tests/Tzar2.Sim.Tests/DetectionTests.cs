using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Xunit;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>GAME-12 detection (ADR 0101) with stream E's cloaking and holders: a detector sees cloaked units near it, not from inside a holder.</summary>
public class DetectionTests
{
    // Test rules: player 1's peasants detect cloaked units within 6 tiles (the classic detectors are spies and ninjas).
    private static readonly Ruleset Rules = ClassicWith("euro.peon: { Detector: { radius: 6 } }");

    [Fact]
    [Trait("Rule", "FOG-006")]
    public void A_detector_reveals_a_cloaked_unit_but_not_from_inside_a_holder()
    {
        using var d = new SimDriver(
            TestMap(40, 12, Unit("euro.mage", 0, 2, 4), Unit("euro.footman", 0, 8, 4), Unit("euro.peon", 1, 10, 4), Structure("euro.castle", 1, 11, 2)),
            ruleset: Rules);
        World w = d.World;
        EntityId mage = OfType(w, "euro.mage").Single();
        EntityId footman = OfType(w, "euro.footman", 0).Single();
        EntityId detector = OfType(w, "euro.peon", 1).Single();
        d.Order(0, CommandKind.SetStance, [mage, footman], arg0: (int)CombatStance.HoldFire);
        d.Order(0, CommandKind.CastSpell, [mage], target: footman, arg0: Spell(Rules, "spell_cloak"));
        d.RunUntil(() => w.IsCloaked(footman), 200);
        d.Step();
        Assert.True(w.IsConcealed(footman));
        Assert.True(w.Visibility.CanSee(w, 1, footman)); // the peasant two tiles away detects it

        d.Order(1, CommandKind.Board, [detector], target: OfType(w, "euro.castle", 1).Single());
        d.RunUntil(() => w.IsHeld(detector), 1200);
        d.Step();
        Assert.True(w.IsCloaked(footman));
        Assert.False(w.Visibility.CanSee(w, 1, footman)); // garrisoned (the castle centre is within 6 tiles), the detector sees nothing
    }
}
