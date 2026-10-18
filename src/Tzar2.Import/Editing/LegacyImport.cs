using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Formats.Text;
using Tzar2.Import.Maps;

namespace Tzar2.Import.Editing;

/// <summary>Settings of an editor import (TDD §18.5: code page override REQ-EDT-07, type mapping REQ-EDT-10).</summary>
public sealed record ImportSettings
{
    /// <summary>The ruleset type keys resolve against (the editor's), or null.</summary>
    public Ruleset? Ruleset { get; init; }

    /// <summary>Forced code page (1250, 1251, 1252), or null to detect it per file.</summary>
    public int? CodePage { get; init; }

    /// <summary>The user's type mapping table (legacy class or <c>civ.class</c> → type key).</summary>
    public TypeMappingTable Mapping { get; init; } = TypeMappingTable.Empty;

    /// <summary>The code pages the override offers.</summary>
    public static ImmutableArray<int> CodePages { get; } = [1250, 1251, 1252];

    internal LegacyConversionOptions Options() => new()
    {
        Ruleset = Ruleset,
        Mapping = Mapping.Entries,
        CodePage = CodePage is int cp ? (CodePage)cp : null,
    };
}

/// <summary>
/// The user-editable type mapping table (REQ-EDT-10): legacy class (or <c>civ.class</c>) → entity type key, saved as a
/// reusable YAML file of <c>legacy: key</c> lines (the format <c>t2 convert --mapping</c> reads).
/// </summary>
public sealed class TypeMappingTable
{
    private TypeMappingTable(ImmutableSortedDictionary<string, string> entries) => Entries = entries;

    /// <summary>An empty table.</summary>
    public static TypeMappingTable Empty { get; } = new(ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal));

    /// <summary>The entries, sorted by legacy name.</summary>
    public ImmutableSortedDictionary<string, string> Entries { get; }

    /// <summary>Parses a table.</summary>
    /// <exception cref="FormatException">A line is not <c>name: value</c>.</exception>
    public static TypeMappingTable Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        Dictionary<string, string> parsed = LegacyTypeResolver.ParseMapping(yaml);
        return new TypeMappingTable(parsed.ToImmutableSortedDictionary(StringComparer.Ordinal));
    }

    /// <summary>A copy with <paramref name="legacy"/> mapped to <paramref name="key"/> (an empty key removes the entry).</summary>
    public TypeMappingTable With(string legacy, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(legacy);
        ArgumentNullException.ThrowIfNull(key);
        return new TypeMappingTable(key.Length == 0 ? Entries.Remove(legacy) : Entries.SetItem(legacy, key));
    }

    /// <summary>The table as YAML (sorted, LF, a header comment).</summary>
    public string ToYaml()
    {
        var sb = new StringBuilder("# Tzar2 legacy type mapping (TDD §18.5 REQ-EDT-10): legacy class or civ.class -> entity type key\n");
        foreach (KeyValuePair<string, string> e in Entries)
        {
            sb.Append(e.Key).Append(": ").Append(e.Value).Append('\n');
        }

        return sb.ToString();
    }
}

/// <summary>One finding of a conversion report, flattened for the report panel (REQ-EDT-06).</summary>
/// <param name="Source">The file or campaign map the finding belongs to.</param>
/// <param name="MapId">The campaign map id (lower-cased map name), or null for a single map.</param>
/// <param name="Severity">Info, warning or error.</param>
/// <param name="Code">Stable code.</param>
/// <param name="Message">Text.</param>
/// <param name="Location">The converter's location text.</param>
/// <param name="Target">Where the finding is in the converted map (click to locate), or <see cref="EditorLocation.Map"/>.</param>
public sealed record ReportRow(string Source, string? MapId, DiagnosticSeverity Severity, string Code, string Message, string Location, EditorLocation Target);

/// <summary>One converted file of an import session.</summary>
public sealed class ImportItem
{
    internal ImportItem(string path, MapConversion? map, CampaignConversion? campaign, byte[]? package, string? error)
    {
        Path = path;
        Map = map;
        Campaign = campaign;
        Package = package;
        Error = error;
    }

    /// <summary>The source file.</summary>
    public string Path { get; }

    /// <summary>The map conversion of a <c>.wmp</c>, or null.</summary>
    public MapConversion? Map { get; }

