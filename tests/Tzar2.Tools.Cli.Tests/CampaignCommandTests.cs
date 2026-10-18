using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Tzar2.Tools.Cli.Tests;

/// <summary><c>t2 campaign run</c> (ADR 0133) on the built-in demo campaign.</summary>
public class CampaignCommandTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int code = CliApp.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void The_demo_campaign_completes_headless_and_writes_a_report()
    {
        string report = Path.Combine(Path.GetTempPath(), "t2-campaign-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            (int code, string output, _) = Run("campaign", "run", "--demo", "--report", report);
            Assert.Equal(ExitCodes.Success, code);
            Assert.Contains("=> COMPLETED", output, StringComparison.Ordinal);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(report));
            JsonElement campaign = doc.RootElement[0];
            Assert.True(campaign.GetProperty("completed").GetBoolean());
            Assert.Equal(3, campaign.GetProperty("missions").GetArrayLength());
        }
        finally
        {
            File.Delete(report);
        }
    }

    [Fact]
    public void Usage_errors()
    {
        Assert.Equal(ExitCodes.UsageError, Run("campaign").Code);
        Assert.Equal(ExitCodes.UsageError, Run("campaign", "run").Code);
        Assert.Equal(ExitCodes.UsageError, Run("campaign", "run", "--bogus").Code);
        Assert.Equal(ExitCodes.Success, Run("campaign", "--help").Code);
    }
}
