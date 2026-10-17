using System.Collections.Generic;
using FsCheck.Xunit;
using Tzar2.Content.Assets;
using Tzar2.Formats.Rle;
using Tzar2.Import.Pipeline;
using Tzar2.Tests.Shared;
using Xunit;

namespace Tzar2.Import.Tests.Pipeline;

public class SpriteConverterTests
{
    [Fact]
    public void A_used_key_index_moves_the_transparent_index()
    {
        RleSheet rle = RleSheet.Parse(SyntheticFormats.Rle(1, 1, 1, 4, 1, (c, x, y) => x == 3 ? null : x, key: 0));
        SpriteSheet sheet = SpriteConverter.Convert(rle, false, []);
        Assert.Equal(PixelFormat.Indexed8, sheet.Format);
        Assert.Equal(255, sheet.Palette[0].A);
        Assert.Equal(0, sheet.Palette[3].A);
        Assert.Equal(new byte[] { 0, 1, 2 }, sheet.PixelData);
    }

    [Fact]
    public void A_sheet_using_all_256_indices_falls_back_to_rgba8()
    {
        var warnings = new List<string>();
        RleSheet rle = RleSheet.Parse(SyntheticFormats.Rle(1, 1, 1, 256, 2, (c, x, y) => y == 0 ? x : x == 0 ? null : 7));
        SpriteSheet sheet = SpriteConverter.Convert(rle, false, warnings);
        Assert.Equal(PixelFormat.Rgba8, sheet.Format);
        Assert.Empty(sheet.Palette);
        Assert.Single(warnings);
        byte[] rgba = SpriteCompositor.SheetToRgba(sheet, null, false);
        Assert.Equal(0, rgba[(256 * 4) + 3]); // (0, 1) is transparent
        Assert.Equal(new byte[] { (byte)rle.Palette[5].R, rle.Palette[5].G, rle.Palette[5].B, 255 }, rgba[(5 * 4)..((5 * 4) + 4)]);
    }

    /// <summary>Every opaque pixel survives conversion with its exact value, for random sheets of every kind.</summary>
    [Property(MaxTest = 60)]
    public void Conversion_is_lossless(int seed, bool unit)
    {
        var random = new System.Random(seed);
        int kind = new[] { 1, 3, 7 }[random.Next(3)];
        int dirs = 1 + random.Next(3), frames = 1 + random.Next(3), width = 1 + random.Next(40), height = 1 + random.Next(20);
        var values = new Dictionary<(int, int, int), int?>();
        int? Pixel(int c, int x, int y)
        {
            if (!values.TryGetValue((c, x, y), out int? v))
            {
                v = random.Next(3) == 0 ? (kind == 3 ? random.Next(0x8000) : random.Next(256)) : null;
                values[(c, x, y)] = v;
            }

            return v;
        }

        RleSheet rle = RleSheet.Parse(SyntheticFormats.Rle(kind, dirs, frames, width, height, Pixel));
        SpriteSheet sheet = SpriteSheet.Parse(SpriteConverter.Convert(rle, unit, []).ToBytes());
        for (int cell = 0; cell < rle.CellCount; cell++)
        {
            SpriteCell box = sheet.Cells[cell];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int? expected = Pixel(cell, x, y);
                    bool inside = x >= box.X && x < box.X + box.Width && y >= box.Y && y < box.Y + box.Height;
                    if (!inside)
                    {
                        Assert.Null(expected);
                        continue;
                    }

                    int i = ((y - box.Y) * box.Width) + (x - box.X);
                    byte[] p = sheet.PixelData;
                    switch (sheet.Format)
                    {
                        case PixelFormat.Indexed8:
                            int index = p[box.Offset + i];
                            Assert.Equal(expected, sheet.Palette[index].A == 0 ? null : index);
                            break;
                        case PixelFormat.Rgb5A1:
                            int word = p[box.Offset + (2 * i)] | (p[box.Offset + (2 * i) + 1] << 8);
                            Assert.Equal(expected, (word & 0x8000) == 0 ? null : word & 0x7FFF);
                            break;
                        case PixelFormat.Rgba8:
                            Assert.Equal(expected is null ? 0 : 255, p[box.Offset + (4 * i) + 3]);
                            break;
                        default:
                            Assert.Equal(expected is null ? 0 : 1, p[box.Offset + i]);
                            break;
                    }
                }
            }
        }
    }
}
