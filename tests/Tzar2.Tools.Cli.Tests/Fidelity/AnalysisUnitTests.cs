using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Formats;
using Tzar2.Formats.Pal;
using Tzar2.Tests.Shared;
using Tzar2.Tools.Cli.Fidelity.Analysis;
using Tzar2.Tools.Cli.Fidelity.Capture;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Scenarios;
using Xunit;

namespace Tzar2.Tools.Cli.Tests.Fidelity;

public class TileMatcherTests
{
    private static readonly SyntheticWorld World = new();

    [Fact]
    public void Finds_the_grid_offset_and_every_tile()
    {
        ushort[] frame = World.Render(37, 51);
        TileMatchResult result = new TileMatcher(World.Library).Match(frame, 1024, 768, SyntheticWorld.Layout.View);

        Assert.Equal((150 - 37 + 64) % 32, result.OffsetX);
        Assert.Equal((30 - 51 + 64) % 32, result.OffsetY);
        Assert.Equal(result.CellsTotal, result.Count(CellMatchKind.Exact));
        CellMatch first = result.Cells[0];
        // Screen cell (0, 0) starts at the first full cell inside the view: world pixel (64, 64) = cell (2, 2).
        Assert.Equal((177, 43), (first.X, first.Y));
        Assert.Same(World.TileOf(2, 2), first.Tile);

        (int? wx, int? wy, double consistency) = TerrainTiles.Align(result, World.Map, null);
        Assert.Equal((2, 2), (wx, wy));
        Assert.Equal(1.0, consistency);
    }

    [Fact]
    public void Partly_covered_cells_and_transitions_are_recognised()
    {
        TerrainTile transition = World.Library.Tiles.First(t => t.IsTransition);
        TerrainTile low = World.Library.Tiles[0], high = World.Library.Tiles[12];
        ushort[] frame = World.Render(0, 0, (x, y, v) =>
        {
            // Cell at screen (150, 30) is world (0, 0): replace it by a transition over terrains 1 (magenta) and 2 (green).
            if (x is >= 150 and < 182 && y is >= 30 and < 62)
            {
                int i = ((y - 30) * 32) + (x - 150);
                ushort key = transition.Pixels[i];
                return key == TerrainTile.Magenta ? low.Pixels[i] : key == TerrainTile.Green ? high.Pixels[i] : key;
            }

            // A "unit" covering the top half of the cell at (182, 30).
            return x is >= 182 and < 214 && y is >= 30 and < 46 ? (ushort)0x7C00 : v;
        });
        TileMatchResult result = new TileMatcher(World.Library).Match(frame, 1024, 768, SyntheticWorld.Layout.View);

        CellMatch t = result.Cells.Single(c => c.X == 150 && c.Y == 30);
        Assert.Equal(CellMatchKind.Transition, t.Kind);
        Assert.Same(transition, t.Tile);
        Assert.True(t.KeyEvidence.MagentaLower > t.KeyEvidence.MagentaHigher);
        Assert.True(t.KeyEvidence.GreenHigher > t.KeyEvidence.GreenLower);
        CellMatch p = result.Cells.Single(c => c.X == 182 && c.Y == 30);
        Assert.Equal(CellMatchKind.Partial, p.Kind);
        Assert.Same(World.TileOf(1, 0), p.Tile);
        Assert.Equal(512, p.EqualPixels);
    }

    [Fact]
    public void Reads_the_terrain_set_and_tiles_of_a_map_file()
    {
        byte[] words = new byte[64 * 64 * 2];
        words[2] = 0x20;
        words[3] = 0x01;
        byte[] header = [.. "2mzt\0Map\0Kill Ally\0snow\0MAPS\\x\0"u8];
        byte[] wmp = SyntheticFormats.Hpfs([("MAPS/X.tiles", words), ("MAPS/X.header", header)]);
        MapInfo map = AnalysisContext.ReadMap("X", wmp)!;
        Assert.Equal(64, map.Side);
        Assert.Equal(0x0120, map.Words[1]);
        Assert.Equal("snow", map.TerrainSet);
        Assert.Null(TerrainLibrary.SetFromMapHeader("nothing here"u8));
    }
}

