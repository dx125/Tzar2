using System;
using System.IO;
using System.Linq;
using Tzar2.Tests.Shared;
using Tzar2.Tools.Cli.Commands;
using Xunit;

namespace Tzar2.Tools.Cli.Tests;

public sealed class ImportDumpCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tzar2-cli-" + Guid.NewGuid().ToString("N"));

    public ImportDumpCommandTests()
    {
        Write("origin/IMAGES/GAME.PAL", SyntheticFormats.Pal());
        Write("origin/IMAGES/UNIT/EURO SPY.RLE", SyntheticFormats.Rle(1, 2, 3, 10, 8, (c, x, y) => x > c ? 240 + (x % 16) : null));
        Write("origin/IMAGES/ICONS/ICONS.BMP", SyntheticFormats.Bmp(2, 1, 16, [0xE0, 0x03, 0x00, 0x7C]));
        Write("origin/LOCAL/FONTS/SMALL.FNT", SyntheticFormats.Fnt("Small Fonts", 4, 5, 2, c => c == 65 ? (3, 1) : (0, 0)));
        Write("origin/DATA/RES.INI", "[res]\r\ngold = 1\r\n"u8.ToArray());
        Write("origin/Maps/a.wmp", SyntheticFormats.Hpfs([("MAPS/a.header", new byte[4])]));
    }

    private string Origin => Path.Combine(_root, "origin");

    private string Output => Path.Combine(_root, "out");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, byte[] data)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliApp.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Import_writes_package_and_report_and_dump_exports_png()
    {
        (int code, string output, string error) = Run("import", Origin, Output, "--quiet");
        Assert.True(code == ExitCodes.Success, output + error);
        Assert.Contains("Result: OK", output, StringComparison.Ordinal);
        string package = Path.Combine(Output, "base.original.t2pkg");
        Assert.True(File.Exists(package));
        Assert.True(File.Exists(Path.Combine(Output, "import-report.json")));

        (code, output, _) = Run("dump", package);
        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("art/unit/euro spy.t2spr", output, StringComparison.Ordinal);
        Assert.Contains("id: base.original", output, StringComparison.Ordinal);

        string pngs = Path.Combine(_root, "png");
        (code, output, _) = Run("dump", package, "art/*", "fonts/small.t2img", "--out", pngs, "--player", "2");
        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("indexed8, 2 dirs × 3 frames", output, StringComparison.Ordinal);
        string[] files = Directory.GetFiles(pngs).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        Assert.Equal(["art_icons_icons.t2img.png", "art_unit_euro spy.t2spr.png", "fonts_small.t2img.png"], files);
        byte[] png = File.ReadAllBytes(Path.Combine(pngs, "art_unit_euro spy.t2spr.png"));
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
        Assert.Equal(30, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]); // 3 frames × 10 px wide

        Assert.Equal(ExitCodes.Mismatch, Run("dump", package, "nothing/here").Code);
    }

    [Fact]
    public void Dump_describes_legacy_files()
    {
        (int code, string output, _) = Run("dump", Path.Combine(Origin, "IMAGES", "UNIT", "EURO SPY.RLE"), "--out", Path.Combine(_root, "legacy"));
        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("RLE kind 1", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "legacy", "EURO SPY.png")));
        Assert.Contains("HPFS", Run("dump", Path.Combine(Origin, "Maps", "a.wmp")).Out, StringComparison.Ordinal);
        Assert.Contains("FNT 'Small Fonts'", Run("dump", Path.Combine(Origin, "LOCAL", "FONTS", "SMALL.FNT")).Out, StringComparison.Ordinal);
        Assert.Contains("PAL 256 entries", Run("dump", Path.Combine(Origin, "IMAGES", "GAME.PAL")).Out, StringComparison.Ordinal);

        Write("bad.rle", [1, 2, 3]);
        (code, output, _) = Run("dump", Path.Combine(_root, "bad.rle"));
        Assert.Equal(ExitCodes.Mismatch, code);
        Assert.Contains("malformed", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("import")]
    [InlineData("import", "only-one")]
    [InlineData("import", "a", "b", "--codepage", "866")]
    [InlineData("import", "a", "b", "--bogus")]
    [InlineData("import", "a", "b", "--id", "../x")]
    [InlineData("import", "a", "b", "--id", "a/b")]
    [InlineData("dump")]
    [InlineData("dump", "x.t2pkg", "--player", "9")]
    public void Bad_usage_fails(params string[] args) => Assert.Equal(ExitCodes.UsageError, Run(args).Code);

    [Fact]
    public void Import_refuses_to_write_into_the_install()
    {
        Assert.Equal(ExitCodes.UsageError, Run("import", Origin, Path.Combine(Origin, "out")).Code);
        // Case-insensitive file systems (Windows, macOS): a differently cased spelling is still inside the install.
        Assert.Equal(ExitCodes.UsageError, Run("import", Origin, Path.Combine(_root, "ORIGIN", "out")).Code);
    }

    [Theory]
    [InlineData("art/unit/a.t2spr", "art_unit_a.t2spr")]
    [InlineData("..\\..\\x", "_.._x")]
    [InlineData("c:x", "c_x")]
    public void Dump_file_names_cannot_escape_the_output_folder(string entry, string expected)
    {
        string name = DumpCommand.FlatName(entry);
        Assert.Equal(expected, name);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, name);
        Assert.DoesNotContain(':', name);
    }

    [Theory]
    [InlineData("base.original", true)]
    [InlineData("mod-1_2", true)]
    [InlineData(".hidden", false)]
    [InlineData("a..b", false)]
    [InlineData("a b", false)]
    public void Package_ids_are_validated(string id, bool valid) => Assert.Equal(valid, ImportCommand.IsValidPackageId(id));

    [Fact]
    public void Help_mentions_import_and_dump()
    {
        Assert.Contains("--codepage", Run("import", "--help").Out, StringComparison.Ordinal);
        Assert.Contains("--player", Run("dump", "--help").Out, StringComparison.Ordinal);
    }
}
