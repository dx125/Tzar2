using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tzar2.Content.Assets;
using Tzar2.Content.Packages;
using Tzar2.Content.Vfs;
using Tzar2.Formats.Text;
using Tzar2.Import.Editions;
using Tzar2.Import.Pipeline;
using Xunit;

namespace Tzar2.Import.Tests.Pipeline;

public sealed class ImportPipelineTests : IDisposable
{
    private readonly SyntheticGame _game = new();
    private readonly string _out = Path.Combine(Path.GetTempPath(), "tzar2-import-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _game.Dispose();
        if (Directory.Exists(_out))
        {
            Directory.Delete(_out, recursive: true);
        }
    }

    private static byte[] BuildBytes(string root, int parallelism)
    {
        (PackageWriter package, _) = ImportPipeline.Build(new DirectorySource(root), null,
            new ImportOptions { OriginPath = root, OutputDirectory = "unused", MaxParallelism = parallelism });
        using var ms = new MemoryStream();
        package.WriteTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Imports_every_kind_without_errors()
    {
        ImportResult result = ImportPipeline.Run(new ImportOptions { OriginPath = _game.Root, OutputDirectory = _out });
        ImportReport report = result.Report;
        Assert.True(report.Succeeded, string.Join("\n", report.Errors));
        Assert.Equal(4, report.Category(SourceKind.Sprite).Imported);
        Assert.Equal(6, report.Category(SourceKind.Bitmap).Imported);
        Assert.Equal(2, report.Category(SourceKind.Dat).Imported);
        Assert.Equal(4, report.Category(SourceKind.Ini).Imported);
        Assert.Equal(1, report.Category(SourceKind.Tsv).Imported);
        Assert.Equal(2, report.Category(SourceKind.Text).Imported);
        Assert.Equal(1, report.Category(SourceKind.Font).Imported);
        Assert.Equal(1, report.Category(SourceKind.Sound).Imported);
        Assert.Equal(1, report.Category(SourceKind.Music).Imported);
        Assert.Equal(1, report.Category(SourceKind.Container).Imported);
        Assert.Equal(1, report.Category(SourceKind.Deferred).Found);
        Assert.Equal(1, report.NestedMaps);
        Assert.Null(report.EditionId);
        Assert.Contains(report.Warnings, w => w.Message.Contains("unknown edition", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Source == "images/building/euro mill.rle" && w.Message.Contains("surplus", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Source == "data/icons.ini" && w.Message.Contains("[galleon]", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Message.Contains("'scenery\\gone.bmp' does not exist", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Message.Contains("13 = 13.bmp does not exist", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_out, ImportPipeline.ReportJson)));
        Assert.True(File.Exists(Path.Combine(_out, ImportPipeline.ReportText)));

        using var package = PackageSource.Open(result.PackagePath);
        PackageManifest manifest = package.ReadManifest()!;
        Assert.Equal("base.original", manifest.Id);
        Assert.StartsWith("sha256:", manifest.GameplayHash, StringComparison.Ordinal);
        foreach (string path in new[]
        {
            "art/unit/euro archer.t2spr", "art/unit/cow.t2spr", "art/building/euro mill.t2spr", "art/building/euro mill s.t2spr",
            "art/icons/icons.t2img", "art/building/mask/euro mill c.t2img", "art/terrain/grass/12.t2img", "art-extra/hero icons/big/h1.t2img",
            "fonts/small.json", "fonts/small.t2img", "palettes/game.json", "sound/command/yes.wav", "music/1.mid",
            "tables/data/images.ini.json", "tables/data/euro units.txt.json", "text/data/changes.txt", "text/data/ai/a.ai",
            "manifests/sprites.json", "manifests/sprite-index.json", "manifests/terrain.json", "manifests/edition.json",
        })
        {
            Assert.True(package.Contains(path), path);
        }

        Assert.DoesNotContain(package.Paths, p => p.Contains("tzar.ini", StringComparison.Ordinal) || p.Contains(".exe", StringComparison.Ordinal) || p.Contains("ddv", StringComparison.Ordinal));
        Assert.Equal("notes ö\r\n", Encoding.UTF8.GetString(package.ReadAllBytes("text/data/changes.txt")));

        using JsonDocument index = JsonDocument.Parse(package.ReadAllBytes("manifests/sprite-index.json"));
        JsonElement archer = index.RootElement.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "e_archer1");
        Assert.Equal("art/unit/euro archer.t2spr", archer.GetProperty("asset").GetString());
        Assert.Equal(["1", "2"], archer.GetProperty("properties").GetProperty("actionrow").EnumerateArray().Select(v => v.GetString()));
        JsonElement light = index.RootElement.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "g_light");
        Assert.Equal("art/terrain/grass/light.t2img", light.GetProperty("asset").GetString());
    }

    [Fact]
    public void Output_is_byte_identical_across_runs_and_thread_counts()
    {
        byte[] first = BuildBytes(_game.Root, 1);
        byte[] second = BuildBytes(_game.Root, 8);
        Assert.Equal(SHA256.HashData(first), SHA256.HashData(second));
    }

    [Fact]
    public void Converted_sprites_keep_every_pixel_and_their_hints()
    {
        (PackageWriter package, _) = ImportPipeline.Build(new DirectorySource(_game.Root), null, new ImportOptions { OriginPath = _game.Root, OutputDirectory = "unused" });
        var entries = package.Entries.ToDictionary(e => e.Key, e => e.Value);

        SpriteSheet archer = SpriteSheet.Parse(entries["art/unit/euro archer.t2spr"]);
        Assert.Equal(PixelFormat.Indexed8, archer.Format);
        Assert.Equal(240, archer.PlayerFirst);
        Assert.Contains(20, archer.ShadowIndices);
        Assert.Equal(0, archer.Palette[0].A); // key index 0 is unused, so it is the transparent index
        Assert.Equal(new SpriteCell(3, 2, 9, 8, archer.Cells[1].Offset), archer.Cells[1]);

        SpriteSheet cow = SpriteSheet.Parse(entries["art/unit/cow.t2spr"]);
        Assert.Equal(0x0020, cow.ShadowColour);
        SpriteSheet mill = SpriteSheet.Parse(entries["art/building/euro mill.t2spr"]);
        Assert.Equal(SpriteSheet.None, mill.ShadowColour);
        Assert.Equal(30, mill.CellHeight);
        SpriteSheet shadow = SpriteSheet.Parse(entries["art/building/euro mill s.t2spr"]);
        Assert.Equal(PixelFormat.R8, shadow.Format);

        ImageAsset icons = ImageAsset.Parse(entries["art/icons/icons.t2img"]);
        Assert.Equal(new byte[] { 0xE0, 0x03, 0x1F, 0xFC, 0x00, 0x80, 0xFF, 0xFF }, icons.Pixels); // green keyed, magenta opaque
        ImageAsset transition = ImageAsset.Parse(entries["art/terrain/grass/12.t2img"]);
        Assert.Equal(new byte[] { 0xE0, 0x03, 0x1F, 0x7C }, transition.Pixels); // both keys transparent, colours kept
        ImageAsset baseTexture = ImageAsset.Parse(entries["art/terrain/grass/11.t2img"]);
        Assert.Equal(0x83, baseTexture.Pixels[1]); // terrain base: green is opaque
        ImageAsset mask = ImageAsset.Parse(entries["art/building/mask/euro mill c.t2img"]);
        Assert.Equal(0, mask.Palette[1].A);
        Assert.Equal(255, mask.Palette[2].A);
        Assert.Equal(PixelFormat.Rgba8, ImageAsset.Parse(entries["art-extra/hero icons/big/h1.t2img"]).Format);
    }

    [Fact]
    public void A_corrupt_file_is_reported_and_the_rest_imported()
    {
        _game.Write("IMAGES/UNIT/BROKEN.RLE", " elr junk"u8.ToArray());
        (PackageWriter package, ImportReport report) = ImportPipeline.Build(new DirectorySource(_game.Root), null, new ImportOptions { OriginPath = _game.Root, OutputDirectory = "unused" });
        Assert.False(report.Succeeded);
        ImportIssue error = Assert.Single(report.Errors);
        Assert.Equal("images/unit/broken.rle", error.Source);
        Assert.Equal(1, report.Category(SourceKind.Sprite).Failed);
        Assert.Equal(4, report.Category(SourceKind.Sprite).Imported);
        Assert.False(package.Contains("art/unit/broken.t2spr"));
    }

    [Fact]
    public void Detected_edition_sets_code_page_and_version()
    {
        string manifestText = "path\tsize\tmd5\n" + string.Concat(Directory.EnumerateFiles(_game.Root, "*", SearchOption.AllDirectories)
            .Select(f => (Path.GetRelativePath(_game.Root, f).Replace('\\', '/'), f))
            .OrderBy(t => t.Item1, StringComparer.Ordinal)
            .Select(t => $"{t.Item1}\t{new FileInfo(t.f).Length}\t{InstallVerifier.ComputeMd5(t.f)}\n"));
        var edition = new KnownEdition("test-1.10-ru", "Test", "1.10", "ru", CodePage.Windows1251, false,
            ReferenceManifest.Parse(new StringReader(manifestText), "test"));
        EditionMatch match = EditionDetector.Detect(_game.Root, [edition]);
        Assert.True(match.IsExact);
        Assert.Equal(1000, match.SizeMatchPerMille);

        (PackageWriter package, ImportReport report) = ImportPipeline.Build(new DirectorySource(_game.Root), match, new ImportOptions { OriginPath = _game.Root, OutputDirectory = "unused" });
        Assert.Equal(CodePage.Windows1251, report.CodePage);
        Assert.Equal("test-1.10-ru", report.EditionId);
        var entries = package.Entries.ToDictionary(e => e.Key, e => e.Value);
        Assert.Equal("notes ц\r\n", Encoding.UTF8.GetString(entries["text/data/changes.txt"]));
        PackageManifest manifest = PackageManifest.Parse(Encoding.UTF8.GetString(entries["package.yaml"]));
        Assert.Equal("1.10.0", manifest.Version);
        Assert.Equal("test-1.10-ru", manifest.SourceEdition);

        _game.Write("DATA/AI/A.AI", "changed!"u8.ToArray());
        EditionMatch modified = EditionDetector.Detect(_game.Root, [edition]);
        Assert.False(modified.IsExact);
        Assert.Equal("DATA/AI/A.AI", Assert.Single(modified.Verification!.Modified).Path);
    }

    [Fact]
    public void Unrelated_folders_match_no_edition()
    {
        EditionMatch match = EditionDetector.Detect(_game.Root);
        Assert.Null(match.Edition);
        Assert.False(match.IsExact);
    }

    [Fact]
    public void Edition_catalog_holds_the_known_builds()
    {
        Assert.Equal(["gog-1.10-en", "gog-1.10-de", "gog-1.10-es", "gog-1.01-pl", "gog-1.01a-pt"], EditionCatalog.Editions.Select(e => e.Id));
        Assert.Equal(2916, EditionCatalog.Reference.Manifest.Entries.Count);
        Assert.Equal(CodePage.Windows1250, EditionCatalog.Editions.Single(e => e.Language == "pl").CodePage);
        Assert.All(EditionCatalog.Editions, e => Assert.DoesNotContain(e.Manifest.Entries, m => m.Path.StartsWith("app/", StringComparison.Ordinal) || m.Path.StartsWith("goggame", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("images/unit/a.rle", SourceKind.Sprite, "art/unit/a.t2spr")]
    [InlineData("images/terrain/grass/12.bmp", SourceKind.Bitmap, "art/terrain/grass/12.t2img")]
    [InlineData("help/bmp/x.bmp", SourceKind.Bitmap, "art-extra/help/bmp/x.t2img")]
    [InlineData("data/euro units.txt", SourceKind.Tsv, "tables/data/euro units.txt.json")]
    [InlineData("data/changes.txt", SourceKind.Text, "text/data/changes.txt")]
    [InlineData("local/fonts/small.fnt", SourceKind.Font, "fonts/small.json")]
    [InlineData("campaign/tzar.wcm", SourceKind.Container, null)]
    [InlineData("tzar.exe", SourceKind.Ignored, null)]
    [InlineData("__support/app/tzar.ini", SourceKind.Ignored, null)]
    public void Classifies_sources(string path, SourceKind kind, string? asset)
    {
        Assert.Equal(kind, SourceClassifier.Classify(path));
        Assert.Equal(asset, SourceClassifier.PackagePath(path, kind));
    }

    [Theory]
    [InlineData("images/terrain/grass/00.bmp", ColourKeyPolicy.None)]
    [InlineData("images/terrain/grass/11.bmp", ColourKeyPolicy.None)]
    [InlineData("images/terrain/grass/12.bmp", ColourKeyPolicy.GreenAndMagenta)]
    [InlineData("images/terrain/grass/18_2.bmp", ColourKeyPolicy.GreenAndMagenta)]
    [InlineData("images/terrain/grass/trees.bmp", ColourKeyPolicy.Green)]
    [InlineData("images/screens/title.bmp", ColourKeyPolicy.Green)]
    public void Colour_key_policy_follows_the_spec(string path, ColourKeyPolicy policy) => Assert.Equal(policy, ImageConverter.PolicyFor(path));
}