    /// <summary>The campaign conversion of a <c>.wcm</c>, or null.</summary>
    public CampaignConversion? Campaign { get; }

    /// <summary>Whether the file is a campaign.</summary>
    public bool IsCampaign => Campaign is not null;

    /// <summary>The written package (<c>.t2map</c> / <c>.t2camp</c>), or null when conversion failed.</summary>
    public byte[]? Package { get; }

    /// <summary>A read error (the file could not be read), or null.</summary>
    public string? Error { get; }

    /// <summary>The report.</summary>
    public ConversionReport? Report => Map?.Report ?? Campaign?.Report;

    /// <summary>Whether conversion succeeded (no errors).</summary>
    public bool Succeeded => Error is null && (Map?.Succeeded ?? Campaign?.Succeeded ?? false);

    /// <summary>The package file name the item is written as (<c>name.t2map</c> / <c>name.t2camp</c>).</summary>
    public string OutputName => LegacyCampaignConverter.Slug(System.IO.Path.GetFileNameWithoutExtension(Path)) + (IsCampaign ? ".t2camp" : ".t2map");

    /// <summary>The converted map opened as an editor document (single maps only).</summary>
    /// <exception cref="InvalidOperationException">The item is a campaign or did not convert.</exception>
    public MapDocument OpenDocument(Func<string?, TerrainRules> rules)
    {
        if (IsCampaign || Package is null)
        {
            throw new InvalidOperationException("Only a converted map opens as a map document.");
        }

        return MapPackage.Read(Package, rules, OutputName);
    }

    /// <summary>The converted campaign opened in the campaign editor.</summary>
    /// <exception cref="InvalidOperationException">The item is not a converted campaign.</exception>
    public CampaignDocument OpenCampaign(Func<string?, TerrainRules> rules)
    {
        if (!IsCampaign || Package is null)
        {
            throw new InvalidOperationException("The item is not a converted campaign.");
        }

        return CampaignPackage.Read(Package, rules, OutputName);
    }

    /// <summary>A converted map of a campaign opened as an editor document (with the campaign's shared resources).</summary>
    /// <exception cref="InvalidOperationException">The item is not a campaign, or the map did not convert.</exception>
    public MapDocument OpenCampaignMap(string mapId, Func<string?, TerrainRules> rules)
    {
        ArgumentNullException.ThrowIfNull(mapId);
        if (Campaign?.Campaign is not { } campaign)
        {
            throw new InvalidOperationException("The item is not a converted campaign.");
        }

        int i = campaign.Maps.Items.ToList().FindIndex(m => m.Id == mapId);
        if (i < 0 || LegacyPackageWriter.WriteMap(Campaign.Maps[i], mapId) is not { } package)
        {
            throw new InvalidOperationException($"The campaign has no converted map '{mapId}'.");
        }

        return MapPackage.Read(package, rules, mapId + ".t2map");
    }

    /// <summary>The flattened report rows, maps of a campaign included.</summary>
    public ImmutableArray<ReportRow> Rows()
    {
        var rows = ImmutableArray.CreateBuilder<ReportRow>();
        if (Error is not null)
        {
            rows.Add(new ReportRow(Path, null, DiagnosticSeverity.Error, "read", Error, string.Empty, EditorLocation.Map));
            return rows.ToImmutable();
        }

        if (Map is not null)
        {
            ReportLocations.Add(rows, Map.Report, Map, null);
        }
        else if (Campaign is not null)
        {
            ReportLocations.Add(rows, Campaign.Report, null, null);
            for (int i = 0; i < Campaign.Maps.Count; i++)
            {
                MapConversion m = Campaign.Maps[i];
                string id = Campaign.Campaign is { } c && i < c.Maps.Length ? c.Maps[i].Id : m.MapName.ToLowerInvariant();
                ReportLocations.Add(rows, m.Report, m, id);
            }
        }

        return rows.ToImmutable();
    }

    /// <summary>The counts of the report (converted objects, rules, events, actions…), maps of a campaign summed.</summary>
    public ImmutableSortedDictionary<string, int> Counts()
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Sum(ConversionReport r)
        {
            foreach (KeyValuePair<string, int> c in r.Counts)
            {
                counts[c.Key] = counts.TryGetValue(c.Key, out int v) ? v + c.Value : c.Value;
            }

            foreach (ConversionReport child in r.Children)
            {
                Sum(child);
            }
        }

