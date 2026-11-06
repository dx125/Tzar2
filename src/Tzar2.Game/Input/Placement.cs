using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Sim.Systems;

namespace Tzar2.Game.Input;

/// <summary>
/// Legality of a build-placement ghost from a frame's tile layers (<see cref="Session.GameFrame.Buildable"/> and
/// <see cref="Session.GameFrame.Terrain"/>, filled by the sim's <c>PlacementQueries</c>): the same answer as the sim's
/// check for the state of that frame, for land, coast (harbors) and bridges, turned or not (CTRL-024, BLD-019).
/// </summary>
public static class Placement
{
    /// <summary><see cref="Session.GameFrame.Terrain"/> bit: open water ships sail.</summary>
    public const byte Water = 1;

    /// <summary><see cref="Session.GameFrame.Terrain"/> bit: walkable land.</summary>
    public const byte Land = 2;

    /// <summary><see cref="Session.GameFrame.Terrain"/> bit: no cliff, structure or bridge (a bridge may cover it).</summary>
    public const byte Free = 4;

    /// <summary><see cref="Session.GameFrame.Terrain"/> bit: a piece of wall, of any kingdom and built or not, which a gate may replace (BLD-018).</summary>
    public const byte Wall = 8;

    /// <summary><see cref="Session.GameFrame.Terrain"/> bit: no building, tree, bridge or field but walls, where a gate's drawbridge may come down (BLD-018).</summary>
    public const byte Clear = 16;

    /// <summary>Whether <paramref name="type"/> fits with its footprint's top-left at (<paramref name="tileX"/>, <paramref name="tileY"/>).</summary>
    public static bool IsLegal(ReadOnlySpan<byte> buildable, int width, int height, EntityTypeDefinition type, int tileX, int tileY)
        => IsLegal(buildable, [], width, height, type, tileX, tileY, rotated: false);

    /// <summary>
    /// Whether <paramref name="type"/>, turned by 90° when <paramref name="rotated"/>, fits with its footprint's top-left
    /// at (<paramref name="tileX"/>, <paramref name="tileY"/>) under its placement rule. Without
    /// <paramref name="terrain"/>, coast and bridge placements are judged as land.
    /// </summary>
    public static bool IsLegal(ReadOnlySpan<byte> buildable, ReadOnlySpan<byte> terrain, int width, int height, EntityTypeDefinition type, int tileX, int tileY, bool rotated)
        => IsLegal(buildable, terrain, width, height, type, tileX, tileY, rotated, mirrored: false);

