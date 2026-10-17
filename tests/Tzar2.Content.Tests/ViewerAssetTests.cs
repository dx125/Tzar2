using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tzar2.Content.Assets;
using Tzar2.Content.Audio;
using Tzar2.Content.Terrain;
using Xunit;

namespace Tzar2.Content.Tests;

/// <summary>Headless logic behind the M1 asset viewer: palettes, sprite index, timing, atlas packing, shading, fonts, terrain, WAV.</summary>
public class ViewerAssetTests
{
    /// <summary>A synthetic <c>game.json</c>: entry i = (i, 255 − i, i / 2).</summary>
    internal static string GamePaletteJson()
    {
        var entries = Enumerable.Range(0, 256).Select(i => $"\"{i:x2}{255 - i:x2}{i / 2:x2}\"");
        return "{\"source\":\"x\",\"entries\":[" + string.Join(",", entries) + "],\"playerRampBase\":112,\"rampLength\":16,\"players\":8}";
    }

    [Fact]
    public void Player_palette_reads_the_eight_ramps_from_game_pal()
    {
        PlayerPalette palette = PlayerPalette.Parse(Encoding.UTF8.GetBytes(GamePaletteJson()));
        Assert.Equal(new Rgba32(112, 143, 56, 255), palette.Ramp(0, 0));
        Assert.Equal(new Rgba32(127 + 16, 255 - 143, 143 / 2, 255), palette.Ramp(1, 15));
        Assert.Equal(new Rgba32(239, 16, 119, 255), palette.Ramp(7, 15));
        byte[] texture = palette.ToRampTexture();
        Assert.Equal(16 * 8 * 4, texture.Length);
        int o = ((3 * 16) + 5) * 4;
        Assert.Equal(new byte[] { 112 + 48 + 5, 255 - 165, 165 / 2, 255 }, texture[o..(o + 4)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => palette.Ramp(8, 0));
        Assert.Throws<InvalidDataException>(() => PlayerPalette.Parse("{\"entries\":[\"000000\"]}"u8));
    }

    private const string IndexJson = """
        [{"name":"e_knight","ini":"data/images.ini","image":"x","asset":"art/unit/euro knight.t2spr","properties":{"rows":["4"],"cols":["8"],"anim_time":["750"]}},
         {"name":"e_knight_attack","ini":"data/images.ini","image":"x","asset":"art/unit/a.t2spr","properties":{"rows":["3"],"delay":["100,200,300"],"object":["45,57,32,32"]}},
         {"name":"e_barrack","ini":"data/images.ini","image":"x","asset":"art/building/b.t2spr","properties":{}},
         {"name":"e_barrack_c","ini":"data/images.ini","image":"x","asset":"art/building/mask/b c.t2img","properties":{"rows":["6"],"object":["23,3"],"fps":["4"]}},
         {"name":"e_barrack_s","ini":"data/images.ini","image":"x","asset":"art/building/b_s.t2spr","properties":{}},
         {"name":"ghost_s","ini":"data/images.ini","image":"x","asset":null,"properties":{}},
         {"name":"arrow","ini":"data/images.ini","image":"x","asset":"art/anims/arrow.t2spr","properties":{}},
         {"name":"arrow_shadow","ini":"data/images.ini","image":"x","asset":"art/anims/arrow_s.t2img","properties":{}}]
        """;

    [Fact]
    public void Sprite_index_links_shadows_and_player_colour_overlays_by_name()
    {
        SpriteIndex index = SpriteIndex.Parse(Encoding.UTF8.GetBytes(IndexJson));
        Assert.Equal(8, index.Entries.Count);
        SpriteIndexEntry barrack = index.Find("e_barrack")!;
        Assert.Equal("e_barrack_c", index.OverlayOf(barrack)!.Name);
        Assert.Equal("e_barrack_s", index.ShadowOf(barrack)!.Name);
        Assert.Equal("arrow_shadow", index.ShadowOf(index.Find("arrow")!)!.Name);
        Assert.Null(index.ShadowOf(index.Find("e_knight")!));
        Assert.True(index.IsCompanion(index.Find("e_barrack_c")!));
        Assert.True(index.IsCompanion(index.Find("arrow_shadow")!));
        Assert.False(index.IsCompanion(index.Find("ghost_s")!));
        Assert.False(index.IsCompanion(barrack));
        Assert.Equal([23, 3], index.Find("e_barrack_c")!.GetInts("object")!);
        Assert.Equal(8, index.Find("e_knight")!.GetInt("cols", 1));
        Assert.Equal(1, barrack.GetInt("cols", 1));
    }

    [Fact]
    public void Animation_timing_prefers_delay_then_anim_time_then_fps()
    {
        SpriteIndex index = SpriteIndex.Parse(Encoding.UTF8.GetBytes(IndexJson));
        SpriteAnimation attack = SpriteAnimation.For(index.Find("e_knight_attack"), 3);
        Assert.Equal("delay", attack.Source);
        Assert.Equal(600, attack.CycleMs);
        Assert.Equal([0, 0, 1, 1, 2, 2, 0], new long[] { 0, 99, 100, 299, 300, 599, 600 }.Select(attack.FrameAt));
        Assert.Equal(2, attack.FrameAt(-1));

        SpriteAnimation walk = SpriteAnimation.For(index.Find("e_knight"), 4);
        Assert.Equal("anim_time", walk.Source);
        Assert.Equal(750, walk.CycleMs);
        Assert.Equal([188, 188, 187, 187], walk.DelaysMs);

        SpriteAnimation flag = SpriteAnimation.For(index.Find("e_barrack_c"), 6);
        Assert.Equal("fps", flag.Source);
        Assert.Equal(250, flag.DelaysMs[0]);

        // A delay list whose length does not match the frame count is ignored.
        Assert.Equal("default", SpriteAnimation.For(index.Find("e_knight_attack"), 5).Source);
        Assert.Equal(SpriteAnimation.DefaultDelayMs, SpriteAnimation.For(null, 2).DelaysMs[1]);
    }

    /// <summary>A 6×4 indexed image, 3 columns × 2 rows of 2×2 cells; index 0 is the key; cell (2,1) is empty.</summary>
    internal static ImageAsset IndexedImage()
    {
        Rgba32[] palette = Enumerable.Range(0, 256).Select(i => new Rgba32((byte)i, (byte)i, (byte)i, i == 0 ? (byte)0 : (byte)255)).ToArray();
        byte[] pixels =
        [
            1, 0, 0, 2, 3, 3,
            0, 0, 0, 0, 3, 3,
            0, 0, 240, 0, 0, 0,
            0, 4, 0, 255, 0, 0,
        ];
        return new ImageAsset { Format = PixelFormat.Indexed8, Width = 6, Height = 4, Palette = palette, Pixels = pixels };
    }

    [Fact]
    public void Image_sheets_split_columns_as_directions_and_rows_as_frames_and_trim_cells()
    {
        SpriteSheet sheet = ImageSheets.FromImage(IndexedImage(), 3, 2, playerColours: true);
        Assert.Equal((3, 2, 2, 2), (sheet.Directions, sheet.FramesPerDirection, sheet.CellWidth, sheet.CellHeight));
        Assert.Equal(240, sheet.PlayerFirst);
        Assert.Equal(new SpriteCell(0, 0, 1, 1, 0), sheet.Cells[0]); // dir 0 frame 0: "1"
        Assert.Equal(new SpriteCell(1, 1, 1, 1, 1), sheet.Cells[1]); // dir 0 frame 1: "4" at (1,1)
        Assert.Equal(new SpriteCell(1, 0, 1, 1, 2), sheet.Cells[2]); // dir 1 frame 0: "2" at (1,0)
        Assert.Equal(new SpriteCell(0, 0, 2, 2, 3), sheet.Cells[3]); // dir 1 frame 1: 240 / 255 diagonal
        Assert.Equal([240, 0, 0, 255], sheet.CellPixels(3).ToArray());
        Assert.Equal(new SpriteCell(0, 0, 2, 2, 7), sheet.Cells[4]); // dir 2 frame 0: solid 3s
        Assert.True(sheet.Cells[5].IsEmpty);
        sheet.Validate();

        SpriteSheet mask = ImageSheets.FromImage(IndexedImage(), 3, 2, asShadowMask: true);
        Assert.Equal(PixelFormat.R8, mask.Format);
        Assert.Equal([1, 0, 0, 1], mask.CellPixels(3).ToArray());
        Assert.Equal(SpriteSheet.None, mask.PlayerFirst);
        Assert.Equal(SpriteSheet.None, ImageSheets.FromImage(IndexedImage(), 3, 2).PlayerFirst);
    }

    private static SpriteSheet Rgb555Sheet() => new()
    {
        Format = PixelFormat.Rgb5A1,
        SourceKind = 3,
        Directions = 1,
        FramesPerDirection = 1,
        CellWidth = 3,
        CellHeight = 1,
        ShadowColour = 0x0020,
        Cells = [new SpriteCell(0, 0, 3, 1, 0)],
        // Opaque red, transparent, opaque baked-shadow colour.
        PixelData = [0x00, 0xFC, 0x00, 0x00, 0x20, 0x80],
    };

    [Fact]
    public void Atlas_packs_each_format_into_its_own_pages_and_records_cell_lookups()
    {
        SpriteSheet units = SpriteSheetTests.Indexed();
        SpriteSheet building = Rgb555Sheet();
        SpriteSheet overlay = ImageSheets.FromImage(IndexedImage(), 3, 2, playerColours: true);
        SpriteAtlas atlas = SpriteAtlas.Build([units, building, overlay], pageSize: 8);

        Assert.Equal(3, atlas.SheetCount);
        Assert.Equal(2 + 1 + 6, atlas.CellCount);
        Assert.Equal(2, atlas.PaletteRows);
        Assert.Single(atlas.Pages(PixelFormat.Indexed8));
        Assert.Single(atlas.Pages(PixelFormat.Rgb5A1));
        Assert.Empty(atlas.Pages(PixelFormat.R8));
        Assert.Equal(8 * 8 * 2, atlas.Pages(PixelFormat.Rgb5A1)[0].Length);

        AtlasCell unit = atlas.GetCell(0, 0);
        Assert.Equal((PixelFormat.Indexed8, 0, 240, 2, 2, 1, 1), (unit.Format, unit.PaletteRow, unit.PlayerFirst, unit.Width, unit.Height, unit.OffsetX, unit.OffsetY));
        Assert.True(atlas.GetCell(0, 1).IsEmpty);
        Assert.Equal(1, atlas.GetCell(2, 3).PaletteRow);
        Assert.Equal(0x0020, atlas.GetCell(1, 0).ShadowColour);

        // Every non-empty cell's pixels are copied verbatim into its page rectangle.
        SpriteSheet[] sheets = [units, building, overlay];
        for (int s = 0; s < sheets.Length; s++)
        {
            for (int c = 0; c < sheets[s].CellCount; c++)
            {
                AtlasCell cell = atlas.GetCell(s, c);
                if (cell.IsEmpty)
                {
                    continue;
                }

                int bpp = PixelFormats.BytesPerPixel(cell.Format);
                byte[] page = atlas.Pages(cell.Format)[cell.Layer];
                byte[] copied = Enumerable.Range(0, cell.Height)
                    .SelectMany(y => page.Skip((((cell.Y + y) * 8) + cell.X) * bpp).Take(cell.Width * bpp))
                    .ToArray();
                Assert.Equal(sheets[s].CellPixels(c).ToArray(), copied);
            }
        }

        // Palette classes: index 7 of the unit sheet is transparent, 3 is a baked-shadow hint.
        Assert.Equal(SpriteAtlas.PaletteTransparent, atlas.PaletteTexture[(7 * 4) + 3]);
        Assert.Equal(SpriteAtlas.PaletteShadow, atlas.PaletteTexture[(3 * 4) + 3]);
        Assert.Equal(SpriteAtlas.PaletteOpaque, atlas.PaletteTexture[(9 * 4) + 3]);
    }

    [Fact]
    public void Atlas_spills_to_more_layers_when_a_page_is_full()
    {
        var sheets = Enumerable.Range(0, 5).Select(_ => Rgb555Sheet()).ToArray();
        SpriteAtlas atlas = SpriteAtlas.Build(sheets, pageSize: 4);
        Assert.Equal(2, atlas.Pages(PixelFormat.Rgb5A1).Count);
        Assert.Equal(4, sheets.Select((_, i) => atlas.GetCell(i, 0)).Count(c => c.Layer == 0));
        Assert.Throws<ArgumentException>(() => SpriteAtlas.Build([Rgb555Sheet()], pageSize: 2));
    }

    [Fact]
    public void Shading_recolours_the_player_range_and_classifies_shadow_like_the_cpu_compositor()
    {
        PlayerPalette ramps = PlayerPalette.Parse(Encoding.UTF8.GetBytes(GamePaletteJson()));
        SpriteSheet units = SpriteSheetTests.Indexed();
        SpriteAtlas atlas = SpriteAtlas.Build([units, Rgb555Sheet()], pageSize: 16);
        AtlasCell cell = atlas.GetCell(0, 0);

        // Cell pixels: [3 (shadow hint), 7 (transparent) / 241 (player k=1), 9].
        Assert.Equal((TexelKind.Shadow, new Rgba32(3, 0, 0, 255)), SpriteShading.Shade(atlas, cell, 0, 0, ramps, 2, true));
        Assert.Equal((TexelKind.Colour, new Rgba32(3, 0, 0, 255)), SpriteShading.Shade(atlas, cell, 0, 0, ramps, 2, false));
        Assert.Equal(TexelKind.None, SpriteShading.Shade(atlas, cell, 1, 0, ramps, 2, true).Kind);
        Assert.Equal((TexelKind.Colour, ramps.Ramp(2, 1)), SpriteShading.Shade(atlas, cell, 0, 1, ramps, 2, true));
        Assert.Equal((TexelKind.Colour, ramps.Ramp(5, 1)), SpriteShading.Shade(atlas, cell, 0, 1, ramps, 5, true));
        Assert.Equal((TexelKind.Colour, units.Palette[241]), SpriteShading.Shade(atlas, cell, 0, 1, ramps, -1, true));
        Assert.Equal((TexelKind.Colour, new Rgba32(9, 0, 0, 255)), SpriteShading.Shade(atlas, cell, 1, 1, ramps, 2, true));

        // Matches the CPU compositor (player ramp, shadows marked) on every opaque, non-shadow pixel.
        byte[] reference = SpriteCompositor.SheetToRgba(units, ramps.RampOf(2), markShadow: false);
        for (int y = 0; y < cell.Height; y++)
        {
            for (int x = 0; x < cell.Width; x++)
            {
                (TexelKind kind, Rgba32 colour) = SpriteShading.Shade(atlas, cell, x, y, ramps, 2, false);
                int o = ((((cell.OffsetY + y) * units.CellWidth) + cell.OffsetX + x) * 4);
                Assert.Equal(reference[o + 3] != 0, kind != TexelKind.None);
                if (kind == TexelKind.Colour)
                {
                    Assert.Equal(new Rgba32(reference[o], reference[o + 1], reference[o + 2], 255), colour);
                }
            }
        }

        AtlasCell rgb = atlas.GetCell(1, 0);
        Assert.Equal((TexelKind.Colour, Rgba32.FromRgb555(0x7C00)), SpriteShading.Shade(atlas, rgb, 0, 0, ramps, 0, true));
        Assert.Equal(TexelKind.None, SpriteShading.Shade(atlas, rgb, 1, 0, ramps, 0, true).Kind);
        Assert.Equal(TexelKind.Shadow, SpriteShading.Shade(atlas, rgb, 2, 0, ramps, 0, true).Kind);
        Assert.Equal(TexelKind.Colour, SpriteShading.Shade(atlas, rgb, 2, 0, ramps, 0, false).Kind);
    }

    private const string FontJson = """
        {"source":"local/fonts/t.fnt","strip":"fonts/t.t2img","face":"Test","codePage":1252,"cellWidth":4,"cellHeight":3,"glyphCount":3,
         "metrics":[0],"characters":[[0,0,0,0],[63,63,0,2],[65,65,1,3],[233,233,2,4]]}
        """;

    [Fact]
    public void Fonts_lay_out_text_with_per_character_widths_and_line_breaks()
    {
        FontAsset font = FontAsset.Parse(Encoding.UTF8.GetBytes(FontJson));
        Assert.Equal(("Test", "fonts/t.t2img", 4, 3), (font.Face, font.Strip, font.CellWidth, font.CellHeight));
        IReadOnlyList<PlacedGlyph> glyphs = font.Layout("Aé\nZA");
        Assert.Equal(
            [new PlacedGlyph(1, 0, 0, 3), new PlacedGlyph(2, 3, 0, 4), new PlacedGlyph(0, 0, 3, 2), new PlacedGlyph(1, 2, 3, 3)],
            glyphs);
        Assert.Equal(7, font.Measure("Aé\nZA"));
        Assert.False(font.TryGetCharacter(0, out _));

        var strip = new ImageAsset { Format = PixelFormat.R8, Width = 2, Height = 1, Pixels = [7, 3] };
        byte[] rgba = FontAsset.StripToRgba(strip, new Rgba32(10, 20, 30, 255));
        Assert.Equal(new byte[] { 10, 20, 30, 255, 10, 20, 30, 109 }, rgba);
        var oneBit = new ImageAsset { Format = PixelFormat.R8, Width = 2, Height = 1, Pixels = [1, 0] };
        Assert.Equal(new byte[] { 255, 255, 255, 255, 255, 255, 255, 0 }, FontAsset.StripToRgba(oneBit, new Rgba32(255, 255, 255, 255)));
    }

    private const string TerrainJson = """
        [{"set":"test","name":"Test","table":"t","folder":"art/terrain/test/","terrains":[{"index":"0","name":"Water"},{"index":"1","name":"Dirt"},{"index":"2","name":"Grass"}],
          "files":[{"pair":"00","asset":"w","size":"1,1,0"},{"pair":"11","asset":"d","size":"2,1,0"},{"pair":"22","asset":"g","size":"1,1,0"},
                   {"pair":"01","asset":"shore","size":"2,14,2"},{"pair":"02","asset":"shore","size":null},{"pair":"12","asset":"t12","size":"2,14,0"},
                   {"pair":"28","asset":"cliff","size":null}],
          "light":"l","trees":"tr","treeShadows":"ts"}]
        """;

    private static ImageAsset Solid(int width, int height, Func<int, int, int> word)
    {
        byte[] pixels = new byte[width * height * 2];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int w = word(x, y);
                pixels[2 * ((y * width) + x)] = (byte)w;
                pixels[(2 * ((y * width) + x)) + 1] = (byte)(w >> 8);
            }
        }

