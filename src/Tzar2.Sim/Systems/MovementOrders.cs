using System;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;
using Tzar2.Sim.Navigation;

namespace Tzar2.Sim.Systems;

/// <summary>Shared transitions of <see cref="Motion"/> used by commands and the movement system.</summary>
public static class MovementOrders
{
    /// <summary>
    /// Starts moving <paramref name="entity"/> toward a point: invalidates any pending plan and queues a path request
    /// (air units fly straight and need none, REQ-PATH-10).
    /// </summary>
    public static void Request(World world, EntityId entity, ref Motion motion, int group, int targetX, int targetY)
    {
        ArgumentNullException.ThrowIfNull(world);
        motion.RequestSerial++;
        motion.State = MotionState.Moving;
        motion.GroupId = group;
        motion.GroupSpeed = 0;
        motion.TargetX = targetX;
        motion.TargetY = targetY;
        motion.SettleRadius = motion.Radius * 2;
        motion.GoalTile = -1;
        motion.PathCursor = 0;
        motion.StallTicks = 0;
        motion.BestDistance = int.MaxValue;
        world.Paths.Clear(entity.Index);
        if (motion.Layer == NavigationGrid.NoLayer)
        {
            motion.Mode = MotionMode.Direct;
            motion.SteerX = targetX;
            motion.SteerY = targetY;
            return;
        }

        motion.Mode = MotionMode.Waiting;
        world.PathQueue.Enqueue(new PathRequest { Entity = entity.Value, Serial = motion.RequestSerial, Group = group });
    }

    /// <summary>Stops a unit and drops its order and plan.</summary>
    public static void Stop(World world, EntityId entity, ref Motion motion)
    {
        ArgumentNullException.ThrowIfNull(world);
        motion.RequestSerial++;
        motion.State = MotionState.Idle;
        motion.Mode = MotionMode.None;
        motion.Order = MotionOrder.None;
        motion.FollowTarget = 0;
        motion.GroupId = 0;
        motion.VelocityX = 0;
        motion.VelocityY = 0;
        world.Paths.Clear(entity.Index);
    }

    /// <summary>Ends the current movement (arrival, settling, giving up), keeping the order for patrol/follow.</summary>
    internal static void Finish(World world, EntityId entity, ref Motion motion, MotionState state)
    {
        motion.State = state;
        motion.Mode = MotionMode.None;
        motion.VelocityX = 0;
        motion.VelocityY = 0;
        world.Paths.Clear(entity.Index);
    }
}
