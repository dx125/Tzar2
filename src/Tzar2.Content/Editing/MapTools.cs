using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Rng;

namespace Tzar2.Content.Editing;

/// <summary>Terrain painting (TDD §18.2 "Terrain"): brushes, water, cliffs.</summary>
public static class TerrainTools
{
    /// <summary>
    /// Paints the corners of a brush with <paramref name="value"/> (a palette value; 0 is water with a set). Cliffs on
    /// every touched tile are removed ("painting terrain over cliffs removes them").
    /// </summary>
    /// <returns>Corners changed.</returns>
    public static int Paint(EditTransaction tx, int cornerX, int cornerY, int size, int value)
    {
        ArgumentNullException.ThrowIfNull(tx);
        MapDocument doc = tx.Document;
        if (!doc.Rules.IsPaintable(value))
        {
            throw new ArgumentException($"Terrain {value} is not in the palette.", nameof(value));
        }

        int changed = 0;
        foreach ((int x, int y) in Brushes.Corners(cornerX, cornerY, size))
        {
            if (!doc.ContainsCorner(x, y))
            {
                continue;
            }

            if (tx.Paint(x, y, value))
            {
                changed++;
            }

            for (int ty = y - 1; ty <= y; ty++)
            {
                for (int tx2 = x - 1; tx2 <= x; tx2++)
                {
                    tx.SetCliff(tx2, ty, false);
                }
            }
        }

        return changed;
    }

    /// <summary>Whether a cliff may stand on tile (<paramref name="x"/>, <paramref name="y"/>): land on all four corners.</summary>
    public static bool CliffAllowed(MapDocument doc, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return doc.ContainsTile(x, y) && doc.IsLandTile(x, y);
    }

    /// <summary>Sets or erases cliffs on the tiles of a brush; tiles that are not land are skipped.</summary>
    /// <returns>Tiles changed.</returns>
    public static int Cliffs(EditTransaction tx, int tileX, int tileY, int size, bool cliff)
    {
        ArgumentNullException.ThrowIfNull(tx);
        int changed = 0;
        foreach ((int x, int y) in Brushes.Tiles(tileX, tileY, size))
        {
            if ((!cliff || CliffAllowed(tx.Document, x, y)) && tx.SetCliff(x, y, cliff))
            {
                changed++;
            }
        }

        return changed;
    }

    /// <summary>The tiles the set cannot draw (shown red), in row order.</summary>
    public static ImmutableArray<(int X, int Y)> InvalidTiles(MapDocument doc, int left, int top, int right, int bottom)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var b = ImmutableArray.CreateBuilder<(int, int)>();
        for (int y = Math.Max(0, top); y <= Math.Min(doc.Height - 1, bottom); y++)
        {
            for (int x = Math.Max(0, left); x <= Math.Min(doc.Width - 1, right); x++)
            {
                if (!doc.IsDrawableTile(x, y))
                {
                    b.Add((x, y));
                }
            }
        }

        return b.ToImmutable();
    }
}

/// <summary>How the tree brush picks a tree (TDD §18.2 "Trees").</summary>
public enum TreeChoice : byte
{
    /// <summary>One specific tree type.</summary>
    Specific,

    /// <summary>A random tree of the chosen type's group.</summary>
    RandomOfType,

    /// <summary>A random tree of all tree types.</summary>
    RandomOfAll,
}

/// <summary>Options of one tree brush application.</summary>
/// <param name="TypeKey">The chosen tree type.</param>
/// <param name="Choice">How a tree is picked.</param>
/// <param name="Radius">Brush radius index (0–4, <see cref="Brushes.TreeRadii"/>).</param>
/// <param name="DensityPercent">Forest density: the share of free tiles that get a tree (1–100).</param>
/// <param name="Accumulate">Accumulation mode: add to the trees already there; otherwise tiles with trees are left alone and the density applies to the brush.</param>
/// <param name="Seed">Seed of the deterministic choice (the editor passes a stroke counter).</param>
public sealed record TreeBrush(string TypeKey, TreeChoice Choice, int Radius, int DensityPercent, bool Accumulate, ulong Seed);