        return new ImageAsset { Format = PixelFormat.Rgb5A1, Width = width, Height = height, Pixels = pixels };
    }

    private static int WordAt(TerrainImage image, int x, int y)
        => image.Pixels[2 * ((y * image.Width) + x)] | (image.Pixels[(2 * ((y * image.Width) + x)) + 1] << 8);

    [Fact]
    public void Terrain_set_parses_pairs_and_inherits_sizes_of_shared_files()
    {
        TerrainSet set = Assert.Single(TerrainSet.ParseAll(Encoding.UTF8.GetBytes(TerrainJson)));
        Assert.Equal(("test", "Test", "Grass"), (set.Id, set.Name, set.TerrainName(2)));
        Assert.Equal(new TerrainSize(2, 14, 2), set.TransitionOf(2, 0)!.Size);
        Assert.True(set.TransitionOf(0, 2)!.SizeInherited);
        Assert.False(set.TransitionOf(0, 1)!.SizeInherited);
        Assert.True(set.BaseOf(1)!.IsBase);
        Assert.True(set.TransitionOf(2, 8)!.IsUnresolved);
        Assert.False(set.TransitionOf(1, 2)!.IsUnresolved);
        Assert.Null(set.TransitionOf(1, 1));
        Assert.Equal("12", set.TransitionOf(2, 1)!.Pair);
    }

    [Fact]
    public void Terrain_demo_grid_exercises_all_fourteen_transition_shapes()
    {
        TerrainGrid grid = TerrainGrid.Demo(1, 2);
        var masks = new SortedSet<int>();
        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                masks.Add(TerrainComposer.MaskOf(grid.CornersOf(x, y), 1));
            }
        }

        Assert.Equal(Enumerable.Range(0, 16), masks);
    }

    [Fact]
    public void Terrain_composer_keys_magenta_to_the_lower_terrain_and_green_to_the_upper()
    {
        TerrainSet set = Assert.Single(TerrainSet.ParseAll(Encoding.UTF8.GetBytes(TerrainJson)));
        const int Dirt = 0x0011, Grass = 0x0222, Fringe = 0x1234;
        // Transition 12: 2 variants × 14 shapes. In every tile the left half is magenta, the right half green, except
        // pixel (31, 31), which is an opaque fringe pixel.
        ImageAsset t12 = Solid(64, 448, (x, y) => (x % 32) == 31 && (y % 32) == 31 ? 0x8000 | Fringe : (x % 32) < 16 ? TerrainComposer.KeyMagenta : TerrainComposer.KeyGreen);
        var images = new Dictionary<string, ImageAsset>
        {
            ["d"] = Solid(64, 32, (x, _) => 0x8000 | (x < 32 ? Dirt : Dirt + 1)),
            ["g"] = Solid(32, 32, (_, _) => 0x8000 | Grass),
            ["t12"] = t12,
        };
        var composer = new TerrainComposer(set, a => images.GetValueOrDefault(a));
        var grid = new TerrainGrid(3, 1, 2);
        grid[1, 0] = 1;
        grid[1, 1] = 1;
        grid[0, 0] = 1;
        grid[0, 1] = 1;
        // Tile 0: all dirt (base, wraps: tile column 0); tile 1: left corners dirt (mask TL|BL = 9); tile 2: grass.
        TerrainImage image = composer.Compose(grid, 0);
        Assert.Empty(image.Issues);
        Assert.Equal((96, 32), (image.Width, image.Height));
        Assert.Equal(0x8000 | Dirt, WordAt(image, 5, 5));
        Assert.Equal(0x8000 | Dirt + 1, WordAt(image, 32 + 5, 5)); // magenta → dirt (A), base tile column 1 % 2
        Assert.Equal(0x8000 | Grass, WordAt(image, 32 + 20, 5)); // green → grass (B)
        Assert.Equal(0x8000 | Fringe, WordAt(image, 63, 31)); // overlay pixel kept
        Assert.Equal(0x8000 | Grass, WordAt(image, 70, 5));
        Assert.Equal(1, composer.FrameCount(grid));
    }

    [Fact]
    public void Terrain_composer_animates_water_and_shores_over_their_common_cycle_and_reports_unresolved_tiles()
    {
        TerrainSet set = Assert.Single(TerrainSet.ParseAll(Encoding.UTF8.GetBytes(TerrainJson)));
        var images = new Dictionary<string, ImageAsset>
        {
            // Water: 3 frames of one 32-px tile, frame f has colour f + 1.
            ["w"] = Solid(32, 96, (_, y) => 0x8000 | ((y / 32) + 1)),
            ["d"] = Solid(64, 32, (_, _) => 0x8000 | 0x0011),
            ["g"] = Solid(32, 32, (_, _) => 0x8000 | 0x0222),
            // Shore: 2 variants × 2 frames; every pixel magenta (water shows through) except column 0 = fringe of frame.
            ["shore"] = Solid(128, 448, (x, _) => x % 32 == 0 ? 0x8000 | (0x100 + ((x / 32) % 2)) : TerrainComposer.KeyMagenta),
        };
        var composer = new TerrainComposer(set, a => images.GetValueOrDefault(a));
        var grid = new TerrainGrid(2, 1, 0);
        grid[2, 0] = 1;
        Assert.Equal(3, composer.BaseFrames(0));
        Assert.Equal(6, composer.FrameCount(grid));
        for (int f = 0; f < 6; f++)
        {
            TerrainImage image = composer.Compose(grid, f);
            Assert.Equal(0x8000 | ((f % 3) + 1), WordAt(image, 10, 10));
            Assert.Equal(0x8000 | ((f % 3) + 1), WordAt(image, 40, 10));
            Assert.Equal(0x8000 | (0x100 + (f % 2)), WordAt(image, 32, 10));
        }

        // Three terrains in one tile, and the unresolved cliff pseudo-terrain 8.
        var mixed = new TerrainGrid(3, 1, 1);
        mixed[0, 0] = 0;
        mixed[0, 1] = 2;
        mixed[3, 0] = 8;
        TerrainImage drawn = composer.Compose(mixed, 0);
        Assert.Equal(2, drawn.Issues.Count);
        Assert.Contains("three or more terrains", drawn.Issues[0], StringComparison.Ordinal);
        Assert.Contains("no usable transition for pair 18", drawn.Issues[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Terrain_variant_choice_is_a_fixed_function_of_the_tile()
    {
        int[] variants = Enumerable.Range(0, 64).Select(i => TerrainComposer.VariantOf(i % 8, i / 8, 2)).ToArray();
        Assert.Equal(variants, Enumerable.Range(0, 64).Select(i => TerrainComposer.VariantOf(i % 8, i / 8, 2)));
        Assert.Contains(0, variants);
        Assert.Contains(1, variants);
        Assert.Equal(0, TerrainComposer.VariantOf(3, 4, 1));
    }

    internal static byte[] Wave(int channels, int rate, int bits, byte[] data, bool extraChunk = true)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8);
        w.Write(0);
        w.Write("WAVE"u8);
        if (extraChunk)
        {
            w.Write("LIST"u8);
            w.Write(3);
            w.Write(new byte[] { 1, 2, 3, 0 });
        }

        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * bits / 8);
        w.Write((short)(channels * bits / 8));
        w.Write((short)bits);
        w.Write("data"u8);
        w.Write(data.Length);
        w.Write(data);
        w.Flush();
        return stream.ToArray();
    }

    [Fact]
    public void Wave_parser_finds_pcm_data_after_other_chunks()
    {
        byte[] file = Wave(1, 22050, 16, new byte[44100]);
        WaveData wave = WaveData.Parse(file);
        Assert.Equal((1, 22050, 16, 22050), (wave.Channels, wave.SampleRate, wave.BitsPerSample, wave.Frames));
        Assert.Equal(1000, wave.DurationMs);
        Assert.Equal(file.Length - 44100, wave.DataOffset);
        Assert.Throws<InvalidDataException>(() => WaveData.Parse(Wave(1, 22050, 24, new byte[6])));
        Assert.Throws<InvalidDataException>(() => WaveData.Parse("RIFF\0\0\0\0WAVE"u8));
        Assert.Throws<InvalidDataException>(() => WaveData.Parse(new byte[4]));
    }
}
