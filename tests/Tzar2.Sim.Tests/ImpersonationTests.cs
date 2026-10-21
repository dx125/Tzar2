using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Snapshots;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>MAG-012 (ADR 0184): a spy takes another kingdom's unit's look and that kingdom leaves it alone.</summary>
public class ImpersonationTests
{
    // The synthetic tables have no spy: footmen get the spy's trait (no macemen, in place of magic creatures), peasants a detector's.
    private static readonly Ruleset Rules = ClassicWith("euro.footman: { Impersonator: { never: [maceman] } }\neuro.peon: { Detector: { radius: 4 } }");

    [Fact]
    [Trait("Rule", "MAG-012")]
    public void A_spy_takes_an_enemy_units_look_and_that_kingdom_leaves_it_alone_until_it_reveals_itself()
    {
        using var d = new SimDriver(TestMap(48, 12, Unit("euro.footman", 0, 4, 4), Unit("euro.footman", 1, 9, 4), Unit("euro.archer", 1, 36, 4)), ruleset: Rules);
        World w = d.World;
        EntityId spy = OfType(w, "euro.footman", 0).Single();
        EntityId look = OfType(w, "euro.footman", 1).Single();
        EntityId archer = OfType(w, "euro.archer", 1).Single();
        d.Order(0, CommandKind.SetStance, [spy], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [look], arg0: (int)CombatStance.HoldFire);

        d.Order(0, CommandKind.Impersonate, [spy], target: spy); // only another kingdom's unit
        d.Order(0, CommandKind.Impersonate, [spy], target: look);
        d.Step();
        Assert.Equal([CommandRejection.InvalidArgument], d.Rejections());
        d.RunUntil(() => w.Disguises.Has(spy), 400);
        Assert.Equal(((byte)1, w.Identities.Get(look).TypeId), (w.Disguises.Get(spy).Owner, w.Disguises.Get(spy).TypeId));
        Assert.True(w.LooksFriendlyTo(spy, 1));
        Assert.False(w.LooksFriendlyTo(spy, 0));
        Assert.Equal(1, d.Count(SimEventKind.Impersonated));

        // It is state: hashed and restored.
        byte[] snapshot = d.Sim.CaptureSnapshot();
        using (Simulation restored = Simulation.Restore(snapshot, w.Ruleset, w.Map.Data))
        {
            Assert.True(restored.World.Disguises.Has(spy));
            Assert.Equal(d.Sim.ComputeFullHash(), restored.ComputeFullHash());
        }

        // Next to the archer, which would shoot a spy, nothing happens.
        int health = w.Healths.Get(spy).Current;
        d.MoveToTile(0, [spy], 33, 4);
        d.RunUntil(() => !d.AnyMoving([spy]), 600);
        d.RunUntil(() => false, 100);
        Assert.Equal(health, w.Healths.Get(spy).Current);

        // Revealed, it is shot at.
        d.Order(0, CommandKind.RevealSelf, [spy]);
        d.RunUntil(() => false, 100);
        Assert.False(w.Disguises.Has(spy));
        Assert.True(w.Healths.Get(spy).Current < health);
    }

    [Fact]
    [Trait("Rule", "MAG-012")]
    public void A_disguised_spy_keeps_its_disguise_while_fighting_and_copies_nothing_on_its_list()
    {
        using var d = new SimDriver(TestMap(40, 12, Unit("euro.footman", 0, 4, 4), Unit("euro.footman", 1, 9, 4), Unit("euro.archer", 1, 20, 4), Unit("euro.maceman", 1, 14, 9)), ruleset: Rules);
        World w = d.World;
        EntityId spy = OfType(w, "euro.footman", 0).Single();
        EntityId look = OfType(w, "euro.footman", 1).Single();
        d.Order(0, CommandKind.SetStance, [spy], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [look], arg0: (int)CombatStance.HoldFire);
        d.Order(0, CommandKind.Impersonate, [spy], target: OfType(w, "euro.maceman", 1).Single());
        d.Step();
        Assert.Equal([CommandRejection.NotAccepted], d.Rejections());

        d.Order(0, CommandKind.Impersonate, [spy], target: look);
        d.RunUntil(() => w.Disguises.Has(spy), 400);
        d.Order(0, CommandKind.Attack, [spy], target: OfType(w, "euro.archer", 1).Single());
        d.RunUntil(() => w.Healths.Get(OfType(w, "euro.archer", 1).Single()).Current < w.Healths.Get(OfType(w, "euro.archer", 1).Single()).Max, 800);
        Assert.True(w.Disguises.Has(spy)); // the owner: fighting does not end the disguise
    }

    [Fact]
    [Trait("Rule", "MAG-012")]
    public void Another_kingdoms_spy_close_by_unmasks_a_disguised_spy_but_an_allys_does_not()
    {
        using var d = new SimDriver(TestMap(40, 12, Unit("euro.footman", 0, 4, 4), Unit("euro.footman", 1, 9, 4), Unit("euro.peon", 2, 30, 4), Unit("euro.peon", 1, 30, 9)), ruleset: Rules, players: 3);
        World w = d.World;
        EntityId spy = OfType(w, "euro.footman", 0).Single();
        EntityId look = OfType(w, "euro.footman", 1).Single();
        d.Order(0, CommandKind.SetStance, [spy], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [look], arg0: (int)CombatStance.HoldFire);
        d.Order(2, CommandKind.SetDiplomacy, [], x: 0, arg0: (int)Kingdoms.Stance.Ally);
        d.Order(0, CommandKind.Impersonate, [spy], target: look);
        d.RunUntil(() => w.Disguises.Has(spy), 400);

        d.MoveToTile(0, [spy], 28, 4); // next to player 2's detector: an ally of player 0
        d.RunUntil(() => !d.AnyMoving([spy]), 600);
        Assert.True(w.Disguises.Has(spy));
        d.MoveToTile(0, [spy], 28, 9); // next to player 1's detector
        d.RunUntil(() => !w.Disguises.Has(spy), 600);
        Assert.False(w.Disguises.Has(spy));
    }
}
