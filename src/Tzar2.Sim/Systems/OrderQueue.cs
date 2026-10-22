using Tzar2.Core.Ids;
using Tzar2.Sim.Components;

namespace Tzar2.Sim.Systems;

/// <summary>
/// The orders a unit carries out one after another, given with Shift (<see cref="WaypointQueue"/>): points to walk to
/// (ORD-005), enemies to attack (CTRL-026), objects to guard (CTRL-027), or the points of a patrol path (ORD-006). An
/// order without Shift drops them (<see cref="GameplayCommands.ClearTasks"/>).
/// </summary>
internal static class OrderQueue
{
    /// <summary>
    /// Whether an order given with Shift waits for what the unit does now: a walk, an attack it was told to make, orders
    /// already queued, or (with <paramref name="guard"/>) the object it guards.
    /// </summary>
    public static bool IsBusy(World world, EntityId unit, bool guard)
    {
        if (world.WaypointQueues.Has(unit))
        {
            return true;
        }

        if (world.Motions.IndexOf(unit) is >= 0 and var m
            && (world.Motions.At(m).Order == MotionOrder.Move || (guard && world.Motions.At(m).Order == MotionOrder.Follow)))
        {
            return true;
        }

        return world.Combats.IndexOf(unit) is >= 0 and var c && world.Combats.At(c).Target != 0 && world.Combats.At(c).Mode == CombatMode.Explicit;
    }

    /// <summary>Adds <paramref name="order"/> at the end of the unit's queue; false when the queue is full.</summary>
    public static bool Add(World world, EntityId unit, in Waypoint order)
    {
        if (!world.WaypointQueues.Has(unit))
        {
            world.WaypointQueues.Set(unit, default);
        }

        ref WaypointQueue q = ref world.WaypointQueues.Get(unit);
        if (q.Count >= WaypointQueue.Capacity)
        {
            return false;
        }

        q.Points[q.Count++] = order;
        return true;
    }

    /// <summary>
    /// A point added with Shift to a patrolling unit's path (ORD-006): the path starts as the two ends of its patrol and
    /// is walked round in the order the points were given.
    /// </summary>
    public static bool AddPatrolPoint(World world, EntityId unit, int x, int y)
    {
        ref Motion m = ref world.Motions.Get(unit);
        if (!world.WaypointQueues.Has(unit))
        {
            WaypointQueue path = default;
            path.Points[0] = new Waypoint { X = m.PatrolX, Y = m.PatrolY, Kind = WaypointKind.Patrol };
            path.Points[1] = new Waypoint { X = m.OrderX, Y = m.OrderY, Kind = WaypointKind.Patrol };
            path.Count = 2;
            path.Cursor = 1; // walking to the second end
            world.WaypointQueues.Set(unit, path);
        }

        return Add(world, unit, new Waypoint { X = x, Y = y, Kind = WaypointKind.Patrol });
    }

    /// <summary>A patrolling unit reached a point of its patrol path and heads for the next one; false without a path.</summary>
    public static bool NextPatrolPoint(World world, EntityId unit, ref Motion m)
    {
        if (world.WaypointQueues.Count == 0 || world.WaypointQueues.IndexOf(unit) is not (>= 0 and var w)
            || world.WaypointQueues.At(w).Points[0].Kind != WaypointKind.Patrol)
        {
            return false;
        }

        ref WaypointQueue q = ref world.WaypointQueues.At(w);
        q.Cursor = (q.Cursor + 1) % q.Count;
        Waypoint next = q.Points[q.Cursor];
        m.OrderX = next.X;
        m.OrderY = next.Y;
        m.Replans = 0;
        MovementOrders.Request(world, unit, ref m, 0, next.X, next.Y);
        return true;
    }

    /// <summary>The unit's current order is over: it starts the next queued order that can still be carried out.</summary>
    public static void Next(World world, EntityId unit)
    {
        while (world.WaypointQueues.Count > 0 && world.WaypointQueues.IndexOf(unit) is >= 0 and var w)
        {
            ref WaypointQueue q = ref world.WaypointQueues.At(w);
            Waypoint next = q.Points[0];
            if (next.Kind == WaypointKind.Patrol)
            {
                world.WaypointQueues.Remove(unit); // a patrol path the unit no longer walks
                return;
            }

            for (int k = 1; k < q.Count; k++)
            {
                q.Points[k - 1] = q.Points[k];
            }

            q.Points[--q.Count] = default;
            if (q.Count == 0)
            {
                world.WaypointQueues.Remove(unit);
            }

            if (Start(world, unit, next))
            {
                return;
            }
        }
    }

    // Starts one queued order; false when it cannot be carried out any more (its target is gone).
    private static bool Start(World world, EntityId unit, in Waypoint next)
    {
        int c = world.Combats.IndexOf(unit);
        switch (next.Kind)
        {
            case WaypointKind.Attack:
            {
                EntityId target = EntityId.FromValue(next.Target);
                byte owner = world.Identities.Get(unit).Owner;
                if (c < 0 || !world.Entities.IsAlive(target) || !world.Identities.Has(target) || world.IsHeld(target) || !world.MayAttack(owner, target))
                {
                    return false;
                }

                ref Combat combat = ref world.Combats.At(c);
                combat.Target = next.Target;
                combat.Mode = CombatMode.Explicit;
                combat.HitInMs = -1;
                combat.ChaseTick = -1;
                combat.Resume = 0;
                return true;
            }

            case WaypointKind.Guard:
            {
                EntityId target = EntityId.FromValue(next.Target);
                if (!world.Entities.IsAlive(target) || world.Positions.IndexOf(target) is not (>= 0 and var t) || world.IsHeld(target))
                {
                    return false;
                }

                if (c >= 0 && world.Combats.At(c).Target == 0)
                {
                    world.Combats.At(c).Mode = CombatMode.None;
                }

                Position at = world.Positions.At(t);
                ref Motion m = ref world.Motions.Get(unit);
                m.Order = MotionOrder.Follow;
                m.FollowTarget = next.Target;
                m.OrderX = at.X;
                m.OrderY = at.Y;
                m.Replans = 0;
                MovementOrders.Request(world, unit, ref m, 0, at.X, at.Y);
                return true;
            }

            default:
            {
                if (c >= 0 && world.Combats.At(c).Target == 0)
                {
                    world.Combats.At(c).Mode = next.Mode;
                }

                ref Motion m = ref world.Motions.Get(unit);
                m.Order = MotionOrder.Move;
                m.OrderX = next.X;
                m.OrderY = next.Y;
                m.FollowTarget = 0;
                m.Replans = 0;
                MovementOrders.Request(world, unit, ref m, 0, next.X, next.Y);
                return true;
            }
        }
    }
}
