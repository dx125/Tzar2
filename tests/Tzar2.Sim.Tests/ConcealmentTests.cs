using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Xunit;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// Streams E and F together (ADRs 0093, 0095, 0100): invisible units and units inside holders are hidden from other
/// kingdoms' fog and targeting; a garrisoned unit reveals nothing.
/// </summary>
public class ConcealmentTests
{
    [Fact]
    public void An_invisible_unit_is_hidden_from_the_enemys_sight_and_targeting()
    {
        using var d = new SimDriver(TestMap(32, 12, Unit("euro.mage", 0, 2, 4), Unit("euro.footman", 0, 7, 4), Unit("euro.footman", 1, 10, 4)), ruleset: Classic);
        EntityId mage = OfType(d.World, "euro.mage").Single();
        EntityId own = OfType(d.World, "euro.footman", 0).Single();
        EntityId enemy = OfType(d.World, "euro.footman", 1).Single();
        d.Order(0, CommandKind.SetStance, [mage, own], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [enemy], arg0: (int)CombatStance.HoldFire);
        d.Step();
        Assert.True(d.World.Visibility.CanSee(d.World, 1, own)); // in the enemy's sight before the spell

        d.Order(0, CommandKind.CastSpell, [mage], target: own, arg0: Spell(Classic, "spell_cloak"));
        d.RunUntil(() => d.World.IsCloaked(own), 200);
        d.Step();
        Assert.False(d.World.Visibility.CanSee(d.World, 1, own)); // fog: invisible
        Assert.True(d.World.Visibility.CanSee(d.World, 0, own)); // its own kingdom still sees it

        d.Order(1, CommandKind.SetStance, [enemy], arg0: (int)CombatStance.Aggressive);
        d.RunUntil(() => false, 60);
        Assert.NotEqual(own.Value, d.World.Combats.Get(enemy).Target); // not acquired
        d.Order(1, CommandKind.Attack, [enemy], target: own);
        d.RunUntil(() => false, 20);
        Assert.Equal(70, Hp(d, own)); // an explicit order does not reach it either
    }

    [Fact]
    public void A_unit_inside_a_holder_is_hidden_and_reveals_nothing()
    {
        using var d = new SimDriver(TestMap(48, 16, Structure("euro.castle", 0, 2, 4), Unit("euro.footman", 0, 30, 7), Unit("euro.footman", 1, 34, 7)), ruleset: Classic);
        EntityId castle = OfType(d.World, "euro.castle").Single();
        EntityId footman = OfType(d.World, "euro.footman", 0).Single();
        EntityId enemy = OfType(d.World, "euro.footman", 1).Single();
        d.Order(1, CommandKind.SetStance, [enemy], arg0: (int)CombatStance.HoldFire);
        d.Order(0, CommandKind.SetStance, [footman], arg0: (int)CombatStance.HoldFire);
        d.Step();
        Assert.True(d.World.Visibility.IsVisible(0, 34, 7)); // the footman sees the enemy

        d.Order(0, CommandKind.Board, [footman], target: castle);
        d.RunUntil(() => d.World.IsHeld(footman), 1200);
        d.RunUntil(() => false, 10);
        Assert.False(d.World.Visibility.IsVisible(0, 34, 7)); // garrisoned, it reveals nothing out there
        Assert.False(d.World.Visibility.CanSee(d.World, 1, footman));
    }
}
