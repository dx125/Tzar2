using System;
using Tzar2.Core.Numerics;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;
using Tzar2.Sim.Navigation;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Shore points for ships (REQ-PATH-09, ADR 0140): where a ship and land units meet to board, unload or repair. A dock
/// is a pair of a land tile the units can stand on (in their component) and a water tile in the ship's component within
/// <see cref="ReachTiles"/> of it (straight-line, across the shore tile that carries neither, ADR 0042). The pair nearest
/// to both parties wins: land tile to the land reference point plus water tile to the ship (ties: ring order, then
/// row-major). Water within <see cref="CrowdRadiusTiles"/> of another ship or of another ship's dock is taken: a dock is
/// looked for among free water first, so ships meeting at one shore spread along it (ADR 0146).
/// </summary>
public static class Shore
{
    /// <summary>Most tiles between the land and the water tile of a dock (UNVERIFIED, UNK-11).</summary>
    public const int ReachTiles = 2;

    /// <summary>How far from the land reference point a dock is searched (tiles; UNVERIFIED).</summary>
    public const int SearchRadiusTiles = 48;

    /// <summary>Water this close to another ship or its dock is taken (tiles; UNVERIFIED, UNK-43).</summary>
    public const int CrowdRadiusTiles = 2;

    /// <summary>Most other ships and docks a dock search keeps clear of (the nearest to the land point).</summary>
    public const int MaxCrowd = 64;

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
        => FindDock(world, Core.Ids.EntityId.None, shipType, ship, landType, landComponent, landX, landY, out land, out water);

    /// <summary>
    /// As <see cref="FindDock(World, ushort, Position, ushort, int, int, int, out Position, out Position)"/> for the ship
    /// <paramref name="self"/>, keeping clear of the other ships and their docks where it can.
    /// </summary>
    public static bool FindDock(World world, Core.Ids.EntityId self, ushort shipType, Position ship, ushort landType, int landComponent, int landX, int landY, out Position land, out Position water)
        => FindDock(world, self, shipType, ship, landType, landComponent, landX, landY, null, out land, out water);

    /// <summary>As the overload above, also keeping clear of <paramref name="avoid"/> (a dock that did not work).</summary>
    public static bool FindDock(World world, Core.Ids.EntityId self, ushort shipType, Position ship, ushort landType, int landComponent, int landX, int landY, Position? avoid, out Position land, out Position water)
    {
        ArgumentNullException.ThrowIfNull(world);
        Span<Position> crowd = stackalloc Position[MaxCrowd + 1];
        int n = Crowd(world, self, landX, landY, crowd[..MaxCrowd]);
        if (avoid is { } a)
        {
            crowd[n++] = a;
        }

        return Search(world, shipType, ship, landType, landComponent, landX, landY, crowd[..n], out land, out water)
            || (n > 0 && Search(world, shipType, ship, landType, landComponent, landX, landY, [], out land, out water));
    }

    // The other ships on the water and the docks they sail to, nearest to the land point first (ties: entity order).
    private static int Crowd(World world, Core.Ids.EntityId self, int x, int y, Span<Position> crowd)
    {
        long reach = (long)(SearchRadiusTiles + CrowdRadiusTiles + ReachTiles) * MapState.UnitsPerTile;
        int n = 0;
        ReadOnlySpan<Core.Ids.EntityId> movers = world.Motions.Entities;
        for (int i = 0; i < movers.Length; i++)
        {
            Core.Ids.EntityId e = movers[i];
            if (e == self || !world.IsShip(e))
            {
                continue;
            }

            Add(world.Positions.Get(e), x, y, reach, crowd, ref n);
            if (world.Dockings.IndexOf(e) is >= 0 and var k)
            {
                Docking d = world.Dockings.At(k);
                Add(new Position(d.WaterX, d.WaterY), x, y, reach, crowd, ref n);
            }
        }

        return n;
    }

    private static void Add(Position p, int x, int y, long reach, Span<Position> crowd, ref int n)
    {
        long d = IntMath.DistanceSquared(p.X, p.Y, x, y);
        if (d > reach * reach)
        {
            return;
        }

        // Keep the nearest MaxCrowd (sorted by distance, stable for equal ones).
        int k;
        if (n < crowd.Length)
        {
            k = n++;
        }
        else if (d < IntMath.DistanceSquared(crowd[n - 1].X, crowd[n - 1].Y, x, y))
        {
            k = n - 1;
        }
        else
        {
            return;
        }

        while (k > 0 && IntMath.DistanceSquared(crowd[k - 1].X, crowd[k - 1].Y, x, y) > d)
        {
            crowd[k] = crowd[k - 1];
            k--;
        }

        crowd[k] = p;
    }

