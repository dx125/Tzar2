using System;
using System.Collections.Generic;
using Tzar2.Formats.Bmp;

namespace Tzar2.Formats.Fnt;

/// <summary>
/// A <c>.FNT</c> bitmap font (docs/formats/fnt.md): face name, cell height, metrics, per-code advance widths and
/// glyph indices, and an embedded 8-bit BMP holding the glyph strip (one glyph of <see cref="CellWidth"/> ×
/// <see cref="CellHeight"/> per row band, top to bottom). Pixel values are coverage levels (0/1, 0–7) or, for
/// <c>FIXSHAD</c>, palette indices.
/// </summary>
public sealed class FntFont
{
    /// <summary>Offset of the embedded BMP.</summary>
    public const int BitmapOffset = 0x232;

    private const string Format = "FNT";

    private FntFont(string face, int cellHeight, ushort[] metrics, int declaredGlyphCount, byte[] widths, byte[] charmap, BmpImage strip)
    {
        Face = face;
        CellHeight = cellHeight;
        Metrics = metrics;
        DeclaredGlyphCount = declaredGlyphCount;
        Widths = widths;
        Charmap = charmap;
        Strip = strip;
    }

    /// <summary>Face name (ASCII).</summary>
    public string Face { get; }

    /// <summary>Height of every glyph cell.</summary>
    public int CellHeight { get; }

    /// <summary>The seven partly understood metric words at 0x22–0x2F.</summary>
    public IReadOnlyList<ushort> Metrics { get; }

    /// <summary>Glyph count written by the tool (may be off by one; <see cref="GlyphCount"/> is authoritative).</summary>
    public int DeclaredGlyphCount { get; }

    /// <summary>Advance width per character code 0–255 (0 = no glyph).</summary>
    public IReadOnlyList<byte> Widths { get; }

    /// <summary>Character code → glyph index.</summary>
    public IReadOnlyList<byte> Charmap { get; }

    /// <summary>The embedded glyph strip (8 bpp, top-down).</summary>
    public BmpImage Strip { get; }

    /// <summary>Width of the strip = maximum glyph width.</summary>
    public int CellWidth => Strip.Width;

    /// <summary>Number of glyphs in the strip (strip height / cell height).</summary>
    public int GlyphCount => Strip.Height / CellHeight;

    /// <summary>Parses a font file.</summary>
    /// <exception cref="LegacyFormatException">The data is malformed.</exception>
    public static FntFont Parse(ReadOnlySpan<byte> data)
    {
        ByteReader.Require(data, 0, BitmapOffset, Format, "header");
        ReadOnlySpan<byte> name = data[..32];
        int nul = name.IndexOf((byte)0);
        if (nul >= 0)
        {
            name = name[..nul];
        }

        char[] faceChars = new char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            faceChars[i] = (char)name[i];
        }

        int cellHeight = ByteReader.U16(data, 0x20, Format, "cell height");
        ushort[] metrics = new ushort[7];
        for (int i = 0; i < metrics.Length; i++)
        {
            metrics[i] = ByteReader.U16(data, 0x22 + (2 * i), Format, "metrics");
        }

        int declared = ByteReader.U16(data, 0x30, Format, "glyph count");
        byte[] widths = data.Slice(0x32, 256).ToArray();
        byte[] charmap = data.Slice(0x132, 256).ToArray();
        if (cellHeight == 0)
        {
            throw new LegacyFormatException(Format, "cell height is 0.");
        }

        BmpImage strip;
        try
        {
            strip = BmpImage.Parse(data[BitmapOffset..]);
        }
        catch (LegacyFormatException ex)
        {
            throw new LegacyFormatException(Format, "embedded glyph bitmap: " + ex.Message);
        }

        if (strip.BitsPerPixel != 8)
        {
            throw new LegacyFormatException(Format, $"glyph bitmap has {strip.BitsPerPixel} bpp (expected 8).");
        }

        if (strip.Height % cellHeight != 0)
        {
            throw new LegacyFormatException(Format, "strip height is not a multiple of the cell height.");
        }

        int glyphs = strip.Height / cellHeight;
        foreach (byte g in charmap)
        {
            if (g >= glyphs)
            {
                throw new LegacyFormatException(Format, "charmap points past the glyph strip.");
            }
        }

        return new FntFont(new string(faceChars), cellHeight, metrics, declared, widths, charmap, strip);
    }

    /// <summary>Coverage value of pixel (x, y) of glyph <paramref name="glyph"/>.</summary>
    public byte GlyphPixel(int glyph, int x, int y) => Strip.IndexAt(x, (glyph * CellHeight) + y);
}