/// <summary>Tree planting and deleting.</summary>
public static class TreeTools
{
    /// <summary>
    /// Plants trees in a disc around tile (<paramref name="tileX"/>, <paramref name="tileY"/>) on free land. Random
    /// choices are deterministic (seeded by <see cref="TreeBrush.Seed"/> and the tile).
    /// </summary>
    /// <returns>Ids of the planted trees.</returns>
    public static ImmutableArray<int> Plant(EditTransaction tx, Occupancy occupancy, EditorPalette palette, int tileX, int tileY, TreeBrush brush)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(occupancy);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(brush);
        ImmutableArray<PaletteEntry> candidates = Candidates(palette, brush);
        if (candidates.IsEmpty)
        {
            return [];
        }

        var rng = Xoshiro256StarStar.FromSeed(SplitMix64.Mix(brush.Seed ^ ((ulong)(uint)tileX << 32) ^ (uint)tileY));
        int density = Math.Clamp(brush.DensityPercent, 1, 100);
        var planted = ImmutableArray.CreateBuilder<int>();
        DocumentState s = tx.State;
        int existing = 0, tiles = 0;
        foreach ((int x, int y) in Brushes.Disc(tileX, tileY, brush.Radius))
        {
            tiles++;
            existing += occupancy.StructureAt(x, y) != 0 && IsTree(occupancy, x, y, palette) ? 1 : 0;
        }

        int wanted = Math.Max(1, tiles * density / 100);
        int budget = brush.Accumulate ? wanted : Math.Max(0, wanted - existing);
        foreach ((int x, int y) in Brushes.Disc(tileX, tileY, brush.Radius))
        {
            if (budget == 0)
            {
                break;
            }

            if (rng.NextInt(100) >= density && brush.Radius > 0)
            {
                continue;
            }

            string key = candidates[rng.NextInt(candidates.Length)].Key;
            MapObject o = MapObject.AtTile(key, MapObject.Nature, x, y);
            if (!occupancy.Check(o).Ok)
            {
                continue;
            }

            s = s.Add(o, null, out int id);
            occupancy.Add(id, o);
            planted.Add(id);
            budget--;
        }

        tx.State = s;
        return planted.ToImmutable();
    }

    /// <summary>Deletes the trees in a disc.</summary>
    /// <returns>How many.</returns>
    public static int Delete(EditTransaction tx, Occupancy occupancy, EditorPalette palette, int tileX, int tileY, int radius)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(occupancy);
        var gone = new SortedSet<int>();
        foreach ((int x, int y) in Brushes.Disc(tileX, tileY, radius))
        {
            int id = occupancy.StructureAt(x, y);
            if (id != 0 && IsTree(occupancy, x, y, palette))
            {
                gone.Add(id);
            }
        }

        foreach (int id in gone)
        {
            if (tx.State.Find(id) is { } o)
            {
                occupancy.Remove(id, o.Placement);
            }
        }

        tx.State = tx.State.Remove(gone);
        return gone.Count;
    }

    /// <summary>The tree types a brush may plant.</summary>
    public static ImmutableArray<PaletteEntry> Candidates(EditorPalette palette, TreeBrush brush)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(brush);
        ImmutableArray<PaletteEntry> trees = palette.Of(PaletteKind.Tree);
        PaletteEntry? chosen = palette.Find(brush.TypeKey);
        return brush.Choice switch
        {
            TreeChoice.RandomOfAll => trees,
            TreeChoice.RandomOfType when chosen is not null => palette.Siblings(chosen),
            _ when chosen is { Kind: PaletteKind.Tree } => [chosen],
            _ => trees.IsEmpty ? [] : [trees[0]],
        };
    }

    private static bool IsTree(Occupancy occupancy, int x, int y, EditorPalette palette)
    {
        int id = occupancy.StructureAt(x, y);
        return id != 0 && occupancy.Document.State.Find(id) is { } o && palette.Find(o.TypeKey) is { Kind: PaletteKind.Tree };
    }
}

/// <summary>Placing single objects with the editor's modifiers (TDD §18.2 "Buildings", "Units", "Items").</summary>
public static class PlaceTools
{
    /// <summary>
    /// The placement of type <paramref name="key"/> at tile (<paramref name="tileX"/>, <paramref name="tileY"/>) with the
    /// original's modifiers: on a gate Ctrl turns it (horizontal/vertical), Alt flips its side and Ctrl+Alt places it open;
    /// on a bridge Alt turns it. Units stand at the tile centre with <paramref name="facing"/>.
    /// </summary>
    public static MapObject Placement(Ruleset ruleset, string key, byte owner, int tileX, int tileY, bool ctrl = false, bool alt = false, byte facing = 0)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        int typeId = ruleset.EntityTypeId(key);
        if (typeId < 0)
        {
            throw new ArgumentException($"Unknown type '{key}'.", nameof(key));
        }

