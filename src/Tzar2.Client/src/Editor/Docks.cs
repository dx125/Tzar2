using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Godot;
using Tzar2.Content.Compiled;
using Tzar2.Content.Editing;

namespace Tzar2.Client.Editor;

/// <summary>
/// The Object Locator (TDD §18.2): filter by type, player and named objects or text; view (centre the map), select for
/// the properties dock, delete.
/// </summary>
public partial class LocatorPanel : VBoxContainer
{
    private static readonly string[] Owners = ["(any)", "Kingdom 1", "Kingdom 2", "Kingdom 3", "Kingdom 4", "Kingdom 5", "Kingdom 6", "Kingdom 7", "Kingdom 8", "Nature"];
    private readonly EditorController _c;
    private readonly Action<int, int> _view;
    private readonly ItemList _list = new() { SizeFlagsVertical = SizeFlags.ExpandFill, SelectMode = ItemList.SelectModeEnum.Multi };
    private ImmutableArray<EditorObject> _found = [];
    private LocatorFilter _filter = new();

    /// <summary>Creates the locator; <paramref name="view"/> centres the map on a tile.</summary>
    public LocatorPanel(EditorController controller, Action<int, int> view)
    {
        _c = controller;
        _view = view;
        Name = "Locator";
        SizeFlagsVertical = SizeFlags.ExpandFill;
        GridContainer g = Forms.Grid(this);
        Forms.Text(g, "Type", string.Empty, v =>
        {
            _filter = _filter with { TypeKey = v.Length == 0 ? null : v };
            Refresh();
        });
        Forms.Choice(g, "Player", Owners, 0, i =>
        {
            _filter = _filter with { Owner = i == 0 ? null : i == 9 ? MapObject.Nature : (byte)(i - 1) };
            Refresh();
        });
        Forms.Check(g, "Named only", false, v =>
        {
            _filter = _filter with { NamedOnly = v };
            Refresh();
        });
        Forms.Text(g, "Text", string.Empty, v =>
        {
            _filter = _filter with { Text = v };
            Refresh();
        });
        AddChild(_list);
        _list.MultiSelected += (_, _) => _c.Select(_list.GetSelectedItems().Select(i => _found[i].Id));
        _list.ItemActivated += i => _view(_found[(int)i].TileX, _found[(int)i].TileY);
        var row = new HBoxContainer();
        Forms.Button(row, "View", () =>
        {
            int[] sel = _list.GetSelectedItems();
            if (sel.Length > 0)
            {
                _view(_found[sel[0]].TileX, _found[sel[0]].TileY);
            }
        });
        Forms.Button(row, "Delete", _c.DeleteSelection);
        AddChild(row);
    }

    /// <summary>Re-runs the filter.</summary>
    public void Refresh()
    {
        _list.Clear();
        _found = ObjectLocator.Find(_c.Document.State, _filter);
        foreach (EditorObject o in _found.Take(5000))
        {
            string owner = o.Owner == MapObject.Nature ? "Nature" : "K" + (o.Owner + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _list.AddItem($"#{o.Id} {o.Label} [{owner}] ({o.TileX}, {o.TileY})");
        }
    }
}

/// <summary>
/// The live validation panel (TDD §18.3): errors and warnings, filtered by severity, click to locate; play is blocked only
/// by blocking findings.
/// </summary>
public partial class ValidationPanel : VBoxContainer
{
    private readonly ItemList _list = new() { SizeFlagsVertical = SizeFlags.ExpandFill };
    private readonly Label _summary = new();
    private readonly Action<EditorLocation> _locate;
    private ValidationResult _result = new([]);
    private ValidationSeverity _minimum = ValidationSeverity.Warning;
    private List<ValidationIssue> _shown = [];

    /// <summary>Creates the panel; <paramref name="locate"/> shows a finding's place.</summary>
    public ValidationPanel(Action<EditorLocation> locate)
    {
        _locate = locate;
        Name = "Validation";
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddChild(_summary);
        GridContainer g = Forms.Grid(this);
        Forms.Choice(g, "Show", ["info and above", "warnings and above", "errors and above", "blocking only"], 1, i =>
        {
            _minimum = (ValidationSeverity)i;
            Show(_result);
        });
        AddChild(_list);
        _list.ItemActivated += i => _locate(_shown[(int)i].Location);
        _list.ItemSelected += i => _locate(_shown[(int)i].Location);
    }

    /// <summary>Whether the current result blocks playing.</summary>
    public bool BlocksPlay => _result.BlocksPlay;

    /// <summary>The current result.</summary>
    public ValidationResult Result => _result;