        if (Report is { } report)
        {
            Sum(report);
        }

        return counts.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }
}

/// <summary>Turns the converter's location texts into editor locations (click-to-locate in the report panel).</summary>
public static partial class ReportLocations
{
    /// <summary>The editor location of a converter location text in <paramref name="map"/>'s converted document.</summary>
    /// <remarks>
    /// Objects get the ids <see cref="MapPackage"/> gives a converted map (index + 1); rules, events, actions and
    /// conversations are found by name in the converted scenario.
    /// </remarks>
    public static EditorLocation Parse(string location, MapConversion? map, string? mapId = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        ScenarioData? s = map?.Scenario;
        Match m = ObjectPattern().Match(location);
        if (m.Success && map is not null && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int legacy)
            && map.LegacyObjectIndices.TryGetValue(legacy, out int index) && map.Map is { } data && index < data.Objects.Length)
        {
            MapObject o = data.Objects[index];
            return new EditorLocation(LocationKind.Object, index + 1, X: o.X / MapData.UnitsPerTile, Y: o.Y / MapData.UnitsPerTile);
        }

        m = NamedPattern().Match(location);
        if (m.Success && s is not null)
        {
            string kind = m.Groups[1].Value, name = m.Groups[2].Value;
            switch (kind)
            {
                case "rule":
                    return Find(s.Rules.Items.Select(r => r.Name), name, LocationKind.Rule);
                case "event":
                    return Find(s.Events.Items.Select(e => e.Name), name, LocationKind.Event);
                case "action":
                    return Find(s.Actions.Items.Select(a => a.Name), name, LocationKind.Action);
                case "conversation":
                    EditorLocation c = Find(s.Conversations.Items.Select(x => x.Name), name, LocationKind.Conversation);
                    Match p = PhrasePattern().Match(location);
                    if (p.Success && c.Index >= 0)
                    {
                        int phrase = s.Conversations[c.Index].Phrases.Items.Select(x => x.Name).ToList().IndexOf(p.Groups[1].Value);
                        return phrase >= 0 ? new EditorLocation(LocationKind.Phrase, c.Index, phrase) : c;
                    }

                    return c;
            }
        }

        m = MapPattern().Match(location);
        if (m.Success)
        {
            return new EditorLocation(LocationKind.CampaignMap, Name: m.Groups[1].Value.ToLowerInvariant());
        }

