using System;
using System.IO;
using System.Text;
using Tzar2.Tests.Shared;
using Xunit;

namespace Tzar2.Tools.Cli.Tests;

/// <summary><c>t2 convert</c> (REQ-EDT-04) on synthetic maps.</summary>
public sealed class ConvertCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "t2-convert-" + Guid.NewGuid().ToString("N"));

    public ConvertCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliApp.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static byte[] Map(string name)
    {
        var m = new SyntheticMap { Name = name };
        m.Objects.Add(new SyntheticObject { Id = 1 });
        m.Actions.Add(("say", 13, new SyntheticPayload().Str("hello").U16(1).ToArray()));
        m.Rules.Add(("r", "1", ["say"]));
        return m.Build();
    }

    [Fact]
    public void Converts_one_map_with_reports()
    {
        string src = Path.Combine(_dir, "one.wmp");
        File.WriteAllBytes(src, Map("One"));
        string json = Path.Combine(_dir, "report.json");
        (int code, string output, string error) = Run("convert", src, "--out", Path.Combine(_dir, "out"), "--json", json);
        Assert.True(code == 0, error + output);
        Assert.True(File.Exists(Path.Combine(_dir, "out", "one.t2map")));
        Assert.True(File.Exists(Path.Combine(_dir, "out", "one.t2map.report.txt")));
        Assert.Contains("\"actions\": 1", File.ReadAllText(json, Encoding.UTF8), StringComparison.Ordinal);
        Assert.Contains("map one.wmp: OK", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Folder_mode_prints_the_pass_rate_and_fails_on_errors()
    {
        File.WriteAllBytes(Path.Combine(_dir, "a.wmp"), Map("A"));
        File.WriteAllBytes(Path.Combine(_dir, "c.wcm"), SyntheticMap.Campaign("C", "D", [("A", true, Map("A"))]));
        (int code, string output, _) = Run("convert", _dir, "-q");
        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("2/2 files convert (100.0 %)", output, StringComparison.Ordinal);

        File.WriteAllBytes(Path.Combine(_dir, "bad.wmp"), [1, 2, 3]);
        (code, output, _) = Run("convert", "--corpus", _dir, "-q", "--json", Path.Combine(_dir, "corpus.json"));
        Assert.Equal(ExitCodes.Mismatch, code);
        Assert.Contains("2/3 files convert", output, StringComparison.Ordinal);
        Assert.Contains("\"passPermille\":666", File.ReadAllText(Path.Combine(_dir, "corpus.json"), Encoding.UTF8), StringComparison.Ordinal);
    }

    [Fact]
    public void Bad_arguments_are_usage_errors()
    {
        Assert.Equal(ExitCodes.UsageError, Run("convert").Code);
        Assert.Equal(ExitCodes.UsageError, Run("convert", Path.Combine(_dir, "missing.wmp")).Code);
        Assert.Equal(ExitCodes.UsageError, Run("convert", _dir, "--codepage", "437").Code);
        Assert.Equal(ExitCodes.Success, Run("convert", "--help").Code);
    }
}