    /// <summary>Shows a validation result.</summary>
    public void Show(ValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _result = result;
        _list.Clear();
        _shown = result.Issues.Where(i => i.Severity >= _minimum).ToList();
        foreach (ValidationIssue i in _shown)
        {
            int index = _list.AddItem($"[{i.Severity}] {i.Message} — {i.Location}");
            _list.SetItemCustomFgColor(index, i.Severity switch
            {
                ValidationSeverity.Blocking => new Color(1, 0.35f, 0.35f),
                ValidationSeverity.Error => new Color(1, 0.6f, 0.4f),
                ValidationSeverity.Warning => new Color(1, 0.9f, 0.5f),
                _ => new Color(0.8f, 0.8f, 0.8f),
            });
        }

        _summary.Text = $"{result.Count(ValidationSeverity.Blocking)} blocking, {result.Count(ValidationSeverity.Error)} errors, {result.Count(ValidationSeverity.Warning)} warnings, {result.Count(ValidationSeverity.Info)} notes"
            + (result.BlocksPlay ? " — playing is blocked" : string.Empty);
    }
}

/// <summary>Layers with lock and hide (TDD §18.3).</summary>
public partial class LayersPanel : VBoxContainer
{
    /// <summary>Creates the panel.</summary>
    public LayersPanel(LayerSettings layers, Action changed)
    {
        ArgumentNullException.ThrowIfNull(layers);
        Name = "Layers";
        GridContainer g = Forms.Grid(this, 3);
        Forms.Label(g, "Layer");
        Forms.Label(g, "Hide");
        Forms.Label(g, "Lock");
        foreach (EditorLayer layer in Enum.GetValues<EditorLayer>())
        {
            Forms.Label(g, layer.ToString());
            Forms.Check(g, string.Empty, layers.IsHidden(layer), v =>
            {
                layers.SetHidden(layer, v);
                changed();
            }, inGrid: false);
            Forms.Check(g, string.Empty, layers.IsLocked(layer), v =>
            {
                layers.SetLocked(layer, v);
                changed();
            }, inGrid: false);
        }
    }
}

/// <summary>The mini-map: terrain colours, objects, the camera frame; click or drag to move the camera (§18.2).</summary>
public partial class Minimap : TextureRect
{
    private readonly EditorController _c;
    private readonly MapView _view;
    private Image? _image;
    private ImageTexture? _texture;

    /// <summary>Creates the mini-map.</summary>
    public Minimap(EditorController controller, MapView view)
    {
        _c = controller;
        _view = view;
        CustomMinimumSize = new Vector2(200, 200);
        ExpandMode = ExpandModeEnum.IgnoreSize;
        StretchMode = StretchModeEnum.KeepAspectCentered;
        TextureFilter = TextureFilterEnum.Nearest;
        MouseFilter = MouseFilterEnum.Stop;
    }

    /// <summary>Redraws the picture.</summary>
    public void Refresh()
    {
        MapDocument doc = _c.Document;
        if (_image is null || _image.GetWidth() != doc.Width || _image.GetHeight() != doc.Height)
        {
            _image = Image.CreateEmpty(doc.Width, doc.Height, false, Image.Format.Rgba8);
            _texture = null;
        }

        Color[]? colours = _view.TileColours;
        for (int y = 0; y < doc.Height; y++)
        {
            for (int x = 0; x < doc.Width; x++)
            {
                Color c = colours is not null && colours.Length == doc.Width * doc.Height ? colours[(y * doc.Width) + x]
                    : doc.IsWaterTile(x, y) ? new Color(0.15f, 0.25f, 0.55f) : new Color(0.3f, 0.45f, 0.18f);
                _image.SetPixel(x, y, doc.IsCliff(x, y) ? new Color(0.35f, 0.25f, 0.15f) : c);
            }
        }

        foreach (EditorObject o in doc.State.Objects)
        {
            if (o.TileX < doc.Width && o.TileY < doc.Height)
            {
                _image.SetPixel(o.TileX, o.TileY, o.Owner < 8 ? Colours.Player(o.Owner) : new Color(0.08f, 0.22f, 0.06f));
            }
        }

        if (_texture is null)
        {
            _texture = ImageTexture.CreateFromImage(_image);
            Texture = _texture;
        }
        else
        {
            _texture.Update(_image);
        }

        QueueRedraw();
    }

    /// <inheritdoc/>
    public override void _Draw()
    {
        MapDocument doc = _c.Document;
        Rect2 area = Area();
        float sx = area.Size.X / doc.Width, sy = area.Size.Y / doc.Height;
        Vector2I pan = _view.Pan;
        var frame = new Rect2(area.Position + new Vector2(pan.X / (float)MapView.TilePixels * sx, pan.Y / (float)MapView.TilePixels * sy), new Vector2(_view.Size.X / MapView.TilePixels * sx, _view.Size.Y / MapView.TilePixels * sy));
        DrawRect(frame, Colors.White, filled: false);
    }

    /// <inheritdoc/>
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb)
        {
            Move(mb.Position);
        }
        else if (@event is InputEventMouseMotion { ButtonMask: MouseButtonMask.Left } motion)
        {
            Move(motion.Position);
        }
    }

    private void Move(Vector2 at)
    {
        MapDocument doc = _c.Document;
        Rect2 area = Area();
        int tx = (int)((at.X - area.Position.X) / area.Size.X * doc.Width), ty = (int)((at.Y - area.Position.Y) / area.Size.Y * doc.Height);
        _view.CenterOnTile(Math.Clamp(tx, 0, doc.Width - 1), Math.Clamp(ty, 0, doc.Height - 1));
        QueueRedraw();
    }

    private Rect2 Area()
    {
        MapDocument doc = _c.Document;
        float scale = Math.Min(Size.X / doc.Width, Size.Y / doc.Height);
        var size = new Vector2(doc.Width * scale, doc.Height * scale);
        return new Rect2((Size - size) / 2, size);
    }
}
