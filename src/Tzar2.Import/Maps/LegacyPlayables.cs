using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;

namespace Tzar2.Import.Maps;

/// <summary>A legacy campaign (or a single map as a one-map campaign) converted for play: its graph and every map.</summary>
/// <param name="Campaign">The campaign graph.</param>
/// <param name="Maps">Each map's terrain and objects with its scenario, by campaign map id.</param>
public sealed record PlayableLegacy(CampaignData Campaign, IReadOnlyDictionary<string, (MapData Map, ScenarioData Scenario)> Maps)
{
    /// <summary>Each map's own resources (its embedded <c>USER</c> files) by package path, by campaign map id (ADR 0284).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, byte[]>> Resources { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, byte[]>>();

    /// <summary>The resources of map <paramref name="id"/> by package path (none when it has none).</summary>
    public IReadOnlyDictionary<string, byte[]> ResourcesOf(string id)
        => Resources.TryGetValue(id, out IReadOnlyDictionary<string, byte[]>? r) ? r : new Dictionary<string, byte[]>();

    /// <summary>The resources of a converted map by package path.</summary>
    public static IReadOnlyDictionary<string, byte[]> Files(MapConversion map)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (UserResource r in map.Resources)
        {
            files.TryAdd(r.Path, r.Data);
        }

        return files;
    }
}

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
            var resources = new Dictionary<string, IReadOnlyDictionary<string, byte[]>>(StringComparer.Ordinal);
            for (int i = 0; i < graph.Maps.Length && i < campaign.Maps.Count; i++)
            {
                if (campaign.Maps[i] is { Map: { } m, Scenario: { } s })
                {
                    maps[graph.Maps[i].Id] = (m, s);
                    resources[graph.Maps[i].Id] = PlayableLegacy.Files(campaign.Maps[i]);
                }
                else
                {
                    problems.Add($"{fileName}: map {graph.Maps[i].Id} did not convert.");
                }
            }

            return new PlayableLegacy(graph, maps) { Resources = resources };
        }

        if (map is { Map: { } single, Scenario: { } scenario } && map.Succeeded)
        {
            string id = map.MapName.ToLowerInvariant();
            var one = new CampaignData(id, map.MapName, scenario.Properties.Description, new[] { new CampaignMap(id, map.MapName, fileName, scenario.Properties.Description, IsEntry: true) }, EquatableArray<CampaignEdge>.Empty);
            return new PlayableLegacy(one, new Dictionary<string, (MapData, ScenarioData)>(StringComparer.Ordinal) { [id] = (single, scenario) })
            {
                Resources = new Dictionary<string, IReadOnlyDictionary<string, byte[]>>(StringComparer.Ordinal) { [id] = PlayableLegacy.Files(map) },
            };
        }

        problems.Add($"{fileName}: conversion failed ({(campaign?.Report ?? map?.Report)?.ToString() ?? "unknown error"}).");
        return null;
    }
}
