using System;
using System.Collections.Generic;
using Tzar2.Formats.Rle;
using Tzar2.Tests.Shared;
using Xunit;

namespace Tzar2.Formats.Tests;

public class RleSheetTests
{
    // A diamond-ish shape: opaque where x + y is in a band, different per cell.
    private static int? Shape(int cell, int x, int y) => (x + y + cell) % 5 < 2 && x > cell ? (x * 7) + y + cell : null;

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void Decodes_every_pixel_of_every_kind(int kind)
    {
        Func<int, int, int, int?> pixel = kind == 3
            ? (c, x, y) => Shape(c, x, y) is { } v ? v & 0x7FFF : null
            : (c, x, y) => Shape(c, x, y) is { } v ? v & 0xFF : null;
        byte[] file = SyntheticFormats.Rle(kind, 2, 3, 20, 7, pixel);
        RleSheet sheet = RleSheet.Parse(file);

        Assert.Equal((RleKind)kind, sheet.Kind);
        Assert.Equal(2, sheet.Directions);
        Assert.Equal(3, sheet.FramesPerDirection);
        Assert.Equal(6, sheet.CellCount);
        Assert.Equal(7, sheet.CellHeight);
        Assert.Equal(0, sheet.ExtraRows);
        Assert.Equal(kind == 1 ? 256 : 0, sheet.Palette.Count);
        for (int cell = 0; cell < sheet.CellCount; cell++)
        {
            int?[,] decoded = Decode(sheet, cell);
            for (int y = 0; y < 7; y++)
            {
                for (int x = 0; x < 20; x++)
                {
                    int? expected = pixel(cell, x, y);
                    Assert.Equal(expected is null ? null : kind == 7 ? 1 : expected, decoded[x, y]);
                }
            }
        }
    }

    [Fact]
    public void Splits_long_runs_and_gaps_and_accepts_rows_without_padding()
    {
        // 600 px wide: gaps and runs longer than 255 must be split by the encoder and joined by the decoder.
        int? Pixel(int c, int x, int y) => x is >= 300 and < 590 ? (x + y) & 0xFF : null;
        foreach (bool pad in new[] { true, false })
        {
            RleSheet sheet = RleSheet.Parse(SyntheticFormats.Rle(1, 1, 1, 600, 3, Pixel, padRows: pad));
            int?[,] decoded = Decode(sheet, 0);
            for (int x = 0; x < 600; x++)
            {
                Assert.Equal(Pixel(0, x, 1), decoded[x, 1]);
            }

            Assert.Equal(new CellBounds(300, 0, 589, 2), sheet.GetCellBounds(0));
        }
    }

    [Fact]
    public void Tolerates_surplus_rows_and_empty_cells()
    {
        RleSheet sheet = RleSheet.Parse(SyntheticFormats.Rle(3, 1, 3, 10, 4, (c, x, y) => c == 1 ? null : 0x1234, extraRows: 1));
        Assert.Equal(13, sheet.Height);
        Assert.Equal(4, sheet.CellHeight);
        Assert.Equal(1, sheet.ExtraRows);
        Assert.Null(sheet.GetCellBounds(1));
        Assert.Equal(new CellBounds(0, 0, 9, 3), sheet.GetCellBounds(2));
        Assert.Equal(80, sheet.OpaquePixelCount);
    }

    [Fact]
    public void Short_palettes_are_kept()
    {
        var palette = new (byte, byte, byte)[120];
        palette[5] = (1, 2, 3);
        RleSheet sheet = RleSheet.Parse(SyntheticFormats.Rle(1, 1, 1, 4, 4, (c, x, y) => 5, palette, key: 200));
        Assert.Equal(120, sheet.Palette.Count);
        Assert.Equal(new Rgb24(1, 2, 3), sheet.Palette[5]);
        Assert.Equal(200u, sheet.KeyColour);
    }

    public static TheoryData<string, Func<byte[], byte[]>> Corruptions() => new()
    {
        { "bad magic", f => { f[0] = (byte)'x'; return f; } },
        { "unknown kind", f => { f[0x18] = 2; return f; } },
        { "truncated", f => f[..^1] },
        { "trailing byte", f => [.. f, 0] },
        { "zero directions", f => { Array.Clear(f, 4, 4); return f; } },
        { "too many cells", f => { f[8] = 200; return f; } },
        { "row offset out of order", f => { f[0x26 + (4 * 256)] = 0xFF; return f; } },
        { "short header", f => f[..20] },
    };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void Malformed_files_throw_the_typed_exception(string name, Func<byte[], byte[]> corrupt)
    {
        byte[] file = SyntheticFormats.Rle(1, 1, 2, 8, 4, (c, x, y) => x > 2 ? x : null);
        var ex = Assert.Throws<LegacyFormatException>(() => RleSheet.Parse(corrupt(file)));
        Assert.Equal("RLE", ex.Format);
        Assert.False(string.IsNullOrEmpty(name));
    }

    [Theory]
    [InlineData(5000, 4)]       // wider than MaxWidth
    [InlineData(4096, 20000)]   // area above MaxPixels
    [InlineData(20000, 20000)]  // the review's 3 MB "bounding-box bomb"
    public void Oversized_geometry_is_rejected_before_anything_is_allocated(int width, int height)
    {
        byte[] header = new byte[RleSheet.HeaderSize];
        " elr"u8.CopyTo(header);
        "RLE\0"u8.CopyTo(header.AsSpan(0x10));
        header[4] = 1;
        header[8] = 1;
        BitConverter.TryWriteBytes(header.AsSpan(0x14), (ushort)width);
        BitConverter.TryWriteBytes(header.AsSpan(0x16), (ushort)height);
        header[0x18] = 7;
        var ex = Assert.Throws<LegacyFormatException>(() => RleSheet.Parse(header));
        Assert.Contains("exceeds the supported size", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Palette_in_a_direct_colour_sheet_is_rejected()
    {
        byte[] file = SyntheticFormats.Rle(3, 1, 1, 4, 4, (c, x, y) => 1, palette: [(1, 2, 3)]);
        Assert.Throws<LegacyFormatException>(() => RleSheet.Parse(file));
    }

    [Fact]
    public void A_run_past_the_width_is_rejected()
    {
        byte[] file = SyntheticFormats.Rle(7, 1, 1, 4, 1, (c, x, y) => 1);
        // The single row is "0, 4, 0, 0"; make the run 5 long.
        int rowData = 0x26 + 4;
        Assert.Equal(4, file[rowData + 1]);
        file[rowData + 1] = 5;
        Assert.Throws<LegacyFormatException>(() => RleSheet.Parse(file));
    }

    internal static int?[,] Decode(RleSheet sheet, int cell)
    {
        var result = new int?[sheet.Width, sheet.CellHeight];
        for (int y = 0; y < sheet.CellHeight; y++)
        {
            foreach (RleRun run in sheet.CellRowRuns(cell, y))
            {
                for (int i = 0; i < run.Count; i++)
                {
                    result[run.X + i, y] = sheet.Kind switch
                    {
                        RleKind.Indexed => sheet.IndexAt(run, i),
                        RleKind.Rgb555 => sheet.Rgb555At(run, i),
                        _ => 1,
                    };
                }
            }
        }

        return result;
    }

    internal static IEnumerable<int> Cells(RleSheet sheet)
    {
        for (int i = 0; i < sheet.CellCount; i++)
        {
            yield return i;
        }
    }
}
