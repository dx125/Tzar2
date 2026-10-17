using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Tools.Cli.Fidelity.Analysis;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Scenarios;

namespace Tzar2.Tools.Cli.Tests.Fidelity;

/// <summary>
/// A synthetic terrain set and map (REQ-LEGAL-03: generated in code). Terrains 1 and 2 have 12 random base tiles
/// each in images "11" and "22"; image "12" holds 2×14 transition tiles with green/magenta keys. Map words are
/// <c>terrain &lt;&lt; 8 | variant</c>, so every word names exactly one tile.
/// </summary>
internal sealed class SyntheticWorld
{
    public const int Side = 64;
    public static readonly GameLayout Layout = new("TEST", 1024, 768, new ScreenRect(150, 30, 874, 738), new ScreenRect(4, 31, 128, 128), new ScreenPoint(70, 400));

    public SyntheticWorld(int seed = 7)
    {
        var random = new Random(seed);
        var tiles = new List<TerrainTile>();
        for (int terrain = 1; terrain <= 2; terrain++)
        {
            string code = $"{terrain}{terrain}";
            for (int v = 0; v < 12; v++)
            {
                ushort[] p = new ushort[32 * 32];
                for (int i = 0; i < p.Length; i++)
                {
                    // Terrain 1 greenish, terrain 2 brownish; bright enough that darkening is measurable.
                    int r = terrain == 1 ? random.Next(4, 12) : random.Next(18, 28);
                    int g = terrain == 1 ? random.Next(16, 30) : random.Next(10, 20);
                    int b = random.Next(4, 14);
                    p[i] = (ushort)((r << 10) | (g << 5) | b);
                }

                tiles.Add(new TerrainTile(tiles.Count, code + ".bmp", [code], v % 4, v / 4, false, true, p));
            }
        }

        for (int shape = 0; shape < 14; shape++)
        {
            for (int variant = 0; variant < 2; variant++)
            {
                ushort[] p = new ushort[32 * 32];
                for (int y = 0; y < 32; y++)
                {
                    for (int x = 0; x < 32; x++)
                    {
                        bool fringe = Math.Abs(x - y) < 6;
                        p[(y * 32) + x] = fringe ? (ushort)random.Next(1, 0x7000) : (x < y ? TerrainTile.Magenta : TerrainTile.Green);
                    }
                }

                tiles.Add(new TerrainTile(tiles.Count, "12.bmp", ["12"], variant, shape, true, true, p));
            }
        }

        Library = new TerrainLibrary("grass", tiles);
        Words = new ushort[Side * Side];
        for (int y = 0; y < Side; y++)
        {
            for (int x = 0; x < Side; x++)
            {
                int terrain = ((x / 8) + (y / 8)) % 2 == 0 ? 1 : 2;
                Words[(y * Side) + x] = (ushort)((terrain << 8) | random.Next(12));
            }
        }

        Map = new MapInfo("SYN", Side, Words, "grass");
    }

    public TerrainLibrary Library { get; }

    public ushort[] Words { get; }

    public MapInfo Map { get; }

    public TerrainTile TileOf(int wx, int wy)
    {
        ushort word = Words[(wy * Side) + wx];
        int terrain = word >> 8, variant = word & 0xFF;
        return Library.Tiles[((terrain - 1) * 12) + variant];
    }

    /// <summary>
    /// The RGB555 screen for a camera at world pixel (camX, camY) = the world pixel shown at the view's top-left.
    /// Pixels outside the view are a flat HUD colour.
    /// </summary>
    public ushort[] Render(int camX, int camY, Func<int, int, ushort, ushort>? overlay = null)
    {
        var frame = new ushort[Layout.Width * Layout.Height];
        ScreenRect view = Layout.View;
        for (int y = 0; y < Layout.Height; y++)
        {
            for (int x = 0; x < Layout.Width; x++)
            {
                ushort v = 0x2108;
                if (view.Contains(x, y))
                {
                    int wx = camX + x - view.X, wy = camY + y - view.Y;
                    v = TileOf(wx / 32, wy / 32).Pixels[((wy % 32) * 32) + (wx % 32)];
                }

                frame[(y * Layout.Width) + x] = overlay is null ? v : overlay(x, y, v);
            }
        }

        return frame;
    }

    /// <summary>RGB555 → RGB565 by green bit replication.</summary>
    public static uint To565(ushort v)
    {
        int r = (v >> 10) & 31, g = (v >> 5) & 31, b = v & 31;
        return (uint)((r << 11) | (((g << 1) | (g >> 4)) << 5) | b);
    }

    /// <summary>RGB555 → 0xRRGGBB by bit replication.</summary>
    public static uint To888(ushort v)
    {
        int r = (v >> 10) & 31, g = (v >> 5) & 31, b = v & 31;
        return (uint)((Rep(r) << 16) | (Rep(g) << 8) | Rep(b));
    }

    public static int Rep(int c5) => (c5 << 3) | (c5 >> 2);

    public static CaptureImage Snapshot565(ushort[] frame) => new(Layout.Width, Layout.Height, PixelFormat.Rgb565, frame.Select(To565).ToArray());

    public static CaptureImage Window888(ushort[] frame) => new(Layout.Width, Layout.Height, PixelFormat.Rgb888, frame.Select(To888).ToArray());
}