public class AnalysisMathTests
{
    [Theory]
    [InlineData(5, 8, 31, 255)]
    [InlineData(5, 8, 16, 132)]
    [InlineData(5, 6, 16, 33)]
    [InlineData(6, 8, 33, 134)]
    [InlineData(8, 5, 255, 31)]
    public void Replicate_matches_bit_replication(int from, int to, int level, int expected) =>
        Assert.Equal(expected, ColourDepth.Replicate(level, from, to));

    [Fact]
    public void Best_model_names_the_expansion_rule()
    {
        int[] replicate = Enumerable.Range(0, 32).Select(v => (v << 3) | (v >> 2)).ToArray();
        int[] shift = Enumerable.Range(0, 32).Select(v => v << 3).ToArray();
        Assert.Equal(("replicate", 32), ColourDepth.BestModel(replicate, 5, 8));
        Assert.Equal(("shift", 32), ColourDepth.BestModel(shift, 5, 8));
        shift[3] = -1;
        Assert.Equal(("shift", 31), ColourDepth.BestModel(shift, 5, 8));
        shift[4] = 7;
        Assert.Equal("shift (partial)", ColourDepth.BestModel(shift, 5, 8).Model);
    }

    [Fact]
    public void Player_colour_lookup_ignores_terrain_and_shared_colours()
    {
        GamePalette palette = GamePalette.Parse(SyntheticFormats.Pal());
        Rgb24 c = palette.PlayerRamp(2)[10];
        int v = ((c.R >> 3) << 10) | ((c.G >> 3) << 5) | (c.B >> 3);
        Assert.Equal(2, PlayerColours.BuildLookup(palette)[v]);
        Assert.Equal(-1, PlayerColours.BuildLookup(palette, [[(ushort)v]])[v]);
        Assert.Equal(-1, PlayerColours.BuildLookup(palette)[0]);
        Assert.Equal(3, PlayerColours.Decide([0, 5, 0, 90, 0, 0, 0, 400], out double confidence));
        Assert.Equal(18, confidence);
        Assert.Equal(7, PlayerColours.Decide([0, 0, 0, 0, 0, 0, 0, 400], out _));
        Assert.Equal(-1, PlayerColours.Decide(new long[8], out _));
    }

    [Fact]
    public void Unit_tracking_measures_a_moving_square()
    {
        var frames = new List<(double, CaptureImage)>();
        uint[] background = Enumerable.Range(0, 200 * 100).Select(i => (uint)((i * 7919) % 0x7FFF)).ToArray();
        for (int f = 0; f < 30; f++)
        {
            double t = f * 100.0;
            uint[] pixels = (uint[])background.Clone();
            int x0 = 20 + (int)Math.Round(0.04 * t), y0 = 40 + (int)Math.Round(0.01 * t); // 40 px/s right, 10 px/s down
            for (int y = 0; y < 10; y++)
            {
                for (int x = 0; x < 10; x++)
                {
                    pixels[((y0 + y) * 200) + x0 + x] = 0x7C00;
                }
            }

            // A "water" pixel that changes every frame is ignored.
            pixels[5] = (uint)f;
            frames.Add((t, new CaptureImage(200, 100, PixelFormat.Rgb555, pixels)));
        }

        UnitSpeed.Track track = UnitSpeed.TrackMotion(frames)!;
        UnitSpeed.Fit fit = UnitSpeed.FitMotion(track)!;
        Assert.InRange(fit.PixelsPerSecond, Math.Sqrt(1700) - 1, Math.Sqrt(1700) + 1);
        Assert.InRange(fit.VelocityX, 39, 41);
        Assert.True(fit.R2 > 0.99);
        Assert.Null(UnitSpeed.TrackMotion(frames.Take(3).ToList()));
        (double slope, double intercept, double r2) = UnitSpeed.LinearFit([2, 4, 6], [40, 80, 120]);
        Assert.Equal((20, 0, 1), (Math.Round(slope, 9), Math.Round(intercept, 9), Math.Round(r2, 9)));
    }

