using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;

namespace Tzar2.Import.Maps;

/// <summary>A legacy campaign (or a single map as a one-map campaign) converted for play: its graph and every map.</summary>
/// <param name="Campaign">The campaign graph.</param>
/// <param name="Maps">Each map's terrain and objects with its scenario, by campaign map id.</param>
public sealed record PlayableLegacy(CampaignData Campaign, IReadOnlyDictionary<string, (MapData Map, ScenarioData Scenario)> Maps);

/// <summary>
/// Converts the user's <c>.wcm</c> campaigns and <c>.wmp</c> maps (never in the repository) with the converter of
/// stream A (ADR 0120) into something playable: the client's campaign screen and the headless runner (ADR 0133) both
/// start missions from it.
/// </summary>
public static class LegacyPlayables
{
    /// <summary>
    /// Converts <paramref name="data"/> (a <c>.wcm</c> when <paramref name="fileName"/> says so, else a <c>.wmp</c>), or
    /// returns null; problems (a map that did not convert, a failed file) are added to <paramref name="problems"/>.
    /// </summary>
    public static PlayableLegacy? Convert(byte[] data, string fileName, LegacyConversionOptions options, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(problems);
        (MapConversion? map, CampaignConversion? campaign) = LegacyCorpus.Convert(data, fileName, options);
        if (campaign is { Campaign: { } graph } && campaign.Succeeded)
        {
            var maps = new Dictionary<string, (MapData, ScenarioData)>(StringComparer.Ordinal);
            for (int i = 0; i < graph.Maps.Length && i < campaign.Maps.Count; i++)
            {
                if (campaign.Maps[i] is { Map: { } m, Scenario: { } s })
                {
                    maps[graph.Maps[i].Id] = (m, s);
                }
                else
                {
                    problems.Add($"{fileName}: map {graph.Maps[i].Id} did not convert.");
                }
            }

            return new PlayableLegacy(graph, maps);
        }

        if (map is { Map: { } single, Scenario: { } scenario } && map.Succeeded)
        {
            string id = map.MapName.ToLowerInvariant();
            var one = new CampaignData(id, map.MapName, scenario.Properties.Description, new[] { new CampaignMap(id, map.MapName, fileName, scenario.Properties.Description, IsEntry: true) }, EquatableArray<CampaignEdge>.Empty);
            return new PlayableLegacy(one, new Dictionary<string, (MapData, ScenarioData)>(StringComparer.Ordinal) { [id] = (single, scenario) });
        }

        problems.Add($"{fileName}: conversion failed ({(campaign?.Report ?? map?.Report)?.ToString() ?? "unknown error"}).");
        return null;
    }
}
