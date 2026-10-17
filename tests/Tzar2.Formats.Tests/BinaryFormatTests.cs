using System;
using System.Linq;
using Tzar2.Formats.Audio;
using Tzar2.Formats.Bmp;
using Tzar2.Formats.Dat;
using Tzar2.Formats.Fnt;
using Tzar2.Formats.Hpfs;
using Tzar2.Formats.Pal;
using Tzar2.Tests.Shared;
using Xunit;

namespace Tzar2.Formats.Tests;

public class BmpImageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reads_16_bit_rgb555_top_down(bool topDown)
    {
        byte[] pixels = new byte[3 * 2 * 2];
        for (int i = 0; i < 6; i++)
        {
            ushort v = (ushort)(0x03E0 + i);
            pixels[2 * i] = (byte)v;
            pixels[(2 * i) + 1] = (byte)(v >> 8);
        }

        BmpImage bmp = BmpImage.Parse(SyntheticFormats.Bmp(3, 2, 16, pixels, topDown: topDown));
        Assert.Equal(16, bmp.BitsPerPixel);
        Assert.Equal(0x03E0, bmp.Rgb555At(0, 0));
        Assert.Equal(0x03E5, bmp.Rgb555At(2, 1));
    }

    [Fact]
    public void Reads_8_bit_with_palette_and_odd_width_padding()
    {
        byte[] pixels = Enumerable.Range(0, 5 * 3).Select(i => (byte)i).ToArray();
        var palette = Enumerable.Range(0, 16).Select(i => ((byte)i, (byte)(i * 2), (byte)(i * 3))).ToArray();
        BmpImage bmp = BmpImage.Parse(SyntheticFormats.Bmp(5, 3, 8, pixels, palette, colorsUsed: 16));
        Assert.Equal(16, bmp.Palette.Count);
        Assert.Equal(new Rgb24(3, 6, 9), bmp.Palette[3]);
        Assert.Equal(14, bmp.IndexAt(4, 2));
        Assert.Equal(5, bmp.IndexAt(0, 1));
    }

    [Fact]
    public void An_8_bit_file_without_palette_uses_bfOffBits()
    {
        BmpImage bmp = BmpImage.Parse(SyntheticFormats.Bmp(4, 2, 8, [1, 2, 3, 4, 5, 6, 7, 8], writePalette: false));
        Assert.Empty(bmp.Palette);
        Assert.Equal(8, bmp.IndexAt(3, 1));
    }

    [Fact]
    public void Reads_24_bit_as_rgb()
    {
        BmpImage bmp = BmpImage.Parse(SyntheticFormats.Bmp(1, 1, 24, [10, 20, 30]));
        Assert.Equal(new byte[] { 30, 20, 10 }, bmp.Pixels);
    }

    [Fact]
    public void Rejects_unsupported_and_truncated_files()
    {
        byte[] ok = SyntheticFormats.Bmp(2, 2, 16, new byte[8]);
        Assert.Throws<LegacyFormatException>(() => BmpImage.Parse(ok[..^1]));
        byte[] rle = (byte[])ok.Clone();
        rle[30] = 1;
        Assert.Throws<LegacyFormatException>(() => BmpImage.Parse(rle));
        byte[] fourBit = (byte[])ok.Clone();
        fourBit[28] = 4;
        Assert.Throws<LegacyFormatException>(() => BmpImage.Parse(fourBit));
        Assert.Throws<LegacyFormatException>(() => BmpImage.Parse("XX"u8));
    }
}

public class DatFileTests
{
    [Fact]
    public void Reads_footprint_grid_and_boxes()
    {
        byte[] grid = [0, 2, 0, 0, 0, 1];
        DatFile dat = DatFile.Parse(SyntheticFormats.Dat((-1, 0, 94, 63), grid, [(1, 2, 3, 4), (5, 6, 7, 8)]));
        Assert.Equal(3, dat.GridWidth);
        Assert.Equal(2, dat.GridHeight);
        Assert.Equal(grid, dat.Grid);
        Assert.Equal(2, dat.FrameBoxes!.Count);
        Assert.Equal(new DatRect(5, 6, 7, 8), dat.FrameBoxes[1]);
    }

    [Fact]
    public void Grid_size_uses_integer_division()
    {
        // 130 px high → 4 rows (the EURO OUTPOST case).
        DatFile dat = DatFile.Parse(SyntheticFormats.Dat((0, -1, 31, 128), new byte[4], null));
        Assert.Equal(4, dat.GridHeight);
        Assert.Null(dat.FrameBoxes);
    }

    [Fact]
    public void Units_have_only_boxes()
    {
        DatFile dat = DatFile.Parse(SyntheticFormats.Dat((0, 0, 0, 0), [], [(0, 0, 9, 9)]));
        Assert.True(dat.Footprint.IsZero);
        Assert.Single(dat.FrameBoxes!);
    }

    [Fact]
    public void Rejects_bad_box_list_length()
    {
        byte[] file = SyntheticFormats.Dat((0, 0, 0, 0), [], [(0, 0, 9, 9)]);
        Assert.Throws<LegacyFormatException>(() => DatFile.Parse(file[..^2]));
        Assert.Throws<LegacyFormatException>(() => DatFile.Parse(file[..5]));
    }
}

public class GamePaletteTests
{
    [Fact]
    public void Reads_entries_and_player_ramps()
    {
        GamePalette pal = GamePalette.Parse(SyntheticFormats.Pal());
        Assert.Equal(256, pal.Entries.Count);
        Assert.Equal(new Rgb24(7, 248, 3), pal.Entries[7]);
        Rgb24[] ramp = pal.PlayerRamp(1);
        Assert.Equal(pal.Entries[128], ramp[0]);
        Assert.Equal(pal.Entries[143], ramp[15]);
        Assert.Throws<ArgumentOutOfRangeException>(() => pal.PlayerRamp(8));
        Assert.Throws<LegacyFormatException>(() => GamePalette.Parse(SyntheticFormats.Pal()[..100]));
    }
}

