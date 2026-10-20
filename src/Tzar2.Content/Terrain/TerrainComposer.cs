using System;
using System.Collections.Generic;
using System.Globalization;
using Tzar2.Content.Assets;

namespace Tzar2.Content.Terrain;

/// <summary>
/// Terrain types on a corner (vertex) grid of <c>(Width + 1) × (Height + 1)</c> values for <c>Width × Height</c> tiles
/// (TDD §9.1 ⚠ HYPOTHESIS, UNK-10: the original may store terrain per corner or per tile).
/// </summary>
public sealed class TerrainGrid
{
    private readonly int[] _corners;

    /// <summary>Creates a grid with every corner set to <paramref name="fill"/>.</summary>
    public TerrainGrid(int width, int height, int fill)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Width = width;
        Height = height;
        _corners = new int[(width + 1) * (height + 1)];
        Array.Fill(_corners, fill);
    }

    /// <summary>Tiles across.</summary>
    public int Width { get; }

    /// <summary>Tiles down.</summary>
    public int Height { get; }

    /// <summary>Terrain at corner (<paramref name="x"/>, <paramref name="y"/>), 0 ≤ x ≤ Width, 0 ≤ y ≤ Height.</summary>
    public int this[int x, int y]
    {
        get => _corners[(y * (Width + 1)) + x];
        set => _corners[(y * (Width + 1)) + x] = value;
    }

    /// <summary>The four corners of tile (<paramref name="x"/>, <paramref name="y"/>): top-left, top-right, bottom-right, bottom-left.</summary>
    public (int Tl, int Tr, int Br, int Bl) CornersOf(int x, int y) => (this[x, y], this[x + 1, y], this[x + 1, y + 1], this[x, y + 1]);

    /// <summary>Whether any corner holds <paramref name="terrain"/>.</summary>
    public bool Contains(int terrain) => Array.IndexOf(_corners, terrain) >= 0;

    /// <summary>
    /// A demonstration grid for the pair (<paramref name="a"/>, <paramref name="b"/>): <paramref name="b"/> everywhere,
    /// a block of <paramref name="a"/> with a one-corner hole, isolated <paramref name="a"/> corners and diagonal pairs,
    /// so every one of the 14 transition shapes (masks 1–14) occurs at least once. Needs at least 20 × 12 tiles.
    /// </summary>
    public static TerrainGrid Demo(int a, int b, int width = 20, int height = 12)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 20);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 12);
        var grid = new TerrainGrid(width, height, b);
        for (int y = 2; y <= 9; y++)
        {
            for (int x = 2; x <= 8; x++)
            {
                grid[x, y] = a;
            }
        }

        grid[5, 5] = b;
        grid[12, 3] = a;
        grid[15, 3] = a;
        grid[12, 7] = a;
        grid[13, 8] = a;
        grid[17, 7] = a;
        grid[16, 8] = a;
        return grid;
    }
}

/// <summary>Result of <see cref="TerrainComposer.Compose(TerrainGrid, int)"/>.</summary>
/// <param name="Width">Pixels across.</param>
/// <param name="Height">Pixels down.</param>
/// <param name="Pixels">RGB5A1 words, little-endian, row-major, all opaque (bit 15 set).</param>
/// <param name="Issues">Tiles that could not be drawn as specified, with the reason.</param>
public sealed record TerrainImage(int Width, int Height, byte[] Pixels, IReadOnlyList<string> Issues);

