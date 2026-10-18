using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Tzar2.Tools.Cli.Tests;

public sealed class MapGenCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "t2-mapgen-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliApp.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Mapgen_writes_a_dump_a_summary_and_a_preview()
    {
        (int code, string output, string error) = Run("mapgen", "--type", "island", "--set", "desert", "--size", "tiny", "--seed", "0x2A", "--players", "3",
            "--teams", "0,0,1", "--civs", "euro,random,asia", "--resources", "high", "--features", "all", "--out", _dir);
        Assert.True(code == ExitCodes.Success, error);
        Assert.Contains("random-island-desert-tiny-000000000000002A", output, StringComparison.Ordinal);
        string[] files = Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        Assert.Equal(["random-island-desert-tiny-000000000000002A.json", "random-island-desert-tiny-000000000000002A.png", "random-island-desert-tiny-000000000000002A.t2mapgen"], files);
        byte[] png = File.ReadAllBytes(Path.Combine(_dir, files[1]));
        Assert.Equal(0x89, png[0]);
        Assert.StartsWith("T2MG", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(_dir, files[2]))[..4]), StringComparison.Ordinal);
        Assert.Contains("\"type\": \"island\"", File.ReadAllText(Path.Combine(_dir, files[0])), StringComparison.Ordinal);

        // Same options, same hash.
        (_, string again, _) = Run("mapgen", "--type", "island", "--set", "desert", "--size", "tiny", "--seed", "42", "--players", "3",
            "--teams", "0,0,1", "--civs", "euro,random,asia", "--resources", "high", "--features", "all");
        Assert.Equal(Hash(output), Hash(again));
    }

    [Theory]
    [InlineData("--type", "swamp")]
    [InlineData("--size", "colossal")]
    [InlineData("--players", "9")]
    [InlineData("--features", "dragons")]
    [InlineData("--resources", "plenty")]
    public void Mapgen_rejects_bad_options(string option, string value)
    {
        (int code, _, string error) = Run("mapgen", option, value);
        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Contains("invalid value", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Mapgen_reports_inconsistent_options()
    {
        (int code, _, string error) = Run("mapgen", "--size", "tiny", "--players", "6");
        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Contains("1…4 players", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Mapgen_reports_an_unusable_output_path()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "a-file");
        File.WriteAllText(file, "x");
        (int code, _, string error) = Run("mapgen", "--size", "tiny", "--out", file);
        Assert.Equal(ExitCodes.UsageError, code);
        Assert.StartsWith("t2 mapgen: cannot use", error, StringComparison.Ordinal);

        (code, _, error) = Run("mapgen", "--size", "tiny", "--out", Path.Combine(file, "below"));
        Assert.Equal(ExitCodes.UsageError, code);
        Assert.StartsWith("t2 mapgen: cannot use", error, StringComparison.Ordinal);

        (code, _, error) = Run("mapgen", "--size", "real-world", "--scale", "16", "--out", _dir);
        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Contains("wider than 4096", error, StringComparison.Ordinal);
    }

    private static string Hash(string output) => output.Split(' ').Single(w => w.StartsWith("hash=", StringComparison.Ordinal));
}
