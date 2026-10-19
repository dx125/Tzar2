using System;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Naval orders (REQ-PATH-09, ADRs 0140 and 0143): ships meet boarders and repairers at the shore nearest to both,
/// sail to the shore to unload, and units (ships too) explore. Validated when they execute, like every command.
/// </summary>
internal static class NavalCommands
{
    /// <summary>Explore: own mobile units on the map head for unexplored ground they can reach, until there is none.</summary>
    public static CommandRejection? Explore(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        int applied = 0;
        foreach (EntityId unit in entities)
        {
            if (!world.Entities.IsAlive(unit) || world.Identities.IndexOf(unit) is not (>= 0 and var i) || world.Identities.At(i).Owner != command.Player
                || world.Motions.IndexOf(unit) is not (>= 0 and var m) || world.Motions.At(m).SpeedPerTick <= 0 || world.IsHeld(unit)
                || world.Navigation.ForType(world.Identities.At(i).TypeId) is null)
            {
                continue;
            }

            GameplayCommands.ClearTasks(world, unit);
            Interaction.Halt(world, unit);
            world.Explorers.Set(unit, new Exploring { GoalTile = -1, NextTick = world.Tick.Value });
            applied++;
        }

        return applied > 0 ? null : CommandRejection.NoValidEntities;
    }

    /// <summary>
    /// A ship that land units come to (boarding, repair) sails to the dock nearest to them and to it: the land tile in
    /// the component of the first unit, nearest to the units' centroid (REQ-PATH-09).
    /// </summary>
    public static void MeetAtShore(World world, EntityId ship, ReadOnlySpan<EntityId> units, DockPurpose purpose)
    {
        if (!world.IsShip(ship) || world.IsHeld(ship) || !world.Motions.Has(ship))
        {
            return;
        }

        long sx = 0;
        long sy = 0;
        int n = 0;
        EntityId first = EntityId.None;
        foreach (EntityId unit in units)
        {
            if (unit == ship || !world.Entities.IsAlive(unit) || !world.Positions.Has(unit) || world.IsHeld(unit) || world.IsShip(unit))
            {
                continue;
            }

            first = first.IsNone ? unit : first;
            Position p = world.Positions.Get(unit);
            sx += p.X;
            sy += p.Y;
            n++;
        }

        if (n == 0)
        {
            return;
        }

        ushort landType = world.Identities.Get(first).TypeId;
        Position from = world.Positions.Get(first);
        int component = Shore.LandComponentAt(world, landType, from.X, from.Y);
        if (Shore.FindDock(world, ship, world.Identities.Get(ship).TypeId, world.Positions.Get(ship), landType, component, (int)(sx / n), (int)(sy / n), out _, out Position water))
        {
            NavalSystem.StartDock(world, ship, purpose, water, 0, 0, 0);
        }
    }

    /// <summary>
    /// Unload from a ship: where there is land of the target's component next to it the units come out now; else the
    /// ship sails to the dock nearest to the point (or to itself) and lets them out there.
    /// </summary>
    /// <returns>Whether the ship unloads now or is on its way.</returns>
    public static bool UnloadShip(World world, EntityId ship, bool toPoint, int x, int y, int limit)
    {
        EntityId first = FirstInside(world, ship);
        if (first.IsNone)
        {
            return false;
        }

        Position at = world.Positions.Get(ship);
        int tx = toPoint ? x : at.X;
        int ty = toPoint ? y : at.Y;
        ushort landType = world.Identities.Get(first).TypeId;
        // The land the point belongs to (looked for further around a point inside a town or on water); without any, the
        // units come out here only when this is near the point (ADR 0146: never back on the shore they left).
        int component = toPoint ? Shore.LandComponentAt(world, landType, x, y, Shore.UnloadSearchTiles) : -1;
        long near = (long)Shore.ComponentSearchTiles * Map.MapState.UnitsPerTile;
        if (world.ExitPoint(ship, landType, tx, ty, out int ex, out int ey)
            && (!toPoint || (component >= 0 ? Shore.LandComponentAt(world, landType, ex, ey) == component : Core.Numerics.IntMath.DistanceSquared(ex, ey, x, y) <= near * near)))
        {
            world.Dockings.Remove(ship);
            return NavalSystem.UnloadNow(world, ship, tx, ty, limit, toPoint) > 0;
        }

        if (!Shore.FindDock(world, ship, world.Identities.Get(ship).TypeId, at, landType, component, tx, ty, out Position land, out Position water))
        {
            return false;
        }

        GameplayCommands.ClearTasks(world, ship);
        NavalSystem.StartDock(world, ship, toPoint ? DockPurpose.UnloadTo : DockPurpose.Unload, water, toPoint ? x : land.X, toPoint ? y : land.Y, limit);
        return true;
    }

    /// <summary>
    /// A ship that could not let all its units out at its dock looks for another one near the same point, keeping clear
    /// of the dock it is at; the retries carry over, so this ends.
    /// </summary>
    public static void Redock(World world, EntityId ship, Docking failed, int moved)
    {
        EntityId first = FirstInside(world, ship);
        if (first.IsNone)
        {
            return;
        }

        ushort landType = world.Identities.Get(first).TypeId;
        Position at = world.Positions.Get(ship);
        int component = failed.Purpose == DockPurpose.UnloadTo ? Shore.LandComponentAt(world, landType, failed.TowardX, failed.TowardY, Shore.UnloadSearchTiles) : -1;
        if (!Shore.FindDock(world, ship, world.Identities.Get(ship).TypeId, at, landType, component, failed.TowardX, failed.TowardY, at, out _, out Position water))
        {
            return;
        }

        int limit = failed.Limit == 0 ? 0 : Math.Max(1, failed.Limit - moved);
        NavalSystem.StartDock(world, ship, failed.Purpose, water, failed.TowardX, failed.TowardY, limit);
        ref Docking next = ref world.Dockings.Get(ship);
        next.Retries = failed.Retries + 1;
    }

    private static EntityId FirstInside(World world, EntityId holder)
    {
        for (int i = 0; i < world.Helds.Count; i++)
        {
            if (world.Helds.Components[i].Holder == holder.Value && !world.Items.Has(world.Helds.Entities[i]))
            {
                return world.Helds.Entities[i];
            }
        }

        return EntityId.None;
    }
}