        EntityTypeDefinition type = ruleset.EntityTypes[typeId];
        if (type.HasFootprint)
        {
            MapObjectFlags flags = MapObjectFlags.None;
            if (type.Traits.Gate is not null)
            {
                flags = ctrl && alt ? MapObjectFlags.Open : (ctrl ? MapObjectFlags.Rotated : 0) | (alt ? MapObjectFlags.Mirrored : 0);
            }
            else if (type.Traits.IsBridge && alt)
            {
                flags = MapObjectFlags.Rotated;
            }

            return MapObject.AtTile(key, owner, tileX, tileY) with { Flags = flags };
        }

        return new MapObject(key, owner, (tileX * MapData.UnitsPerTile) + (MapData.UnitsPerTile / 2), (tileY * MapData.UnitsPerTile) + (MapData.UnitsPerTile / 2)) { Facing = (byte)(facing & 7) };
    }

    /// <summary>Places one object if the placement rules allow it.</summary>
    /// <returns>The new id, or 0 with the reason.</returns>
    public static int Place(EditTransaction tx, Occupancy occupancy, MapObject placement, ObjectInfo? info, out string reason)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(occupancy);
        PlacementCheck check = occupancy.Check(placement);
        reason = check.Reason;
        if (!check.Ok)
        {
            return 0;
        }

        tx.State = tx.State.Add(placement, info, out int id);
        occupancy.Add(id, placement);
        return id;
    }

    /// <summary>
    /// Places a random type of <paramref name="entry"/>'s group (items, patches: "random within a group"); the choice is
    /// deterministic in <paramref name="seed"/>.
    /// </summary>
    public static string RandomOfGroup(EditorPalette palette, PaletteEntry entry, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ImmutableArray<PaletteEntry> group = palette.Siblings(entry);
        if (group.IsEmpty)
        {
            return entry.Key;
        }

        var rng = Xoshiro256StarStar.FromSeed(SplitMix64.Mix(seed));
        return group[rng.NextInt(group.Length)].Key;
    }

    /// <summary>Turns a unit by one direction (TAB clockwise, Shift+TAB counter-clockwise).</summary>
    public static DocumentState Rotate(this DocumentState state, IEnumerable<int> ids, bool counterClockwise)
        => state.UpdateAll(ids, o => o with { Placement = o.Placement with { Facing = (byte)((o.Placement.Facing + (counterClockwise ? 7 : 1)) & 7) } });

    /// <summary>Sets the resource amount of resource objects (0 restores the type's default).</summary>
    public static DocumentState SetAmount(this DocumentState state, IEnumerable<int> ids, int amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        return state.UpdateAll(ids, o => o with { Placement = o.Placement with { Amount = amount } });
    }
}

/// <summary>Walls by drag (TDD §18.2 "walls by drag"): a Bresenham line of segments, like the sim's BuildWall (ADR 0043).</summary>
public static class WallTool
{
    /// <summary>The tiles of a wall line from one tile to another (both inclusive).</summary>
    public static ImmutableArray<(int X, int Y)> Line(int x0, int y0, int x1, int y1)
    {
        var b = ImmutableArray.CreateBuilder<(int, int)>();
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int x = x0, y = y0;
        while (true)
        {
            b.Add((x, y));
            if (x == x1 && y == y1)
            {
                break;
            }

            int e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y += sy;
            }
        }

        return b.ToImmutable();
    }

    /// <summary>Places wall segments of <paramref name="key"/> along the line; segments that do not fit are skipped.</summary>
    /// <returns>Ids of the placed segments.</returns>
    public static ImmutableArray<int> Drag(EditTransaction tx, Occupancy occupancy, string key, byte owner, int x0, int y0, int x1, int y1)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(occupancy);
        var placed = ImmutableArray.CreateBuilder<int>();
        foreach ((int x, int y) in Line(x0, y0, x1, y1))
        {
            int id = PlaceTools.Place(tx, occupancy, MapObject.AtTile(key, owner, x, y), null, out _);
            if (id != 0)
            {
                placed.Add(id);
            }
        }

        return placed.ToImmutable();
    }
}