    [Fact]
    public void Unit_speed_rejects_short_fits_and_fits_against_the_order()
    {
        // Batch B (ADR 0171): the peasant's clean walk, a maceman heading east for a cow in the "west" burst, and a
        // crusader's two-second dash.
        var walk = new UnitSpeed.Fit(39.98, -39.92, 2.29, 0.96, 78, 7.71, 303.7);
        var against = new UnitSpeed.Fit(40.98, 40.13, 8.35, 0.95, 40, 4.0, 160);
        var dash = new UnitSpeed.Fit(41.78, -41.77, 0.89, 0.91, 21, 2.0, 79);
        Assert.Null(UnitSpeed.Rejection("west", walk));
        Assert.Contains("east in a burst ordered west", UnitSpeed.Rejection("west", against), StringComparison.Ordinal);
        Assert.Null(UnitSpeed.Rejection("east", against));
        Assert.Null(UnitSpeed.Rejection("north", against));
        Assert.StartsWith("too short", UnitSpeed.Rejection("west", dash), StringComparison.Ordinal);
    }

    [Fact]
    public void Attack_timing_picks_the_series_that_steps_down()
    {
        long[] times = Enumerable.Range(0, 100).Select(i => (long)i * 10_000).ToArray();
        long[] hp = times.Select(t => Math.Max(0, 65 - (7 * (t / 250_000)))).ToArray();
        long[] noise = times.Select(t => t % 3).ToArray();
        long[] constant = times.Select(_ => 65L).ToArray();
        var series = new MemoryProbe.SampleSeries([1, 2, 3], 4, times, [noise, hp, constant]);
        AttackTiming.HitSeries hits = AttackTiming.BestSeries(series)!;
        Assert.Equal(2ul, hits.Address);
        Assert.Equal(3, hits.HitTimesMs.Count);
        Assert.All(hits.Damage, d => Assert.Equal(7, d));
        Assert.Equal([250.0, 500.0, 750.0], hits.HitTimesMs);
    }

    [Fact]
    public void Shadow_models_include_halving_and_subtraction()
    {
        Assert.Contains(ShadowBlend.Models, m => m.Name == "floor(c*16/32)" && m.Apply(31, 5) == 15);
        Assert.Contains(ShadowBlend.Models, m => m.Name == "c-3" && m.Apply(2, 5) == 0 && m.Apply(20, 6) == 14);
    }

    [Fact]
    public void Shadow_rule_is_told_apart_from_its_neighbours_by_the_level_table()
    {
        // The first capture's tables: every level halved (c >> 1). floor(c*17/32) shares most pairs but not the table.
        int[][] halved = [.. Enumerable.Range(0, 3).Select(_ => Enumerable.Range(0, 32).Select(c => c >> 1).ToArray())];
        ShadowBlend.Model half = ShadowBlend.Models.Single(m => m.Name == "floor(c*16/32)");
        ShadowBlend.Model near = ShadowBlend.Models.Single(m => m.Name == "floor(c*17/32)");
        Assert.Equal(1.0, ShadowBlend.TableAgreement(halved, half, [5, 5, 5]));
        Assert.InRange(ShadowBlend.TableAgreement(halved, near, [5, 5, 5]), 0.5, 0.8);
        halved[0][7] = -1;   // a level not seen does not count
        Assert.Equal(1.0, ShadowBlend.TableAgreement(halved, half, [5, 5, 5]));
    }

    [Fact]
    public void Swatch_colours_are_matched_to_the_nearest_ramp()
    {
        GamePalette palette = GamePalette.Parse(SyntheticFormats.Pal());
        for (int ramp = 0; ramp < GamePalette.PlayerCount; ramp++)
        {
            Rgb24 c = palette.PlayerRamp(ramp)[15];
            Assert.Equal(ramp, PlayerColours.NearestRamp(palette, (c.R, c.G, c.B), out double margin));
            Assert.True(margin > 1);
        }
    }

    [Fact]
    public void Burst_regions_are_cropped()
    {
        var image = new CaptureImage(4, 3, PixelFormat.Rgb888, Enumerable.Range(0, 12).Select(i => (uint)i).ToArray());
        CaptureImage crop = CaptureRunner.Crop(image, new ScreenRect(1, 1, 2, 5));
        Assert.Equal((2, 2), (crop.Width, crop.Height));
        Assert.Equal([5u, 6u, 9u, 10u], crop.Native);
        Assert.Throws<InvalidOperationException>(() => CaptureRunner.Crop(image, new ScreenRect(9, 9, 2, 2)));
    }
}
