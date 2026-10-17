using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Tzar2.Content.Assets;
using Tzar2.Content.Vfs;
using Tzar2.Formats.Pal;
using Tzar2.Formats.Rle;
using Tzar2.Import.Pipeline;
using Tzar2.Tests.Shared;
using Xunit;

namespace Tzar2.Import.Tests.Pipeline;

/// <summary>
/// M1 exit criteria on the reference build (TDD §25), skipped without <c>TZAR2_ORIGIN</c>: everything imports without
/// errors, the import is deterministic, and 20 golden sprites rendered by the proven Python decoder
/// (<c>tools/re/make_goldens.py</c>) are pixel-identical to the C# pipeline's output.
/// </summary>
public sealed class OriginImportTests : IDisposable
{
    private readonly string _out = Path.Combine(Path.GetTempPath(), "tzar2-origin-import-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_out))
        {
            Directory.Delete(_out, recursive: true);
        }
    }

    [OriginFact]
    public void Reference_build_imports_without_errors_and_deterministically()
    {
        string origin = OriginPaths.RequireOrigin();
        ImportResult first = ImportPipeline.Run(new ImportOptions { OriginPath = origin, OutputDirectory = Path.Combine(_out, "a") });
        ImportResult second = ImportPipeline.Run(new ImportOptions { OriginPath = origin, OutputDirectory = Path.Combine(_out, "b"), MaxParallelism = 1 });
        ImportReport report = first.Report;

        Assert.True(report.Succeeded, string.Join("\n", report.Errors));
        Assert.Equal("gog-1.10-en", report.EditionId);
        Assert.Equal(report.PackageSha256, second.Report.PackageSha256);
        Assert.Equal(File.ReadAllBytes(first.PackagePath), File.ReadAllBytes(second.PackagePath));

        // All 961 RLE + all BMP/WAV/MID/INI/TSV/FNT of the reference build (ADR 0013, TDD §25 M1).
        var expected = new Dictionary<SourceKind, int>
        {
            [SourceKind.Sprite] = 961,
            [SourceKind.Dat] = 621,
            [SourceKind.Bitmap] = 625,
            [SourceKind.Sound] = 438,
            [SourceKind.Music] = 14,
            [SourceKind.Ini] = 175,
            [SourceKind.Tsv] = 8,
            [SourceKind.Font] = 7,
            [SourceKind.Palette] = 1,
            [SourceKind.Container] = 9,
        };
        foreach ((SourceKind kind, int count) in expected)
        {
            CategoryCount c = report.Category(kind);
            Assert.True(c.Found == count && c.Imported == count && c.Failed == 0, $"{kind}: found {c.Found}, imported {c.Imported}, failed {c.Failed}");
        }

        Assert.Equal(26, report.NestedMaps);
        Assert.InRange(report.SpriteMemory.Values.Sum(s => s.StoredBytes), 112_000_000, 112_400_000);
    }

    [GoldenFact]
    public void Twenty_golden_sprites_are_pixel_identical()
    {
        string origin = OriginPaths.RequireOrigin();
        var source = new DirectorySource(origin);
        GamePalette pal = GamePalette.Parse(source.ReadAllBytes("images/game.pal"));
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(OriginPaths.GoldensDirectory, "goldens.json")));
        int compared = 0;
        var kinds = new HashSet<int>();
        foreach (JsonElement golden in doc.RootElement.EnumerateArray())
        {
            string rel = golden.GetProperty("source").GetString()!;
            int width = golden.GetProperty("width").GetInt32();
            int cellHeight = golden.GetProperty("cellHeight").GetInt32();
            int cells = golden.GetProperty("cells").GetInt32();
            int player = golden.GetProperty("player").GetInt32();
            bool markShadow = golden.GetProperty("markShadow").GetBoolean();
            byte[] expected = File.ReadAllBytes(Path.Combine(OriginPaths.GoldensDirectory, golden.GetProperty("file").GetString()!));

            // The same path as the importer: RLE → lossless trimmed sheet → .t2spr bytes → parse → shader-equivalent expansion.
            string path = Content.ContentPath.Normalize(rel);
            RleSheet rle = RleSheet.Parse(source.ReadAllBytes(path));
            SpriteSheet sheet = SpriteSheet.Parse(SpriteConverter.Convert(rle, path.StartsWith("images/unit/", StringComparison.Ordinal), []).ToBytes());
            Rgba32[] ramp = pal.PlayerRamp(player).Select(c => new Rgba32(c.R, c.G, c.B, 255)).ToArray();
            byte[] actual = SpriteCompositor.SheetToRgba(sheet, ramp, markShadow);

            // Surplus rows below the last cell (BRIBE) are not part of any cell and are dropped by design (rle.md §4).
            int cellBytes = width * cellHeight * cells * 4;
            Assert.Equal(cellBytes, actual.Length);
            int firstDiff = actual.AsSpan().CommonPrefixLength(expected.AsSpan(0, cellBytes));
            Assert.True(firstDiff == cellBytes, $"{rel}: first difference at pixel {firstDiff / 4} (x {firstDiff / 4 % width}, y {firstDiff / 4 / width})");
            compared++;
            kinds.Add(golden.GetProperty("kind").GetInt32());
        }

        Assert.Equal(20, compared);
        Assert.Equal([1, 3, 7], kinds.Order());
    }
}
