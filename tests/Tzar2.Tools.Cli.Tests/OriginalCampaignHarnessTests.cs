using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Vfs;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Session;
using Tzar2.Tools.Cli.Commands;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Tools.Cli.Tests;

/// <summary>Runs only with a local original install (<c>TZAR2_ORIGIN</c>) and its imported base package (<c>TZAR2_PACKAGE</c>).</summary>
public sealed class OriginalCampaignFactAttribute : FactAttribute
{
    public OriginalCampaignFactAttribute()
    {
        if (OriginalCampaignHarnessTests.Origin is null || OriginalCampaignHarnessTests.Package is null)
        {
            Skip = "Set TZAR2_ORIGIN to an original install and TZAR2_PACKAGE to its imported base package to play the original campaigns.";
        }
    }
}

/// <summary>
/// The M4 exit criterion's harness (TDD §25 "all original campaign missions completable"; ADR 0133): every campaign of
/// the local install (<c>Campaign/*.wcm</c>, converted on the fly, never stored) is played headless by the scripted
/// solver; the per-mission table goes to the test output.
/// </summary>
public sealed class OriginalCampaignHarnessTests(ITestOutputHelper output)
{
    public static string? Origin { get; } = Environment.GetEnvironmentVariable("TZAR2_ORIGIN") is { Length: > 0 } o && Directory.Exists(o) ? o : null;

    public static string? Package { get; } = Environment.GetEnvironmentVariable("TZAR2_PACKAGE") is { Length: > 0 } p && File.Exists(p) ? p : null;

    [OriginalCampaignFact]
    public void Every_original_campaign_mission_is_played_and_reported()
    {
        using PackageSource package = PackageSource.Open(Package!);
        Ruleset ruleset = RulesetLoader.Compile(package).Ruleset;
        var problems = new List<string>();
        List<ICampaignSource> campaigns = LegacyCampaigns.Load(Path.Combine(Origin!, "Campaign"), ruleset, problems);
        Assert.Empty(problems);
        Assert.NotEmpty(campaigns);
        var options = new AutoplayOptions { Ai = SkirmishAi.Resolve(package) };
        int missions = 0;
        int completed = 0;
        foreach (ICampaignSource campaign in campaigns)
        {
            CampaignReport report = CampaignAutoplay.Run(campaign, ruleset, options, line => output.WriteLine($"{campaign.Campaign.Id} {line}"));
            output.WriteLine($"{campaign.Campaign.Id}: {(report.Completed ? "COMPLETED" : "NOT COMPLETED")} {report.Error}");
            Assert.DoesNotContain(report.Missions, m => m.Assist == "error");
            missions += report.Missions.Length;
            completed += report.Missions.Count(m => m.Completed);
        }

        output.WriteLine($"{completed} of {missions} missions played were completed.");
    }
}
