using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Tzar2.Formats.Rle;

/// <summary>Pixel kind of an <c>.RLE</c> sheet (docs/formats/rle.md §2).</summary>
public enum RleKind : ushort
{
    /// <summary>8-bit palette indices with a per-file palette.</summary>
    Indexed = 1,

    /// <summary>Direct RGB555 colour.</summary>
    Rgb555 = 3,

    /// <summary>Shadow mask: runs without payload mark shadowed pixels.</summary>
    Shadow = 7,
}

/// <summary>One opaque run of a row.</summary>
/// <param name="X">First pixel column.</param>
/// <param name="Count">Number of pixels (≥ 1).</param>
/// <param name="DataOffset">Offset of the run's payload in <see cref="RleSheet.Data"/> (meaningless for shadow sheets).</param>
public readonly record struct RleRun(int X, int Count, int DataOffset);

/// <summary>Inclusive bounding box of the opaque pixels of a cell, in cell coordinates.</summary>
public readonly record struct CellBounds(int X0, int Y0, int X1, int Y1)
{
    /// <summary>Width in pixels.</summary>
    public int Width => X1 - X0 + 1;

    /// <summary>Height in pixels.</summary>
    public int Height => Y1 - Y0 + 1;
}

/// <summary>
/// A decoded <c>.RLE</c> sprite sheet (docs/formats/rle.md). The sheet is one column of
/// <see cref="Directions"/> × <see cref="FramesPerDirection"/> cells, stored direction-major
/// (cell = dir × frames + frame). Rows are kept as runs over the original bytes, so nothing is expanded until asked.
/// </summary>
public sealed class RleSheet
{
    /// <summary>Size of the fixed header before the palette.</summary>
    public const int HeaderSize = 0x26;

    private const string Format = "RLE";
    private static ReadOnlySpan<byte> Magic => " elr"u8;
    private static ReadOnlySpan<byte> Magic2 => "RLE\0"u8;

    private readonly RleRun[] _runs;
    private readonly int[] _rowStart;

    private RleSheet(byte[] data, int directions, int frames, int width, int height, RleKind kind, uint keyColour,
        Rgb24[] palette, RleRun[] runs, int[] rowStart)
    {
        Data = data;
        Directions = directions;
        FramesPerDirection = frames;
        Width = width;
        Height = height;
        Kind = kind;
        KeyColour = keyColour;
        Palette = palette;
        _runs = runs;
        _rowStart = rowStart;
        CellCount = directions * frames;
        CellHeight = height / CellCount;
        ExtraRows = height - (CellHeight * CellCount);
    }

    /// <summary>The file bytes the runs point into.</summary>
    public byte[] Data { get; }

    /// <summary>Number of directions (<c>cols</c> in IMAGES.INI).</summary>
    public int Directions { get; }

    /// <summary>Frames per direction (<c>rows</c> in IMAGES.INI).</summary>
    public int FramesPerDirection { get; }

    /// <summary>Sheet width = cell width.</summary>
    public int Width { get; }

    /// <summary>Sheet height in rows.</summary>
    public int Height { get; }

    /// <summary>Pixel kind.</summary>
    public RleKind Kind { get; }

    /// <summary>The transparent colour of the source bitmap (index for kind 1, RGB555 otherwise); informational.</summary>
    public uint KeyColour { get; }

    /// <summary>The palette (kind 1 only; may have fewer than 256 entries).</summary>
    public IReadOnlyList<Rgb24> Palette { get; }

    /// <summary><see cref="Directions"/> × <see cref="FramesPerDirection"/>.</summary>
    public int CellCount { get; }

    /// <summary>Height of one cell: <see cref="Height"/> / <see cref="CellCount"/> (integer division).</summary>
    public int CellHeight { get; }

    /// <summary>Surplus rows at the bottom that belong to no cell (1 in <c>ANIMS/BRIBE.RLE</c>); ignored.</summary>
    public int ExtraRows { get; }

    /// <summary>Bytes per pixel of the run payload: 1, 2 or 0.</summary>
    public int BytesPerPixel => Kind switch
    {
        RleKind.Indexed => 1,
        RleKind.Rgb555 => 2,
        _ => 0,
    };

    /// <summary>Total number of opaque pixels.</summary>
    public long OpaquePixelCount
    {
        get
        {
            long total = 0;
            foreach (RleRun run in _runs)
            {
                total += run.Count;
            }

            return total;
        }
    }

    /// <summary>Parses an <c>.RLE</c> file.</summary>
    /// <exception cref="LegacyFormatException">The data is not a well-formed RLE sheet.</exception>
    public static RleSheet Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ReadOnlySpan<byte> d = data;
        if (d.Length < HeaderSize || !ByteReader.Matches(d, 0, Magic) || !ByteReader.Matches(d, 0x10, Magic2))
        {
            throw new LegacyFormatException(Format, "bad magic or header shorter than 38 bytes.");
        }

        uint dirs = ByteReader.U32(d, 4, Format, "dirs");
        uint frames = ByteReader.U32(d, 8, Format, "frames");
        int width = ByteReader.U16(d, 0x14, Format, "width");
        int height = ByteReader.U16(d, 0x16, Format, "height");
        ushort kindValue = ByteReader.U16(d, 0x18, Format, "kind");
        uint dataSize = ByteReader.U32(d, 0x1A, Format, "dataSize");
        uint paletteCount = ByteReader.U32(d, 0x1E, Format, "paletteCount");
        uint key = ByteReader.U32(d, 0x22, Format, "keyColour");

