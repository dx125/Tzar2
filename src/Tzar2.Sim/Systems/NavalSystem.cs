using System;
using System.Collections.Generic;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;
using Tzar2.Sim.Navigation;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 6 (activities) for ships and explorers (REQ-PATH-09, ADRs 0140 and 0143): ships sail to their shore
/// point and, there, let their units out (or wait for boarders and repairers); exploring units head for the nearest
/// unexplored tile they can reach, ships on water.
/// </summary>
public sealed class NavalSystem : ISimSystem
{
    /// <summary>Least ticks between two movement requests of a docking ship.</summary>
    public const int MoveIntervalTicks = 10;

    /// <summary>Movement requests without arriving before a docking is given up.</summary>
    public const int MaxRetries = 8;

    /// <summary>Ticks between two looks for an exploration goal.</summary>
    public const int ExploreIntervalTicks = 20;

    /// <summary>How far an explorer looks for unexplored ground (tiles).</summary>
    public const int ExploreRadiusTiles = 96;

    /// <summary>How far around the clicked point a unit sent to explore an area looks for unexplored ground (tiles; ⚠ UNVERIFIED, ORD-007).</summary>
    public const int ExploreAreaRadiusTiles = 12;

    private readonly List<EntityId> _finished = [];

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Activities;

    /// <inheritdoc />
    public string Name => "naval";

    /// <summary>Sends a ship to a shore point (replacing a docking in progress).</summary>
    public static void StartDock(World world, EntityId ship, DockPurpose purpose, Position water, int towardX, int towardY, int limit)
    {
        ArgumentNullException.ThrowIfNull(world);
        bool unloading = purpose is DockPurpose.Unload or DockPurpose.UnloadTo;
        world.Dockings.Set(ship, new Docking
        {
            WaterX = water.X,
            WaterY = water.Y,
            TowardX = unloading ? towardX : 0,
            TowardY = unloading ? towardY : 0,
            MoveTick = -1,
            Limit = unloading ? (ushort)Math.Clamp(limit, 0, Content.Compiled.HolderDefinition.MaxCapacity) : (ushort)0,
            Purpose = purpose,
        });
    }

    /// <summary>
    /// Lets up to <paramref name="limit"/> (0 = all) units out of a holder toward a point, in entity order; with
    /// <paramref name="sendOn"/> they then walk there.
    /// </summary>
    /// <returns>How many came out.</returns>
    public static int UnloadNow(World world, EntityId holder, int towardX, int towardY, int limit, bool sendOn, bool around = false, byte select = 0)
    {
        ArgumentNullException.ThrowIfNull(world);
        Span<uint> inside = stackalloc uint[Content.Compiled.HolderDefinition.MaxCapacity];
        int n = 0;
        for (int i = 0; i < world.Helds.Count && n < inside.Length; i++)
        {
            if (world.Helds.Components[i].Holder == holder.Value && !world.Items.Has(world.Helds.Entities[i]))
            {
                inside[n++] = world.Helds.Entities[i].Value;
            }
        }

        int max = limit > 0 ? limit : int.MaxValue;
        int moved = 0;
        for (int k = 0; k < n && moved < max; k++)
        {
            EntityId unit = EntityId.FromValue(inside[k]);
            if (!world.Unload(unit, towardX, towardY, around, select))
            {
                break; // no room around the holder
            }

            moved++;
            if (sendOn)
            {
                Interaction.MoveTo(world, unit, towardX, towardY);
            }
        }

        return moved;
    }

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        int tick = world.Tick.Value;
        if (world.Dockings.Count != 0)
        {
            Dock(world, tick);
        }

