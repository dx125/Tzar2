using System;
using System.Collections.Generic;

namespace Tzar2.Content.Assets;

/// <summary>
/// Where one sprite cell lives on the GPU and how the sprite shader must decode it (ADR 0013): page (texture-array
/// layer) and rectangle inside the page array of its <see cref="Format"/>, the player range of its sheet and the
/// RGB555 value that is baked shadow (RGB5A1 sheets).
/// </summary>
/// <param name="Layer">Array layer, −1 for an empty cell.</param>
/// <param name="X">Left in the page.</param>
/// <param name="Y">Top in the page.</param>
/// <param name="Width">Trimmed width.</param>
/// <param name="Height">Trimmed height.</param>
/// <param name="OffsetX">Left of the trimmed box inside the full cell.</param>
/// <param name="OffsetY">Top of the trimmed box inside the full cell.</param>
/// <param name="Format">Pixel format (selects the texture array).</param>
/// <param name="PaletteRow">Row of the sheet's palette in <see cref="SpriteAtlas.PaletteTexture"/>, −1 when not indexed.</param>
/// <param name="PlayerFirst">First player-colour palette index, −1 for none.</param>
/// <param name="ShadowColour">RGB555 baked-shadow value of an RGB5A1 sheet, −1 for none.</param>
public readonly record struct AtlasCell(
    int Layer, int X, int Y, int Width, int Height, int OffsetX, int OffsetY,
    PixelFormat Format, int PaletteRow, int PlayerFirst, int ShadowColour)
{
    /// <summary>Whether the cell has no pixels.</summary>
    public bool IsEmpty => Layer < 0;
}

/// <summary>
/// Packs the trimmed cells of the resident sprite sheets into square pages, one page list per GPU pixel format (ADR
/// 0013: R8 indices, RG8 RGB5A1 words, RGBA8, R8 shadow masks; 2,048² by default), with
/// <see cref="AtlasPacker"/> (deterministic). Indexed sheets get one row each in a 256-wide RGBA8 palette texture whose
/// alpha encodes the entry class: <see cref="PaletteTransparent"/>, <see cref="PaletteOpaque"/> or
/// <see cref="PaletteShadow"/> (the importer's baked-shadow hint, rle.md §5.4). The sprite shader recolours the player
/// range, so the atlas itself is player-independent and shared by all players.
/// </summary>
public sealed class SpriteAtlas
{
    /// <summary>Page size used by the client (ADR 0013).</summary>
    public const int DefaultPageSize = 2048;

    /// <summary>Palette-texture alpha of a transparent index.</summary>
    public const byte PaletteTransparent = 0;

    /// <summary>Palette-texture alpha of a baked-shadow index (rgb keeps the original colour).</summary>
    public const byte PaletteShadow = 128;

    /// <summary>Palette-texture alpha of an ordinary opaque index.</summary>
    public const byte PaletteOpaque = 255;

    private static readonly PixelFormat[] Formats = [PixelFormat.Indexed8, PixelFormat.Rgb5A1, PixelFormat.Rgba8, PixelFormat.R8];

    private readonly AtlasCell[][] _cells;
    private readonly Dictionary<PixelFormat, List<byte[]>> _pages;

    private SpriteAtlas(int pageSize, AtlasCell[][] cells, Dictionary<PixelFormat, List<byte[]>> pages, byte[] palettes, int paletteRows)
    {
        PageSize = pageSize;
        _cells = cells;
        _pages = pages;
        PaletteTexture = palettes;
        PaletteRows = paletteRows;
    }

    /// <summary>Page width and height in pixels.</summary>
    public int PageSize { get; }

    /// <summary>Number of sheets packed.</summary>
    public int SheetCount => _cells.Length;

    /// <summary>256 × <see cref="PaletteRows"/> RGBA8 pixels (at least one row).</summary>
    public byte[] PaletteTexture { get; }

    /// <summary>Rows of <see cref="PaletteTexture"/>.</summary>
    public int PaletteRows { get; }

    /// <summary>Total number of cells over all sheets.</summary>
    public int CellCount
    {
        get
        {
            int n = 0;
            foreach (AtlasCell[] cells in _cells)
            {
                n += cells.Length;
            }

            return n;
        }
    }

    /// <summary>Cell <paramref name="cell"/> of sheet <paramref name="sheet"/>.</summary>
    public AtlasCell GetCell(int sheet, int cell) => _cells[sheet][cell];

    /// <summary>Cells of one sheet, direction-major.</summary>
    public IReadOnlyList<AtlasCell> CellsOf(int sheet) => _cells[sheet];

