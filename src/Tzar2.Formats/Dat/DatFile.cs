using System;
using System.Collections.Generic;

namespace Tzar2.Formats.Dat;

/// <summary>An inclusive rectangle <c>x0, y0, x1, y1</c> as stored in a <c>.DAT</c>.</summary>
public readonly record struct DatRect(short X0, short Y0, short X1, short Y1)
{
    /// <summary>Whether all four values are zero ("no footprint").</summary>
    public bool IsZero => X0 == 0 && Y0 == 0 && X1 == 0 && Y1 == 0;
}

/// <summary>
/// A sprite <c>.DAT</c> (docs/formats/dat.md): an optional footprint rectangle with a per-tile grid, and an optional list
/// of per-cell frame boxes stored frame-major (<c>frame × dirs + dir</c>). Everything is recomputable from the RLE and
/// IMAGES.INI; the importer uses it as a cross-check only.
/// </summary>
public sealed class DatFile
{
    private const string Format = "DAT";

    private DatFile(DatRect footprint, int gridWidth, int gridHeight, byte[] grid, DatRect[]? boxes)
    {
        Footprint = footprint;
        GridWidth = gridWidth;
        GridHeight = gridHeight;
        Grid = grid;
        FrameBoxes = boxes;
    }

    /// <summary>Footprint rectangle in sprite pixels; all zero = none.</summary>
    public DatRect Footprint { get; }

    /// <summary>Grid width in 32-px tiles.</summary>
    public int GridWidth { get; }

    /// <summary>Grid height in 32-px tiles.</summary>
    public int GridHeight { get; }

    /// <summary>Row-major tile values (0 occupied, 2 inside the rectangle but free, 1 farm-only, see dat.md §3).</summary>
    public IReadOnlyList<byte> Grid { get; }

    /// <summary>Frame boxes (frame-major), or null when the file has no box list.</summary>
    public IReadOnlyList<DatRect>? FrameBoxes { get; }

    /// <summary>Parses a <c>.DAT</c> file.</summary>
    /// <exception cref="LegacyFormatException">The data is malformed.</exception>
    public static DatFile Parse(ReadOnlySpan<byte> data)
    {
        var rect = new DatRect(
            ByteReader.I16(data, 0, Format, "footprint"),
            ByteReader.I16(data, 2, Format, "footprint"),
            ByteReader.I16(data, 4, Format, "footprint"),
            ByteReader.I16(data, 6, Format, "footprint"));
        int gw = 0, gh = 0;
        if (!rect.IsZero)
        {
            int w = rect.X1 - rect.X0 + 1;
            int h = rect.Y1 - rect.Y0 + 1;
            if (w < 0 || h < 0)
            {
                throw new LegacyFormatException(Format, "footprint rectangle has negative size.");
            }

            gw = w / 32;
            gh = h / 32;
        }

        int p = 8 + (gw * gh);
        ByteReader.Require(data, 8, gw * gh, Format, "footprint grid");
        byte[] grid = data.Slice(8, gw * gh).ToArray();
        DatRect[]? boxes = null;
        if (p < data.Length)
        {
            int n = ByteReader.U16(data, p, Format, "box count");
            if (data.Length != p + 2 + (8 * n))
            {
                throw new LegacyFormatException(Format, $"box list of {n} entries does not match the remaining {data.Length - p} bytes.");
            }

            boxes = new DatRect[n];
            for (int i = 0; i < n; i++)
            {
                int o = p + 2 + (8 * i);
                boxes[i] = new DatRect(
                    ByteReader.I16(data, o, Format, "box"),
                    ByteReader.I16(data, o + 2, Format, "box"),
                    ByteReader.I16(data, o + 4, Format, "box"),
                    ByteReader.I16(data, o + 6, Format, "box"));
            }
        }

        return new DatFile(rect, gw, gh, grid, boxes);
    }
}
