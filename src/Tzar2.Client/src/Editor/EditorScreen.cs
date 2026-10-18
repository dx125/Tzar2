using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Tzar2.Client.Logic;
using Tzar2.Client.Viewer;
using Tzar2.Content.Compiled;
using Tzar2.Content.Editing;
using Tzar2.Content.Terrain;

namespace Tzar2.Client.Editor;

/// <summary>
/// The scenario and campaign editor, a mode of the client (TDD §18, REQ-EDT-01, ADR 0150): menu bar, the tool dock, the
/// map (the game's renderer), the properties / locator / validation / layers docks, the mini-map and the status line.
/// Every change goes through the document model; this class only wires views and commands.
/// </summary>
public partial class EditorScreen : Control
{
    private readonly ContentStore _store;
    private readonly Ruleset _rules;
    private readonly PresentationCatalog? _catalog;
    private readonly EditorController _controller;
    private readonly Label _status = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
    private readonly HashSet<string> _hiddenDocks = [];
    private MapView _view = null!;
    private ToolPanel _tools = null!;
    private PropertiesPanel _properties = null!;
    private LocatorPanel _locator = null!;
    private ValidationPanel _validation = null!;
    private Minimap _minimap = null!;
    private TabContainer _right = null!;
    private Control _left = null!;
    private string? _path;
    private double _validateAt = -1;
    private double _now;
    private string _clipboard = string.Empty;