    /// <summary>
    /// The pages of <paramref name="format"/> (each <see cref="PageSize"/>² × bytes per pixel); empty when unused or
    /// after <see cref="ReleasePages"/>.
    /// </summary>
    public IReadOnlyList<byte[]> Pages(PixelFormat format)
        => _pages.TryGetValue(format, out List<byte[]>? pages) ? pages : [];

    /// <summary>Whether <see cref="ReleasePages"/> dropped the pixels.</summary>
    public bool PagesReleased { get; private set; }

    /// <summary>
    /// Drops the pages' pixels once they are on the GPU; the cells stay (code review 2026-10, item 16: a match kept a
    /// second copy of every page in managed memory).
    /// </summary>
    public void ReleasePages()
    {
        _pages.Clear();
        PagesReleased = true;
    }

    /// <summary>Packs <paramref name="sheets"/>; their order defines the sheet indices.</summary>
    /// <exception cref="ArgumentException">A trimmed cell is larger than a page.</exception>
    public static SpriteAtlas Build(IReadOnlyList<SpriteSheet> sheets, int pageSize = DefaultPageSize)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var cells = new AtlasCell[sheets.Count][];
        var pages = new Dictionary<PixelFormat, List<byte[]>>();
        int[] paletteRow = new int[sheets.Count];
        var palettes = new List<byte>();
        int rows = 0;
        for (int s = 0; s < sheets.Count; s++)
        {
            cells[s] = new AtlasCell[sheets[s].CellCount];
            paletteRow[s] = -1;
            if (sheets[s].Format == PixelFormat.Indexed8)
            {
                paletteRow[s] = rows++;
                AppendPalette(palettes, sheets[s]);
            }
        }

        if (rows == 0)
        {
            palettes.AddRange(new byte[256 * 4]);
            rows = 1;
        }

        foreach (PixelFormat format in Formats)
        {
            var sizes = new List<(int Width, int Height)>();
            var owners = new List<(int Sheet, int Cell)>();
            for (int s = 0; s < sheets.Count; s++)
            {
                SpriteSheet sheet = sheets[s];
                if (sheet.Format != format)
                {
                    continue;
                }

                for (int c = 0; c < sheet.CellCount; c++)
                {
                    SpriteCell cell = sheet.Cells[c];
                    sizes.Add(cell.IsEmpty ? (0, 0) : (cell.Width, cell.Height));
                    owners.Add((s, c));
                }
            }

            if (sizes.Count == 0)
            {
                continue;
            }

            AtlasPackResult packed = AtlasPacker.Pack(sizes, pageSize);
            int bpp = PixelFormats.BytesPerPixel(format);
            var list = new List<byte[]>();
            for (int l = 0; l < packed.Layers; l++)
            {
                list.Add(new byte[pageSize * pageSize * bpp]);
            }

            for (int i = 0; i < owners.Count; i++)
            {
                (int s, int c) = owners[i];
                SpriteSheet sheet = sheets[s];
                SpriteCell cell = sheet.Cells[c];
                AtlasPlacement at = packed.Placements[i];
                int player = sheet.PlayerFirst >= 0 && sheet.PlayerCount == PlayerPalette.RampLength ? sheet.PlayerFirst : -1;
                int shadow = format == PixelFormat.Rgb5A1 ? sheet.ShadowColour : -1;
                if (at.Layer < 0)
                {
                    cells[s][c] = new AtlasCell(-1, 0, 0, 0, 0, cell.X, cell.Y, format, paletteRow[s], player, shadow);
                    continue;
                }

                cells[s][c] = new AtlasCell(at.Layer, at.X, at.Y, cell.Width, cell.Height, cell.X, cell.Y, format, paletteRow[s], player, shadow);
                ReadOnlySpan<byte> src = sheet.CellPixels(c);
                byte[] page = list[at.Layer];
                int rowBytes = cell.Width * bpp;
                for (int y = 0; y < cell.Height; y++)
                {
                    src.Slice(y * rowBytes, rowBytes).CopyTo(page.AsSpan((((at.Y + y) * pageSize) + at.X) * bpp, rowBytes));
                }
            }

            pages[format] = list;
        }

        return new SpriteAtlas(pageSize, cells, pages, [.. palettes], rows);
    }

    private static void AppendPalette(List<byte> palettes, SpriteSheet sheet)
    {
        var shadow = new bool[256];
        foreach (int i in sheet.ShadowIndices)
        {
            shadow[i] = true;
        }

        for (int i = 0; i < 256; i++)
        {
            Rgba32 c = i < sheet.Palette.Count ? sheet.Palette[i] : Rgba32.Transparent;
            palettes.Add(c.R);
            palettes.Add(c.G);
            palettes.Add(c.B);
            palettes.Add(c.A == 0 ? PaletteTransparent : shadow[i] ? PaletteShadow : PaletteOpaque);
        }
    }
}
