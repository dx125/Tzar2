using System;
using System.Linq;
using Tzar2.Tests.Shared;
using Tzar2.Tools.Cli.Fidelity.Analysis;
using Tzar2.Tools.Cli.Fidelity.Scenarios;
using Xunit;

namespace Tzar2.Tools.Cli.Tests.Fidelity;

/// <summary>Local-only checks of the analysis inputs read from the original install (skipped without TZAR2_ORIGIN).</summary>
public class OriginInstallTests
{
    [OriginFact]
    public void Loads_every_terrain_set_and_the_maps_used_by_the_scenarios()
    {
        string origin = OriginPaths.RequireOrigin();
        foreach (string set in TerrainLibrary.SetNames)
        {
            TerrainLibrary library = TerrainLibrary.Load(origin, set);
            Assert.True(library.Tiles.Count(t => !t.IsTransition) >= 256 * 3, set);
            Assert.Contains(library.Tiles, t => t.IsTransition);
            Assert.True(library.ColoursByTerrain.Count >= 4, set);
        }

        var ctx = new AnalysisContext(null!, origin, new GameLayout("L", 1024, 768, new ScreenRect(150, 30, 874, 738), new ScreenRect(4, 31, 128, 128), new ScreenPoint(70, 400)));
        Assert.Equal("snow", ctx.Map("MP1")!.TerrainSet);
        Assert.Equal("grass", ctx.Map("MP2")!.TerrainSet);
        Assert.Equal("summer", ctx.Map("MP4")!.TerrainSet);
        Assert.Equal(256, ctx.Map("MP4")!.Side);
        Assert.NotNull(ctx.Palette);
    }

    [OriginFact]
    public void Real_terrain_tiles_are_found_in_a_composed_screen()
    {
        TerrainLibrary library = TerrainLibrary.Load(OriginPaths.RequireOrigin(), "grass");
        TerrainTile[] bases = library.Tiles.Where(t => !t.IsTransition && t.InDeclaredSize).ToArray();
        var random = new Random(3);
        var grid = new TerrainTile[40, 40];
        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                grid[x, y] = bases[random.Next(bases.Length)];
            }
        }

        ushort[] frame = new ushort[1024 * 768];
        for (int y = 0; y < 768; y++)
        {
            for (int x = 0; x < 1024; x++)
            {
                int wx = x + 9, wy = y + 21;
                frame[(y * 1024) + x] = grid[wx / 32, wy / 32].Pixels[((wy % 32) * 32) + (wx % 32)];
            }
        }

        TileMatchResult result = new TileMatcher(library).Match(frame, 1024, 768, new ScreenRect(150, 30, 874, 738));
        Assert.Equal((32 - 9, 32 - 21), (result.OffsetX, result.OffsetY));
        // Identical atlas cells exist (flat areas); any of them is a correct answer, so compare pixels, not indices.
        Assert.All(result.Cells, c => Assert.Equal(CellMatchKind.Exact, c.Kind));
        Assert.Equal(result.CellsTotal, result.Cells.Count);
    }
}
