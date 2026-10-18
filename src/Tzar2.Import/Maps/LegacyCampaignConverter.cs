using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Formats;
using Tzar2.Formats.Maps;
using Tzar2.Formats.Text;

namespace Tzar2.Import.Maps;

/// <summary>
/// Converts a legacy campaign (<c>.wcm</c>): every embedded map through <see cref="LegacyMapConverter"/>, and the
/// campaign graph rebuilt from the maps' Load Map / Player Win / Player Lose actions with names matched
/// case-insensitively (REQ-EDT-08). Reports unreachable maps, dangling next-map names and the persistent-unit mapping.
/// </summary>
public static class LegacyCampaignConverter
{
    /// <summary>Converts a campaign file.</summary>
    /// <param name="data">The <c>.wcm</c> bytes.</param>
    /// <param name="fileName">Source file name.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    public static CampaignConversion Convert(byte[] data, string fileName, LegacyConversionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        options ??= new LegacyConversionOptions();
        var report = new ConversionReport("campaign", fileName) { SourceSize = data.Length, SourceMd5 = LegacyMapConverter.Md5(data) };
        try
        {
            CodePage codePage;
            if (options.CodePage is CodePage forced)
            {
                codePage = forced;
                report.CodePageSource = "override";
            }
            else
            {
                CodePageGuess guess = CodePageDetector.Detect(LegacyMapReader.CampaignTextSamples(data));
                codePage = guess.CodePage;
                report.CodePageSource = guess.HighBytes == 0 ? "ascii" : "detected";
                report.CodePageAmbiguous = guess.Ambiguous;
                report.Language = guess.Language;
            }

            report.CodePage = (int)codePage;
            LegacyCampaign campaign = LegacyMapReader.ReadCampaign(data, codePage);
            return Build(campaign, Stem(fileName), report, options, codePage);
        }
        catch (LegacyFormatException e)
        {
            report.Error("format", e.Message);
            return new CampaignConversion { Report = report };
        }
    }

    private static CampaignConversion Build(LegacyCampaign campaign, string stem, ConversionReport report, LegacyConversionOptions options, CodePage codePage)
    {
        foreach (FormatWarning w in campaign.Warnings)
        {
            report.Warn("format", w.Message);
        }

        var files = new Dictionary<string, LegacyFile>(StringComparer.OrdinalIgnoreCase);
        foreach (LegacyFile f in campaign.MapFiles)
        {
            string name = Stem(f.Path);
            if (!files.TryAdd(name, f))
            {
                report.Warn("campaign.map", $"two embedded maps are named '{name}'; the first is used");
            }
        }

        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conversions = new List<MapConversion>();
        var maps = ImmutableArray.CreateBuilder<CampaignMap>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, bool entry) in campaign.Maps)
        {
            if (!listed.Add(name))
            {
                report.Warn("campaign.map", $"map '{name}' is listed twice in maps.ini", "maps.ini");
                continue;
            }

            if (!files.TryGetValue(name, out LegacyFile? file))
            {
                report.Error("campaign.map", $"map '{name}' is listed but not embedded", "maps.ini");
                continue;
            }

            var mapReport = new ConversionReport("map", file.Path) { SourceSize = file.Data.Length };
            MapConversion converted = LegacyMapConverter.Convert(file.Data, mapReport, options, codePage);
            report.AddChild(mapReport);
            string id = UniqueId(name, ids);
            conversions.Add(converted);
            maps.Add(new CampaignMap(id, name, "maps/" + id + ".t2map", converted.Scenario?.Properties.Description ?? string.Empty, entry));
        }

        foreach (string name in files.Keys)
        {
            if (!listed.Contains(name))
            {
                report.Warn("campaign.map", $"embedded map '{name}' is not listed in maps.ini; not converted");
            }
        }

        bool anyEntry = false;
        foreach (CampaignMap m in maps)
        {
            anyEntry |= m.IsEntry;
        }

        if (!anyEntry)
        {
            report.Error("campaign.entry", "no starting map (the original rejects such a campaign)", "maps.ini");
        }

        ImmutableArray<CampaignMap> mapList = maps.ToImmutable();
        var lookup = new Dictionary<string, CampaignMap>(StringComparer.OrdinalIgnoreCase);
        foreach (CampaignMap m in mapList)
        {
            lookup.TryAdd(m.MapName, m);
        }

        var edges = ImmutableArray.CreateBuilder<CampaignEdge>();
        for (int i = 0; i < mapList.Length; i++)
        {
            foreach ((int action, CampaignEdgeKind kind, string target) in conversions[i].Transitions)
            {
                CampaignMap? to = lookup.TryGetValue(target, out CampaignMap? t) ? t : null;
                if (to is null)
                {
                    report.Warn("campaign.edge", $"'{target}' is not a map of this campaign", $"map '{mapList[i].MapName}' action #{action}");
                }

                edges.Add(new CampaignEdge(mapList[i].Id, target, to?.Id, kind, action));
            }
        }

        ImmutableArray<CampaignEdge> edgeList = edges.ToImmutable();
        ReportReachability(report, mapList, edgeList);
        ReportPersistence(report, mapList, conversions);
        report.Count("maps", mapList.Length);
        report.Count("edges", edgeList.Length);
        CampaignData? data = null;
        try
        {
            string title = campaign.Title.Length > 0 ? campaign.Title : stem;
            data = new CampaignData(Slug(stem), title, campaign.Description, mapList, edgeList);
        }
        catch (ArgumentException e)
        {
            report.Error("campaign", e.Message);
        }

        return new CampaignConversion { Campaign = data, Maps = conversions, Report = report };
    }

    private static void ReportReachability(ConversionReport report, ImmutableArray<CampaignMap> maps, ImmutableArray<CampaignEdge> edges)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (CampaignMap m in maps)
        {
            if (m.IsEntry && reached.Add(m.Id))
            {
                queue.Enqueue(m.Id);
            }
        }

        while (queue.Count > 0)
        {
            string from = queue.Dequeue();
            foreach (CampaignEdge e in edges)
            {
                if (e.FromMapId == from && e.ToMapId is string to && reached.Add(to))
                {
                    queue.Enqueue(to);
                }
            }
        }

        foreach (CampaignMap m in maps)
        {
            if (!reached.Contains(m.Id))
            {
                report.Warn("campaign.unreachable", $"map '{m.MapName}' cannot be reached from a starting map", $"map '{m.MapName}'");
            }
        }
    }

    private static void ReportPersistence(ConversionReport report, ImmutableArray<CampaignMap> maps, List<MapConversion> conversions)
    {
        var carriers = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < maps.Length; i++)
        {
            ScenarioData? s = conversions[i].Scenario;
            if (s is null)
            {
                continue;
            }

            foreach (ObjectInfo info in s.Objects)
            {
                if ((info.Flags & ObjectFlags.Persistent) != 0 && info.Name.Length > 0)
                {
                    if (!carriers.TryGetValue(info.Name, out List<string>? list))
                    {
                        carriers[info.Name] = list = [];
                    }

                    if (!list.Contains(maps[i].MapName))
                    {
                        list.Add(maps[i].MapName);
                    }
                }
            }
        }

        foreach (KeyValuePair<string, List<string>> c in carriers)
        {
            report.Info("campaign.persistent", $"persistent unit '{c.Key}' appears in {c.Value.Count} map(s): {string.Join(", ", c.Value)}");
        }

        report.Count("persistentUnits", carriers.Count);
    }

    private static string UniqueId(string name, HashSet<string> ids)
    {
        string baseId = Slug(name);
        string id = baseId;
        for (int n = 2; !ids.Add(id); n++)
        {
            id = baseId + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return id;
    }

    /// <summary>A lower-case id of letters, digits, '-' and '_' (other characters become '-').</summary>
    internal static string Slug(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '-');
        }

        return sb.Length > 0 ? sb.ToString() : "map";
    }

    private static string Stem(string path)
    {
        string name = path.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        name = slash >= 0 ? name[(slash + 1)..] : name;
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}
