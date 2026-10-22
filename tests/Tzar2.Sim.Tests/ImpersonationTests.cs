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

/// <summary>
/// MAG-012, MAG-021 (ADR 0184): a spy takes another kingdom's unit over, that kingdom still sees one of its own and
/// leaves it alone, and its spies see through it when close enough in front of it.
/// </summary>
public class ImpersonationTests
{
    // The synthetic tables have no spy: footmen get the spy's trait (no macemen, in place of magic creatures), peasants a detector's.
    private static readonly Ruleset Rules = ClassicWith("euro.footman: { Impersonator: { never: [maceman] } }\neuro.peon: { Detector: { radius: 4 } }");

    private const int West = 4;

    private static EntityId TakeOver(SimDriver d, EntityId spy, EntityId target)
    {
        d.Order(0, CommandKind.Impersonate, [spy], target: target);
        d.RunUntil(() => d.World.Disguises.Has(target), 400);
        return target;
    }

    [Fact]
    [Trait("Rule", "MAG-012")]
    public void A_spy_takes_an_enemy_unit_over_that_kingdom_leaves_it_alone_and_revealed_it_is_a_spy_again()
    {
        using var d = new SimDriver(TestMap(48, 12, Unit("euro.footman", 0, 4, 4), Unit("euro.archer", 1, 9, 4), Unit("euro.archer", 1, 36, 4)), ruleset: Rules);
        World w = d.World;
        EntityId spy = OfType(w, "euro.footman", 0).Single();
        ushort spyType = w.Identities.Get(spy).TypeId;
        EntityId look = OfType(w, "euro.archer", 1).First(a => w.Positions.Get(a).X < 20 * Tile);
        d.Order(0, CommandKind.SetStance, [spy], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [look], arg0: (int)CombatStance.HoldFire);

        d.Order(0, CommandKind.Impersonate, [spy], target: spy); // only another kingdom's unit
        d.Step();
        Assert.Equal([CommandRejection.InvalidArgument], d.Rejections());
        EntityId unit = TakeOver(d, spy, look);

        // The owner (batch J): the spy is gone; the archer is ours with its own type and stats, and seems kingdom 1's.
        Assert.False(w.Entities.IsAlive(spy));
        Assert.Equal((byte)0, w.Identities.Get(unit).Owner);
        Assert.Equal(Id(Rules, "euro.archer"), w.Identities.Get(unit).TypeId);
        Assert.Equal((spyType, (byte)1, (byte)0), (w.Disguises.Get(unit).TypeId, w.Disguises.Get(unit).Owner, w.Disguises.Get(unit).Mastery));
        Assert.True(w.LooksFriendlyTo(unit, 1));
        Assert.Equal(1, d.Count(SimEventKind.Impersonated));

        // It is state: hashed and restored.
        byte[] snapshot = d.Sim.CaptureSnapshot();
        using (Simulation restored = Simulation.Restore(snapshot, w.Ruleset, w.Map.Data))
        {
            Assert.True(restored.World.Disguises.Has(unit));
            Assert.Equal(d.Sim.ComputeFullHash(), restored.ComputeFullHash());
        }

        // Next to kingdom 1's other archer, which would shoot an enemy, nothing happens.
        int health = w.Healths.Get(unit).Current;
        d.MoveToTile(0, [unit], 33, 4);
        d.RunUntil(() => !d.AnyMoving([unit]), 600);
        d.RunUntil(() => false, 100);
        Assert.Equal(health, w.Healths.Get(unit).Current);

        // Revealed, it is a spy of ours at its place, and is shot at.
        d.Order(0, CommandKind.RevealSelf, [unit]);
        d.Step();
        Assert.False(w.Entities.IsAlive(unit));
        EntityId revealed = OfType(w, "euro.footman", 0).Single();
        Assert.Equal(0, w.Disguises.Count);
        d.RunUntil(() => w.Healths.Get(revealed).Current < w.Healths.Get(revealed).Max, 200);
        Assert.True(w.Healths.Get(revealed).Current < w.Healths.Get(revealed).Max);
    }