    /// <summary>Creates the editor over an opened package and its compiled ruleset.</summary>
    public EditorScreen(ContentStore store, Ruleset rules, PresentationCatalog? catalog, MapDocument? document = null, string? path = null)
    {
        _store = store;
        _rules = rules;
        _catalog = catalog;
        _path = path;
        string set = store.TerrainSets.Count > 0 ? store.TerrainSets[0].Id : "grass";
        document ??= MapDocument.CreateEmpty("New map", 64, 64, RulesFor(set));
        _controller = new EditorController(document, rules);
        _controller.TerrainValue = document.Rules.DefaultLand;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    /// <summary>Raised when the user leaves the editor.</summary>
    public event Action? Closed;

    /// <summary>The interaction model.</summary>
    public EditorController Controller => _controller;

    /// <summary>The open document.</summary>
    public MapDocument Document => _controller.Document;

    /// <summary>The validation dock.</summary>
    public ValidationPanel Validation => _validation;

    /// <summary>The map view.</summary>
    public MapView View => _view;

    /// <summary>The file the document was opened from or saved to, or null.</summary>
    public string? FilePath => _path;

    /// <summary>Painting rules for a terrain-set id of the package (null = no set).</summary>
    public TerrainRules RulesFor(string? setId)
    {
        if (setId is null)
        {
            return TerrainRules.WithoutSet(_rules);
        }

        TerrainSet? set = _store.TerrainSets.FirstOrDefault(s => string.Equals(s.Id, setId, StringComparison.OrdinalIgnoreCase));
        return TerrainRules.ForSet(setId, set, _rules);
    }

    /// <inheritdoc/>
    public override void _Ready()
    {
        Window root = GetTree().Root;
        root.ContentScaleMode = Window.ContentScaleModeEnum.Disabled;
        var layout = new VBoxContainer();
        layout.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(layout);
        layout.AddChild(Menus());

        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        layout.AddChild(split);
        var leftBox = new VBoxContainer { CustomMinimumSize = new Vector2(270, 0) };
        _left = leftBox;
        _tools = new ToolPanel(_controller, DecorationCatalog());
        leftBox.AddChild(_tools);
        _view = new MapView(_store, _catalog, _controller);
        _minimap = new Minimap(_controller, _view);
        leftBox.AddChild(_minimap);
        split.AddChild(leftBox);

        var inner = new HSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        split.AddChild(inner);
        inner.AddChild(_view);
        _right = new TabContainer { CustomMinimumSize = new Vector2(340, 0) };
        _properties = new PropertiesPanel(_controller);
        _locator = new LocatorPanel(_controller, (x, y) => _view.CenterOnTile(x, y));
        _validation = new ValidationPanel(Locate);
        _right.AddChild(_properties);
        _right.AddChild(_locator);
        _right.AddChild(_validation);
        _right.AddChild(new LayersPanel(_controller.Layers, _view.Invalidate));
        inner.AddChild(_right);
        layout.AddChild(_status);

        _controller.SelectionChanged += () =>
        {
            _properties.Refresh();
            _view.Invalidate();
        };
        _controller.Status += Status;
        _view.CameraMoved += () => _minimap.QueueRedraw();
        Attach(_controller.Document);
        _view.Rebuild();
        _minimap.Refresh();
        _view.GrabFocus();
        Status("Editor ready. Ctrl+Z undo, Ctrl+Y redo, Ctrl+S save.");
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        GetTree().Root.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        _now += delta;
        if (_validateAt >= 0 && _now >= _validateAt)
        {
            _validateAt = -1;
            Revalidate();
        }
    }

    /// <summary>Runs the validator now (the panel also re-validates half a second after every change).</summary>
    public ValidationResult Revalidate()
    {
        ValidationResult result = MapValidator.Validate(Document, new ValidationOptions { Ruleset = _rules });
        _validation.Show(result);
        return result;
    }

    /// <summary>Replaces the document (new map, open).</summary>
    public void Open(MapDocument document, string? path)
    {
        ArgumentNullException.ThrowIfNull(document);
        Detach(Document);
        _path = path;
        _controller.SetDocument(document);
        _controller.TerrainValue = document.Rules.DefaultLand;
        Attach(document);
        _tools.RefreshTerrain();
        _view.Rebuild();
        _minimap.Refresh();
        _locator.Refresh();
        Revalidate();
        Status(path is null ? "New map." : "Opened " + path);
    }

    /// <summary>Saves the document as a <c>.t2map</c> at <paramref name="path"/>.</summary>
    public bool SaveTo(string path)
    {
        try
        {
            byte[] bytes = MapPackage.Write(Document);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
            Document.MarkSaved();
            _path = path;
            Status($"Saved {path} ({bytes.Length:N0} bytes).");
            UpdateTitle();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status("Save failed: " + e.Message);
            return false;
        }
    }

    /// <summary>Opens a <c>.t2map</c> file.</summary>
    public bool OpenFile(string path)
    {
        try
        {
            Open(MapPackage.Read(File.ReadAllBytes(path), RulesFor, Path.GetFileName(path)), path);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Status("Cannot open " + path + ": " + e.Message);
            return false;
        }
    }

    /// <summary>Shows a status-line message.</summary>
    public void Status(string message)
    {
        _status.Text = message;
        GD.Print("Editor: " + message);
    }

    /// <summary>Undo.</summary>
    public void Undo()
    {
        if (Document.History.CanUndo && !Document.InTransaction)
        {
            string label = Document.History.UndoLabel!;
            Document.Undo();
            Status("Undid: " + label);
        }
    }

    /// <summary>Redo.</summary>
    public void Redo()
    {
        if (Document.History.CanRedo && !Document.InTransaction)
        {
            string label = Document.History.RedoLabel!;
            Document.Redo();
            Status("Redid: " + label);
        }
    }

    /// <inheritdoc/>
    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true } key)
        {
            return;
        }

        bool ctrl = key.CtrlPressed || key.MetaPressed;
        switch (key.Keycode)
        {
            case Key.Z when ctrl && key.ShiftPressed:
            case Key.Y when ctrl:
                Redo();
                break;
            case Key.Z when ctrl:
                Undo();
                break;
            case Key.S when ctrl:
                Save(saveAs: key.ShiftPressed);
                break;
            case Key.C when ctrl:
                _clipboard = _controller.CopySelection();
                DisplayServer.ClipboardSet(_clipboard);
                Status($"Copied {_controller.Selection.Length} object(s).");
                break;
            case Key.V when ctrl:
                string text = DisplayServer.ClipboardGet();
                Status($"Pasted {_controller.Paste(text.Length > 0 ? text : _clipboard)} object(s).");
                break;
            default:
                return;
        }

        GetViewport().SetInputAsHandled();
    }

    private void Attach(MapDocument doc)
    {
        doc.Changed += OnChanged;
        UpdateTitle();
    }

    private void Detach(MapDocument doc) => doc.Changed -= OnChanged;

    private void OnChanged(DocumentChange change)
    {
        _view.OnChanged(change);
        if (change.Kind != ChangeKind.Edit || !InTyping())
        {
            _controller.PruneSelection();
        }

        if (change.Kind is ChangeKind.Undo or ChangeKind.Redo)
        {
            _properties.Refresh();
        }

        if (change.StateChanged)
        {
            _locator.Refresh();
        }

        _minimap.Refresh();
        _validateAt = _now + 0.5;
        UpdateTitle();
    }

    private bool InTyping() => GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit or SpinBox;

    private void UpdateTitle()
    {
        string name = _path is null ? Document.State.Name : Path.GetFileName(_path);
        if (IsInsideTree())
        {
            GetWindow().Title = $"Tzar2 Editor — {name}{(Document.IsDirty ? " *" : string.Empty)}";
        }
    }

    private void Locate(EditorLocation at)
    {
        if (at.X >= 0 && at.Y >= 0)
        {
            _view.CenterOnTile(at.X, at.Y);
        }

        switch (at.Kind)
        {
            case LocationKind.Object:
                _controller.Select([at.Index]);
                _right.CurrentTab = 0;
                break;
            case LocationKind.Kingdom:
                if (Document.State.Scenario.Kingdom(at.Index) is { StartTileX: >= 0 } k)
                {
                    _view.CenterOnTile(k.StartTileX, k.StartTileY);
                }

                Dialogs.Kingdoms(this, Document, _rules);
                break;
            case LocationKind.Properties:
                Dialogs.MapProperties(this, Document);
                break;
            default:
                LocateOther?.Invoke(at);
                break;
        }
    }

    /// <summary>Raised for findings in parts other docks show (triggers, conversations, campaigns).</summary>
    public event Action<EditorLocation>? LocateOther;

    private void Save(bool saveAs)
    {
        if (_path is not null && !saveAs)
        {
            SaveTo(_path);
            return;
        }

        Files.Save(this, MapPackage.Slug(Document.State.Name) + ".t2map", path => SaveTo(path.EndsWith(".t2map", StringComparison.OrdinalIgnoreCase) ? path : path + ".t2map"), ["*.t2map ; Tzar2 map"]);
    }

    private MenuBar Menus()
    {
        var bar = new MenuBar();
        PopupMenu file = Menu(bar, "File");
        Item(file, "New map…", () => Dialogs.NewMap(this, _rules, _store.TerrainSets.Count > 0 ? _store.TerrainSets.Select(s => s.Id).ToArray() : ["grass"], doc => Open(doc, null), RulesFor));
        Item(file, "Open…", () => Files.Open(this, ["*.t2map ; Tzar2 map"], paths => OpenFile(paths[0]), many: false));
        Item(file, "Save (Ctrl+S)", () => Save(saveAs: false));
        Item(file, "Save as… (Ctrl+Shift+S)", () => Save(saveAs: true));
        file.AddSeparator();
        FileMenuExtras?.Invoke(file);
        file.AddSeparator();
        Item(file, "Close editor", () => Closed?.Invoke());

        PopupMenu edit = Menu(bar, "Edit");
        Item(edit, "Undo (Ctrl+Z)", Undo);
        Item(edit, "Redo (Ctrl+Y)", Redo);
        edit.AddSeparator();
        Item(edit, "Copy (Ctrl+C)", () =>
        {
            _clipboard = _controller.CopySelection();
            DisplayServer.ClipboardSet(_clipboard);
        });
        Item(edit, "Paste at the pointer (Ctrl+V)", () => _controller.Paste(_clipboard));
        Item(edit, "Delete selection (Del)", _controller.DeleteSelection);
        edit.AddSeparator();
        Item(edit, "Save selection as prefab…", () => Files.Save(this, "prefab.t2prefab.json", path => File.WriteAllText(path, _controller.CopySelection())));
        Item(edit, "Insert prefab at the pointer…", () => Files.Open(this, ["*.json ; Prefab"], paths => _controller.Paste(File.ReadAllText(paths[0])), many: false));

        PopupMenu view = Menu(bar, "View");
        Item(view, "Toggle grid", () => _view.ShowGrid = !_view.ShowGrid);
        Item(view, "Toggle tool dock", () => _left.Visible = !_left.Visible);
        Item(view, "Toggle side dock", () => _right.Visible = !_right.Visible);
        Item(view, "Validate now", () => Revalidate());

        PopupMenu map = Menu(bar, "Map");
        Item(map, "Map properties…", () => Dialogs.MapProperties(this, Document));
        Item(map, "Kingdoms and diplomacy…", () => Dialogs.Kingdoms(this, Document, _rules));
        Item(map, "User resources…", () => Dialogs.Resources(this, Document, path => Tzar2.Import.Maps.PngEncoder.FromBmp(File.ReadAllBytes(path))));
        Item(map, "Custom sounds and music…", () => Dialogs.Sounds(this, Document, () => _controller.Selection.IsEmpty ? 0 : _controller.Selection[0]));
        MapMenuExtras?.Invoke(map);
        ExtraMenus?.Invoke(bar);
        return bar;
    }

    /// <summary>Lets later parts of the editor add File-menu entries (converter import, campaign editor).</summary>
    public event Action<PopupMenu>? FileMenuExtras;

    /// <summary>Lets later parts of the editor add Map-menu entries.</summary>
    public event Action<PopupMenu>? MapMenuExtras;

    /// <summary>Lets later parts of the editor add menus (Triggers, Play, Campaign).</summary>
    public event Action<MenuBar>? ExtraMenus;

    /// <summary>Adds a menu to the bar.</summary>
    public static PopupMenu Menu(MenuBar bar, string title)
    {
        ArgumentNullException.ThrowIfNull(bar);
        var menu = new PopupMenu { Name = title };
        bar.AddChild(menu);
        return menu;
    }

    /// <summary>Adds an entry to a menu.</summary>
    public static void Item(PopupMenu menu, string label, Action action)
    {
        ArgumentNullException.ThrowIfNull(menu);
        int id = menu.ItemCount;
        menu.AddItem(label, id);
        menu.IdPressed += pressed =>
        {
            if (pressed == id)
            {
                action();
            }
        };
    }

    // Decoration shapes by class: sprite-index names of the package that look like ground patches/objects (the IMAGES.INI
    // groups are not decoded; ⚠ name heuristics).
    private Dictionary<string, IReadOnlyList<string>> DecorationCatalog()
    {
        var patches = new List<string>();
        var objects = new List<string>();
        var water = new List<string>();
        foreach (Content.Assets.SpriteIndexEntry e in _store.Index.Entries)
        {
            string n = e.Name.ToLowerInvariant();
            if (n.Contains("patch", StringComparison.Ordinal) || n.Contains("flower", StringComparison.Ordinal) || n.Contains("grass", StringComparison.Ordinal))
            {
                patches.Add(e.Name);
            }
            else if (n.Contains("reed", StringComparison.Ordinal) || n.Contains("water", StringComparison.Ordinal))
            {
                water.Add(e.Name);
            }
            else if (n.Contains("stone", StringComparison.Ordinal) || n.Contains("sign", StringComparison.Ordinal) || n.Contains("ruin", StringComparison.Ordinal))
            {
                objects.Add(e.Name);
            }
        }

        return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["ground_patch"] = patches,
            ["ground_object"] = objects,
            ["water_object"] = water,
        };
    }
}
