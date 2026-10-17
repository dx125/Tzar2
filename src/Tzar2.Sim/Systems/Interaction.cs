using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Shared geometry and queries of the gameplay systems: distances edge to edge (a unit is a disc, a structure its
/// footprint rectangle), the walkable tile to approach a structure from, the nearest drop-off and resource node.
/// Every tie is broken by scan order (ascending tile or entity index), so results never depend on anything but state.
/// </summary>
internal static class Interaction
{
    /// <summary>The footprint rectangle of a structure in world units, half-open [X0, X1) × [Y0, Y1).</summary>
    public static (int X0, int Y0, int X1, int Y1) Rect(World world, EntityId structure)
    {
        Footprint f = world.Footprints.Get(structure);
        EntityTypeDefinition type = world.TypeOf(structure);
        return (f.TileX * MapState.UnitsPerTile, f.TileY * MapState.UnitsPerTile,
            (f.TileX + type.FootprintWidth) * MapState.UnitsPerTile, (f.TileY + type.FootprintHeight) * MapState.UnitsPerTile);
    }

    /// <summary>Squared distance from a point to a rectangle (0 inside).</summary>
    public static long DistanceSquaredToRect(int x, int y, (int X0, int Y0, int X1, int Y1) r)
    {
        long dx = x < r.X0 ? r.X0 - x : x >= r.X1 ? x - (r.X1 - 1) : 0;
        long dy = y < r.Y0 ? r.Y0 - y : y >= r.Y1 ? y - (r.Y1 - 1) : 0;
        return (dx * dx) + (dy * dy);
    }

    /// <summary>Radius of an entity for edge distances: the type's collision radius for units, 0 for structures.</summary>
    public static int RadiusOf(World world, EntityId entity)
    {
        EntityTypeDefinition type = world.TypeOf(entity);
        return type.HasFootprint ? 0 : type.Radius;
    }

    /// <summary>
    /// Edge-to-edge distance between two entities (never negative): disc to disc, or disc to footprint rectangle.
    /// </summary>
    public static int EdgeDistance(World world, Position fromPosition, int fromRadius, EntityId to)
    {
        long d;
        if (world.Footprints.Has(to))
        {
            d = IntMath.ISqrt(DistanceSquaredToRect(fromPosition.X, fromPosition.Y, Rect(world, to)));
        }
        else
        {
            Position p = world.Positions.Get(to);
            d = IntMath.ISqrt(IntMath.DistanceSquared(fromPosition.X, fromPosition.Y, p.X, p.Y)) - RadiusOf(world, to);
        }

        return (int)Math.Clamp(d - fromRadius, 0, int.MaxValue);
    }

    /// <summary>Whether a unit at <paramref name="position"/> reaches the footprint of <paramref name="structure"/>.</summary>
    public static bool Reaches(World world, Position position, int radius, EntityId structure)
    {
        long reach = (long)radius + world.Ruleset.Gameplay.InteractionReachUnits;
        return DistanceSquaredToRect(position.X, position.Y, Rect(world, structure)) <= reach * reach;
    }

    /// <summary>
    /// The center of the walkable tile next to a structure's footprint that is nearest to (<paramref name="towardX"/>,
    /// <paramref name="towardY"/>): where a worker stands to work on it, or where a trained unit appears. Searches the
    /// rings around the footprint outward (up to 3 tiles); ties go to the first tile in row-major order.
    /// </summary>
    /// <returns><c>false</c> when no ring tile is walkable for <paramref name="locomotor"/>.</returns>
    public static bool ApproachPoint(World world, EntityId structure, int towardX, int towardY, Locomotor locomotor, out int x, out int y)
    {
        Footprint f = world.Footprints.Get(structure);
        EntityTypeDefinition type = world.TypeOf(structure);
        MapState map = world.Map;
        for (int ring = 1; ring <= 3; ring++)
        {
            long best = long.MaxValue;
            int bx = 0;
            int by = 0;
            int x0 = f.TileX - ring;
            int y0 = f.TileY - ring;
            int x1 = f.TileX + type.FootprintWidth - 1 + ring;
            int y1 = f.TileY + type.FootprintHeight - 1 + ring;
            for (int ty = y0; ty <= y1; ty++)
            {
                bool edgeRow = ty == y0 || ty == y1;
                for (int tx = x0; tx <= x1; tx += edgeRow || tx == x1 ? 1 : x1 - x0)
                {
                    if (!map.IsPassable(locomotor, tx, ty))
                    {
                        continue;
                    }

                    int cx = (tx * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2);
                    int cy = (ty * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2);
                    long d = IntMath.DistanceSquared(cx, cy, towardX, towardY);
                    if (d < best)
                    {
                        best = d;
                        bx = cx;
                        by = cy;
                    }
                }
            }

            if (best != long.MaxValue)
            {
                x = bx;
                y = by;
                return true;
            }
        }

        x = 0;
        y = 0;
        return false;
    }

    /// <summary>Whether <paramref name="entity"/> is a live, completed structure.</summary>
    public static bool IsCompletedStructure(World world, EntityId entity)
        => world.Entities.IsAlive(entity) && world.Footprints.Has(entity) && !world.Constructions.Has(entity);

    /// <summary>
    /// The owner's completed drop-off accepting <paramref name="resource"/> nearest to <paramref name="from"/> (edge
    /// distance; ties: lower entity index), or <see cref="EntityId.None"/>.
    /// </summary>
    public static EntityId NearestDropOff(World world, byte owner, int resource, Position from)
    {
        EntityId best = EntityId.None;
        long bestDistance = long.MaxValue;
        ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
        for (int i = 0; i < structures.Length; i++)
        {
            EntityId s = structures[i];
            Identity identity = world.Identities.Get(s);
            if (identity.Owner != owner || !world.Ruleset.EntityTypes[identity.TypeId].Traits.AcceptsDropOff(resource) || world.Constructions.Has(s))
            {
                continue;
            }

            long d = DistanceSquaredToRect(from.X, from.Y, Rect(world, s));
            if (d < bestDistance)
            {
                bestDistance = d;
                best = s;
            }
        }

        return best;
    }

    /// <summary>
    /// The resource node of <paramref name="resource"/> with stock left nearest to (<paramref name="x"/>,
    /// <paramref name="y"/>) within <paramref name="radiusTiles"/> (edge distance; ties: lower entity index), or
    /// <see cref="EntityId.None"/>. Fields under construction and nodes another kingdom owns do not count.
    /// </summary>
    public static EntityId NearestNode(World world, byte owner, int resource, int x, int y, int radiusTiles)
    {
        EntityId best = EntityId.None;
        long limit = (long)radiusTiles * MapState.UnitsPerTile;
        long bestDistance = (limit * limit) + 1;
        ReadOnlySpan<EntityId> nodes = world.ResourceStocks.Entities;
        Span<ResourceStock> stocks = world.ResourceStocks.Components;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (stocks[i].Amount == 0 || world.TypeOf(nodes[i]).Traits.ResourceNode!.Resource != resource || world.Constructions.Has(nodes[i])
                || !MayHarvest(world, owner, nodes[i]))
            {
                continue;
            }

            long d = DistanceSquaredToRect(x, y, Rect(world, nodes[i]));
            if (d < bestDistance)
            {
                bestDistance = d;
                best = nodes[i];
            }
        }

        return best;
    }

    /// <summary>
    /// Whether a worker of <paramref name="owner"/> may gather from <paramref name="node"/>: Nature's nodes (trees,
    /// rocks) are anyone's, a kingdom's own nodes (fields) only its own.
    /// </summary>
    public static bool MayHarvest(World world, byte owner, EntityId node)
    {
        byte nodeOwner = world.Identities.Get(node).Owner;
        return nodeOwner == owner || !world.HasPlayer(nodeOwner);
    }

    /// <summary>Starts a move of a unit (no-op for immobile entities) as a plain Move order.</summary>
    public static void MoveTo(World world, EntityId unit, int x, int y)
    {
        int m = world.Motions.IndexOf(unit);
        if (m < 0)
        {
            return;
        }

        ref Motion motion = ref world.Motions.At(m);
        if (motion.SpeedPerTick <= 0)
        {
            return;
        }

        motion.Order = MotionOrder.Move;
        motion.OrderX = Math.Clamp(x, 0, world.Map.WorldWidth - 1);
        motion.OrderY = Math.Clamp(y, 0, world.Map.WorldHeight - 1);
        motion.FollowTarget = 0;
        motion.Replans = 0;
        MovementOrders.Request(world, unit, ref motion, 0, motion.OrderX, motion.OrderY);
    }

    /// <summary>Whether a unit is executing a movement.</summary>
    public static bool IsMoving(World world, EntityId unit)
    {
        int m = world.Motions.IndexOf(unit);
        return m >= 0 && world.Motions.At(m).State == MotionState.Moving;
    }

    /// <summary>Stops a unit's current movement (it keeps no order).</summary>
    public static void Halt(World world, EntityId unit)
    {
        int m = world.Motions.IndexOf(unit);
        if (m >= 0 && world.Motions.At(m).State == MotionState.Moving)
        {
            MovementOrders.Stop(world, unit, ref world.Motions.At(m));
        }
    }
}
