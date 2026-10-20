using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using FsCheck.Xunit;
using Tzar2.Content.Packages;
using Tzar2.Content.Vfs;
using Tzar2.Formats;
using Tzar2.Formats.Rle;
using Tzar2.Import.Pipeline;
using Tzar2.Tests.Shared;
using Xunit;

namespace Tzar2.Import.Tests.Pipeline;

/// <summary>
/// The per-file error contract (REQ-IMP-03): whatever a (modded or damaged) install contains, the import finishes,
/// writes a package and reports each bad file; it never aborts.
/// </summary>
public sealed class ImportRobustnessTests : IDisposable
{
    /// <summary>
    /// SHA-256 of the package built from <see cref="SyntheticGame"/> (no edition). Pinned so that the CI jobs on Linux,
    /// macOS and Windows prove cross-OS byte identity (REQ-IMP-02). Update it only for an intentional format change
    /// (2026-10-20: campaigns and maps are kept under <c>legacy/</c>).
    /// </summary>
    private const string SyntheticPackageSha256 = "1b94ab124ac34186e764d90f101b75670a562b4a288380a9396e2b5929c4531f";

    private readonly SyntheticGame _game = new();

    public void Dispose() => _game.Dispose();

    private (PackageWriter Package, ImportReport Report) Build()
        => ImportPipeline.Build(new DirectorySource(_game.Root), null, new ImportOptions { OriginPath = _game.Root, OutputDirectory = "unused" });

    private static byte[] Bytes(PackageWriter package)
    {
        using var ms = new MemoryStream();
        package.WriteTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Synthetic_package_hash_is_pinned_across_operating_systems()
    {
        string actual = Convert.ToHexString(SHA256.HashData(Bytes(Build().Package))).ToLowerInvariant();
        Assert.True(actual == SyntheticPackageSha256, $"synthetic package sha256 is {actual}");
    }

    [Fact]
    public void Cells_larger_than_a_texture_layer_fail_that_file_only()
    {
        // The review's repro: a valid ~50-byte RGB555 sheet 2,100 px wide with pixels at both ends of one row.
        _game.Write("IMAGES/BUILDING/WIDE.RLE", SyntheticFormats.Rle(3, 1, 1, 2100, 4, (c, x, y) => y == 0 && (x == 0 || x == 2099) ? 0x1234 : null));
        (PackageWriter package, ImportReport report) = Build();
        ImportIssue error = Assert.Single(report.Errors);
        Assert.Equal("images/building/wide.rle", error.Source);
        Assert.Contains("2048", error.Message, StringComparison.Ordinal);
        Assert.True(package.Contains("art/unit/euro archer.t2spr"));
        Assert.Throws<LegacyFormatException>(() => SpriteConverter.Convert(RleSheet.Parse(SyntheticFormats.Rle(7, 1, 1, 2100, 1, (c, x, y) => 1)), false, []));
    }

    [Fact]
    public void Copies_of_palettes_and_fonts_in_other_folders_do_not_collide()
    {
        _game.Write("EXTRA/GAME.PAL", SyntheticFormats.Pal(16));
        _game.Write("EXTRA/FONTS/SMALL.FNT", SyntheticFormats.Fnt("Small Fonts", 4, 5, 3, c => (0, 0)));
        _game.Write("LOCAL/FONTS/OLD/SMALL.FNT", SyntheticFormats.Fnt("Small Fonts", 4, 5, 3, c => (0, 0)));
        (PackageWriter package, ImportReport report) = Build();
        Assert.True(report.Succeeded, string.Join("\n", report.Errors));
        foreach (string path in new[]
        {
            "palettes/game.json", "palettes-extra/extra/game.pal.json",
            "fonts/small.json", "fonts-extra/extra/fonts/small.json", "fonts-extra/local/fonts/old/small.json",
        })
        {
            Assert.True(package.Contains(path), path);
        }
    }

    /// <summary>
    /// Fuzz over the whole Process path (decoders, converters, manifests, package assembly): one file of the synthetic
    /// install is mutated per case; the import must finish and the file must be either imported or reported.
    /// </summary>
    [Property(MaxTest = 120)]
    public void A_mutated_file_never_aborts_the_import(int seed)
    {
        var random = new Random(seed);
        string[] files = Directory.GetFiles(_game.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        string victim = files[random.Next(files.Length)];
        byte[] original = File.ReadAllBytes(victim);
        byte[] data = (byte[])original.Clone();
        for (int i = 0; i < 1 + random.Next(6); i++)
        {
            switch (random.Next(4))
            {
                case 0 when data.Length > 0:
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                    break;
                case 1 when data.Length > 0:
                    int at = random.Next(data.Length);
                    for (int k = at; k < Math.Min(data.Length, at + 4); k++)
                    {
                        data[k] = 0xFF;
                    }

                    break;
                case 2:
                    data = data[..random.Next(data.Length + 1)];
                    break;
                default:
                    data = [.. data, .. Enumerable.Range(0, random.Next(40)).Select(_ => (byte)random.Next(256))];
                    break;
            }
        }

        try
        {
            File.WriteAllBytes(victim, data);
            (PackageWriter package, ImportReport report) = Build();
            Assert.True(package.Contains(PackageWriter.ManifestPath));
            Assert.DoesNotContain(report.Errors, e => e.Message.StartsWith("internal error", StringComparison.Ordinal));
            Assert.True(Bytes(package).Length > 0);
        }
        finally
        {
            File.WriteAllBytes(victim, original);
        }
    }
}