        if (world.Explorers.Count != 0)
        {
            Explore(world, tick);
        }
    }

    private void Dock(World world, int tick)
    {
        _finished.Clear();
        for (int i = 0; i < world.Dockings.Count; i++)
        {
            EntityId ship = world.Dockings.Entities[i];
            ref Docking d = ref world.Dockings.At(i);
            if (!world.CanAct(ship))
            {
                continue; // frozen or stone: it waits
            }

            if (d.Purpose == DockPurpose.Board && !AwaitsBoarders(world, ship))
            {
                _finished.Add(ship); // everyone is aboard (or gave up): no need to go on
                continue;
            }

            Position at = world.Positions.Get(ship);
            long away = IntMath.DistanceSquared(at.X, at.Y, d.WaterX, d.WaterY);
            long quarter = MapState.UnitsPerTile / 4;
            if (away <= quarter * quarter)
            {
                _finished.Add(ship);
                continue;
            }

            // Path requests are served asynchronously: a ship that stopped some ticks after its request has arrived as
            // close as it can get when it is near the point; otherwise it tries again.
            if (Interaction.IsMoving(world, ship) || (d.MoveTick >= 0 && tick - d.MoveTick < MoveIntervalTicks))
            {
                continue;
            }

            if (d.MoveTick >= 0 && away <= 4L * MapState.UnitsPerTile * MapState.UnitsPerTile)
            {
                _finished.Add(ship);
                continue;
            }

            if (++d.Retries > MaxRetries)
            {
                _finished.Add(ship);
                continue;
            }

            d.MoveTick = tick;
            Interaction.MoveTo(world, ship, d.WaterX, d.WaterY);
        }

        // Unloading changes stores (units get movement back): after the pass, in order.
        foreach (EntityId ship in _finished)
        {
            Docking d = world.Dockings.Get(ship);
            world.Dockings.Remove(ship);
            Interaction.Halt(world, ship);
            if (d.Purpose is DockPurpose.Unload or DockPurpose.UnloadTo)
            {
                int moved = UnloadNow(world, ship, d.TowardX, d.TowardY, d.Limit, d.Purpose == DockPurpose.UnloadTo);
                if ((d.Limit == 0 || moved < d.Limit) && world.Holders.Get(ship).Count > 0 && d.Retries < MaxRetries)
                {
                    // No room here (other ships, a crowd ashore): another dock along the shore (ADR 0146).
                    NavalCommands.Redock(world, ship, d, moved);
                }
            }
        }
    }

    private static bool AwaitsBoarders(World world, EntityId ship)
    {
        ReadOnlySpan<Errand> errands = world.Errands.Components;
        for (int i = 0; i < errands.Length; i++)
        {
            if (errands[i].Kind == ErrandKind.Board && errands[i].Target == ship.Value)
            {
                return true;
            }
        }

        return false;
    }

    private void Explore(World world, int tick)
    {
        _finished.Clear();
        for (int i = 0; i < world.Explorers.Count; i++)
        {
            EntityId unit = world.Explorers.Entities[i];
            ref Exploring x = ref world.Explorers.At(i);
            if (tick < x.NextTick || Interaction.IsMoving(world, unit) || !world.CanAct(unit))
            {
                continue;
            }

            x.NextTick = tick + ExploreIntervalTicks;
            int goal = NextGoal(world, unit, x.AreaTile);
            if (goal < 0)
            {
                _finished.Add(unit); // nothing left it can reach: the order is done
                continue;
            }

            x.GoalTile = goal;
            NavLayer layer = world.Navigation.ForType(world.Identities.Get(unit).TypeId)!;
            Interaction.MoveTo(world, unit, layer.CenterX(goal), layer.CenterY(goal));
        }

        foreach (EntityId unit in _finished)
        {
            world.Explorers.Remove(unit);
        }
    }

    // The unexplored tile of the unit's component nearest to it, or to the middle of the area it was sent to (ring order,
    // then row-major), or -1.
    private static int NextGoal(World world, EntityId unit, int area)
    {
        Identity identity = world.Identities.Get(unit);
        NavLayer? layer = world.Navigation.ForType(identity.TypeId);
        if (layer is null || identity.Owner >= world.Players.Length)
        {
            return -1;
        }

        Position at = world.Positions.Get(unit);
        int start = layer.TileOf(at.X, at.Y);
        int component = layer.ComponentOf(start);
        if (component < 0)
        {
            return -1;
        }

        int middle = area >= 0 ? area : start;
        int cx = middle % layer.Width;
        int cy = middle / layer.Width;
        int radius = Math.Min(area >= 0 ? ExploreAreaRadiusTiles : ExploreRadiusTiles, Math.Max(layer.Width, layer.Height));
        for (int r = 1; r <= radius; r++)
        {
            for (int ty = cy - r; ty <= cy + r; ty++)
            {
                bool edgeRow = ty == cy - r || ty == cy + r;
                for (int tx = cx - r; tx <= cx + r; tx += edgeRow ? 1 : 2 * r)
                {
                    if (!layer.IsWalkable(tx, ty) || world.Visibility.IsExplored(identity.Owner, tx, ty))
                    {
                        continue;
                    }

                    int tile = (ty * layer.Width) + tx;
                    if (layer.ComponentOf(tile) == component)
                    {
                        return tile;
                    }
                }
            }
        }

        return -1;
    }
}
