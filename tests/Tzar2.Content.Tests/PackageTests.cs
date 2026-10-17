using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using Tzar2.Content.Packages;
using Tzar2.Content.Vfs;
using Xunit;

namespace Tzar2.Content.Tests;

public class ContentPathTests
{
    [Theory]
    [InlineData("IMAGES\\Unit\\Euro Archer.RLE", "images/unit/euro archer.rle")]
    [InlineData("/a//b/", "a/b")]
    public void Normalizes(string input, string expected) => Assert.Equal(expected, ContentPath.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("a/../b")]
    [InlineData("./a")]
    [InlineData("a\u0001b")]
    public void Rejects_bad_paths(string input) => Assert.Throws<ArgumentException>(() => ContentPath.Normalize(input));

    [Theory]
    [InlineData("Cafe\u0301/E\u0300TE\u0301.ini", "café/èté.ini")]       // macOS NFD -> NFC
    [InlineData("\u0418\u0306/\u0435\u0308.txt", "й/ё.txt")]          // Cyrillic (CP1251)
    [InlineData("Z\u030C/a\u0328.txt", "ž/ą.txt")]                      // CP1250
    [InlineData("caf\u00E9", "café")]                                   // already NFC
    public void Composes_decomposed_letters(string input, string expected)
        => Assert.Equal(expected, ContentPath.Normalize(input));

    [Fact]
    public void Changes_extensions() => Assert.Equal("a.b/c.t2spr", ContentPath.ChangeExtension("a.b/c.rle", ".t2spr"));
}

public class PackageTests
{
    private static PackageWriter Sample(bool reversed = false)
    {
        var writer = new PackageWriter();
        var entries = new (string, byte[])[]
        {
            ("art/unit/a.t2spr", [1, 2, 3]),
            ("tables/data/images.ini.json", "{}"u8.ToArray()),
            ("empty", []),
            ("sound/ü.wav", Enumerable.Range(0, 70_000).Select(i => (byte)i).ToArray()),
        };
        foreach ((string path, byte[] data) in reversed ? Enumerable.Reverse(entries) : entries)
        {
            writer.Add(path, data);
        }

        writer.AddText(PackageWriter.ManifestPath, new PackageManifest { Id = "base.original", Name = "Test", Version = "1.10.0" }.ToYaml());
        return writer;
    }

    private static byte[] Bytes(PackageWriter writer)
    {
        using var ms = new MemoryStream();
        writer.WriteTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Crc32_matches_the_standard_check_value()
    {
        Assert.Equal(0xCBF43926u, Crc32Checksum.Compute("123456789"u8));
        Assert.Equal(0u, Crc32Checksum.Compute([]));
        uint running = Crc32Checksum.Append(Crc32Checksum.Append(Crc32Checksum.Start, "1234"u8), "56789"u8);
        Assert.Equal(0xCBF43926u, Crc32Checksum.Finish(running));
    }

    [Fact]
    public void Output_is_independent_of_insertion_order()
        => Assert.Equal(SHA256.HashData(Bytes(Sample())), SHA256.HashData(Bytes(Sample(reversed: true))));

    [Fact]
    public void Round_trips_through_our_reader_and_the_bcl_zip_reader()
    {
        byte[] bytes = Bytes(Sample());
        using (PackageReader reader = PackageReader.Open(new MemoryStream(bytes), "test"))
        {
            Assert.Equal(["art/unit/a.t2spr", "empty", "package.yaml", "sound/ü.wav", "tables/data/images.ini.json"], reader.Entries.Select(e => e.Path));
            Assert.Equal(new byte[] { 1, 2, 3 }, reader.ReadAllBytes("art/unit/a.t2spr"));
            Assert.Equal(70_000, reader.ReadAllBytes("sound/ü.wav").Length);
            Assert.Empty(reader.ReadAllBytes("empty"));
            Assert.Throws<FileNotFoundException>(() => reader.ReadAllBytes("nope"));
        }

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Equal(5, zip.Entries.Count);
        using var stream = zip.GetEntry("sound/ü.wav")!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        Assert.Equal(70_000, copy.Length);
    }

    [Fact]
    public void Reads_deflated_entries_written_by_other_tools()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream s = zip.CreateEntry("mod/readme.txt", CompressionLevel.Optimal).Open();
            s.Write(new byte[5000]);
        }