public class FntFontTests
{
    [Fact]
    public void Reads_metrics_charmap_and_glyph_strip()
    {
        byte[] file = SyntheticFormats.Fnt("Small Fonts", 6, 9, 4, c => c is >= 65 and <= 67 ? (c - 63, c - 64) : (0, 0), declaredGlyphs: 3);
        FntFont font = FntFont.Parse(file);
        Assert.Equal("Small Fonts", font.Face);
        Assert.Equal(9, font.CellHeight);
        Assert.Equal(6, font.CellWidth);
        Assert.Equal(4, font.GlyphCount);
        Assert.Equal(3, font.DeclaredGlyphCount);
        Assert.Equal(2, font.Widths[65]);
        Assert.Equal(1, font.Charmap[65]);
        Assert.Equal(2, font.GlyphPixel(1, 0, 0));
        Assert.Equal(0, font.GlyphPixel(1, 1, 0));
    }

    [Fact]
    public void Rejects_charmap_past_the_strip_and_partial_cells()
    {
        Assert.Throws<LegacyFormatException>(() => FntFont.Parse(SyntheticFormats.Fnt("x", 4, 4, 2, c => (1, 5))));
        byte[] file = SyntheticFormats.Fnt("x", 4, 4, 2, c => (1, 0));
        file[0x20] = 3; // cell height 3 does not divide 8
        Assert.Throws<LegacyFormatException>(() => FntFont.Parse(file));
        Assert.Throws<LegacyFormatException>(() => FntFont.Parse(file[..0x100]));
    }
}

public class HpfsArchiveTests
{
    [Fact]
    public void Reads_nested_directories_large_files_and_truncated_tails()
    {
        byte[] big = Enumerable.Range(0, 130 * 512 + 17).Select(i => (byte)(i * 31)).ToArray();
        byte[] inner = SyntheticFormats.Hpfs([("MAPS/x.header", [1, 2, 3])], truncate: 100);
        byte[] image = SyntheticFormats.Hpfs(
        [
            ("MAPS/maps.ini", "[maps]\r\n"u8.ToArray()),
            ("MAPS/one.wmp", inner),
            ("USER/big.bmp", big),
            ("readme", []),
        ], truncate: 163);

        HpfsArchive archive = HpfsArchive.Open(image);
        Assert.Equal(163, archive.TruncatedBytes);
        var entries = archive.ReadAll();
        Assert.Equal(["MAPS/", "MAPS/maps.ini", "MAPS/one.wmp", "USER/", "USER/big.bmp", "readme"], entries.Select(e => e.Path));
        Assert.Equal(big, entries.Single(e => e.Path == "USER/big.bmp").Data);
        HpfsEntry nested = entries.Single(e => e.Path == "MAPS/one.wmp");
        Assert.True(nested.IsNestedArchive);
        Assert.Equal(new byte[] { 1, 2, 3 }, HpfsArchive.Open(nested.Data).ReadAll().Single(e => !e.IsDirectory).Data);
        Assert.Equal(archive.BlockCount, archive.ReferencedBlockCount);
        Assert.Equal(entries.Count, archive.ReadAll().Count);
    }

    [Fact]
    public void Rejects_shared_blocks_bad_geometry_and_bad_pointers()
    {
        byte[] image = SyntheticFormats.Hpfs([("a", [1]), ("b", [2])]);
        // Point file b's data pointer at file a's data block.
        HpfsArchive ok = HpfsArchive.Open(image);
        var entries = ok.ReadAll();
        int aInode = entries[0].Inode, bInode = entries[1].Inode;
        Array.Copy(image, (aInode * 512) + 8, image, (bInode * 512) + 8, 4);
        Assert.Throws<LegacyFormatException>(() => HpfsArchive.Open(image).ReadAll());

        byte[] geometry = SyntheticFormats.Hpfs([("a", [1])]);
        Assert.Throws<LegacyFormatException>(() => HpfsArchive.Open([.. geometry, .. new byte[600]]));

        byte[] pointer = SyntheticFormats.Hpfs([("a", [1])]);
        pointer[(2 * 512) + 8] = 200;
        Assert.Throws<LegacyFormatException>(() => HpfsArchive.Open(pointer).ReadAll());
    }
}

public class AudioTests
{
    [Fact]
    public void Wav_header_is_read()
    {
        WavInfo wav = WavInfo.Parse(SyntheticFormats.Wav(2, 22050, 16, 2205));
        Assert.Equal((1, 2, 22050, 16, 4), (wav.FormatTag, wav.Channels, wav.SampleRate, wav.BitsPerSample, wav.BlockAlign));
        Assert.Equal(2205, wav.SampleFrames);
        Assert.Equal(100, wav.DurationMilliseconds);
        Assert.False(wav.DataTruncated);
        Assert.True(WavInfo.Parse(SyntheticFormats.Wav()[..^10]).DataTruncated);
        Assert.Throws<LegacyFormatException>(() => WavInfo.Parse(SyntheticFormats.Wav()[..30]));
    }

    [Fact]
    public void Midi_header_is_read()
    {
        MidiInfo mid = MidiInfo.Parse(SyntheticFormats.Mid());
        Assert.Equal((0, 1, 1, 96), (mid.FormatType, mid.DeclaredTracks, mid.TrackChunks, mid.Division));
        Assert.Throws<LegacyFormatException>(() => MidiInfo.Parse(SyntheticFormats.Mid()[..14]));
    }
}
