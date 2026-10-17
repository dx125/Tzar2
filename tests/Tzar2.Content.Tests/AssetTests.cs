using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FsCheck.Xunit;
using Tzar2.Content.Assets;
using Xunit;

namespace Tzar2.Content.Tests;

public class SpriteSheetTests
{
    internal static SpriteSheet Indexed()
    {
        var palette = Enumerable.Range(0, 256).Select(i => new Rgba32((byte)i, 0, 0, i == 7 ? (byte)0 : (byte)255)).ToArray();
        return new SpriteSheet
        {
            Format = PixelFormat.Indexed8,
            SourceKind = 1,
            Directions = 1,
            FramesPerDirection = 2,
            CellWidth = 4,
            CellHeight = 3,
            Palette = palette,
            PlayerFirst = 240,
            PlayerCount = 16,
            ShadowIndices = [3],
            // Cell 0: 2×2 box at (1,1) = [3, 7 / 241, 9]; cell 1 empty.
            Cells = [new SpriteCell(1, 1, 2, 2, 0), new SpriteCell(0, 0, 0, 0, 4)],
            PixelData = [3, 7, 241, 9],
        };
    }

    [Fact]
    public void Round_trips()
    {
        SpriteSheet sheet = Indexed();
        byte[] bytes = sheet.ToBytes();
        SpriteSheet parsed = SpriteSheet.Parse(bytes);
        Assert.Equal(bytes, parsed.ToBytes());
        Assert.Equal([3], parsed.ShadowIndices);
        Assert.Equal(240, parsed.PlayerFirst);
        Assert.Equal(SpriteSheet.None, parsed.ShadowColour);
        Assert.True(parsed.Cells[1].IsEmpty);
        Assert.Equal(4, parsed.TrimmedPixelCount);
    }

    [Fact]
    public void Compositor_applies_palette_player_ramp_and_shadow()
    {
        SpriteSheet sheet = Indexed();
        var ramp = Enumerable.Range(0, 16).Select(i => new Rgba32(0, 0, (byte)(i * 10), 255)).ToArray();
        byte[] rgba = SpriteCompositor.SheetToRgba(sheet, ramp, markShadow: true);
        Assert.Equal(4 * 6 * 4, rgba.Length);
        Rgba32 At(int x, int y) => new(rgba[((y * 4) + x) * 4], rgba[(((y * 4) + x) * 4) + 1], rgba[(((y * 4) + x) * 4) + 2], rgba[(((y * 4) + x) * 4) + 3]);
        Assert.Equal(Rgba32.Transparent, At(0, 0));
        Assert.Equal(Rgba32.ShadowDebug, At(1, 1));
        Assert.Equal(0, At(2, 1).A);
        Assert.Equal(new Rgba32(0, 0, 10, 255), At(1, 2));
        Assert.Equal(new Rgba32(9, 0, 0, 255), At(2, 2));
        Assert.Equal(Rgba32.Transparent, At(1, 4));

        byte[] plain = SpriteCompositor.SheetToRgba(sheet, null, markShadow: false);
        Assert.Equal(new byte[] { 3, 0, 0, 255 }, plain.AsSpan(((1 * 4) + 1) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 241, 0, 0, 255 }, plain.AsSpan(((2 * 4) + 1) * 4, 4).ToArray());
    }

    [Fact]
    public void Rgb5A1_keeps_the_original_value()
    {
        var sheet = new SpriteSheet
        {
            Format = PixelFormat.Rgb5A1,
            Directions = 1,
            FramesPerDirection = 1,
            CellWidth = 2,
            CellHeight = 1,
            ShadowColour = 0x0020,
            Cells = [new SpriteCell(0, 0, 2, 1, 0)],
            PixelData = [0x20, 0x80, 0x1F, 0xFC],
        };
        byte[] rgba = SpriteCompositor.SheetToRgba(SpriteSheet.Parse(sheet.ToBytes()), null, markShadow: true);
        Assert.Equal(new byte[] { 0, 0, 0, 128, 255, 0, 255, 255 }, rgba);
    }

    [Fact]
    public void Rejects_inconsistent_sheets()
    {
        byte[] bytes = Indexed().ToBytes();
        Assert.Throws<InvalidDataException>(() => SpriteSheet.Parse(bytes[..^1]));
        byte[] badBox = (byte[])bytes.Clone();
        int cellTable = 64 + 1024;
        badBox[cellTable + 4] = 9; // width 9 > cell width 4
        Assert.Throws<InvalidDataException>(() => SpriteSheet.Parse(badBox));
        Assert.Throws<InvalidDataException>(() => SpriteSheet.Parse("T2SPxx"u8));
    }

    [Property(MaxTest = 300)]
    public void Corrupt_sprite_bytes_only_throw_invalid_data(int seed)
    {
        var random = new Random(seed);
        byte[] data = Indexed().ToBytes();
        for (int i = 0; i < 1 + random.Next(6); i++)
        {
            data[random.Next(data.Length)] = (byte)random.Next(256);
        }

        try
        {
            SpriteSheet.Parse(data[..random.Next(data.Length + 1)]);
        }
        catch (InvalidDataException)
        {
        }
    }
}

public class ImageAssetTests
{
    [Fact]
    public void Round_trips_and_expands()
    {
        var image = new ImageAsset { Format = PixelFormat.Rgb5A1, Width = 2, Height = 1, Pixels = [0xE0, 0x03, 0x00, 0xFC] };
        ImageAsset parsed = ImageAsset.Parse(image.ToBytes());
        Assert.Equal(image.Pixels, parsed.Pixels);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 255, 0, 0, 255 }, parsed.ToRgba());
        Assert.Throws<InvalidDataException>(() => ImageAsset.Parse(image.ToBytes()[..^1]));
        Assert.Throws<InvalidDataException>(() => new ImageAsset { Format = PixelFormat.Indexed8, Width = 1, Height = 1, Pixels = [0] }.ToBytes());
    }
}

public class AtlasPackerTests
{
    [Property(MaxTest = 100)]
    public void Placements_are_inside_pages_and_do_not_overlap(int seed)
    {
        var random = new Random(seed);
        var sizes = Enumerable.Range(0, 1 + random.Next(150)).Select(_ => (random.Next(0, 90), random.Next(0, 90))).ToList();
        AtlasPackResult result = AtlasPacker.Pack(sizes, 256, padding: 1);
        var placed = new List<(int Layer, int X, int Y, int W, int H)>();
        for (int i = 0; i < sizes.Count; i++)
        {
            (int w, int h) = sizes[i];
            AtlasPlacement p = result.Placements[i];
            if (w == 0 || h == 0)
            {
                Assert.Equal(-1, p.Layer);
                continue;
            }

            Assert.InRange(p.Layer, 0, result.Layers - 1);
            Assert.True(p.X >= 0 && p.Y >= 0 && p.X + w <= 256 && p.Y + h <= 256);
            foreach ((int layer, int x, int y, int ow, int oh) in placed)
            {
                bool overlap = layer == p.Layer && p.X < x + ow && x < p.X + w && p.Y < y + oh && y < p.Y + h;
                Assert.False(overlap);
            }

            placed.Add((p.Layer, p.X, p.Y, w, h));
        }

        Assert.Equal(result, AtlasPacker.Pack(sizes, 256, padding: 1) with { Placements = result.Placements });
    }

    [Fact]
    public void Oversized_rectangles_are_rejected()
        => Assert.Throws<ArgumentException>(() => AtlasPacker.Pack([(300, 10)], 256));
}