        using PackageReader reader = PackageReader.Open(new MemoryStream(ms.ToArray()), "mod");
        Assert.Equal(5000, reader.ReadAllBytes("mod/readme.txt").Length);
    }

    [Fact]
    public void Detects_corruption()
    {
        byte[] bytes = Bytes(Sample());
        int at = bytes.AsSpan().IndexOf("{}"u8);
        bytes[at] = (byte)'[';
        using PackageReader reader = PackageReader.Open(new MemoryStream(bytes), "bad");
        Assert.Throws<InvalidDataException>(() => reader.ReadAllBytes("tables/data/images.ini.json"));
        Assert.Throws<InvalidDataException>(() => PackageReader.Open(new MemoryStream(new byte[100]), "zero"));
    }

    [Fact]
    public void Rejects_unnormalised_and_duplicate_paths()
    {
        var writer = new PackageWriter();
        Assert.Throws<ArgumentException>(() => writer.Add("Art/X", []));
        writer.Add("a", []);
        Assert.Throws<ArgumentException>(() => writer.Add("a", []));
    }

    [Fact]
    public void Manifest_round_trips()
    {
        var manifest = new PackageManifest
        {
            Id = "base.original",
            Name = "Imported \"original\" content",
            Version = "1.10.0",
            Kind = PackageKind.AssetPack,
            DependsOn = ["engine.defaults"],
            Provides = ["art", "sound"],
            GameplayHash = "sha256:00ff",
            SourceEdition = "gog-1.10-en",
        };
        string yaml = manifest.ToYaml();
        Assert.Contains("kind: asset-pack\n", yaml, StringComparison.Ordinal);
        PackageManifest parsed = PackageManifest.Parse(yaml);
        Assert.Equal(manifest.ToYaml(), parsed.ToYaml());
        Assert.Equal(["art", "sound"], parsed.Provides);
        Assert.Throws<InvalidDataException>(() => PackageManifest.Parse("name: x\nversion: 1\n"));
        Assert.Throws<InvalidDataException>(() => PackageManifest.Parse("id: a\nid: b\nname: x\nversion: 1\n"));
    }

    [Fact]
    public void List_items_with_commas_round_trip()
    {
        var manifest = new PackageManifest { Id = "m", Name = "M", Version = "1.0.0", Provides = ["a, b", "c\"d", "e"] };
        Assert.Equal(["a, b", "c\"d", "e"], PackageManifest.Parse(manifest.ToYaml()).Provides);
    }

    [Fact]
    public void Deflate_bombs_are_rejected_before_allocation()
    {
        var writer = new PackageWriter();
        writer.Add("x", new byte[10]);
        using var ms = new MemoryStream();
        writer.WriteTo(ms);
        byte[] bytes = ms.ToArray();
        int central = bytes.AsSpan().IndexOf(new byte[] { 0x50, 0x4B, 0x01, 0x02 });
        bytes[central + 10] = 8;                                        // method = deflate
        BitConverter.TryWriteBytes(bytes.AsSpan(central + 24), 1u << 30); // uncompressed size = 1 GiB
        Assert.Throws<InvalidDataException>(() => PackageReader.Open(new MemoryStream(bytes), "bomb"));
    }
}

public class VirtualFileSystemTests
{
    [Fact]
    public void Later_mounts_override_and_lookups_ignore_case()
    {
        string root = Path.Combine(Path.GetTempPath(), "tzar2-vfs-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "base", "DATA"));
            Directory.CreateDirectory(Path.Combine(root, "mod", "data"));
            File.WriteAllText(Path.Combine(root, "base", "DATA", "RES.INI"), "base");
            File.WriteAllText(Path.Combine(root, "base", "DATA", "BONUS.INI"), "bonus");
            File.WriteAllText(Path.Combine(root, "mod", "data", "res.ini"), "mod");

            var vfs = new VirtualFileSystem();
            var baseSource = new DirectorySource(Path.Combine(root, "base"));
            vfs.Mount(baseSource);
            Assert.Equal("base", System.Text.Encoding.ASCII.GetString(vfs.ReadAllBytes("data\\res.ini")));
            vfs.Mount(new DirectorySource(Path.Combine(root, "mod")));
            Assert.Equal("mod", System.Text.Encoding.ASCII.GetString(vfs.ReadAllBytes("DATA/RES.INI")));
            Assert.Equal(["data/bonus.ini", "data/res.ini"], vfs.Enumerate("Data"));
            Assert.Equal("DATA/BONUS.INI", baseSource.GetRelativePath("data/bonus.ini"));
            Assert.False(vfs.Exists("data/none.ini"));
            Assert.Throws<FileNotFoundException>(() => vfs.ReadAllBytes("data/none.ini"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Packages_mount_like_folders()
    {
        var writer = new PackageWriter();
        writer.Add("data/res.ini", [42]);
        writer.AddText(PackageWriter.ManifestPath, new PackageManifest { Id = "m", Name = "M", Version = "1.0.0", Kind = PackageKind.Mod }.ToYaml());
        using var ms = new MemoryStream();
        writer.WriteTo(ms);
        using var source = new PackageSource(PackageReader.Open(new MemoryStream(ms.ToArray()), "m.t2pkg"));
        var vfs = new VirtualFileSystem();
        vfs.Mount(source);
        Assert.Equal(new byte[] { 42 }, vfs.ReadAllBytes("DATA/RES.INI"));
        Assert.Equal(PackageKind.Mod, source.ReadManifest()!.Kind);
        Assert.Same(source, vfs.Resolve("data/res.ini"));
        Assert.Empty(source.SkippedEntries);
    }

    [Fact]
    public void Non_normalised_entries_are_reported_not_silently_dropped()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("Art/Unit/X.t2spr");
            zip.CreateEntry("art/ok.t2spr");
        }

        using var source = new PackageSource(PackageReader.Open(new MemoryStream(ms.ToArray()), "mod.t2pkg"));
        Assert.Equal(["art/ok.t2spr"], source.Paths);
        Assert.Equal(["Art/Unit/X.t2spr"], source.SkippedEntries);
    }
}
