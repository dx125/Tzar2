using System;
using System.IO;
using Xunit;

namespace Tzar2.Tools.Cli.Tests;

/// <summary><c>t2 ai check</c> and <c>t2 bench --scenario ai</c>.</summary>
public sealed class AiCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "t2-ai-" + Guid.NewGuid().ToString("N"));

    public AiCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliApp.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Check_reports_located_errors_and_compiles_strategy_scripts()
    {
        File.WriteAllText(Path.Combine(_dir, "MAIN.AIS"), "include \"part.ai\"\nrule when castle have 2 houses end\n");
        File.WriteAllText(Path.Combine(_dir, "Part.AI"), "rule have farm end\n");
        File.WriteAllText(Path.Combine(_dir, "AI.INI"), "[scripts]\nMain = Main.ais\n");
        (int code, string output, string error) = Run("ai", "check", _dir, "--bind");
        Assert.True(code == 0, output + error);
        Assert.Contains("compiled   main.ais: 2 rules from 2 files", output, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(_dir, "BAD.AI"), "rule when castle have\n");
        (code, output, _) = Run("ai", "check", _dir);
        Assert.Equal(ExitCodes.Mismatch, code);
        Assert.Contains("bad.ai(1,22): error AI1008", output, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.UsageError, Run("ai", "check").Code);
        Assert.Equal(ExitCodes.UsageError, Run("ai", "frobnicate").Code);
    }

    [Fact]
    public void Bench_runs_the_ai_scenario_deterministically()
    {
        string[] small = ["bench", "--scenario", "ai", "--map", "64", "--players", "2", "--ticks", "600", "--warmup", "0", "--workers", "2"];
        (int code, string output, string error) = Run(small);
        Assert.True(code == ExitCodes.Success, error);
        Assert.Contains("p99", output, StringComparison.Ordinal);
        string hash = Array.Find(output.Split('\n'), l => l.StartsWith("final hash ", StringComparison.Ordinal))!["final hash ".Length..].Trim();
        (code, _, error) = Run("bench", "--scenario", "ai", "--map", "64", "--players", "2", "--ticks", "20", "--warmup", "0");
        Assert.Equal(ExitCodes.Mismatch, code); // nothing built yet after one second
        Assert.Contains("did not gather, build, train", error, StringComparison.Ordinal);
        (_, string again, _) = Run([.. small, "--workers", "1"]);
        Assert.Contains("final hash " + hash, again, StringComparison.Ordinal);
    }
}
