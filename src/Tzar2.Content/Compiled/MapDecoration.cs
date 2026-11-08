using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Tzar2.Content.Compiled;

/// <summary>
/// A decoration of a map (<c>decorations.json</c>, ADR 0121, ADR 0281): a ground patch, a ground object, a ruin, a tree
/// stump. It is drawn; ground objects and ruins also close the cells their picture covers (<see cref="SceneryTable"/>).
/// </summary>
/// <param name="Class">Legacy class (<c>ground_patch</c>, <c>ground_object</c>, <c>ruin</c>, <c>tree</c>, <c>animation</c>…).</param>
/// <param name="Image">Image name (an <c>IMAGES.INI</c> entry of the map's terrain set), or <c>stump N</c> for a tree stump.</param>
/// <param name="TileX">Tile column.</param>
/// <param name="TileY">Tile row.</param>
/// <param name="PixelX">Pixel x inside the map, or −1.</param>
/// <param name="PixelY">Pixel y inside the map, or −1.</param>
public sealed record MapDecoration(string Class, string Image, int TileX, int TileY, int PixelX, int PixelY)
{
    /// <summary>The class of a ground patch (drawn flat on the ground, never in the way).</summary>
    public const string GroundPatch = "ground_patch";

    /// <summary>The class of a ground object (a rock, a mound, a cage…).</summary>
    public const string GroundObject = "ground_object";

    /// <summary>The class of a ruin (a statue, an altar…).</summary>
    public const string Ruin = "ruin";

    /// <summary>The class of a tree stump.</summary>
    public const string Tree = "tree";

    /// <summary>
    /// Whether this decoration closes the cells under its picture: ground objects and ruins do (ReTzar's reading of the
    /// original, 0x434d10 and 0x41ddc0; ADR 0281).
    /// </summary>
    public bool Obstructs => Class is GroundObject or Ruin;

    /// <summary>
    /// Where a ground patch lies, in map pixels: its own pixel when it has one (the original stores patches at cell (0, 0)
    /// with their exact pixel; ReTzar, <c>MapObject::location</c>), else its tile's corner.
    /// </summary>
    public (int X, int Y) PatchPoint => Class == GroundPatch && PixelX >= 0 && PixelY >= 0 && TileX == 0 && TileY == 0
        ? (Math.Max(0, (int)(short)PixelX), Math.Max(0, (int)(short)PixelY))
        : (TileX * 32, TileY * 32);

    /// <summary>The tile the decoration lies on: a patch's by its own pixel, else its tile.</summary>
    public (int X, int Y) Tile
    {
        get
        {
            (int px, int py) = PatchPoint;
            return (px / 32, py / 32);
        }
    }
}

/// <summary>
/// The cells a decoration picture covers (ADR 0281): its <c>IMAGES.INI</c> <c>object</c> box in whole tiles, and per cell
/// whether it stays free (the original's <c>&lt;picture&gt;.dat</c> grid, bit 2; 0x497c90, 0x41de41).
/// </summary>
/// <param name="Set">Terrain set of the <c>IMAGES.INI</c> it comes from (<c>grass</c>, <c>common</c>…).</param>
/// <param name="Image">Image name.</param>
/// <param name="Width">Width in tiles (at least 1).</param>
/// <param name="Height">Height in tiles (at least 1).</param>
/// <param name="Free">Per cell, row-major (<paramref name="Width"/> × <paramref name="Height"/>), whether it stays free.</param>
public sealed record SceneryShape(string Set, string Image, int Width, int Height, ImmutableArray<bool> Free)
{
    /// <summary>Whether cell (<paramref name="dx"/>, <paramref name="dy"/>) of the picture is closed.</summary>
    public bool Closes(int dx, int dy) => !Free[(dy * Width) + dx];
}

/// <summary>The decoration shapes of a ruleset, looked up by terrain set and image name (case-insensitive, as the original).</summary>
public sealed class SceneryTable
{
    /// <summary>The terrain set whose pictures every map may use.</summary>
    public const string CommonSet = "common";

    private readonly Dictionary<string, SceneryShape> _byKey = new(StringComparer.OrdinalIgnoreCase); // "set|image"; lookup only (T2D004)
    private readonly Dictionary<string, SceneryShape> _anySet = new(StringComparer.OrdinalIgnoreCase); // lookup only (T2D004)

    /// <summary>Creates a table; <paramref name="shapes"/> are kept sorted by set, then image.</summary>
    public SceneryTable(IEnumerable<SceneryShape> shapes)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        var sorted = new List<SceneryShape>(shapes);
        sorted.Sort(static (a, b) =>
        {
            int c = string.CompareOrdinal(a.Set, b.Set);
            return c != 0 ? c : string.CompareOrdinal(a.Image, b.Image);
        });
        foreach (SceneryShape s in sorted)
        {
            if (s.Width < 1 || s.Height < 1 || s.Free.Length != s.Width * s.Height)
            {
                throw new ArgumentException($"Scenery '{s.Set}/{s.Image}' has a malformed shape.", nameof(shapes));
            }

            _byKey.TryAdd(Key(s.Set, s.Image), s);
            _anySet.TryAdd(s.Image, s);
        }

        Shapes = [.. sorted];
    }

    /// <summary>An empty table (rulesets without the original's data).</summary>
    public static SceneryTable Empty { get; } = new([]);

    /// <summary>Every shape, sorted by set, then image.</summary>
    public ImmutableArray<SceneryShape> Shapes { get; }

    /// <summary>
    /// The shape of <paramref name="image"/> on a map of terrain set <paramref name="set"/>: the set's own picture, else
    /// the common one, else any set's; null when the ruleset knows none.
    /// </summary>
    public SceneryShape? Find(string? set, string image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (set is not null && _byKey.TryGetValue(Key(set, image), out SceneryShape? own))
        {
            return own;
        }

        return _byKey.TryGetValue(Key(CommonSet, image), out SceneryShape? common) ? common : _anySet.GetValueOrDefault(image);
    }

    private static string Key(string set, string image) => set + "|" + image;
}