    [Fact]
    [Trait("Rule", "MAG-012")]
    public void A_taken_over_unit_keeps_its_look_while_fighting_and_a_spy_copies_nothing_on_its_list()
    {
        using var d = new SimDriver(TestMap(40, 12, Unit("euro.footman", 0, 4, 4), Unit("euro.archer", 1, 9, 4), Unit("euro.archer", 1, 20, 4), Unit("euro.maceman", 1, 14, 9)), ruleset: Rules);
        World w = d.World;
        EntityId spy = OfType(w, "euro.footman", 0).Single();
        EntityId look = OfType(w, "euro.archer", 1).First(a => w.Positions.Get(a).X < 12 * Tile);
        EntityId other = OfType(w, "euro.archer", 1).First(a => a != look);
        d.Order(0, CommandKind.SetStance, [spy], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [look], arg0: (int)CombatStance.HoldFire);
        d.Order(0, CommandKind.Impersonate, [spy], target: OfType(w, "euro.maceman", 1).Single());
        d.Step();
        Assert.Equal([CommandRejection.NotAccepted], d.Rejections());

        EntityId unit = TakeOver(d, spy, look);
        d.Order(0, CommandKind.Attack, [unit], target: other);
        d.RunUntil(() => w.Healths.Get(other).Current < w.Healths.Get(other).Max, 800);
        Assert.True(w.Healths.Get(other).Current < w.Healths.Get(other).Max);
        Assert.True(w.Disguises.Has(unit)); // the owner: fighting does not end the disguise
    }

    [Fact]
    [Trait("Rule", "MAG-012")]
    public void Only_the_copied_kingdoms_spy_unmasks_it_close_by_and_only_in_front_of_it()
    {
        // Detectors: player 2's peasant at (30, 4), player 1's at (30, 9).
        using var d = new SimDriver(TestMap(40, 14, Unit("euro.footman", 0, 4, 4), Unit("euro.archer", 1, 9, 4), Unit("euro.peon", 2, 30, 4), Unit("euro.peon", 1, 30, 9)), ruleset: Rules, players: 3);
        World w = d.World;
        EntityId spy = OfType(w, "euro.footman", 0).Single();
        EntityId look = OfType(w, "euro.archer", 1).Single();
        EntityId detector = OfType(w, "euro.peon", 1).Single();
        d.Order(0, CommandKind.SetStance, [spy], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [look, detector], arg0: (int)CombatStance.HoldFire);
        d.Order(2, CommandKind.SetStance, [.. OfType(w, "euro.peon", 2)], arg0: (int)CombatStance.HoldFire);
        EntityId unit = TakeOver(d, spy, look);
        foreach (EntityId p in OfType(w, "euro.peon"))
        {
            w.Identities.Get(p).Facing = West;
        }

        d.MoveToTile(0, [unit], 28, 4); // right in front of player 2's spy: a third kingdom does not see through it (batch J)
        d.RunUntil(() => !d.AnyMoving([unit]), 600);
        d.RunUntil(() => false, 20);
        Assert.True(w.Disguises.Has(unit));

        w.Identities.Get(detector).Facing = 0; // player 1's spy looks east, away from it
        d.MoveToTile(0, [unit], 29, 9);
        d.RunUntil(() => !d.AnyMoving([unit]), 600);
        d.RunUntil(() => false, 20);
        Assert.True(w.Disguises.Has(unit)); // behind its back (the owner)

        w.Identities.Get(detector).Facing = West; // it turns round
        d.RunUntil(() => !w.Disguises.Has(unit), 10);
        Assert.False(w.Disguises.Has(unit));
        Assert.Single(OfType(w, "euro.footman", 0)); // a spy again
    }