        return mapId is not null ? new EditorLocation(LocationKind.CampaignMap, Name: mapId) : EditorLocation.Map;
    }

    internal static void Add(ImmutableArray<ReportRow>.Builder rows, ConversionReport report, MapConversion? map, string? mapId)
    {
        foreach (ConversionDiagnostic d in report.Diagnostics)
        {
            rows.Add(new ReportRow(report.Source, mapId, d.Severity, d.Code, d.Message, d.Location, Parse(d.Location, map, mapId)));
        }
    }

    private static EditorLocation Find(IEnumerable<string> names, string name, LocationKind kind)
    {
        int i = names.ToList().FindIndex(n => string.Equals(n, name, StringComparison.Ordinal));
        return i >= 0 ? new EditorLocation(kind, i, Name: name) : EditorLocation.Map;
    }

    [GeneratedRegex(@"object #(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectPattern();

    [GeneratedRegex(@"^(rule|event|action|conversation) '([^']*)'", RegexOptions.CultureInvariant)]
    private static partial Regex NamedPattern();

    [GeneratedRegex(@"phrase '([^']*)'", RegexOptions.CultureInvariant)]
    private static partial Regex PhrasePattern();

    [GeneratedRegex(@"^map '([^']*)'", RegexOptions.CultureInvariant)]
    private static partial Regex MapPattern();
}

/// <summary>
/// The editor's legacy import (TDD §18.5 REQ-EDT-04…12): files, several files or whole folders (anywhere on disk) are
/// converted by the one converter (REQ-EDT-05), each with its report; the packages are written to a folder of the user's
/// choice, never next to the sources (REQ-EDT-11). Code page and type mapping can be overridden and the files converted
/// again.
/// </summary>
public sealed class LegacyImportSession
{
    private readonly List<string> _files = [];
    private readonly List<ImportItem> _items = [];

    /// <summary>The settings of the next conversion.</summary>
    public ImportSettings Settings { get; set; } = new();

    /// <summary>The queued source files, in the order added.</summary>
    public IReadOnlyList<string> Files => _files;

    /// <summary>The results of the last conversion, in file order.</summary>
    public IReadOnlyList<ImportItem> Items => _items;

    /// <summary>Queues a <c>.wmp</c>/<c>.wcm</c> file, or every legacy file under a folder (sorted).</summary>
    /// <returns>How many files were queued.</returns>
    public int Add(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Directory.Exists(path))
        {
            int n = 0;
            foreach (string f in LegacyCorpus.Find(path))
            {
                n += Queue(f) ? 1 : 0;
            }

            return n;
        }

        return LegacyCorpus.IsLegacyFile(path) && Queue(path) ? 1 : 0;
    }

    /// <summary>Removes every queued file and result.</summary>
    public void Clear()
    {
        _files.Clear();
        _items.Clear();
    }

    /// <summary>Converts every queued file with <see cref="Settings"/> (the sources are only read).</summary>
    public IReadOnlyList<ImportItem> Convert()
    {
        _items.Clear();
        foreach (string file in _files)
        {
            _items.Add(ConvertOne(file, Settings));
        }

        return _items;
    }

    /// <summary>Converts one file.</summary>
    public static ImportItem ConvertOne(string file, ImportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            (MapConversion? map, CampaignConversion? campaign) = LegacyCorpus.ConvertFile(file, settings.Options());
            byte[]? package = map is not null
                ? LegacyPackageWriter.WriteMap(map, System.IO.Path.GetFileNameWithoutExtension(file))
                : LegacyPackageWriter.WriteCampaign(campaign!);
            return new ImportItem(file, map, campaign, package, null);
        }
#pragma warning disable CA1031 // A user-picked file must never take the editor down; a converter bug becomes a report row (ADR 0154).
        catch (Exception e) when (e is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            string what = e is IOException or UnauthorizedAccessException ? e.Message : $"the converter failed on this file ({e.GetType().Name}: {e.Message})";
            return new ImportItem(file, null, null, null, what);
        }
    }

    /// <summary>Writes the converted packages into <paramref name="folder"/>; returns the written paths.</summary>
    /// <exception cref="ArgumentException">The folder is a folder of a source file (sources stay untouched next to nothing new).</exception>
    public IReadOnlyList<string> Save(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        Directory.CreateDirectory(folder);
        var written = new List<string>();
        foreach (ImportItem item in _items)
        {
            if (item.Package is null)
            {
                continue;
            }

            string target = System.IO.Path.Combine(folder, item.OutputName);
            if (string.Equals(System.IO.Path.GetFullPath(target), System.IO.Path.GetFullPath(item.Path), StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The output would overwrite a source file.", nameof(folder));
            }

            File.WriteAllBytes(target, item.Package);
            written.Add(target);
        }

        return written;
    }

    /// <summary>The legacy classes the converter could not place (placeholders), for the type mapping table.</summary>
    public ImmutableSortedSet<string> Unresolved()
    {
        var set = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (ImportItem item in _items)
        {
            IEnumerable<MapConversion> maps = item.Map is not null ? [item.Map] : item.Campaign?.Maps ?? [];
            foreach (MapConversion m in maps)
            {
                foreach (Placeholder p in m.Placeholders)
                {
                    set.Add(p.ClassName);
                }
            }
        }

        return set.ToImmutable();
    }

    /// <summary>The whole report of the last conversion as JSON (an array of the per-file reports).</summary>
    public byte[] ExportReports()
    {
        var sb = new StringBuilder("[\n");
        for (int i = 0; i < _items.Count; i++)
        {
            string json = _items[i].Report is { } r ? Encoding.UTF8.GetString(r.ToJson()).TrimEnd('\n') : "{\"source\": \"" + _items[i].Path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\", \"error\": true}";
            sb.Append(json).Append(i + 1 < _items.Count ? ",\n" : "\n");
        }

        return Encoding.UTF8.GetBytes(sb.Append("]\n").ToString());
    }

    private bool Queue(string file)
    {
        string full = System.IO.Path.GetFullPath(file);
        if (_files.Contains(full, StringComparer.Ordinal))
        {
            return false;
        }

        _files.Add(full);
        return true;
    }
}