/// <summary>
/// Composes a <see cref="TerrainGrid"/> from the imported bitmaps of a <see cref="TerrainSet"/> (docs/formats/bmp.md §3)
/// losslessly in RGB555, frame by frame. This is the viewer's reference implementation of the terrain rules that are
/// known; the parts that are not are isolated and named:
/// <list type="bullet">
/// <item>Base tile <c>(x mod cols, y mod rows)</c> of the <c>cols × rows</c> atlas: the base textures are seamless
/// (checked visually), but whether the engine wraps them like this or stores a tile per cell is ⚠ UNK-10.</item>
/// <item>Transition shape row <c>mask − 1</c>, mask bits TL = 1, TR = 2, BR = 4, BL = 8 set where the corner is the
/// lower terrain <c>A</c>; magenta key = <c>A</c> shows through, green key = <c>B</c> (⚠ HYPOTHESIS, bmp.md §3.2).</item>
/// <item>Variant column chosen by <see cref="VariantOf"/> (⚠ UNK-10: hash or stored per cell is unknown).</item>
/// <item>Water (<c>00.BMP</c>, 5 frames) and shores (<c>F</c> frames) both advance one frame per tick of
/// <c>WATERFPS</c>; how the original synchronises 5 and 4 frames is open (bmp.md §3.3), so the cycle here is their
/// least common multiple.</item>
/// <item>Tiles touching three or more terrains, pairs without a transition file, and the cliff pseudo-terrains 8/9
/// (bmp.md §3.4) are not drawn by rule: the lowest terrain's base is drawn and the tile is listed in
/// <see cref="TerrainImage.Issues"/>.</item>
/// </list>
/// </summary>
public sealed class TerrainComposer
{
    /// <summary>Tile edge in pixels.</summary>
    public const int TileSize = 32;

    /// <summary>Magenta key of transition tiles: terrain A shows through.</summary>
    public const int KeyMagenta = 0x7C1F;

    /// <summary>Green key of transition tiles: terrain B shows through.</summary>
    public const int KeyGreen = 0x03E0;

    /// <summary>Terrain index of water.</summary>
    public const int Water = 0;

    private readonly TerrainSet _set;
    private readonly Func<string, ImageAsset?> _load;
    private readonly Dictionary<string, ImageAsset?> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<int, BaseSource?> _bases = [];

