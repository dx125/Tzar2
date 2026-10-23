using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Editing;
using Tzar2.Import.Editing;
using Tzar2.Import.Maps;

namespace Tzar2.Client.Editor;

/// <summary>
/// File → Import legacy map/campaign (TDD §18.5): files, several files or a folder anywhere on disk (or dropped on the
/// editor), the code page override (REQ-EDT-07), the type mapping table (REQ-EDT-10), the report panel with counts,
/// severity filter, click-to-locate and export (REQ-EDT-06), the reconstructed campaign graph with unreachable maps,
/// dangling names and the persistent-unit preview (REQ-EDT-08), the 600-tick smoke test (REQ-EDT-12), writing the
/// packages to a chosen folder (REQ-EDT-11), opening a converted map in the editor and playing a converted campaign.
/// </summary>
public partial class ImportWindow : Window
{
    private static readonly string[] CodePages = ["Detect per file", "1250 Central European", "1251 Cyrillic", "1252 Western"];
    private readonly EditorScreen _screen;
    private readonly LegacyImportSession _session = new();
    private readonly ItemList _files = new() { CustomMinimumSize = new Vector2(0, 120) };
    private readonly ItemList _results = new() { CustomMinimumSize = new Vector2(0, 160) };
    private readonly ItemList _rows = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 200) };
    private readonly Label _header = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label _counts = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly VBoxContainer _mapping = new();
    private readonly CampaignGraphView _graph = new() { Visible = false, CustomMinimumSize = new Vector2(0, 260) };
    private readonly Label _persistent = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private ImmutableArray<ReportRow> _shown = [];
    private int _minimum = 1;
    private string _output;

    /// <summary>Creates the window; <paramref name="play"/> starts a converted campaign in the game.</summary>
    public ImportWindow(EditorScreen screen, Action<ICampaignSource>? play)
    {
        _screen = screen;
        Play = play;
        Title = "Import legacy maps and campaigns";
        Size = new Vector2I(1150, 820);
        Transient = true;
        _output = ProjectSettings.GlobalizePath("user://imported");
        _session.Settings = new ImportSettings { Ruleset = screen.Controller.Ruleset };
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var split = new HSplitContainer();
        margin.AddChild(split);
        AddChild(margin);
        CloseRequested += QueueFree;

        // Left: sources and settings.
        VBoxContainer left = Forms.Scroll(split);
        left.CustomMinimumSize = new Vector2(380, 0);
        Forms.Heading(left, "Sources (never modified)");
        left.AddChild(_files);
        var add = new HBoxContainer();
        Forms.Button(add, "Add files…", () => Files.Open(this, ["*.wmp, *.wcm ; Tzar maps and campaigns"], paths => AddPaths(paths)));
        Forms.Button(add, "Add folder…", () => Files.Folder(this, path => AddPaths([path])));
        Forms.Button(add, "Clear", () =>
        {
            _session.Clear();
            Refresh();
        });
        left.AddChild(add);
        Forms.Label(left, "You can also drop .wmp/.wcm files or folders on the editor window.").AutowrapMode = TextServer.AutowrapMode.WordSmart;
        GridContainer g = Forms.Grid(left);
        Forms.Choice(g, "Code page", CodePages, 0, i => _session.Settings = _session.Settings with { CodePage = i == 0 ? null : ImportSettings.CodePages[i - 1] });
        Forms.Label(g, "Output folder");
        var output = new LineEdit { Text = _output, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        output.TextChanged += t => _output = t;
        g.AddChild(output);
        Forms.Button(left, "Choose output folder…", () => Files.Folder(this, path =>
        {
            _output = path;
            output.Text = path;
        }));
        Forms.Heading(left, "Type mapping (legacy class → type key)");
        var mappingButtons = new HBoxContainer();
        Forms.Button(mappingButtons, "Load…", () => Files.Open(this, ["*.yaml, *.yml ; Mapping table"], paths =>
        {
            try
            {
                _session.Settings = _session.Settings with { Mapping = TypeMappingTable.Parse(File.ReadAllText(paths[0])) };
                RefreshMapping();
            }
            catch (FormatException e)
            {
                _screen.Status("Not a mapping table: " + e.Message);
            }
        }, many: false));
        Forms.Button(mappingButtons, "Save…", () => Files.Save(this, "type-mapping.yaml", path => File.WriteAllText(path, _session.Settings.Mapping.ToYaml()), ["*.yaml ; Mapping table"]));
        left.AddChild(mappingButtons);
        left.AddChild(_mapping);
        Forms.Button(left, "Convert", Convert);

        // Right: results and the report panel.
        VBoxContainer right = Forms.Scroll(split);
        Forms.Heading(right, "Results");
        right.AddChild(_results);
        // The buttons wrap, so the window fits a 1024-wide screen (UI-L3).
        var actions = new HFlowContainer();
        Forms.Button(actions, "Save packages", Save);
        Forms.Button(actions, "Export report…", () => Files.Save(this, "conversion-report.json", path => File.WriteAllBytes(path, _session.ExportReports()), ["*.json ; Report"]));
        Forms.Button(actions, "Open in editor", OpenSelected);
        Forms.Button(actions, "Play converted campaign", PlaySelected);
        Forms.Button(actions, "Smoke test (600 ticks)", Smoke);
        right.AddChild(actions);
        right.AddChild(_header);
        right.AddChild(_counts);
        GridContainer filter = Forms.Grid(right);
        Forms.Choice(filter, "Show", ["info and above", "warnings and errors", "errors only"], 1, i =>
        {
            _minimum = i;
            ShowReport();
        });
        right.AddChild(_rows);
        Forms.Heading(right, "Campaign graph");
        right.AddChild(_graph);
        right.AddChild(_persistent);
        _results.ItemSelected += _ => ShowReport();
        _rows.ItemActivated += i => Locate(_shown[(int)i]);
        _graph.MapSelected += id => _screen.Status("Map " + id);
    }

    /// <summary>Starts a converted campaign in the game, or null when the game mode is not attached.</summary>
    public Action<ICampaignSource>? Play { get; }

    /// <summary>The import session (tests, drag and drop).</summary>
    public LegacyImportSession Session => _session;

    /// <inheritdoc/>
    public override void _Ready()
    {
        Forms.ShowCentered(this);
        RefreshMapping();
    }

    /// <summary>Queues dropped or chosen files and folders.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        int n = 0;
        foreach (string p in paths)
        {
            n += _session.Add(p);
        }

        _screen.Status($"{n} legacy file(s) queued.");
        Refresh();
    }

    /// <summary>Converts the queued files.</summary>
    public void Convert()
    {
        _session.Convert();
        Refresh();
        RefreshMapping();
        if (_results.ItemCount > 0)
        {
            _results.Select(0);
            ShowReport();
        }
    }

    private ImportItem? Selected => _results.GetSelectedItems() is { Length: > 0 } s && s[0] < _session.Items.Count ? _session.Items[s[0]] : null;

    private void Refresh()
    {
        _files.Clear();
        foreach (string f in _session.Files)
        {
            _files.AddItem(f);
        }

        _results.Clear();
        foreach (ImportItem item in _session.Items)
        {
            ConversionReport? r = item.Report;
            int i = _results.AddItem($"{(item.Succeeded ? "✓" : "✗")} {Path.GetFileName(item.Path)} → {item.OutputName}  ({r?.ErrorCount ?? 1} errors, {r?.WarningCount ?? 0} warnings{(item.IsCampaign ? $", {item.Campaign!.Maps.Count} maps" : string.Empty)})");
            _results.SetItemCustomFgColor(i, item.Succeeded ? new Color(0.7f, 1f, 0.7f) : new Color(1f, 0.6f, 0.6f));
        }
    }

    private void RefreshMapping()
    {
        Forms.Clear(_mapping);
        GridContainer g = Forms.Grid(_mapping);
        IEnumerable<string> names = _session.Unresolved().Concat(_session.Settings.Mapping.Entries.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (string legacy in names)
        {
            string each = legacy;
            Forms.Text(g, legacy, _session.Settings.Mapping.Entries.TryGetValue(legacy, out string? key) ? key : string.Empty, v =>
                _session.Settings = _session.Settings with { Mapping = _session.Settings.Mapping.With(each, v.Trim()) });
        }

        if (!names.Any())
        {
            Forms.Label(_mapping, "No unresolved types. Unknown classes (maps for mods) appear here after a conversion; map them and convert again.").AutowrapMode = TextServer.AutowrapMode.WordSmart;
        }
    }

    private void ShowReport()
    {
        _rows.Clear();
        ImportItem? item = Selected;
        _graph.Visible = false;
        _persistent.Text = string.Empty;
        if (item is null)
        {
            _header.Text = string.Empty;
            _counts.Text = string.Empty;
            return;
        }

        if (item.Report is { } r)
        {
            _header.Text = string.Create(CultureInfo.InvariantCulture, $"{r.Kind} {r.Source}: {r.SourceSize:N0} bytes, MD5 {r.SourceMd5}; code page {r.CodePage} ({r.CodePageSource}{(r.CodePageAmbiguous ? ", ambiguous" : string.Empty)}), language {r.Language}; converter {ConversionReport.ConverterVersion}");
        }
        else
        {
            _header.Text = item.Error ?? string.Empty;
        }

        _counts.Text = string.Join(", ", item.Counts().Select(c => $"{c.Key} {c.Value.ToString(CultureInfo.InvariantCulture)}"));
        _shown = item.Rows().Where(x => (int)x.Severity >= _minimum).ToImmutableArray();
        foreach (ReportRow row in _shown)
        {
            int i = _rows.AddItem($"[{row.Severity}] {(row.MapId is null ? string.Empty : row.MapId + ": ")}{row.Message}{(row.Location.Length > 0 ? " — " + row.Location : string.Empty)}");
            _rows.SetItemCustomFgColor(i, row.Severity switch { DiagnosticSeverity.Error => new Color(1, 0.4f, 0.4f), DiagnosticSeverity.Warning => new Color(1, 0.9f, 0.5f), _ => new Color(0.8f, 0.8f, 0.8f) });
        }

        if (item.Campaign?.Campaign is { } campaign)
        {
            CampaignGraph graph = CampaignGraph.Build(campaign, id => ScenarioOf(item, id));
            _graph.Visible = true;
            _graph.Show(graph.Nodes.Select(n => new GraphMap(n.Id, n.Name, n.IsEntry, n.Reachable ? null : "⚠ unreachable", null)).ToList(), graph.Edges);
            _persistent.Text = graph.Persistent.Count == 0 ? "No persistent units." : "Persistent units carried by name: " + string.Join("; ", graph.Persistent.Select(p => $"{p.Key} ({string.Join(" → ", p.Value)})"));
        }
    }

    private static ScenarioData? ScenarioOf(ImportItem item, string id)
    {
        CampaignData c = item.Campaign!.Campaign!;
        int i = c.Maps.Items.ToList().FindIndex(m => m.Id == id);
        return i >= 0 ? item.Campaign.Maps[i].Scenario : null;
    }

    private void Locate(ReportRow row)
    {
        ImportItem? item = Selected;
        if (item?.Package is null)
        {
            return;
        }

        try
        {
            string? mapId = row.MapId ?? (row.Target.Kind == LocationKind.CampaignMap ? row.Target.Name : null);
            MapDocument doc = item.IsCampaign && mapId is not null ? item.OpenCampaignMap(mapId, _screen.RulesFor) : item.OpenDocument(_screen.RulesFor);
            _screen.Open(doc, null);
            _screen.LocateFinding(row.Target);
        }
        catch (InvalidOperationException e)
        {
            _screen.Status(e.Message);
        }
    }

    private void OpenSelected()
    {
        ImportItem? item = Selected;
        if (item?.Package is null)
        {
            return;
        }

        if (item.IsCampaign)
        {
            OpenCampaign?.Invoke(item);
            return;
        }

        _screen.Open(item.OpenDocument(_screen.RulesFor), null);
    }

    /// <summary>Raised to open a converted campaign in the campaign editor.</summary>
    public event Action<ImportItem>? OpenCampaign;

    private void PlaySelected()
    {
        if (Selected is not { Campaign.Campaign: { } campaign } item || Play is null)
        {
            _screen.Status("Choose a converted campaign first.");
            return;
        }

        var maps = new Dictionary<string, MissionContent>(StringComparer.Ordinal);
        for (int i = 0; i < campaign.Maps.Length; i++)
        {
            if (item.Campaign.Maps[i] is { Map: { } map, Scenario: { } scenario })
            {
                maps[campaign.Maps[i].Id] = new MissionContent(map, scenario);
            }
        }

        Play(new InMemoryCampaignSource(campaign, maps));
    }

    private void Smoke()
    {
        int passed = 0, total = 0;
        var lines = new List<string>();
        foreach (ImportItem item in _session.Items)
        {
            IEnumerable<(string Name, MapConversion Map)> maps = item.Map is not null
                ? [(Path.GetFileName(item.Path), item.Map)]
                : item.Campaign?.Maps.Select(m => (m.MapName, m)) ?? [];
            foreach ((string name, MapConversion map) in maps)
            {
                if (map.Map is null || map.Scenario is null)
                {
                    continue;
                }

                total++;
                SmokeResult r = MapSmoke.Run(new MissionContent(map.Map, map.Scenario), _screen.Controller.Ruleset);
                passed += r.Passed ? 1 : 0;
                lines.Add($"{(r.Passed ? "PASS" : "FAIL")} {name}: {r.Ticks} ticks, {r.Faults} scenario fault(s){(r.Error is null ? string.Empty : ", " + r.Error)}");
            }
        }

        _counts.Text = $"Smoke test: {passed}/{total} maps ran {MapSmoke.DefaultTicks} ticks.\n" + string.Join("\n", lines);
        _screen.Status($"Smoke test: {passed}/{total} passed.");
    }

    private void Save()
    {
        try
        {
            IReadOnlyList<string> written = _session.Save(_output);
            _screen.Status($"{written.Count} package(s) written to {_output}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _screen.Status("Saving failed: " + e.Message);
        }
    }
}