        if (kindValue is not (1 or 3 or 7))
        {
            throw new LegacyFormatException(Format, $"unknown kind {kindValue}.");
        }

        var kind = (RleKind)kindValue;
        if (kind != RleKind.Indexed && paletteCount != 0)
        {
            throw new LegacyFormatException(Format, "palette in a non-indexed sheet.");
        }

        if (paletteCount > 256)
        {
            throw new LegacyFormatException(Format, $"palette has {paletteCount} entries (max 256).");
        }

        if (width == 0 || height == 0)
        {
            throw new LegacyFormatException(Format, "empty sheet.");
        }

        long cells = (long)dirs * frames;
        if (cells == 0 || cells > height)
        {
            throw new LegacyFormatException(Format, $"{dirs} directions × {frames} frames do not fit {height} rows.");
        }

        ByteReader.Require(d, HeaderSize, 4L * paletteCount, Format, "palette");
        var palette = new Rgb24[paletteCount];
        for (int i = 0; i < palette.Length; i++)
        {
            int p = HeaderSize + (4 * i);
            palette[i] = new Rgb24(d[p + 2], d[p + 1], d[p]);
        }

        long table = HeaderSize + (4L * paletteCount);
        if (table + dataSize != d.Length)
        {
            throw new LegacyFormatException(Format, $"row table at {table} + data size {dataSize} != file size {d.Length}.");
        }

        ByteReader.Require(d, table, 4L * height, Format, "row table");
        int bpp = kind switch
        {
            RleKind.Indexed => 1,
            RleKind.Rgb555 => 2,
            _ => 0,
        };

        var runs = new List<RleRun>(height * 2);
        int[] rowStart = new int[height + 1];
        int tableStart = (int)table;
        long firstData = tableStart + (4L * height);
        for (int y = 0; y < height; y++)
        {
            long p = tableStart + (long)BinaryPrimitives.ReadUInt32LittleEndian(d[(tableStart + (4 * y))..]);
            long e = y + 1 < height
                ? tableStart + (long)BinaryPrimitives.ReadUInt32LittleEndian(d[(tableStart + (4 * (y + 1)))..])
                : tableStart + (long)dataSize;
            if (p < firstData || p > e || e > d.Length)
            {
                throw new LegacyFormatException(Format, $"bad row offset at row {y}.");
            }

            rowStart[y] = runs.Count;
            int x = 0;
            while (p < e)
            {
                x += d[(int)p];
                p++;
                if (p >= e)
                {
                    break;
                }

                int n = d[(int)p];
                p++;
                if (n == 0)
                {
                    continue;
                }

                if (x + n > width)
                {
                    throw new LegacyFormatException(Format, $"row {y} runs past the sheet width {width}.");
                }

                if (p + ((long)n * bpp) > e)
                {
                    throw new LegacyFormatException(Format, $"row {y} payload runs past the row end.");
                }

                runs.Add(new RleRun(x, n, (int)p));
                p += (long)n * bpp;
                x += n;
            }

            if (x != width || p != e)
            {
                throw new LegacyFormatException(Format, $"row {y} does not fill the width ({x}/{width}).");
            }
        }

        rowStart[height] = runs.Count;
        return new RleSheet(data, (int)dirs, (int)frames, width, height, kind, key, palette, runs.ToArray(), rowStart);
    }

    /// <summary>The runs of sheet row <paramref name="y"/>, left to right.</summary>
    public ReadOnlySpan<RleRun> RowRuns(int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return _runs.AsSpan(_rowStart[y], _rowStart[y + 1] - _rowStart[y]);
    }

    /// <summary>The runs of row <paramref name="row"/> of cell <paramref name="cell"/>.</summary>
    public ReadOnlySpan<RleRun> CellRowRuns(int cell, int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cell);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cell, CellCount);
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, CellHeight);
        return RowRuns((cell * CellHeight) + row);
    }

    /// <summary>Index of the cell of direction <paramref name="direction"/>, frame <paramref name="frame"/>.</summary>
    public int CellIndex(int direction, int frame) => (direction * FramesPerDirection) + frame;

    /// <summary>The tight bounding box of the opaque pixels of a cell, or null for an empty cell.</summary>
    public CellBounds? GetCellBounds(int cell)
    {
        int x0 = int.MaxValue, y0 = -1, x1 = -1, y1 = -1;
        for (int row = 0; row < CellHeight; row++)
        {
            ReadOnlySpan<RleRun> runs = CellRowRuns(cell, row);
            if (runs.IsEmpty)
            {
                continue;
            }

            x0 = Math.Min(x0, runs[0].X);
            x1 = Math.Max(x1, runs[^1].X + runs[^1].Count - 1);
            if (y0 < 0)
            {
                y0 = row;
            }

            y1 = row;
        }

        return x1 < 0 ? null : new CellBounds(x0, y0, x1, y1);
    }

    /// <summary>The palette index of pixel <paramref name="i"/> of an indexed run.</summary>
    public byte IndexAt(in RleRun run, int i) => Data[run.DataOffset + i];

    /// <summary>The RGB555 value of pixel <paramref name="i"/> of a direct-colour run.</summary>
    public ushort Rgb555At(in RleRun run, int i) => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(run.DataOffset + (2 * i)));
}
