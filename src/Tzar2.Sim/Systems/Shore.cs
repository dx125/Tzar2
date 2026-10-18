using System;
using Tzar2.Core.Numerics;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;
using Tzar2.Sim.Navigation;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Shore points for ships (REQ-PATH-09, ADR 0140): where a ship and land units meet to board, unload or repair. A dock
/// is a pair of a land tile the units can stand on (in their component) and a water tile in the ship's component within
/// <see cref="ReachTiles"/> of it (across the shore tile that carries neither, ADR 0042). The pair nearest to both
/// parties wins: land tile to the land reference point plus water tile to the ship (ties: ring order, then row-major).
/// </summary>
public static class Shore
{
    /// <summary>Most tiles between the land and the water tile of a dock (UNVERIFIED, UNK-11).</summary>
    public const int ReachTiles = 2;

    /// <summary>How far from the land reference point a dock is searched (tiles; UNVERIFIED).</summary>
    public const int SearchRadiusTiles = 48;

    /// <summary>
    /// Finds a dock for a ship of <paramref name="shipType"/> at <paramref name="ship"/> and land units of
    /// <paramref name="landType"/> near (<paramref name="landX"/>, <paramref name="landY"/>).
    /// </summary>
    /// <param name="world">The world.</param>
    /// <param name="shipType">The ship's type.</param>
    /// <param name="ship">The ship's position.</param>
    /// <param name="landType">The land units' type (its navigation layer decides where they can stand).</param>
    /// <param name="landComponent">The land component the land tile must be in, or -1 for any.</param>
    /// <param name="landX">The land reference point (the units, or where they are to go).</param>
    /// <param name="landY">The land reference point.</param>
    /// <param name="land">The land tile's center.</param>
    /// <param name="water">The water tile's center (where the ship sails to).</param>
    /// <returns><c>false</c> if no dock is within the search radius.</returns>
    public static bool FindDock(World world, ushort shipType, Position ship, ushort landType, int landComponent, int landX, int landY, out Position land, out Position water)
    {
        ArgumentNullException.ThrowIfNull(world);
        land = default;
        water = default;
        NavLayer? sea = world.Navigation.ForType(shipType);
        NavLayer? ground = world.Navigation.ForType(landType);
        if (sea is null || ground is null)
        {
            return false;
        }

        int shipComponent = sea.ComponentOf(sea.TileOf(ship.X, ship.Y));
        if (shipComponent < 0)
        {
            return false;
        }

        int cx = ground.TileX(landX);
        int cy = ground.TileY(landY);
        long best = long.MaxValue;
        for (int r = 0; r <= SearchRadiusTiles; r++)
        {
            // Every tile of ring r is at least (r - 1) tiles from the reference point: nothing further can win.
            if (best != long.MaxValue && (long)(r - 1) * MapState.UnitsPerTile > best)
            {
                break;
            }

            for (int ty = cy - r; ty <= cy + r; ty++)
            {
                bool edgeRow = ty == cy - r || ty == cy + r;
                for (int tx = cx - r; tx <= cx + r; tx += edgeRow || r == 0 ? 1 : 2 * r)
                {
                    if (!ground.IsWalkable(tx, ty))
                    {
                        continue;
                    }

                    int tile = (ty * ground.Width) + tx;
                    if (landComponent >= 0 && ground.ComponentOf(tile) != landComponent)
                    {
                        continue;
                    }

                    int lx = ground.CenterX(tile);
                    int ly = ground.CenterY(tile);
                    if (!NearestWater(sea, shipComponent, lx, ly, ship, out int wx, out int wy))
                    {
                        continue;
                    }

                    long score = IntMath.ISqrt(IntMath.DistanceSquared(lx, ly, landX, landY)) + IntMath.ISqrt(IntMath.DistanceSquared(wx, wy, ship.X, ship.Y));
                    if (score < best)
                    {
                        best = score;
                        land = new Position(lx, ly);
                        water = new Position(wx, wy);
                    }
                }
            }
        }

        return best != long.MaxValue;
    }

    /// <summary>Whether a resource node lies on water (a fish patch: boats only; GAME-02, ADR 0141).</summary>
    public static bool IsOnWater(World world, Core.Ids.EntityId node)
    {
        ArgumentNullException.ThrowIfNull(world);
        return Interaction.IsOnWater(world, node);
    }

    /// <summary>
    /// The node of <paramref name="resource"/> on water (fish) with stock left nearest to a point within
    /// <paramref name="radiusTiles"/> that <paramref name="owner"/>'s boats may fish, or none (the sim's own choice).
    /// </summary>
    public static Core.Ids.EntityId NearestFish(World world, byte owner, int resource, int x, int y, int radiusTiles)
    {
        ArgumentNullException.ThrowIfNull(world);
        return Interaction.NearestNode(world, owner, resource, x, y, radiusTiles, Content.Compiled.Locomotor.Ship);
    }

    /// <summary>How far around a blocked point (a building) its land is looked for (tiles).</summary>
    public const int ComponentSearchTiles = 6;

    /// <summary>
    /// The land component at a point for units of <paramref name="landType"/>: of the tile under it, or of the walkable
    /// tile nearest to it within <see cref="ComponentSearchTiles"/> (a building's own tiles are blocked); -1 if none.
    /// </summary>
    public static int LandComponentAt(World world, ushort landType, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(world);
        NavLayer? ground = world.Navigation.ForType(landType);
        if (ground is null)
        {
            return -1;
        }

        int tile = NavQueries.NearestWalkable(ground, ground.TileX(x), ground.TileY(y), ComponentSearchTiles);
        return tile < 0 ? -1 : ground.ComponentOf(tile);
    }

    // The water tile of the ship's component within the reach of a land point nearest to it (then to the ship).
    private static bool NearestWater(NavLayer sea, int component, int lx, int ly, Position ship, out int x, out int y)
    {
        int tx0 = sea.TileX(lx);
        int ty0 = sea.TileY(ly);
        long best = long.MaxValue;
        long bestShip = long.MaxValue;
        x = 0;
        y = 0;
        for (int ty = ty0 - ReachTiles; ty <= ty0 + ReachTiles; ty++)
        {
            for (int tx = tx0 - ReachTiles; tx <= tx0 + ReachTiles; tx++)
            {
                if (!sea.IsWalkable(tx, ty))
                {
                    continue;
                }

                int tile = (ty * sea.Width) + tx;
                if (sea.ComponentOf(tile) != component)
                {
                    continue;
                }

                int px = sea.CenterX(tile);
                int py = sea.CenterY(tile);
                long d = IntMath.DistanceSquared(px, py, lx, ly);
                long s = IntMath.DistanceSquared(px, py, ship.X, ship.Y);
                if (d < best || (d == best && s < bestShip))
                {
                    best = d;
                    bestShip = s;
                    x = px;
                    y = py;
                }
            }
        }

        return best != long.MaxValue;
    }
}
