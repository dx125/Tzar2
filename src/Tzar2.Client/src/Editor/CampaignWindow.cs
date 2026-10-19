using System;
using System.IO;
using System.Linq;
using Godot;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Game.Editing;
using Tzar2.Import.Editing;

namespace Tzar2.Client.Editor;

/// <summary>
/// The campaign editor (TDD §18.2 "Campaign editor", §18.3 visual campaign graph, ADR 0155): new / open / save
/// <c>.t2camp</c>; maps imported from <c>.t2map</c> or legacy files, exported and deleted; descriptions, details and entry
/// maps; the graph of transitions (Load Map / Player Win / Player Lose) with entry, unreachable and dangling marks; the
/// persistence mapping and carried variables; validation; editing a map in the main editor; playing the campaign.
/// </summary>
public partial class CampaignWindow : Window
{
    private readonly EditorScreen _screen;
    private readonly ItemList _maps = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(220, 0) };
    private readonly CampaignGraphView _graph = new();
    private readonly VBoxContainer _details = new() { CustomMinimumSize = new Vector2(330, 0) };
    private readonly Label _status = new();
    private CampaignDocument _campaign;
    private string? _path;
    private string? _selected;
    private bool _dirty = true;

    /// <summary>Creates the window over a campaign (a new one when null).</summary>
    public CampaignWindow(EditorScreen screen, CampaignDocument? campaign = null, string? path = null)
    {
        _screen = screen;
        _campaign = campaign ?? CampaignDocument.New("New campaign");
        _path = path;
        Title = "Campaign editor";
        Size = new Vector2I(1200, 760);
        Transient = true;
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var outer = new VBoxContainer();
        margin.AddChild(outer);
        AddChild(margin);
        var bar = new HBoxContainer();
        Forms.Button(bar, "New", () => SetCampaign(CampaignDocument.New("New campaign"), null));
        Forms.Button(bar, "Open…", () => Files.Open(this, ["*.t2camp ; Tzar2 campaign"], paths => OpenFile(paths[0]), many: false));
        Forms.Button(bar, "Save", () => Save(saveAs: false));
        Forms.Button(bar, "Save as…", () => Save(saveAs: true));
        Forms.Button(bar, "Undo", () => _campaign.Undo());
        Forms.Button(bar, "Redo", () => _campaign.Redo());
        Forms.Button(bar, "Play from the start", () => Play(null));
        Forms.Button(bar, "Play selected map", () => Play(_selected));
        outer.AddChild(bar);
        outer.AddChild(_status);
        var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var left = new VBoxContainer();
        Forms.Heading(left, "Maps");
        left.AddChild(_maps);
        Forms.Button(left, "New empty map", () => _campaign.AddMap(NewMap.Empty(new NewMapOptions("Map " + (_campaign.State.Maps.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), 64, _screen.Document.TerrainSet), _screen.RulesFor(_screen.Document.TerrainSet), _screen.Controller.Ruleset)));
        Forms.Button(left, "Add the map open in the editor", () => _campaign.AddMap(_screen.Document));
        Forms.Button(left, "Import map (.t2map)…", () => Files.Open(this, ["*.t2map ; Tzar2 map"], paths =>
        {
            foreach (string p in paths)
            {
                _campaign.AddMap(MapPackage.Read(File.ReadAllBytes(p), _screen.RulesFor, Path.GetFileName(p)));
            }
        }));
        Forms.Button(left, "Add map from legacy file (.wmp)…", () => Files.Open(this, ["*.wmp ; Tzar map"], paths =>
        {
            foreach (string p in paths)
            {
                ImportItem item = LegacyImportSession.ConvertOne(p, new ImportSettings { Ruleset = _screen.Controller.Ruleset });
                if (item.Package is null || item.Map is null)
                {
                    _screen.Status($"{Path.GetFileName(p)} did not convert: {item.Report?.ToText() ?? item.Error}");
                    continue;
                }

                _campaign.AddMap(item.OpenDocument(_screen.RulesFor), item.Map.MapName);
            }
        }));
        Forms.Button(left, "Export selected map…", () =>
        {
            if (_selected is { } id)
            {
                Files.Save(this, id + ".t2map", path => File.WriteAllBytes(path, CampaignPackage.WriteMap(_campaign, id)), ["*.t2map ; Tzar2 map"]);
            }
        });
        Forms.Button(left, "Delete selected map", () =>
        {
            if (_selected is { } id)
            {
                _campaign.RemoveMap(id);
                _selected = null;
            }
        });
        var order = new HBoxContainer();
        Forms.Button(order, "Up", () => Move(-1));
        Forms.Button(order, "Down", () => Move(+1));
        left.AddChild(order);
        split.AddChild(left);
        var middle = new HSplitContainer();
        middle.AddChild(_graph);
        middle.AddChild(Scrolled(_details));
        split.AddChild(middle);
        outer.AddChild(split);
        _maps.ItemSelected += i =>
        {
            _selected = _campaign.State.Maps[(int)i].Id;
            _dirty = true;
        };
        _maps.ItemActivated += i => EditMap(_campaign.State.Maps[(int)i].Id);
        _graph.MapSelected += id =>
        {
            _selected = id;
            _dirty = true;
        };
        _graph.EndNodeMove += SavePositions;
        CloseRequested += QueueFree;
        _campaign.Changed += () => _dirty = true;
    }

    /// <summary>The campaign being edited.</summary>
    public CampaignDocument Campaign => _campaign;

    /// <summary>Raised when the user plays the campaign: the source and the map to start at (null = the entry).</summary>
    public event Action<CampaignPlaytest, string?>? PlayRequested;

    /// <inheritdoc/>
    public override void _Ready() => Forms.ShowCentered(this);

    /// <summary>Opens a <c>.t2camp</c>.</summary>
    public bool OpenFile(string path)
    {
        try
        {
            SetCampaign(CampaignPackage.Read(File.ReadAllBytes(path), _screen.RulesFor, Path.GetFileName(path)), path);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _screen.Status("Cannot open " + path + ": " + e.Message);
            return false;
        }
    }

    /// <summary>Saves the campaign to <paramref name="path"/>.</summary>
    public bool SaveTo(string path)
    {
        try
        {
            byte[] bytes = CampaignPackage.Write(_campaign);
            File.WriteAllBytes(path + ".tmp", bytes);
            File.Move(path + ".tmp", path, overwrite: true);
            _campaign.MarkSaved();
            _path = path;
            _screen.Status($"Saved campaign {path} ({bytes.Length:N0} bytes).");
            _dirty = true;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _screen.Status("Saving the campaign failed: " + e.Message);
            return false;
        }
    }

    /// <summary>Shows another campaign.</summary>
    public void SetCampaign(CampaignDocument campaign, string? path)
    {
        _campaign = campaign;
        _path = path;
        _selected = null;
        _campaign.Changed += () => _dirty = true;
        _dirty = true;
    }

    /// <summary>Saves (with the last path) or asks for a file.</summary>
    public void Save(bool saveAs)
    {
        if (_path is not null && !saveAs)
        {
            SaveTo(_path);
            return;
        }

        Files.Save(this, _campaign.State.Id + ".t2camp", path => SaveTo(path.EndsWith(".t2camp", StringComparison.OrdinalIgnoreCase) ? path : path + ".t2camp"), ["*.t2camp ; Tzar2 campaign"]);
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        CampaignState s = _campaign.State;
        _maps.Clear();
        foreach (CampaignEntry m in s.Maps)
        {
            int i = _maps.AddItem((m.IsEntry ? "▶ " : "   ") + m.MapName + (m.Document.IsDirty ? " *" : string.Empty));
            if (m.Id == _selected)
            {
                _maps.Select(i);
            }
        }

        CampaignGraph graph;
        try
        {
            graph = _campaign.Graph();
        }
        catch (ArgumentException e)
        {
            _status.Text = e.Message;
            return;
        }

        _graph.Show(graph.Nodes.Select(n => new GraphMap(n.Id, n.Name, n.IsEntry, n.Reachable ? null : "⚠ unreachable", s.Positions.TryGetValue(n.Id, out (int X, int Y) p) ? new Vector2(p.X, p.Y) : null)).ToList(), graph.Edges);
        ValidationResult result = _campaign.Validate(new ValidationOptions { Ruleset = _screen.Controller.Ruleset });
        _status.Text = $"{s.Name}: {s.Maps.Count} maps, {graph.Edges.Length} transitions · {result.Count(ValidationSeverity.Blocking)} blocking, {result.Count(ValidationSeverity.Error)} errors, {result.Count(ValidationSeverity.Warning)} warnings{(_campaign.IsDirty ? " · unsaved" : string.Empty)}";
        Details(graph, result);
    }

    private void Details(CampaignGraph graph, ValidationResult result)
    {
        Forms.Clear(_details);
        CampaignState s = _campaign.State;
        Forms.Heading(_details, "Campaign");
        GridContainer g = Forms.Grid(_details);
        Forms.Text(g, "Id", s.Id, v => _campaign.Apply(x => x with { Id = MapPackage.Slug(v) }));
        Forms.Text(g, "Title", s.Name, v => _campaign.Apply(x => x with { Name = v }));
        Forms.Multiline(g, "Description", s.Description, v => _campaign.Apply(x => x with { Description = v }));
        if (_selected is { } id && _campaign.Find(id) is { } m)
        {
            Forms.Heading(_details, "Map " + m.MapName);
            GridContainer mg = Forms.Grid(_details);
            Forms.Text(mg, "Map name (used by actions)", m.MapName, v => _campaign.UpdateMap(id, x => x with { MapName = v }));
            Forms.Multiline(mg, "Description", m.Description, v => _campaign.UpdateMap(id, x => x with { Description = v }));
            Forms.Check(mg, "Starting map", m.IsEntry, v => _campaign.UpdateMap(id, x => x with { IsEntry = v }));
            Forms.Multiline(mg, "Details (map description)", m.Document.State.Scenario.Properties.Description, v =>
                m.Document.Apply("Map details", st => st.WithScenario(sc => sc with { Properties = sc.Properties with { Description = v } })));
            Forms.Button(_details, "Edit this map in the editor", () => EditMap(id));
        }

        Forms.Heading(_details, "Persistence (units carried by name)");
        foreach (System.Collections.Generic.KeyValuePair<string, System.Collections.Immutable.ImmutableArray<string>> p in graph.Persistent)
        {
            Forms.Label(_details, $"{p.Key}: {string.Join(" → ", p.Value)}");
        }

        Forms.Heading(_details, "Carried variables (#name)");
        foreach (CarriedVariable v in _campaign.CarriedVariables())
        {
            Forms.Label(_details, $"{v.Name}: {string.Join(", ", v.Maps)}");
        }

        Forms.Heading(_details, "Validation");
        foreach (ValidationIssue i in result.Issues.Where(i => i.Severity >= ValidationSeverity.Warning).Take(60))
        {
            Forms.Label(_details, $"[{i.Severity}] {i.Message}").AutowrapMode = TextServer.AutowrapMode.WordSmart;
        }
    }

    private void EditMap(string id)
    {
        if (_campaign.Find(id) is { } m)
        {
            _screen.Open(m.Document, null);
            _screen.CampaignContext = this;
            _screen.Status($"Editing map '{m.MapName}' of campaign '{_campaign.State.Name}'. Ctrl+S saves the campaign.");
        }
    }

    private void Move(int by)
    {
        if (_selected is { } id)
        {
            int i = _campaign.State.Maps.FindIndex(m => m.Id == id);
            _campaign.MoveMap(id, i + by);
        }
    }

    private void Play(string? mapId)
    {
        if (_campaign.Validate(new ValidationOptions { Ruleset = _screen.Controller.Ruleset }).BlocksPlay)
        {
            _screen.Status("The campaign cannot be played: fix its blocking findings first.");
            return;
        }

        PlayRequested?.Invoke(new CampaignPlaytest(_campaign), mapId);
    }

    private void SavePositions()
    {
        _campaign.Apply(s =>
        {
            var positions = s.Positions;
            foreach (Node child in _graph.GetChildren())
            {
                if (child is GraphNode node)
                {
                    positions = positions.SetItem(node.Name.ToString(), ((int)node.PositionOffset.X, (int)node.PositionOffset.Y));
                }
            }

            return s with { Positions = positions };
        });
    }

    private static Control Scrolled(Control inner)
    {
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(340, 0) };
        inner.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(inner);
        return scroll;
    }
}
