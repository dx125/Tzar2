using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;

namespace Tzar2.Game.Input;

/// <summary>
/// Legality of a build-placement ghost from a frame's buildable-tile layer (<see cref="Session.GameFrame.Buildable"/>,
/// filled by the sim's <c>PlacementQueries</c>): the same answer as the sim's check for the state of that frame.
/// </summary>
public static class Placement
{
    /// <summary>Whether <paramref name="type"/> fits with its footprint's top-left at (<paramref name="tileX"/>, <paramref name="tileY"/>).</summary>
    public static bool IsLegal(ReadOnlySpan<byte> buildable, int width, int height, EntityTypeDefinition type, int tileX, int tileY)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.HasFootprint || tileX < 0 || tileY < 0 || tileX + type.FootprintWidth > width || tileY + type.FootprintHeight > height
            || buildable.Length != width * height)
        {
            return false;
        }

        ReadOnlySpan<byte> mask = type.Footprint;
        for (int dy = 0; dy < type.FootprintHeight; dy++)
        {
            for (int dx = 0; dx < type.FootprintWidth; dx++)
            {
                if (mask[(dy * type.FootprintWidth) + dx] != 0 && buildable[((tileY + dy) * width) + tileX + dx] == 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The footprint cells (offsets from the top-left) where <paramref name="type"/> cannot stand at (<paramref name="tileX"/>,
    /// <paramref name="tileY"/>): off the map or not buildable. The placement ghost marks only these.
    /// </summary>
    public static List<(int Dx, int Dy)> BlockedCells(ReadOnlySpan<byte> buildable, int width, int height, EntityTypeDefinition type, int tileX, int tileY)
    {
        ArgumentNullException.ThrowIfNull(type);
        var blocked = new List<(int Dx, int Dy)>();
        ReadOnlySpan<byte> mask = type.Footprint;
        for (int dy = 0; dy < type.FootprintHeight; dy++)
        {
            for (int dx = 0; dx < type.FootprintWidth; dx++)
            {
                if (mask.Length == type.FootprintWidth * type.FootprintHeight && mask[(dy * type.FootprintWidth) + dx] == 0)
                {
                    continue;
                }

                int x = tileX + dx, y = tileY + dy;
                if (x < 0 || y < 0 || x >= width || y >= height || buildable.Length != width * height || buildable[(y * width) + x] == 0)
                {
                    blocked.Add((dx, dy));
                }
            }
        }

        return blocked;
    }

    /// <summary>The footprint top-left tile for a ghost centred on the world point (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static (int TileX, int TileY) TopLeftFor(EntityTypeDefinition type, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(type);
        int tx = (int)Math.Floor((x / (double)MapData.UnitsPerTile) - (type.FootprintWidth / 2.0) + 0.5);
        int ty = (int)Math.Floor((y / (double)MapData.UnitsPerTile) - (type.FootprintHeight / 2.0) + 0.5);
        return (tx, ty);
    }
}
