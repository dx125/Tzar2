using System;
using System.IO;
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
    [InlineData("M1", "import")]
    [InlineData("M4", "convert", "map.wmp")]
    [InlineData("M3", "validate")]
    [InlineData("M1", "pack")]
    [InlineData("M6", "replay", "verify", "a.t2rep")]
    [InlineData("M6", "desync", "diff", "a.snap", "b.snap")]
    [InlineData("M2", "bench")]
    [InlineData("M3", "fidelity", "capture")]
    [InlineData("M1", "dump", "file.rle")]
    public void Planned_commands_report_not_implemented(string milestone, params string[] args)
    {
        (int code, _, string error) = Run(args);
        Assert.Equal(ExitCodes.NotImplemented, code);
        Assert.Contains($"not implemented ({milestone})", error, StringComparison.Ordinal);
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
