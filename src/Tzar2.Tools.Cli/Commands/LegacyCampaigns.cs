using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
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
            if (LegacyPlayables.Convert(File.ReadAllBytes(file), Path.GetFileName(file), options, problems) is { } playable)
            {
                sources.Add(Source(playable));
            }
        }

        return sources;
    }

    /// <summary>A campaign source over a converted legacy campaign or map.</summary>
    public static ICampaignSource Source(PlayableLegacy playable)
    {
        ArgumentNullException.ThrowIfNull(playable);
        return new InMemoryCampaignSource(playable.Campaign, playable.Maps.ToDictionary(m => m.Key, m => new MissionContent(m.Value.Map, m.Value.Scenario), StringComparer.Ordinal));
    }
}
