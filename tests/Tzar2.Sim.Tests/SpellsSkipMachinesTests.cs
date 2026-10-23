using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>Healing, freezing, the curse and raise dead skip machines and the bodiless (MAG-022, ADR 0254).</summary>
public class SpellsSkipMachinesTests
{
    private const int Tile = Map.MapState.UnitsPerTile;

    [Fact]
    [Trait("Rule", "MAG-022")]
    public void Healing_and_freezing_do_not_take_on_a_machine()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.priest", 0, 4, 4), Unit("comm.catapult", 0, 6, 4), Unit("euro.footman", 0, 6, 6)), ruleset: Classic);
        EntityId priest = OfType(d.World, "euro.priest").Single();
        EntityId catapult = OfType(d.World, "comm.catapult").Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        foreach (EntityId e in new[] { catapult, footman })
        {
            d.World.Healths.Get(e).Current = d.World.Healths.Get(e).Max / 2;
        }

        int catapultHp = Hp(d, catapult);
        int footmanHp = Hp(d, footman);
        MagicSystem.ApplyEffect(d.World, Spell(d.World.Ruleset, "spell_heal"), priest, 0, catapult, 0, 0, []);
        MagicSystem.ApplyEffect(d.World, Spell(d.World.Ruleset, "spell_heal"), priest, 0, footman, 0, 0, []);
        Assert.Equal(catapultHp, Hp(d, catapult));
        Assert.True(Hp(d, footman) > footmanHp);

        MagicSystem.ApplyEffect(d.World, Spell(d.World.Ruleset, "spell_freeze"), priest, 1, catapult, 0, 0, []);
        Assert.False(d.World.Statuses.Has(catapult));
    }

    [Fact]
    [Trait("Rule", "MAG-022")]
    public void Raise_dead_leaves_the_bodies_of_machines_and_of_the_bodiless()
    {
        using var d = new SimDriver(
            TestMap(32, 12, Unit("euro.mage", 0, 4, 4), Unit("euro.peon", 1, 10, 3), Unit("comm.ork", 1, 10, 4), Unit("comm.catapult", 1, 10, 6)),
            ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        d.Order(1, CommandKind.DebugKill, [.. OfType(d.World, "euro.peon"), .. OfType(d.World, "comm.ork"), .. OfType(d.World, "comm.catapult")]);
        d.Step();
        int corpses = d.World.Decals.Count;
        Grant(d.World, 0, "te_necromancy");
        d.Order(0, CommandKind.CastSpell, [mage], 10 * Tile, 4 * Tile, arg0: Spell(d.World.Ruleset, "raisedead"));
        d.RunUntil(() => d.Count(SimEventKind.SpellCast) == 1, 200);
        Assert.Single(OfType(d.World, "comm.skeleton", 0)); // the peasant's body only
        Assert.Equal(corpses - 1, d.World.Decals.Count);
    }
}
