using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Xunit;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>Rules of the magic chapter that were implemented without a test naming them.</summary>
public class MagicRulesTests
{
    [Fact]
    [Trait("Rule", "MAG-006")]
    public void Raise_dead_raises_the_dead_men_but_never_a_destroyed_building()
    {
        using var d = new SimDriver(
            TestMap(32, 16, Unit("euro.mage", 0, 4, 4), Unit("euro.peon", 1, 8, 4), Structure("euro.house", 1, 8, 7)),
            ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        d.Order(1, CommandKind.DebugKill, [OfType(d.World, "euro.peon").Single(), OfType(d.World, "euro.house").Single()]);
        d.Step();
        Assert.Empty(OfType(d.World, "euro.house"));
        Grant(d.World, 0, "te_necromancy");
        d.Order(0, CommandKind.CastSpell, [mage], arg0: Spell(d.World.Ruleset, "raisedead"));
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 1, 200);
        Assert.Single(OfType(d.World, "comm.skeleton", 0)); // the peasant's body; the house's ruin raises nothing
    }
}
