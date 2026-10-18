using System;
using System.Collections.Generic;
using System.IO;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Game.Campaigns;
using Tzar2.Import.Maps;

namespace Tzar2.Tools.Cli.Commands;

/// <summary>
/// Legacy <c>.wcm</c> campaigns and <c>.wmp</c> maps (the user's own files, never in the repository) converted on the fly
/// by the converter of stream A (ADR 0120) into campaign sources for the headless runner (ADR 0133). A single map
/// becomes a one-map campaign.
/// </summary>
internal static class LegacyCampaigns
{
    /// <summary>The campaigns in a file or folder (sorted by path), with the conversion problems as messages.</summary>
    public static List<ICampaignSource> Load(string path, Ruleset ruleset, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        var options = new LegacyConversionOptions { Ruleset = ruleset, ConvertImages = false };
        var files = new List<string>();
        if (Directory.Exists(path))
        {
            files.AddRange(LegacyCorpus.Find(path));
            files.Sort(StringComparer.Ordinal);
        }
        else if (File.Exists(path))
        {
            files.Add(path);
        }

        var sources = new List<ICampaignSource>();
        foreach (string file in files)
        {
            (MapConversion? map, CampaignConversion? campaign) = LegacyCorpus.ConvertFile(file, options);
            if (campaign is { Campaign: { } data } && campaign.Succeeded)
            {
                var maps = new Dictionary<string, MissionContent>(StringComparer.Ordinal);
                for (int i = 0; i < data.Maps.Length && i < campaign.Maps.Count; i++)
                {
                    if (campaign.Maps[i] is { Map: { } m, Scenario: { } s })
                    {
                        maps[data.Maps[i].Id] = new MissionContent(m, s);
                    }
                    else
                    {
                        problems.Add($"{Path.GetFileName(file)}: map {data.Maps[i].Id} did not convert.");
                    }
                }

                sources.Add(new InMemoryCampaignSource(data, maps));
            }
            else if (map is { Map: { } m, Scenario: { } s } && map.Succeeded)
            {
                string id = map.MapName.ToLowerInvariant();
                var single = new CampaignData(id, map.MapName, s.Properties.Description, new[] { new CampaignMap(id, map.MapName, Path.GetFileName(file), s.Properties.Description, IsEntry: true) }, EquatableArray<CampaignEdge>.Empty);
                sources.Add(new InMemoryCampaignSource(single, new Dictionary<string, MissionContent>(StringComparer.Ordinal) { [id] = new MissionContent(m, s) }));
            }
            else
            {
                problems.Add($"{Path.GetFileName(file)}: conversion failed ({(campaign?.Report ?? map?.Report)?.ToString() ?? "unknown error"}).");
            }
        }

        return sources;
    }
}
