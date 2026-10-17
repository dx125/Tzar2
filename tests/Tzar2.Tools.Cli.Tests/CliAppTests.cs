using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Tzar2.Tools.Cli.Tests;

public class CliAppTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliApp.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Theory]
    [InlineData("M4", "convert", "map.wmp")]
    [InlineData("M3", "validate")]
    [InlineData("M1", "pack")]
    [InlineData("M6", "replay", "verify", "a.t2rep")]
    [InlineData("M6", "desync", "diff", "a.snap", "b.snap")]
    public void Planned_commands_report_not_implemented(string milestone, params string[] args)
    {
        (int code, _, string error) = Run(args);
        Assert.Equal(ExitCodes.NotImplemented, code);
        Assert.Contains($"not implemented ({milestone})", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Bench_runs_a_small_scenario_and_checks_the_expected_hash()
    {
        string[] small = ["bench", "--entities", "200", "--map", "32", "--players", "2", "--ticks", "20", "--warmup", "5", "--workers", "2"];
        (int code, string output, string error) = Run(small);
        Assert.True(code == ExitCodes.Success, error);
        string hash = output.Split('\n').Single(l => l.StartsWith("final hash ", StringComparison.Ordinal))["final hash ".Length..].Trim();
        Assert.Contains("p99", output, StringComparison.Ordinal);

        (code, _, _) = Run([.. small, "--workers", "1", "--expect-hash", hash]);
        Assert.Equal(ExitCodes.Success, code);
        (code, _, error) = Run([.. small, "--expect-hash", "0123456789ABCDEF"]);
        Assert.Equal(ExitCodes.Mismatch, code);
        Assert.Contains("determinism failure", error, StringComparison.Ordinal);
        (code, _, _) = Run("bench", "--map", "2000");
        Assert.Equal(ExitCodes.UsageError, code);
    }

    [Fact]
    public void Help_lists_all_commands()
    {
        (int code, string output, _) = Run("--help");
        Assert.Equal(ExitCodes.Success, code);
        foreach (string command in new[] { "verify-original", "import", "convert", "validate", "pack", "replay verify", "desync diff", "bench", "fidelity capture", "dump" })
        {
            Assert.Contains(command, output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_arguments_prints_usage_to_stderr_and_fails()
    {
        (int code, string output, string error) = Run();
        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Empty(output);
        Assert.Contains("Usage: t2 <command>", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_prints_usage_to_stdout()
    {
        (_, string output, string error) = Run("--help");
        Assert.Contains("Usage: t2 <command>", output, StringComparison.Ordinal);
        Assert.Empty(error);
    }

    [Fact]
    public void Unknown_command_fails()
    {
        Assert.Equal(ExitCodes.UsageError, Run("frobnicate").Code);
        Assert.Equal(ExitCodes.UsageError, Run("replay").Code);
    }
}
