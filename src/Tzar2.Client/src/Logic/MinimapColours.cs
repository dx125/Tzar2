using System;
using System.Collections.Generic;
using System.Globalization;

namespace Tzar2.Client.Logic;

/// <summary>
/// The minimap's colours of a terrain set (HUD-030): <c>TERRAIN.INI [minidatargb]</c> gives one colour per terrain
/// index, one for trees and one for cliffs. A tile takes the colour of the terrain most of its four corners have (ties:
/// the top-left corner's), which is ours: the data colours terrains, the map stores them per corner.
/// </summary>
public sealed class MinimapColours
{
    private readonly Dictionary<int, (byte R, byte G, byte B)> _terrains;

    private MinimapColours(Dictionary<int, (byte R, byte G, byte B)> terrains, (byte R, byte G, byte B)? tree, (byte R, byte G, byte B)? cliff)
    {
        _terrains = terrains;
        Tree = tree;
        Cliff = cliff;
    }

    /// <summary>The colour of trees, or null.</summary>
    public (byte R, byte G, byte B)? Tree { get; }

    /// <summary>The colour of cliffs, or null.</summary>
    public (byte R, byte G, byte B)? Cliff { get; }

    /// <summary>Reads <c>[minidatargb]</c>; null when the section is missing or holds no colour.</summary>
    public static MinimapColours? FromSection(IniSection? section)
    {
        if (section is null)
        {
            return null;
        }

        var terrains = new Dictionary<int, (byte, byte, byte)>();
        (byte, byte, byte)? tree = null, cliff = null;
        foreach (KeyValuePair<string?, string> e in section.Entries)
        {
            if (e.Key is null || Rgb(e.Value) is not { } c)
            {
                continue;
            }

            if (string.Equals(e.Key, "tree", StringComparison.OrdinalIgnoreCase))
            {
                tree = c;
            }
            else if (string.Equals(e.Key, "cliff", StringComparison.OrdinalIgnoreCase))
            {
                cliff = c;
            }
            else if (int.TryParse(e.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                terrains[index] = c;
            }
        }

        return terrains.Count == 0 && tree is null && cliff is null ? null : new MinimapColours(terrains, tree, cliff);
    }

    /// <summary>The colour of terrain <paramref name="index"/>, or null when the data has none.</summary>
    public (byte R, byte G, byte B)? Terrain(int index) => _terrains.TryGetValue(index, out (byte, byte, byte) c) ? c : null;

    /// <summary>The terrain a tile shows on the minimap: the one most of its corners have, else the top-left one's.</summary>
    public static int TileTerrain(int topLeft, int topRight, int bottomRight, int bottomLeft)
    {
        Span<int> corners = [topLeft, topRight, bottomRight, bottomLeft];
        int best = topLeft, bestCount = 0;
        foreach (int t in corners)
        {
            int count = 0;
            foreach (int u in corners)
            {
                count += u == t ? 1 : 0;
            }

            if (count > bestCount)
            {
                best = t;
                bestCount = count;
            }
        }

        return best;
    }

    private static (byte, byte, byte)? Rgb(string value)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return null;
        }

        byte[] c = new byte[3];
        for (int i = 0; i < 3; i++)
        {
            if (!byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out c[i]))
            {
                return null;
            }
        }

        return (c[0], c[1], c[2]);
    }
}
