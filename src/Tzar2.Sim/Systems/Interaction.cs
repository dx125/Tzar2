using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;
using Tzar2.Sim.Navigation;

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
        (int tx, int ty, int w, int h) = world.FootprintRect(structure);
        return (tx * MapState.UnitsPerTile, ty * MapState.UnitsPerTile, (tx + w) * MapState.UnitsPerTile, (ty + h) * MapState.UnitsPerTile);
    }

    /// <summary>
    /// Squared distance from a point to an entity's body: its footprint rectangle (structures), else its center
    /// (units, fish patches, meat; their radius is not subtracted).
    /// </summary>
    public static long DistanceSquaredTo(World world, int x, int y, EntityId entity)
    {
        if (world.Footprints.Has(entity))
        {
            return DistanceSquaredToRect(x, y, Rect(world, entity));
        }

        Position p = world.Positions.Get(entity);
        return IntMath.DistanceSquared(x, y, p.X, p.Y);
    }

    /// <summary>
    /// The larger of the two axis distances from a point to an entity: to its footprint (0 inside), or to a unit's
    /// position. A square area's test (spell areas in the original's cells).
    /// </summary>
    public static long ChebyshevTo(World world, int x, int y, EntityId entity)
    {
        if (world.Footprints.Has(entity))
        {
            (int x0, int y0, int x1, int y1) = Rect(world, entity);
            long dx = x < x0 ? x0 - x : x >= x1 ? x - (x1 - 1) : 0;
            long dy = y < y0 ? y0 - y : y >= y1 ? y - (y1 - 1) : 0;
            return Math.Max(dx, dy);
        }

        Position p = world.Positions.Get(entity);
        return Math.Max(Math.Abs((long)x - p.X), Math.Abs((long)y - p.Y));
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

    /// <summary>
    /// Whether a worker reaches <paramref name="target"/> to work on it. Across the shore (a ship at a building, a peasant
    /// at a ship) the reach is one tile longer: the shore tiles between land and water carry neither (ADR 0042).
    /// </summary>
    public static bool WorkerReaches(World world, EntityId worker, EntityId target)
    {
        if (world.Footprints.Has(target) && world.TypeOf(target).Traits.WalkableFootprint && !world.Constructions.Has(target)
            && world.TypeOf(worker).Locomotor != Locomotor.Ship)
        {
            // A farmer works on the field, not beside it: at its own tile of the field, or wherever it stopped on it when
            // that tile is taken.
            Position at = world.Positions.Get(worker);
            if (DistanceSquaredTo(world, at.X, at.Y, target) != 0)
            {
                return false;
            }

            (int wx, int wy) = WorkPoint(world, target, at, world.TypeOf(worker).Locomotor, worker);
            const long near = MapState.UnitsPerTile / 3;
            return IntMath.DistanceSquared(at.X, at.Y, wx, wy) <= near * near || !IsMoving(world, worker);
        }

        EntityTypeDefinition type = world.TypeOf(worker);
        EntityTypeDefinition other = world.TypeOf(target);
        bool shipWorker = type.Locomotor == Locomotor.Ship;
        bool across = shipWorker ? world.Footprints.Has(target) : other.IsMobile && other.Locomotor == Locomotor.Ship;
        int extra = across ? MapState.UnitsPerTile : 0;
        return Reaches(world, world.Positions.Get(worker), type.Radius + extra, target, IsMoving(world, worker));
    }

    /// <summary>
    /// Whether a unit at <paramref name="position"/> reaches <paramref name="target"/>: its footprint (structures), or
    /// its body (units: cows, ships; footprint-less nodes: fish patches, meat) within the interaction reach; a unit still
    /// <paramref name="moving"/> reaches a structure only once it stands next to it (the touch reach).
    /// </summary>
    public static bool Reaches(World world, Position position, int radius, EntityId target, bool moving = false)
    {
        GameplaySettings g = world.Ruleset.Gameplay;
        long reach = (long)radius + (moving && world.Footprints.Has(target) ? g.InteractionTouchUnits : g.InteractionReachUnits);
        if (!world.Footprints.Has(target))
        {
            reach += RadiusOf(world, target);
        }

        return DistanceSquaredTo(world, position.X, position.Y, target) <= reach * reach;
    }

    /// <summary>
    /// The center of the walkable tile next to a structure's footprint that is nearest to (<paramref name="towardX"/>,
    /// <paramref name="towardY"/>): where a worker stands to work on it, or where a trained unit appears. Searches the
    /// rings around the footprint outward (up to 3 tiles); ties go to the first tile in row-major order.
    /// </summary>
    /// <returns><c>false</c> when no ring tile is walkable for <paramref name="locomotor"/>.</returns>
    public static bool ApproachPoint(World world, EntityId structure, int towardX, int towardY, Locomotor locomotor, out int x, out int y)
    {
        (int fx, int fy, int fw, int fh) = world.FootprintRect(structure);
        MapState map = world.Map;
        for (int ring = 1; ring <= 3; ring++)
        {
            long best = long.MaxValue;
            int bx = 0;
            int by = 0;
            int x0 = fx - ring;
            int y0 = fy - ring;
            int x1 = fx + fw - 1 + ring;
            int y1 = fy + fh - 1 + ring;
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
    /// The owner's completed drop-off accepting <paramref name="resource"/> from workers of <paramref name="access"/>
    /// (land workers, or boats at harbors) nearest to <paramref name="from"/> (edge distance; ties: lower entity index),
    /// or <see cref="EntityId.None"/>.
    /// </summary>
    public static EntityId NearestDropOff(World world, byte owner, int resource, Position from, DropOffAccess access = DropOffAccess.Land)
    {
        EntityId best = EntityId.None;
        long bestDistance = long.MaxValue;
        ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
        for (int i = 0; i < structures.Length; i++)
        {
            EntityId s = structures[i];
            Identity identity = world.Identities.Get(s);
            EntityTraits traits = world.Ruleset.EntityTypes[identity.TypeId].Traits;
            if (identity.Owner != owner || !traits.AcceptsDropOff(resource) || (traits.DropOffAccess & access) == 0 || world.Constructions.Has(s))
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
    public static EntityId NearestNode(World world, byte owner, int resource, int x, int y, int radiusTiles, Locomotor locomotor = Locomotor.Foot)
    {
        bool boat = locomotor == Locomotor.Ship;
        EntityId best = EntityId.None;
        long limit = (long)radiusTiles * MapState.UnitsPerTile;
        long bestDistance = (limit * limit) + 1;
        ReadOnlySpan<EntityId> nodes = world.ResourceStocks.Entities;
        Span<ResourceStock> stocks = world.ResourceStocks.Components;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (stocks[i].Amount == 0 || world.TypeOf(nodes[i]).Traits.ResourceNode!.Resource != resource || world.Constructions.Has(nodes[i])
                || !MayHarvest(world, owner, nodes[i]) || IsOnWater(world, nodes[i]) != boat)
            {
                continue;
            }

            long d = DistanceSquaredTo(world, x, y, nodes[i]);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = nodes[i];
            }
        }

        return best;
    }

    /// <summary>
    /// The next node of <paramref name="resource"/> for <paramref name="worker"/>, which worked at (<paramref name="x"/>,
    /// <paramref name="y"/>) (ADR 0180; the owner's play-test: peasants walked to far trees): among the nodes within
    /// <paramref name="radiusTiles"/> that the worker can get next to (a tree inside a forest is left for later), the one
    /// with the shortest way from there plus the way from it to the owner's nearest drop-off for the resource, so the
    /// cutting moves toward the lumber mill (ties: lower entity index), or <see cref="EntityId.None"/>.
    /// </summary>
    public static EntityId NextNode(World world, EntityId worker, int resource, int x, int y, int radiusTiles)
    {
        byte owner = world.Identities.Get(worker).Owner;
        EntityTypeDefinition type = world.TypeOf(worker);
        bool boat = type.Locomotor == Locomotor.Ship;
        DropOffAccess access = AccessOf(type);
        Span<int> drops = stackalloc int[2 * MaxDropOffs];
        int dropCount = DropOffCenters(world, owner, resource, access, drops);
        NavLayer? layer = world.Navigation.ForType(type.Id);
        Position at = world.Positions.Get(worker);
        int component = layer is null ? -1 : layer.ComponentOf(layer.TileOf(at.X, at.Y));
        long limit = (long)radiusTiles * MapState.UnitsPerTile;
        long limitSquared = limit * limit;
        EntityId best = EntityId.None;
        long bestScore = long.MaxValue;
        ReadOnlySpan<EntityId> nodes = world.ResourceStocks.Entities;
        Span<ResourceStock> stocks = world.ResourceStocks.Components;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (stocks[i].Amount == 0 || world.TypeOf(nodes[i]).Traits.ResourceNode!.Resource != resource || world.Constructions.Has(nodes[i])
                || !MayHarvest(world, owner, nodes[i]) || IsOnWater(world, nodes[i]) != boat || !HasRoom(world, nodes[i], worker))
            {
                continue;
            }

            long d = DistanceSquaredTo(world, x, y, nodes[i]);
            if (d > limitSquared)
            {
                continue;
            }

            Position p = world.Positions.Get(nodes[i]);
            long toDrop = 0;
            if (dropCount > 0)
            {
                toDrop = long.MaxValue;
                for (int k = 0; k < dropCount; k++)
                {
                    toDrop = Math.Min(toDrop, IntMath.DistanceSquared(p.X, p.Y, drops[2 * k], drops[(2 * k) + 1]));
                }
            }

            long score = IntMath.ISqrt(d) + IntMath.ISqrt(toDrop);
            if (score < bestScore && (component < 0 || CanWorkAt(world, nodes[i], layer!, component)))
            {
                bestScore = score;
                best = nodes[i];
            }
        }

        return best;
    }

    /// <summary>Most drop-offs <see cref="NextNode"/> weighs (the nearest of the first ones in entity order).</summary>
    public const int MaxDropOffs = 32;

    // The centres of the owner's completed drop-offs taking the resource from the access (at most MaxDropOffs).
    private static int DropOffCenters(World world, byte owner, int resource, DropOffAccess access, Span<int> centers)
    {
        int n = 0;
        ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
        for (int i = 0; i < structures.Length && n < MaxDropOffs; i++)
        {
            EntityId s = structures[i];
            Identity identity = world.Identities.Get(s);
            EntityTraits traits = world.Ruleset.EntityTypes[identity.TypeId].Traits;
            if (identity.Owner != owner || !traits.AcceptsDropOff(resource) || (traits.DropOffAccess & access) == 0 || world.Constructions.Has(s))
            {
                continue;
            }

            Position p = world.Positions.Get(s);
            centers[2 * n] = p.X;
            centers[(2 * n) + 1] = p.Y;
            n++;
        }

        return n;
    }

    /// <summary>
    /// Whether a worker standing in <paramref name="component"/> of <paramref name="layer"/> can get next to
    /// <paramref name="target"/>: a walkable tile of that component touches its footprint (a tree inside a forest has
    /// none), or a footprint-less target stands on or next to one.
    /// </summary>
    public static bool CanWorkAt(World world, EntityId target, NavLayer layer, int component)
    {
        ArgumentNullException.ThrowIfNull(layer);
        bool footprint = world.Footprints.Has(target);
        int fx, fy, fw, fh;
        if (footprint)
        {
            (fx, fy, fw, fh) = world.FootprintRect(target);
        }
        else
        {
            Position p = world.Positions.Get(target);
            (fx, fy, fw, fh) = (p.X / MapState.UnitsPerTile, p.Y / MapState.UnitsPerTile, 1, 1);
        }

        for (int ty = fy - 1; ty <= fy + fh; ty++)
        {
            for (int tx = fx - 1; tx <= fx + fw; tx++)
            {
                bool inside = tx >= fx && tx < fx + fw && ty >= fy && ty < fy + fh;
                if ((footprint && inside) || !layer.IsWalkable(tx, ty))
                {
                    continue;
                }

                if (layer.ComponentOf((ty * layer.Width) + tx) == component)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>How many rings of tiles around a building <see cref="SpawnPoint"/> looks at.</summary>
    public const int SpawnRings = 4;

    /// <summary>
    /// Where a unit of <paramref name="type"/> made in <paramref name="structure"/> appears (ADR 0180; the owner's
    /// play-test: they appeared on top of each other): a tile around the building, on the ring nearest to its walls,
    /// that no unit stands on, nearest to (<paramref name="towardX"/>, <paramref name="towardY"/>), in the connected area
    /// around the building with the most room, so a pocket closed in by walls or other buildings is left out when there
    /// is a way out elsewhere (the original's units could appear where they could not leave). Without a free tile within
    /// <see cref="SpawnRings"/> rings the unit stands on the best tile anyway; false when no tile it can stand on is around.
    /// </summary>
    public static bool SpawnPoint(World world, EntityId structure, int towardX, int towardY, EntityTypeDefinition type, out int x, out int y)
    {
        ArgumentNullException.ThrowIfNull(type);
        NavLayer? layer = world.Navigation.ForType(type.Id);
        if (layer is null || !world.Footprints.Has(structure))
        {
            return ApproachPoint(world, structure, towardX, towardY, type.Locomotor, out x, out y);
        }

        (int fx, int fy, int fw, int fh) = world.FootprintRect(structure);
        int area = -1;
        int room = 0;
        for (int ring = 1; ring <= SpawnRings; ring++)
        {
            for (int ty = fy - ring; ty <= fy + fh - 1 + ring; ty++)
            {
                bool edgeRow = ty == fy - ring || ty == fy + fh - 1 + ring;
                for (int tx = fx - ring; tx <= fx + fw - 1 + ring; tx += edgeRow || tx == fx + fw - 1 + ring ? 1 : fw - 1 + (2 * ring))
                {
                    if (!layer.IsWalkable(tx, ty))
                    {
                        continue;
                    }

                    int c = layer.ComponentOf((ty * layer.Width) + tx);
                    int size = layer.ComponentSize(c);
                    if (size > room || (size == room && c < area))
                    {
                        room = size;
                        area = c;
                    }
                }
            }
        }

        if (area < 0)
        {
            return ApproachPoint(world, structure, towardX, towardY, type.Locomotor, out x, out y);
        }

        MapState map = world.Map;
        long fallback = long.MaxValue;
        (x, y) = (0, 0);
        for (int ring = 1; ring <= SpawnRings; ring++)
        {
            long best = long.MaxValue;
            int bx = 0;
            int by = 0;
            for (int ty = fy - ring; ty <= fy + fh - 1 + ring; ty++)
            {
                bool edgeRow = ty == fy - ring || ty == fy + fh - 1 + ring;
                for (int tx = fx - ring; tx <= fx + fw - 1 + ring; tx += edgeRow || tx == fx + fw - 1 + ring ? 1 : fw - 1 + (2 * ring))
                {
                    int tile = (ty * layer.Width) + tx;
                    if (!layer.IsWalkable(tx, ty) || layer.ComponentOf(tile) != area)
                    {
                        continue;
                    }

                    int cx = layer.CenterX(tile);
                    int cy = layer.CenterY(tile);
                    long d = IntMath.DistanceSquared(cx, cy, towardX, towardY);
                    if (ring == 1 && d < fallback)
                    {
                        fallback = d;
                        (x, y) = (cx, cy);
                    }

                    if (d < best && TileFree(map, tx, ty))
                    {
                        best = d;
                        bx = cx;
                        by = cy;
                    }
                }
            }

            if (best != long.MaxValue)
            {
                (x, y) = (bx, by);
                return true;
            }
        }

        return fallback != long.MaxValue || ApproachPoint(world, structure, towardX, towardY, type.Locomotor, out x, out y);
    }

    // Whether no unit stands on any occupancy cell of a tile.
    private static bool TileFree(MapState map, int tx, int ty)
    {
        int per = MapState.UnitsPerTile / map.UnitsPerCell;
        ReadOnlySpan<int> occupancy = map.Occupancy;
        for (int cy = ty * per; cy < (ty + 1) * per; cy++)
        {
            for (int cx = tx * per; cx < (tx + 1) * per; cx++)
            {
                if (occupancy[(cy * map.CellWidth) + cx] != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a resource node lies on water (a fish patch): only boats gather there, and boats gather nowhere else
    /// (GAME-02, ADR 0141).
    /// </summary>
    public static bool IsOnWater(World world, EntityId node)
    {
        Position p = world.Positions.Get(node);
        return !world.Footprints.Has(node) && world.Map.IsPassableAt(Locomotor.Ship, p.X, p.Y);
    }

    /// <summary>Whether a worker of <paramref name="locomotor"/> may gather from <paramref name="node"/> (boats on water only).</summary>
    public static bool CanReachNode(World world, Locomotor locomotor, EntityId node) => IsOnWater(world, node) == (locomotor == Locomotor.Ship);

    /// <summary>The access a worker of <paramref name="type"/> delivers with: boats at water drop-offs, others on land.</summary>
    public static DropOffAccess AccessOf(EntityTypeDefinition type) => type.Locomotor == Locomotor.Ship ? DropOffAccess.Water : DropOffAccess.Land;

    /// <summary>
    /// Whether <paramref name="worker"/> may work <paramref name="node"/> beside the others already on it (ECO-021: one
    /// peasant per field). Those gathering on it or carrying from it hold it; of several heading there, the first to
    /// arrive takes it and the others look for another.
    /// </summary>
    public static bool HasRoom(World world, EntityId node, EntityId worker)
    {
        int max = world.Ruleset.EntityTypes[world.Identities.Get(node).TypeId].Traits.ResourceNode?.MaxWorkers ?? 0;
        if (max == 0)
        {
            return true;
        }

        int ahead = 0;
        ReadOnlySpan<EntityId> workers = world.Workers.Entities;
        Span<Worker> states = world.Workers.Components;
        for (int i = 0; i < workers.Length; i++)
        {
            ref Worker w = ref states[i];
            if (workers[i] == worker || w.Task != WorkerTask.Harvest || w.Target != node.Value)
            {
                continue;
            }

            if (w.Phase != WorkerPhase.ToNode)
            {
                ahead++;
            }
        }

        return ahead < max;
    }

    /// <summary>
    /// Whether <paramref name="node"/> can be gathered now: alive, not empty, not a site, of the resource, allowed. With
    /// <paramref name="waitForRegrowth"/>, an empty node that regrows (a cow milked dry) counts too: its own milker waits.
    /// </summary>
    public static bool IsHarvestable(World world, EntityId node, int resource, byte owner, bool waitForRegrowth = false)
    {
        int s = world.ResourceStocks.IndexOf(node);
        return s >= 0 && world.Entities.IsAlive(node) && !world.Constructions.Has(node)
            && (world.ResourceStocks.At(s).Amount != 0 || (waitForRegrowth && world.TypeOf(node).Traits.ResourceNode is { RegenMs: > 0 }))
            && world.TypeOf(node).Traits.ResourceNode!.Resource == resource && MayHarvest(world, owner, node);
    }

    /// <summary>
    /// Where a worker goes to work on <paramref name="target"/>: the walkable tile next to a structure nearest to the
    /// worker, or the position of a footprint-less target (a cow, a fish patch, meat).
    /// </summary>
    public static (int X, int Y) WorkPoint(World world, EntityId target, Position from, Locomotor locomotor, EntityId worker = default)
    {
        if (world.Footprints.Has(target) && world.TypeOf(target).Traits.WalkableFootprint && locomotor != Locomotor.Ship)
        {
            // On a field: one of its tiles, picked by the worker so that several farmers spread out.
            (int tx, int ty, int w, int h) = world.FootprintRect(target);
            int cell = (int)((uint)worker.Index % (uint)Math.Max(1, w * h));
            return (((tx + (cell % w)) * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2), ((ty + (cell / w)) * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2));
        }

        if (world.Footprints.Has(target))
        {
            if (ApproachPoint(world, target, from.X, from.Y, locomotor, out int x, out int y))
            {
                return (x, y);
            }
        }

        Position p = world.Positions.Get(target);
        return (p.X, p.Y);
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
            // A guard stops to fight but keeps guarding (the follow order of a guard order).
            ref Motion motion = ref world.Motions.At(m);
            uint guarded = motion.Order == MotionOrder.Follow ? motion.FollowTarget : 0;
            MovementOrders.Stop(world, unit, ref motion);
            if (guarded != 0)
            {
                motion.Order = MotionOrder.Follow;
                motion.FollowTarget = guarded;
            }
        }
    }
}