    /// <summary>
    /// <see cref="IsLegal(ReadOnlySpan{byte}, ReadOnlySpan{byte}, int, int, EntityTypeDefinition, int, int, bool)"/> for
    /// a gate facing the other way along its axis when <paramref name="mirrored"/> (CTRL-024): the tiles before its door
    /// change sides.
    /// </summary>
    public static bool IsLegal(ReadOnlySpan<byte> buildable, ReadOnlySpan<byte> terrain, int width, int height, EntityTypeDefinition type, int tileX, int tileY, bool rotated, bool mirrored)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.HasFootprint)
        {
            return false;
        }

        return BlockedCells(buildable, terrain, width, height, type, tileX, tileY, rotated, mirrored).Count == 0
            && (terrain.Length != width * height || type.Traits.Placement != PlacementKind.Coast || NearWater(terrain, width, height, type, tileX, tileY, rotated))
            && (terrain.Length != width * height || type.Traits.Placement != PlacementKind.Bridge || CrossesWater(terrain, width, type, tileX, tileY, rotated));
    }

    /// <summary>
    /// The footprint cells (offsets from the top-left) where <paramref name="type"/> cannot stand at (<paramref name="tileX"/>,
    /// <paramref name="tileY"/>): off the map or not buildable. The placement ghost marks only these.
    /// </summary>
    public static List<(int Dx, int Dy)> BlockedCells(ReadOnlySpan<byte> buildable, int width, int height, EntityTypeDefinition type, int tileX, int tileY)
        => BlockedCells(buildable, [], width, height, type, tileX, tileY, rotated: false);

    /// <summary>
    /// The footprint cells (offsets from the top-left, of the turned footprint when <paramref name="rotated"/>) where
    /// <paramref name="type"/> cannot stand: off the map, not buildable, or for a bridge a cliff, structure or bridge, or
    /// an end off the land.
    /// </summary>
    public static List<(int Dx, int Dy)> BlockedCells(ReadOnlySpan<byte> buildable, ReadOnlySpan<byte> terrain, int width, int height, EntityTypeDefinition type, int tileX, int tileY, bool rotated)
        => BlockedCells(buildable, terrain, width, height, type, tileX, tileY, rotated, mirrored: false);

    /// <summary>
    /// The cells (offsets from the top-left) where <paramref name="type"/> cannot stand, as above; for a gate, also the
    /// tiles before its door (they lie outside its footprint) that are off the map or hold a building, tree, bridge or
    /// field (BLD-018). <paramref name="mirrored"/>: the gate faces the other way along its axis.
    /// </summary>
    public static List<(int Dx, int Dy)> BlockedCells(ReadOnlySpan<byte> buildable, ReadOnlySpan<byte> terrain, int width, int height, EntityTypeDefinition type, int tileX, int tileY, bool rotated, bool mirrored)
    {
        ArgumentNullException.ThrowIfNull(type);
        var blocked = new List<(int Dx, int Dy)>();
        bool turned = Turned(type, rotated);
        (int w, int h) = Size(type, rotated);
        bool bridge = type.Traits.Placement == PlacementKind.Bridge && terrain.Length == width * height;
        bool gate = type.Traits.Gate is not null && terrain.Length == width * height;
        bool horizontal = w >= h;
        ReadOnlySpan<byte> mask = type.FootprintMask(turned);
        for (int dy = 0; dy < h; dy++)
        {
            for (int dx = 0; dx < w; dx++)
            {
                int m = (dy * w) + dx;
                if (!bridge && mask.Length == w * h && mask[m] == 0)
                {
                    continue;
                }

                int x = tileX + dx, y = tileY + dy;
                if (x < 0 || y < 0 || x >= width || y >= height || buildable.Length != width * height)
                {
                    blocked.Add((dx, dy));
                }
                else if (bridge)
                {
                    // A bridge's ends stand on land; every tile is free (ADR 0043).
                    bool end = horizontal ? dx == 0 || dx == w - 1 : dy == 0 || dy == h - 1;
                    byte t = terrain[(y * width) + x];
                    if ((t & Free) == 0 || (end && (t & Land) == 0))
                    {
                        blocked.Add((dx, dy));
                    }
                }
                else if (buildable[(y * width) + x] == 0 && !(gate && (terrain[(y * width) + x] & Wall) != 0))
                {
                    blocked.Add((dx, dy)); // a gate may stand on walls, which it replaces (BLD-018)
                }
            }
        }

        if (gate)
        {
            Approach(terrain, width, height, type, tileX, tileY, turned, mirrored, blocked);
        }

        return blocked;
    }

    /// <summary>
    /// The tiles of a wall dragged from (<paramref name="x0"/>, <paramref name="y0"/>) to (<paramref name="x1"/>,
    /// <paramref name="y1"/>), as the sim lays them (BLD-011): the original's L, the longer side first, the other with
    /// <paramref name="alt"/>.
    /// </summary>
    public static void WallRow(int x0, int y0, int x1, int y1, bool alt, List<(int X, int Y)> row)
        => PlacementQueries.WallRow(x0, y0, x1, y1, alt, null, row);

    /// <summary>The footprint's width and height in tiles, turned when <paramref name="rotated"/> (its own turned footprint).</summary>
    public static (int Width, int Height) Size(EntityTypeDefinition type, bool rotated)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.FootprintSize(Turned(type, rotated));
    }

    /// <summary>
    /// The facing of a gate or bridge being placed while Alt and Ctrl are held (CTRL-024, BLD-019; the original's help: "by
    /// holding down the Alt or Ctrl key", a bridge by Alt): Alt turns it by 90°, as a bridge; Ctrl, on a gate, has it face
    /// the other way. 0 to 3, the bits of the Build command's argument (2 turned, 1 the other way).
    /// </summary>
    public static int FacingOf(EntityTypeDefinition type, bool alt, bool ctrl)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!CanTurn(type))
        {
            return 0;
        }

        return (alt ? 2 : 0) + (ctrl && type.Traits.Gate is not null ? 1 : 0);
    }

    /// <summary>Whether a placement of <paramref name="type"/> can be turned (gates and bridges, CTRL-024, BLD-019).</summary>
    public static bool CanTurn(EntityTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return (type.Traits.Gate is not null || type.Traits.IsBridge) && type.CanTurn;
    }

    /// <summary>The footprint top-left tile for a ghost centred on the world point (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static (int TileX, int TileY) TopLeftFor(EntityTypeDefinition type, int x, int y) => TopLeftFor(type, x, y, rotated: false);

    /// <summary>The footprint top-left tile for a ghost, turned when <paramref name="rotated"/>, centred on (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static (int TileX, int TileY) TopLeftFor(EntityTypeDefinition type, int x, int y, bool rotated)
    {
        ArgumentNullException.ThrowIfNull(type);
        (int w, int h) = Size(type, rotated);
        int tx = (int)Math.Floor((x / (double)MapData.UnitsPerTile) - (w / 2.0) + 0.5);
        int ty = (int)Math.Floor((y / (double)MapData.UnitsPerTile) - (h / 2.0) + 0.5);
        return (tx, ty);
    }

    private static bool Turned(EntityTypeDefinition type, bool rotated) => rotated && type.CanTurn;

    /// <summary>
    /// The tiles before a gate's door where its drawbridge comes down (BLD-018), as offsets from its footprint's top-left
    /// tile; none for anything else. The placement ghost shows them, so which way the gate opens can be seen (CTRL-024).
    /// </summary>
    public static List<(int Dx, int Dy)> ApproachTiles(EntityTypeDefinition type, bool turned, bool mirrored)
    {
        ArgumentNullException.ThrowIfNull(type);
        var tiles = new List<(int Dx, int Dy)>();
        if (type.Traits.Gate is not { } gate)
        {
            return tiles;
        }

        turned = Turned(type, turned);
        (int w, int h) = Size(type, turned);
        ReadOnlySpan<byte> door = gate.PassageOf(turned);
        int reach = gate.ApproachOf((turned ? 2 : 0) + (mirrored ? 1 : 0));
        (int sx, int sy) = w >= h ? (0, mirrored ? -1 : 1) : (mirrored ? 1 : -1, 0);
        for (int dy = 0; dy < h && door.Length == w * h; dy++)
        {
            for (int dx = 0; dx < w; dx++)
            {
                for (int n = 1; door[(dy * w) + dx] != 0 && n <= reach; n++)
                {
                    tiles.Add((dx + (sx * n), dy + (sy * n)));
                }
            }
        }

        return tiles;
    }

    // The tiles before a gate's door, as the sim's site test (BLD-018): blocked when off the map or not clear.
    private static void Approach(ReadOnlySpan<byte> terrain, int width, int height, EntityTypeDefinition type, int tileX, int tileY, bool turned, bool mirrored, List<(int Dx, int Dy)> blocked)
    {
        foreach ((int ax, int ay) in ApproachTiles(type, turned, mirrored))
        {
            int x = tileX + ax, y = tileY + ay;
            if (x < 0 || y < 0 || x >= width || y >= height || (terrain[(y * width) + x] & Clear) == 0)
            {
                blocked.Add((ax, ay));
            }
        }
    }

    // A harbor needs water for its ships within its type's reach of its footprint (as the sim).
    private static bool NearWater(ReadOnlySpan<byte> terrain, int width, int height, EntityTypeDefinition type, int tileX, int tileY, bool rotated)
    {
        (int w, int h) = Size(type, rotated);
        int reach = type.Traits.CoastReachTiles;
        for (int y = Math.Max(0, tileY - reach); y < Math.Min(height, tileY + h + reach); y++)
        {
            for (int x = Math.Max(0, tileX - reach); x < Math.Min(width, tileX + w + reach); x++)
            {
                if ((terrain[(y * width) + x] & Water) != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // A bridge spans open water somewhere.
    private static bool CrossesWater(ReadOnlySpan<byte> terrain, int width, EntityTypeDefinition type, int tileX, int tileY, bool rotated)
    {
        (int w, int h) = Size(type, rotated);
        for (int dy = 0; dy < h; dy++)
        {
            for (int dx = 0; dx < w; dx++)
            {
                if ((terrain[((tileY + dy) * width) + tileX + dx] & Water) != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
