using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Maps;
using Tzar2.Content.Packages;
using Tzar2.Import.Maps;
using Tzar2.Tests.Shared;
using Xunit;

namespace Tzar2.Import.Tests.Maps;

/// <summary>
/// The M4 exit criterion on the reference build: every original map and campaign converts with zero errors, the
/// counts match the decoded source, and conversion is deterministic. A community corpus is run when
/// <c>TZAR2_CORPUS</c> is set (pass rate ≥ 90 %). Skipped without the corresponding variable.
/// </summary>
public class OriginConversionTests
{
    [OriginFact]
    public void Every_original_map_and_campaign_converts_without_errors()
    {
        string root = OriginPaths.RequireOrigin();
        IReadOnlyList<CorpusEntry> entries = LegacyCorpus.Run(root);
        Assert.Equal(9, entries.Count);
        Assert.True(entries.All(e => e.Succeeded), LegacyCorpus.Summary(entries));
        Assert.Equal(33, entries.Sum(e => e.Maps));
    }

    [OriginFact]
    public void Campaign_conversion_is_deterministic_and_complete()
    {
        string file = Directory.EnumerateFiles(OriginPaths.RequireOrigin(), "*.wcm", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First();
        byte[] data = File.ReadAllBytes(file);
        CampaignConversion a = LegacyCampaignConverter.Convert(data, Path.GetFileName(file));
        CampaignConversion b = LegacyCampaignConverter.Convert(data, Path.GetFileName(file));
        Assert.True(a.Succeeded, a.Report.ToText());
        Assert.Equal(LegacyPackageWriter.WriteCampaign(a), LegacyPackageWriter.WriteCampaign(b));
        CampaignData campaign = a.Campaign!;
        Assert.Single(campaign.Maps, m => m.IsEntry);
        Assert.DoesNotContain(a.Report.Diagnostics, d => d.Code == "campaign.unreachable");
        Assert.All(campaign.Edges, e => Assert.NotNull(e.ToMapId));
        foreach (MapConversion map in a.Maps)
        {
            Assert.Empty(map.Placeholders);
            Assert.Equal(map.Map!.ContentHash, MapBinary.Read(MapBinary.Write(map.Map)).ContentHash);
            Assert.Equal(map.Scenario!.ContentHash, ScenarioJson.Read(ScenarioJson.Write(map.Scenario)).ContentHash);
            Assert.DoesNotContain(map.Scenario.Actions, x => x is UnknownAction);
            Assert.DoesNotContain(map.Scenario.Events, x => x is UnknownEvent);
        }
    }

    [CorpusFact]
    public void Community_corpus_converts_at_least_90_percent()
    {
        IReadOnlyList<CorpusEntry> entries = LegacyCorpus.Run(CorpusFactAttribute.Root!);
        Assert.True(LegacyCorpus.PassPermille(entries) >= 900, LegacyCorpus.Summary(entries));
    }
}

/// <summary>A fact that needs a community corpus folder (<c>TZAR2_CORPUS</c>); skipped when it is unset.</summary>
internal sealed class CorpusFactAttribute : FactAttribute
{
    public CorpusFactAttribute()
    {
        if (Root is null)
        {
            Skip = "Needs a community map corpus: set " + LegacyCorpus.EnvironmentVariable + " to a folder of .wmp/.wcm files.";
        }
    }

    /// <summary>The corpus folder, or null.</summary>
    public static string? Root
    {
        get
        {
            string? root = Environment.GetEnvironmentVariable(LegacyCorpus.EnvironmentVariable);
            return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root : null;
        }
    }
}
