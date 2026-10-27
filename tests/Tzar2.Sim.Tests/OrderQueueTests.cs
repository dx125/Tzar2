using System.Collections.Generic;
using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// Orders given with Shift wait for the current one (manual ch. 5 §2.3): a set of enemies attacked in turn (CTRL-026),
/// a set of objects guarded in turn (CTRL-027), a patrol path of several points (ORD-006).
/// </summary>
public class OrderQueueTests
{
    private static int C(int tile) => (tile * Tile) + (Tile / 2);

    [Fact]
    [Trait("Rule", "CTRL-026")]
    public void Shift_attacks_a_set_of_enemies_in_turn_and_then_walks_on()
    {
        using var d = new SimDriver(
            TestMap(40, 20, Unit("euro.knight", 0, 4, 9), Unit("euro.peon", 1, 12, 4), Unit("euro.peon", 1, 12, 15), Unit("euro.peon", 1, 20, 9)),
            ruleset: Classic);
        EntityId knight = OfType(d.World, "euro.knight").Single();
        EntityId[] peons = [.. OfType(d.World, "euro.peon")];
        d.Order(1, CommandKind.SetStance, peons, arg0: (int)CombatStance.HoldFire);
        EntityId a = peons.Single(p => d.PositionOf(p).Y < 6 * Tile);
        EntityId b = peons.Single(p => d.PositionOf(p).Y > 12 * Tile);
        EntityId c = peons.Single(p => d.PositionOf(p).X > 16 * Tile);
        d.Order(0, CommandKind.Attack, [knight], target: a);
        d.Order(0, CommandKind.Attack, [knight], target: b, flags: CommandFlags.Queue);
        d.Order(0, CommandKind.Attack, [knight], target: c, flags: CommandFlags.Queue);
        d.Order(0, CommandKind.Move, [knight], C(30), C(9), flags: CommandFlags.Queue);
        d.Step();
        Assert.Equal(3, d.World.WaypointQueues.Get(knight).Count);

        d.RunUntil(() => d.Count(SimEventKind.Killed) >= 3, 6000);
        List<EntityId> killed = [.. d.Events.Where(x => x.Event.Kind == SimEventKind.Killed).Select(x => x.Event.Entity)];
        Assert.Equal([a, b, c], killed);
        d.RunUntil(() => !d.IsMoving(knight) && d.PositionOf(knight).X > 27 * Tile, 2000);
        Assert.InRange(d.PositionOf(knight).X / Tile, 28, 32);
        Assert.False(d.World.WaypointQueues.Has(knight));
    }

    [Fact]
    [Trait("Rule", "CTRL-027")]
    public void Shift_guards_a_set_of_objects_in_turn()
    {
        using var d = new SimDriver(
            TestMap(40, 20, Unit("euro.footman", 0, 4, 9), Unit("euro.peon", 0, 12, 4), Unit("euro.peon", 0, 20, 15)),
            ruleset: Classic);
        EntityId guard = OfType(d.World, "euro.footman").Single();
        EntityId first = OfType(d.World, "euro.peon").Single(p => d.PositionOf(p).Y < 6 * Tile);
        EntityId second = OfType(d.World, "euro.peon").Single(p => d.PositionOf(p).Y > 12 * Tile);
        d.Order(0, CommandKind.Follow, [guard], target: first);
        d.Order(0, CommandKind.Follow, [guard], target: second, flags: CommandFlags.Queue);
        d.RunUntil(() => false, 120);
        Assert.Equal(MotionOrder.Follow, d.MotionOf(guard).Order);
        Assert.Equal(first.Value, d.MotionOf(guard).FollowTarget);

        d.Order(0, CommandKind.Delete, [first]);
        d.RunUntil(() => d.MotionOf(guard).FollowTarget == second.Value, 60);
        Assert.Equal(MotionOrder.Follow, d.MotionOf(guard).Order);
        d.RunUntil(() => false, 600);
        Position g = d.PositionOf(guard), s = d.PositionOf(second);
        Assert.True(System.Math.Abs((g.X / Tile) - (s.X / Tile)) <= 3 && System.Math.Abs((g.Y / Tile) - (s.Y / Tile)) <= 3, "the guard went to the second object"); // within 3 tiles (ORD-001)
    }

    [Fact]
    [Trait("Rule", "ORD-006")]
    public void Shift_makes_a_patrol_path_walked_round_in_the_order_given()
    {
        using var d = new SimDriver(TestMap(40, 20, Unit("euro.footman", 0, 3, 3)), ruleset: Classic);
        EntityId u = OfType(d.World, "euro.footman").Single();
        d.Order(0, CommandKind.Patrol, [u], C(14), C(3));
        d.Order(0, CommandKind.Patrol, [u], C(14), C(14), flags: CommandFlags.Queue);
        d.Order(0, CommandKind.Patrol, [u], C(3), C(14), flags: CommandFlags.Queue);
        d.Step();
        Assert.Equal(4, d.World.WaypointQueues.Get(u).Count);

        var stops = new List<(int X, int Y)>();
        int arrived = 0;
        d.RunUntil(() =>
        {
            if (d.Count(SimEventKind.Arrived) > arrived)
            {
                arrived = d.Count(SimEventKind.Arrived);
                Position p = d.PositionOf(u);
                stops.Add((((p.X / Tile) + 1) / 11, ((p.Y / Tile) + 1) / 11)); // 0 near tile 3, 1 near tile 14
            }

            return arrived >= 6;
        }, 8000);
        Assert.Equal([(1, 0), (1, 1), (0, 1), (0, 0), (1, 0), (1, 1)], stops);
        Assert.Equal(MotionOrder.Patrol, d.MotionOf(u).Order);
    }
}
