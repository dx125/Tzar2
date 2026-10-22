using System;
using System.Collections.Generic;
using System.Globalization;

namespace Tzar2.Client.Logic;

/// <summary>
/// How a terrain set draws its trees (PRES-009), from its <c>TERRAIN.INI</c>:
/// <list type="bullet">
/// <item><c>[trees]</c> <c>pos_tree</c> and <c>pos_shadow</c>: the tiles a tree's and its shadow's pictures reach left
/// of and above the tree's tile (a picture's top-left corner is the tile's minus 32 px times the anchor);</item>
/// <item><c>[treename]</c>: the kinds of the 31-picture strip in order, with how many pictures each has;</item>
/// <item><c>[treeterrain]</c>: the terrains each kind grows on.</item>
/// </list>
/// Read as ReTzar reads the original (0x43cb0e, 0x43cc84); the code is ours.
/// </summary>
public sealed class TreeLayout
{
    /// <summary>A tile's side in pixels.</summary>
    public const int Tile = 32;

    private readonly List<(string Kind, int First, int Count)> _kinds = [];
    private readonly Dictionary<string, HashSet<int>> _terrains = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The layout the client used before the data was read: anchor (1, 3) for trees and their shadows.</summary>
    public static TreeLayout Default { get; } = new() { TreeAnchor = (1, 3), ShadowAnchor = (1, 3) };

    /// <summary>The tree picture's anchor in tiles (<c>pos_tree</c>).</summary>
    public (int X, int Y) TreeAnchor { get; private init; } = (1, 3);

    /// <summary>The shadow picture's anchor in tiles (<c>pos_shadow</c>).</summary>
    public (int X, int Y) ShadowAnchor { get; private init; } = (0, 1);

    /// <summary>The kinds of the strip in order: name, first picture (0-based) and count.</summary>
    public IReadOnlyList<(string Kind, int First, int Count)> Kinds => _kinds;

    /// <summary>Reads the three sections; missing ones keep the defaults (anchors (1, 3) and (0, 1), no kinds).</summary>
    public static TreeLayout FromIni(IniSection? trees, IniSection? names, IniSection? terrains)
    {
        var layout = new TreeLayout
        {
            TreeAnchor = Pair(trees?.Get("pos_tree")) ?? (1, 3),
            ShadowAnchor = Pair(trees?.Get("pos_shadow")) ?? (0, 1),
        };
        int first = 0;
        foreach (KeyValuePair<string?, string> e in names?.Entries ?? [])
        {
            if (e.Key is { Length: > 0 } kind && int.TryParse(e.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count > 0)
            {
                layout._kinds.Add((kind, first, count));
                first += count;
            }
        }

        foreach (KeyValuePair<string?, string> e in terrains?.Entries ?? [])
        {
            if (e.Key is { Length: > 0 } kind)
            {
                var set = new HashSet<int>();
                foreach (string part in e.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int t))
                    {
                        set.Add(t);
                    }
                }

                layout._terrains[kind] = set;
            }
        }

        return layout;
    }

    /// <summary>The top-left corner of the tree picture of the tree on tile (<paramref name="tileX"/>, <paramref name="tileY"/>).</summary>
    public (int X, int Y) TreeOrigin(int tileX, int tileY) => ((tileX - TreeAnchor.X) * Tile, (tileY - TreeAnchor.Y) * Tile);

    /// <summary>The top-left corner of its shadow's picture.</summary>
    public (int X, int Y) ShadowOrigin(int tileX, int tileY) => ((tileX - ShadowAnchor.X) * Tile, (tileY - ShadowAnchor.Y) * Tile);

    /// <summary>
    /// The picture (0-based) of a tree on terrain <paramref name="terrain"/>: one of the pictures of the kinds that grow
    /// there, picked by <paramref name="phase"/> (Tzar2's trees carry no kind). Without kinds for the terrain: one of
    /// pictures 8 to 26, as before.
    /// </summary>
    public int Picture(int terrain, uint phase)
    {
        int total = 0;
        foreach ((string kind, int _, int count) in _kinds)
        {
            total += Grows(kind, terrain) ? count : 0;
        }

        if (total == 0)
        {
            return 8 + (int)(phase % 19u);
        }

        int pick = (int)(phase % (uint)total);
        foreach ((string kind, int first, int count) in _kinds)
        {
            if (!Grows(kind, terrain))
            {
                continue;
            }

            if (pick < count)
            {
                return first + pick;
            }

            pick -= count;
        }

        return 8;
    }

    private bool Grows(string kind, int terrain) => _terrains.TryGetValue(kind, out HashSet<int>? set) && set.Contains(terrain);

    private static (int, int)? Pair(string? value)
    {
        string[] parts = (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x)
            && int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int y) ? (x, y) : null;
    }
}
