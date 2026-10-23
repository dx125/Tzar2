using System;
using Tzar2.AI.Runtime;
using Tzar2.Content.Compiled;
using Tzar2.Sim;
using Tzar2.Sim.Map;
using Tzar2.Sim.Systems;

namespace Tzar2.AI.Executors;

/// <summary>Where a building should go (ADR 0052). Walls and towers get their own roles when the sim has them.</summary>
public enum AiPlacementRole : byte
{
    /// <summary>Near the town center.</summary>
    Town = 0,

    /// <summary>A drop-off near the resource it accepts.</summary>
    DropOff = 1,

    /// <summary>A field next to a food drop-off.</summary>
    Field = 2,

    /// <summary>A harbor: on the coast nearest to the town (ADR 0144).</summary>
    Coast = 3,
}

/// <summary>
/// Build-site search (REQ-AI-02 "build placement"): square rings of growing radius around an anchor tile, in a fixed
/// order (top row left to right, then right column, bottom row, left column), first fit wins. A site is valid when the
/// sim would place it (<see cref="PlacementQueries.CanPlace"/>) and the one-tile ring around its footprint is walkable
/// (or off the map), so buildings never wall in a path or a neighbour. The number of candidates checked is bounded.
/// </summary>
public static class AiBuildPlacement
{
    /// <summary>Most candidate positions checked by one search.</summary>
    public const int MaxCandidates = 1200;

    /// <summary>Most candidate positions checked by one coast search (most are skipped by a cheap water test).</summary>
    public const int MaxCoastCandidates = 8000;

    /// <summary>How far from the town a harbor is searched (tiles; UNVERIFIED).</summary>
    public const int CoastSearchRadiusTiles = 40;

    /// <summary>Finds a site for <paramref name="typeId"/> around (<paramref name="anchorX"/>, <paramref name="anchorY"/>) (tiles).</summary>
    /// <returns>Whether a site was found; (<paramref name="tileX"/>, <paramref name="tileY"/>) is the footprint's top-left.</returns>
    public static bool TryFind(World world, ushort typeId, int anchorX, int anchorY, int maxRadius, out int tileX, out int tileY)
    {
        ArgumentNullException.ThrowIfNull(world);
        EntityTypeDefinition type = world.Ruleset.EntityTypes[typeId];
        int w = type.FootprintWidth;
        int h = type.FootprintHeight;
        int checks = 0;
        bool coast = type.Traits.Placement == PlacementKind.Coast;
        int cap = coast ? MaxCoastCandidates : MaxCandidates;
        for (int r = 0; r <= maxRadius; r++)
        {
            int side = r == 0 ? 1 : 8 * r;
            for (int k = 0; k < side; k++)
            {
                (int dx, int dy) = RingOffset(r, k);
                int x = anchorX + dx - (w / 2);
                int y = anchorY + dy - (h / 2);
                if (++checks > cap)
                {
                    tileX = tileY = 0;
                    return false;
                }

                if (coast ? IsGoodCoastSite(world, typeId, w, h, x, y) : IsGoodSite(world, typeId, w, h, x, y))
                {
                    tileX = x;
                    tileY = y;
                    return true;
                }
            }
        }

        tileX = tileY = 0;
        return false;
    }

    /// <summary>The anchor tile and search radius for a building of <paramref name="role"/>.</summary>
    internal static (int X, int Y, int Radius) Anchor(AiExecutorContext context, ushort typeId, AiPlacementRole role)
    {
        AiCensusPlayer player = context.Player;
        (int tx, int ty) = AiExecutorContext.TileOf(player.Town.X, player.Town.Y);
        EntityTraits traits = context.World.Ruleset.EntityTypes[typeId].Traits;
        if (role == AiPlacementRole.DropOff)
        {
            // The node of the first accepted resource nearest to the town (not too far away).
            long best = long.MaxValue;
            (int X, int Y) at = (tx, ty);
            long limit = 30L * MapState.UnitsPerTile;
            foreach (AiNode node in context.Census.Nodes)
            {
                if (!traits.AcceptsDropOff(node.Resource) || context.World.HasPlayer(node.Owner))
                {
                    continue;
                }

                long d = AiExecutorContext.Distance(player.Town.X, player.Town.Y, node.X, node.Y);
                if (d < best && d <= limit)
                {
                    best = d;
                    at = AiExecutorContext.TileOf(node.X, node.Y);
                }
            }

            return (at.X, at.Y, 10);
        }

        if (role == AiPlacementRole.Field)
        {
            // Next to the food drop-off (farm) nearest to the town, else the town.
            long best = long.MaxValue;
            (int X, int Y) at = (tx, ty);
            foreach (Tzar2.Core.Ids.EntityId b in player.Buildings)
            {
                ushort t = context.World.Identities.Get(b).TypeId;
                EntityTraits bt = context.World.Ruleset.EntityTypes[t].Traits;
                int food = context.World.Ruleset.ResourceId("food");
                if (food < 0 || !bt.AcceptsDropOff(food) || BitCount(bt.DropOffMask) > 1)
                {
                    continue;
                }

                Sim.Components.Position p = context.World.Positions.Get(b);
                long d = AiExecutorContext.Distance(player.Town.X, player.Town.Y, p.X, p.Y);
                if (d < best)
                {
                    best = d;
                    at = AiExecutorContext.TileOf(p.X, p.Y);
                }
            }

            return (at.X, at.Y, 14);
        }

        if (role == AiPlacementRole.Coast)
        {
            return (tx, ty, CoastSearchRadiusTiles);
        }

        return (tx, ty, 24);
    }