    [Fact]
    [Trait("Rule", "MAG-021")]
    public void The_meter_fills_in_its_time_and_a_new_takeover_starts_it_again()
    {
        using var d = new SimDriver(TestMap(40, 12, Unit("euro.footman", 0, 4, 4), Unit("euro.archer", 1, 6, 4), Unit("euro.archer", 1, 6, 8)), ruleset: Rules);
        World w = d.World;
        EntityId spy = OfType(w, "euro.footman", 0).Single();
        EntityId[] archers = [.. OfType(w, "euro.archer", 1)];
        d.Order(0, CommandKind.SetStance, [spy], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, archers, arg0: (int)CombatStance.HoldFire);
        EntityId unit = TakeOver(d, spy, archers[0]);
        Assert.InRange(w.Disguises.Get(unit).Mastery, 0, 1);

        // Full after DisguiseMasteryMs, and no further: one point per 1.25 s (batch J, manual.yaml spy-disguise).
        Assert.Equal(125_000, w.Ruleset.Gameplay.DisguiseMasteryMs);
        int ticks = w.Ruleset.Gameplay.DisguiseMasteryMs / w.Config.TickLengthMilliseconds;
        d.RunUntil(() => false, ticks / 2);
        Assert.InRange(w.Disguises.Get(unit).Mastery, 48, 52);
        d.RunUntil(() => false, (ticks / 2) + 100);
        Assert.Equal(100, w.Disguises.Get(unit).Mastery);

        // Revealed and taking another unit over, the meter starts from nothing (batch J run 1).
        d.Order(0, CommandKind.RevealSelf, [unit]);
        d.Step();
        EntityId again = OfType(w, "euro.footman", 0).Single();
        d.Order(0, CommandKind.SetStance, [again], arg0: (int)CombatStance.HoldFire);
        EntityId second = TakeOver(d, again, archers[1]);
        Assert.InRange(w.Disguises.Get(second).Mastery, 0, 1);
    }

    [Fact]
    [Trait("Rule", "MAG-021")]
    public void The_fuller_the_meter_the_closer_the_copied_kingdoms_spy_must_come()
    {
        // Player 1's peasant spy at (30, 4) faces west; the reach is 4 tiles for an empty meter and 1 for a full one.
        using var d = new SimDriver(TestMap(40, 12, Unit("euro.footman", 0, 4, 4), Unit("euro.archer", 1, 9, 4), Unit("euro.peon", 1, 30, 4)), ruleset: Rules);
        World w = d.World;
        EntityId spy = OfType(w, "euro.footman", 0).Single();
        EntityId look = OfType(w, "euro.archer", 1).Single();
        EntityId detector = OfType(w, "euro.peon", 1).Single();
        d.Order(0, CommandKind.SetStance, [spy], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.SetStance, [look, detector], arg0: (int)CombatStance.HoldFire);
        EntityId unit = TakeOver(d, spy, look);
        w.Identities.Get(detector).Facing = West;
        void Meter(byte value) => w.Disguises.At(w.Disguises.IndexOf(unit)).Mastery = value;

        // Half full: 2.5 tiles; 3 tiles off is not enough.
        Meter(50);
        d.MoveToTile(0, [unit], 27, 4);
        d.RunUntil(() => !d.AnyMoving([unit]) || !w.Disguises.Has(unit), 600);
        d.RunUntil(() => false, 20);
        Assert.True(w.Disguises.Has(unit));

        // Full: 2 tiles off is not enough either; right next to it is.
        Meter(100);
        d.MoveToTile(0, [unit], 28, 4);
        d.RunUntil(() => !d.AnyMoving([unit]) || !w.Disguises.Has(unit), 600);
        d.RunUntil(() => false, 20);
        Assert.True(w.Disguises.Has(unit));
        d.MoveToTile(0, [unit], 29, 4);
        d.RunUntil(() => !w.Disguises.Has(unit), 600);
        Assert.False(w.Disguises.Has(unit));
    }
}