    private static bool Search(World world, ushort shipType, Position ship, ushort landType, int landComponent, int landX, int landY, ReadOnlySpan<Position> crowd, out Position land, out Position water)
    {
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
                    if (!NearestWater(sea, shipComponent, lx, ly, ship, crowd, out int wx, out int wy))
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

    private static bool IsTaken(int x, int y, ReadOnlySpan<Position> crowd, long clear)
    {
        foreach (Position p in crowd)
        {
            if (IntMath.DistanceSquared(x, y, p.X, p.Y) < clear * clear)
            {
                return true;
            }
        }

        return false;
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

    /// <summary>The edge distance from a point to an entity (structures: to the footprint; units: minus the radius).</summary>
    public static int EdgeDistance(World world, Position from, Core.Ids.EntityId to)
    {
        ArgumentNullException.ThrowIfNull(world);
        return Interaction.EdgeDistance(world, from, 0, to);
    }

    /// <summary>
    /// The land tile of the unit's component within <see cref="ReachTiles"/> (straight-line) of a ship, nearest to the
    /// ship (then to the unit; then row-major): where a boarder goes. <c>false</c> when the ship is not at such a shore.
    /// </summary>
    public static bool LandingNear(World world, ushort landType, Position unit, Position ship, out Position landing)
    {
        ArgumentNullException.ThrowIfNull(world);
        landing = default;
        NavLayer? ground = world.Navigation.ForType(landType);
        if (ground is null)
        {
            return false;
        }

        int component = ground.ComponentOf(ground.TileOf(unit.X, unit.Y));
        if (component < 0)
        {
            return false;
        }

        long reach = (long)ReachTiles * MapState.UnitsPerTile;
        int cx = ground.TileX(ship.X);
        int cy = ground.TileY(ship.Y);
        long best = long.MaxValue;
        long bestUnit = long.MaxValue;
        for (int ty = cy - ReachTiles - 1; ty <= cy + ReachTiles + 1; ty++)
        {
            for (int tx = cx - ReachTiles - 1; tx <= cx + ReachTiles + 1; tx++)
            {
                if (!ground.IsWalkable(tx, ty))
                {
                    continue;
                }

                int tile = (ty * ground.Width) + tx;
                int px = ground.CenterX(tile);
                int py = ground.CenterY(tile);
                long d = IntMath.DistanceSquared(px, py, ship.X, ship.Y);
                if (d > reach * reach || ground.ComponentOf(tile) != component)
                {
                    continue;
                }

                long u = IntMath.DistanceSquared(px, py, unit.X, unit.Y);
                if (d < best || (d == best && u < bestUnit))
                {
                    best = d;
                    bestUnit = u;
                    landing = new Position(px, py);
                }
            }
        }

        return best != long.MaxValue;
    }

    /// <summary>How far around a blocked point (a building) its land is looked for (tiles).</summary>
    public const int ComponentSearchTiles = 6;

    /// <summary>
    /// The land component at a point for units of <paramref name="landType"/>: of the tile under it, or of the walkable
    /// tile nearest to it within <see cref="ComponentSearchTiles"/> (a building's own tiles are blocked); -1 if none.
    /// </summary>
    public static int LandComponentAt(World world, ushort landType, int x, int y) => LandComponentAt(world, landType, x, y, ComponentSearchTiles);

    /// <summary>How far around an unload point its land is looked for (tiles).</summary>
    public const int UnloadSearchTiles = 24;

    /// <summary>As <see cref="LandComponentAt(World, ushort, int, int)"/>, looking up to <paramref name="radiusTiles"/> around the point.</summary>
    public static int LandComponentAt(World world, ushort landType, int x, int y, int radiusTiles)
    {
        ArgumentNullException.ThrowIfNull(world);
        NavLayer? ground = world.Navigation.ForType(landType);
        if (ground is null)
        {
            return -1;
        }

        int tile = NavQueries.NearestWalkable(ground, ground.TileX(x), ground.TileY(y), radiusTiles);
        return tile < 0 ? -1 : ground.ComponentOf(tile);
    }

    // The water tile of the ship's component within the reach of a land point nearest to it (then to the ship).
    private static bool NearestWater(NavLayer sea, int component, int lx, int ly, Position ship, ReadOnlySpan<Position> crowd, out int x, out int y)
    {
        long reach = (long)ReachTiles * MapState.UnitsPerTile;
        long clear = (long)CrowdRadiusTiles * MapState.UnitsPerTile;
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
                if (d > reach * reach || IsTaken(px, py, crowd, clear))
                {
                    continue; // beyond the shore reach (boarding and repair need it), or another ship's place
                }

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