    /// <summary>The role of a building type: fields (unlimited food nodes), single-resource drop-offs, the rest.</summary>
    internal static AiPlacementRole RoleOf(EntityTypeDefinition type)
    {
        EntityTraits t = type.Traits;
        if (t.Placement == PlacementKind.Coast)
        {
            return AiPlacementRole.Coast;
        }

        if (t.ResourceNode is { Amount: ResourceNodeDefinition.Unlimited })
        {
            return AiPlacementRole.Field;
        }

        int accepted = BitCount(t.DropOffMask);
        return accepted is >= 1 and <= 3 && t.PopulationProvided == 0 ? AiPlacementRole.DropOff : AiPlacementRole.Town;
    }

    private static bool IsGoodSite(World world, ushort typeId, int w, int h, int x, int y)
    {
        if (!PlacementQueries.CanPlace(world, typeId, x, y))
        {
            return false;
        }

        MapState map = world.Map;
        for (int i = -1; i <= w; i++)
        {
            if (!Free(map, x + i, y - 1) || !Free(map, x + i, y + h))
            {
                return false;
            }
        }

        for (int j = 0; j < h; j++)
        {
            if (!Free(map, x - 1, y + j) || !Free(map, x + w, y + j))
            {
                return false;
            }
        }

        return true;
    }

    // A harbor: placeable (land, water within reach), and the land side of its ring walkable for the builders; the
    // water side cannot be (shore tiles carry neither, ADR 0042). Most candidates fail the cheap water test first.
    private static bool IsGoodCoastSite(World world, ushort typeId, int w, int h, int x, int y)
    {
        MapState map = world.Map;
        bool water = false;
        int reach = world.Ruleset.EntityTypes[typeId].Traits.CoastReachTiles;
        for (int j = y - reach; j < y + h + reach && !water; j++)
        {
            for (int i = x - reach; i < x + w + reach && !water; i++)
            {
                water = map.ContainsTile(i, j) && map.IsPassable(Locomotor.Ship, i, j);
            }
        }

        if (!water || !PlacementQueries.CanPlace(world, typeId, x, y))
        {
            return false;
        }

        int free = 0;
        for (int i = -1; i <= w; i++)
        {
            free += (Walkable(map, x + i, y - 1) ? 1 : 0) + (Walkable(map, x + i, y + h) ? 1 : 0);
        }

        for (int j = 0; j < h; j++)
        {
            free += (Walkable(map, x - 1, y + j) ? 1 : 0) + (Walkable(map, x + w, y + j) ? 1 : 0);
        }

        return free >= w + h;
    }

    private static bool Walkable(MapState map, int tx, int ty) => map.ContainsTile(tx, ty) && map.IsPassable(Locomotor.Foot, tx, ty);

    private static bool Free(MapState map, int tx, int ty) => !map.ContainsTile(tx, ty) || map.IsPassable(Locomotor.Foot, tx, ty);

    // The k-th tile of the square ring at Chebyshev radius r (r > 0: 8r tiles).
    private static (int Dx, int Dy) RingOffset(int r, int k)
    {
        if (r == 0)
        {
            return (0, 0);
        }

        int side = 2 * r;
        if (k < side)
        {
            return (-r + k, -r);
        }

        k -= side;
        if (k < side)
        {
            return (r, -r + k);
        }

        k -= side;
        if (k < side)
        {
            return (r - k, r);
        }

        k -= side;
        return (-r, r - k);
    }

    private static int BitCount(int mask) => System.Numerics.BitOperations.PopCount((uint)mask);
}