    /// <summary>Creates a composer that reads bitmaps through <paramref name="load"/> (package path → image, null when missing).</summary>
    public TerrainComposer(TerrainSet set, Func<string, ImageAsset?> load)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(load);
        _set = set;
        _load = load;
    }

    /// <summary>The variant column (0 or 1) of transition tile (<paramref name="x"/>, <paramref name="y"/>) — ⚠ UNK-10 placeholder: a fixed hash.</summary>
    public static int VariantOf(int x, int y, int variants)
    {
        if (variants <= 1)
        {
            return 0;
        }

        uint h = unchecked(((uint)x * 73856093u) ^ ((uint)y * 19349663u));
        h ^= h >> 13;
        return (int)(h % (uint)variants);
    }

    /// <summary>Transition mask of a tile for the lower terrain <paramref name="a"/> (bits TL = 1, TR = 2, BR = 4, BL = 8).</summary>
    public static int MaskOf((int Tl, int Tr, int Br, int Bl) corners, int a)
        => (corners.Tl == a ? 1 : 0) | (corners.Tr == a ? 2 : 0) | (corners.Br == a ? 4 : 0) | (corners.Bl == a ? 8 : 0);

    /// <summary>
    /// The one rule (UNK-10 assumptions above) for whether a tile is drawn as specified: its corners hold one terrain, or
    /// exactly two whose pair has a usable transition (<paramref name="hasTransition"/>, called with the lower terrain
    /// first). Tiles that fail it are drawn as the lowest terrain's base and reported. The random map generator paints
    /// only tiles that pass it (ADR 0060).
    /// </summary>
    public static bool IsDrawnByRule((int Tl, int Tr, int Br, int Bl) corners, Func<int, int, bool> hasTransition)
    {
        ArgumentNullException.ThrowIfNull(hasTransition);
        int lo = Math.Min(Math.Min(corners.Tl, corners.Tr), Math.Min(corners.Br, corners.Bl));
        int hi = Math.Max(Math.Max(corners.Tl, corners.Tr), Math.Max(corners.Br, corners.Bl));
        return lo == hi || (Distinct(corners) == 2 && hasTransition(lo, hi));
    }

    /// <summary>Whether this composer's set has a transition for (<paramref name="a"/>, <paramref name="b"/>) that it can draw.</summary>
    public bool HasUsableTransition(int a, int b)
        => _set.TransitionOf(a, b) is { IsUnresolved: false } transition && Image(transition.Asset) is not null;

    /// <summary>Animation frames of terrain <paramref name="terrain"/>'s base texture (water: 5, others 1).</summary>
    public int BaseFrames(int terrain)
    {
        TerrainFile? file = _set.BaseOf(terrain);
        if (file?.Size is not { } size || Image(file.Asset) is not { } image)
        {
            return 1;
        }

        return terrain == Water ? Math.Max(1, image.Height / (size.Rows * TileSize)) : 1;
    }

    /// <summary>Frames in one full animation cycle of <paramref name="grid"/> (least common multiple of all animated sources used).</summary>
    public int FrameCount(TerrainGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        int n = 1;
        if (grid.Contains(Water))
        {
            n = Lcm(n, BaseFrames(Water));
        }

        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                (int tl, int tr, int br, int bl) = grid.CornersOf(x, y);
                int lo = Math.Min(Math.Min(tl, tr), Math.Min(br, bl));
                int hi = Math.Max(Math.Max(tl, tr), Math.Max(br, bl));
                if (lo != hi && _set.TransitionOf(lo, hi)?.Size is { Frames: > 1 } size)
                {
                    n = Lcm(n, size.Frames);
                }
            }
        }

        return n;
    }

    /// <summary>Composes animation frame <paramref name="frame"/> of <paramref name="grid"/>.</summary>
    public TerrainImage Compose(TerrainGrid grid, int frame) => Compose(grid, frame, 0, 0);

    /// <summary>
    /// Composes animation frame <paramref name="frame"/> of <paramref name="grid"/>, a window of a larger map whose
    /// top-left tile is (<paramref name="originX"/>, <paramref name="originY"/>): base tiles and variants follow the map
    /// coordinates, so chunks composed separately join without seams. With <paramref name="still"/> (frame 0 of the same
    /// window), only the tiles that animate are drawn again and the rest is copied: the same pixels, much less work.
    /// </summary>
    public TerrainImage Compose(TerrainGrid grid, int frame, int originX, int originY, TerrainImage? still = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        ArgumentOutOfRangeException.ThrowIfNegative(originX);
        ArgumentOutOfRangeException.ThrowIfNegative(originY);
        int width = grid.Width * TileSize, height = grid.Height * TileSize;
        if (still is not null && (still.Width != width || still.Height != height))
        {
            throw new ArgumentException("The still frame is of another window.", nameof(still));
        }

        byte[] pixels = still is not null ? (byte[])still.Pixels.Clone() : new byte[width * height * 2];
        var issues = new List<string>();
        Func<int, int, bool> usable = HasUsableTransition;
        for (int ty = 0; ty < grid.Height; ty++)
        {
            for (int tx = 0; tx < grid.Width; tx++)
            {
                (int tl, int tr, int br, int bl) corners = grid.CornersOf(tx, ty);
                int lo = Math.Min(Math.Min(corners.tl, corners.tr), Math.Min(corners.br, corners.bl));
                int hi = Math.Max(Math.Max(corners.tl, corners.tr), Math.Max(corners.br, corners.bl));
                bool twoTerrains = Distinct(corners) == 2;
                TerrainFile? transition = lo != hi ? _set.TransitionOf(lo, hi) : null;
                ImageAsset? overlay = transition is { IsUnresolved: false } ? Image(transition.Asset) : null;
                if (!IsDrawnByRule(corners, usable))
                {
                    issues.Add(string.Create(CultureInfo.InvariantCulture,
                        $"tile {tx},{ty}: {(twoTerrains ? $"no usable transition for pair {lo}{hi}" : "three or more terrains")} (UNK-10); drew {lo}'s base"));
                    hi = lo;
                }

                int mask = MaskOf(corners, lo);
                int shapeRow = mask - 1;
                int variants = 1, frames = 1;
                if (lo != hi && transition?.Size is { } size)
                {
                    variants = size.Columns;
                    frames = Math.Max(1, size.Frames);
                }

                if (still is not null && frames == 1 && BaseFrames(lo) == 1 && (lo == hi || BaseFrames(hi) == 1))
                {
                    continue; // nothing on this tile moves: frame 0's pixels stand
                }

                int column = (VariantOf(originX + tx, originY + ty, variants) * frames) + (frame % frames);

                // The base bitmaps of both terrains are looked up once per tile, not per pixel (a 96 x 96 map with water
                // composes tens of millions of pixels when a match starts).
                BaseSource? low = SourceOf(lo), high = SourceOf(hi);
                for (int py = 0; py < TileSize; py++)
                {
                    for (int px = 0; px < TileSize; px++)
                    {
                        int word;
                        if (lo == hi)
                        {
                            word = BasePixel(low, originX + tx, originY + ty, px, py, frame);
                        }
                        else
                        {
                            int ox = (column * TileSize) + px, oy = (shapeRow * TileSize) + py;
                            int t = overlay is not null && ox < overlay.Width && oy < overlay.Height
                                ? overlay.Pixels[2 * ((oy * overlay.Width) + ox)] | (overlay.Pixels[(2 * ((oy * overlay.Width) + ox)) + 1] << 8)
                                : KeyGreen;
                            if ((t & 0x8000) != 0)
                            {
                                word = t & 0x7FFF;
                            }
                            else
                            {
                                word = BasePixel((t & 0x7FFF) == KeyMagenta ? low : high, originX + tx, originY + ty, px, py, frame);
                            }
                        }

                        int o = 2 * ((((ty * TileSize) + py) * width) + (tx * TileSize) + px);
                        word |= 0x8000;
                        pixels[o] = (byte)word;
                        pixels[o + 1] = (byte)(word >> 8);
                    }
                }
            }
        }

        return new TerrainImage(width, height, pixels, issues);
    }

    private static int BasePixel(BaseSource? source, int tx, int ty, int px, int py, int frame)
    {
        if (source is not { } b)
        {
            return 0;
        }

        int x = ((tx % b.Columns) * TileSize) + px;
        int y = ((frame % b.Frames) * b.Rows * TileSize) + ((ty % b.Rows) * TileSize) + py;
        if (x >= b.Image.Width || y >= b.Image.Height)
        {
            return 0;
        }

        int o = 2 * ((y * b.Image.Width) + x);
        return (b.Image.Pixels[o] | (b.Image.Pixels[o + 1] << 8)) & 0x7FFF;
    }

    // The base bitmap of a terrain with its layout and animation frames, or null when the set has none (drawn black).
    private BaseSource? SourceOf(int terrain)
    {
        if (_bases.TryGetValue(terrain, out BaseSource? known))
        {
            return known;
        }

        TerrainFile? file = _set.BaseOf(terrain);
        BaseSource? source = file?.Size is { } size && Image(file.Asset) is { } image
            ? new BaseSource(image, size.Columns, size.Rows, BaseFrames(terrain))
            : null;
        _bases[terrain] = source;
        return source;
    }

    private readonly record struct BaseSource(ImageAsset Image, int Columns, int Rows, int Frames);

    private ImageAsset? Image(string? asset)
    {
        if (asset is null)
        {
            return null;
        }

        if (!_images.TryGetValue(asset, out ImageAsset? image))
        {
            image = _load(asset);
            if (image is not null && image.Format != PixelFormat.Rgb5A1)
            {
                image = null;
            }

            _images[asset] = image;
        }

        return image;
    }

    private static int Distinct((int A, int B, int C, int D) c)
    {
        int n = 1;
        if (c.B != c.A)
        {
            n++;
        }

        if (c.C != c.A && c.C != c.B)
        {
            n++;
        }

        if (c.D != c.A && c.D != c.B && c.D != c.C)
        {
            n++;
        }

        return n;
    }

    private static int Lcm(int a, int b)
    {
        int x = a, y = b;
        while (y != 0)
        {
            (x, y) = (y, x % y);
        }

        return a / x * b;
    }
}
