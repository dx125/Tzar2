using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Content.Terrain;

namespace Tzar2.Client.Logic;

/// <summary>
/// Turns a map's sim terrain (ruleset terrain ids per corner) into the terrain-set indices the
/// <see cref="TerrainComposer"/> draws. ⚠ Every rule here rests on UNK-10 (how the original stores and chooses terrain)
/// and is kept in this one place (ADR 0071):
/// <list type="bullet">
/// <item>sim water → the set's terrain 0 (Water);</item>
/// <item>land corners within one corner of water → the set's shore terrain (the terrain with a water transition file,
/// e.g. Dirt), because the sets only have transitions from water to that terrain;</item>
/// <item>other land → the set's grass (named "Grass…" or the first other terrain), with patches of a second grass
/// type at least two corners away from the shore, so no tile ever needs three terrains;</item>
/// <item>patches come from a fixed hash of a coarse grid (placeholder variety).</item>
/// </list>
/// The M3 terrain-set conversion (§9.1) replaces this when maps carry the original terrain indices.
/// </summary>
public static class TerrainPresentation
{
    /// <summary>The set terrains used: water, shore, land, variety (−1 = none).</summary>
    public static (int Water, int Shore, int Land, int Variety) Palette(TerrainSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        int shore = -1;
        foreach (TerrainFile f in set.Files)
        {
            if (f.A == 0 && f.B > 0 && f.B < 8 && !f.IsUnresolved && (shore < 0 || f.B < shore))
            {
                shore = f.B;
            }
        }

        if (shore < 0)
        {
            shore = 1;
        }

        int land = -1, variety = -1;
        foreach (TerrainType t in set.Terrains)
        {
            if (t.Index > 0 && t.Index < 8 && t.Name.StartsWith("Grass", StringComparison.OrdinalIgnoreCase))
            {
                if (land < 0)
                {
                    land = t.Index;
                }
                else if (variety < 0 && Pair(set, land, t.Index))
                {
                    variety = t.Index;
                }
            }
        }

        if (land < 0)
        {
            land = shore;
        }

        if (land != shore && !Pair(set, shore, land))
        {
            land = shore;
        }

        return (0, shore, land, variety);
    }

    /// <summary>Builds the composer grid for <paramref name="map"/>.</summary>
    public static TerrainGrid Build(MapData map, Ruleset ruleset, TerrainSet set)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(ruleset);
        (int water, int shore, int land, int variety) = Palette(set);
        int w = map.Width, h = map.Height, stride = w + 1;
        bool[] wet = new bool[stride * (h + 1)];
        IReadOnlyList<TerrainTypeDefinition> types = ruleset.TerrainTypes;
        ReadOnlySpan<byte> corners = map.TerrainCorners;
        for (int i = 0; i < wet.Length; i++)
        {
            wet[i] = corners[i] < types.Count && types[corners[i]].IsWater;
        }

        // Distance (Chebyshev, capped at 3) from each corner to the nearest water corner.
        int[] distance = new int[wet.Length];
        Array.Fill(distance, 3);
        for (int y = 0; y <= h; y++)
        {
            for (int x = 0; x <= w; x++)
            {
                if (!wet[(y * stride) + x])
                {
                    continue;
                }

                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx <= w && ny <= h)
                        {
                            int i = (ny * stride) + nx;
                            distance[i] = Math.Min(distance[i], Math.Max(Math.Abs(dx), Math.Abs(dy)));
                        }
                    }
                }
            }
        }

        var grid = new TerrainGrid(w, h, land);
        for (int y = 0; y <= h; y++)
        {
            for (int x = 0; x <= w; x++)
            {
                int d = distance[(y * stride) + x];
                grid[x, y] = d == 0 ? water : d == 1 ? shore : land;
            }
        }

        if (variety >= 0)
        {
            for (int y = 0; y <= h; y++)
            {
                for (int x = 0; x <= w; x++)
                {
                    if (Patch(x, y) && FarFrom(grid, x, y, land, variety))
                    {
                        grid[x, y] = variety;
                    }
                }
            }
        }

        return grid;
    }

    private static bool Pair(TerrainSet set, int a, int b) => set.TransitionOf(a, b) is { IsUnresolved: false };

    // Coarse 6×6-corner blobs chosen by a fixed hash (placeholder variety, UNK-10).
    private static bool Patch(int x, int y)
    {
        int cx = x / 6, cy = y / 6;
        uint hsh = unchecked(((uint)cx * 2654435761u) ^ ((uint)cy * 40503u));
        hsh ^= hsh >> 15;
        if (hsh % 3 != 0)
        {
            return false;
        }

        int lx = x % 6, ly = y % 6;
        return lx is >= 1 and <= 4 && ly is >= 1 and <= 4;
    }

    // Every corner within two is land or variety (so a variety corner never meets the shore in one tile).
    private static bool FarFrom(TerrainGrid grid, int x, int y, int land, int variety)
    {
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx <= grid.Width && ny <= grid.Height && grid[nx, ny] != land && grid[nx, ny] != variety)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
