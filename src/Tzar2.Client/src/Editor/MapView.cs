using System;
using System.Collections.Generic;
using Godot;
using Tzar2.Client.Game;
using Tzar2.Client.Logic;
using Tzar2.Client.Viewer;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Content.Terrain;
using Tzar2.Core.Ids;
using Tzar2.Game.Editing;
using Tzar2.Game.Session;

namespace Tzar2.Client.Editor;

/// <summary>
/// The editor's map: the game's own world renderer (REQ-EDT-01) inside a sub-viewport, an overlay for the editor marks
/// (red invalid tiles, cliffs, selection, brush, starting locations, areas, sound sources), pointer and key input
/// forwarded to the <see cref="EditorController"/>, scrolling by keys, middle drag, the mini-map and auto-scroll while
/// painting near the edge (§18.2).
/// </summary>
public partial class MapView : Control
{
    /// <summary>Pixels per tile (the composer's tile size).</summary>
    public const int TilePixels = TerrainComposer.TileSize;

    private const int UnitsPerPixel = WorldRenderer.UnitsPerPixel;

    private readonly ContentStore _store;
    private readonly PresentationCatalog? _catalog;
    private readonly EditorController _controller;
    private readonly SubViewport _viewport;
    private readonly Overlay _overlay;
    private readonly GameFrame _frame = new();
    private readonly FrameInterpolator _interpolator = new();
    private WorldRenderer? _world;
    private SpriteBank? _bank;
    private TerrainSet? _set;
    private Vector2I _pan;
    private bool _buttonDown;
    private bool _panning;
    private Vector2 _panFrom;
    private bool _frameDirty = true;
    private double _time;

    /// <summary>Creates the view.</summary>
    public MapView(ContentStore store, PresentationCatalog? catalog, EditorController controller)
    {
        _store = store;
        _catalog = catalog;
        _controller = controller;
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        ClipContents = true;
        var container = new SubViewportContainer { Stretch = true, MouseFilter = MouseFilterEnum.Ignore };
        container.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(container);
        _viewport = new SubViewport { HandleInputLocally = false, TransparentBg = false, Disable3D = true };
        container.AddChild(_viewport);
        _overlay = new Overlay(this) { ZIndex = 200 };
        _viewport.AddChild(_overlay);
    }

    /// <summary>Raised when the camera moves (mini-map frame).</summary>
    public event Action? CameraMoved;

    /// <summary>The camera's top-left in world pixels.</summary>
    public Vector2I Pan => _pan;

    /// <summary>Show the tile grid.</summary>
    public bool ShowGrid { get; set; }

    /// <summary>The terrain renderer, or null when the package has no terrain art (flat colours are drawn instead).</summary>
    public WorldRenderer? World => _world;

    /// <summary>Average colour per tile (mini-map), or null.</summary>
    public Color[]? TileColours => _world?.Terrain.TileColours;

    /// <summary>Areas to outline (named areas, the selected trigger's area), with labels.</summary>
    public List<(RectArea Area, string Label, Color Colour)> Areas { get; } = [];

    /// <summary>Rebuilds the renderer for the controller's document (new map, opened map).</summary>
    public void Rebuild()
    {
        _world?.Dispose();
        _world = null;
        MapDocument doc = _controller.Document;
        string setId = doc.TerrainSet ?? "grass";
        _set = null;
        foreach (TerrainSet s in _store.TerrainSets)
        {
            if (string.Equals(s.Id, setId, StringComparison.OrdinalIgnoreCase))
            {
                _set = s;
            }
        }

        _set ??= _store.TerrainSets.Count > 0 ? _store.TerrainSets[0] : null;
        if (_set is not null && _catalog is not null)
        {
            if (_bank is null)
            {
                _bank = new SpriteBank(_store);
                foreach (EntityTypeDefinition type in _controller.Ruleset.EntityTypes)
                {
                    foreach (string image in _catalog.Type(type.Id).Images.Values)
                    {
                        _bank.Add(image);
                    }
                }

                _bank.AddTrees(_set);
                _bank.Upload();
            }

            _world = new WorldRenderer(this, _viewport.World2D.Canvas, _controller.Ruleset, _catalog, _bank, _set, Grid(doc), _store.LoadImage, null);
        }

        _pan = Vector2I.Zero;
        _frameDirty = true;
        CameraMoved?.Invoke();
    }

    /// <summary>The document changed: recompose the touched terrain chunks and rebuild the object frame.</summary>
    public void OnChanged(DocumentChange change)
    {
        if (change.TerrainChanged && _world is not null)
        {
            _world.Terrain.Recompose(Grid(_controller.Document), change.TileLeft, change.TileTop, change.TileRight, change.TileBottom);
        }

        if (change.StateChanged)
        {
            _frameDirty = true;
        }

        _overlay.QueueRedraw();
    }

    /// <summary>Marks the objects for a redraw (selection, layers).</summary>
    public void Invalidate()
    {
        _frameDirty = true;
        _overlay.QueueRedraw();
    }

    /// <summary>Centres the camera on a tile.</summary>
    public void CenterOnTile(int tileX, int tileY)
    {
        Vector2 size = Size;
        _pan = ClampPan(new Vector2I((tileX * TilePixels) + (TilePixels / 2) - (int)(size.X / 2), (tileY * TilePixels) + (TilePixels / 2) - (int)(size.Y / 2)));
        CameraMoved?.Invoke();
        _overlay.QueueRedraw();
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        _time += delta;
        MapDocument doc = _controller.Document;
        if (_frameDirty)
        {
            _frameDirty = false;
            EditorFrames.Fill(_frame, doc.State, _controller.Ruleset, 8, o => !_controller.Layers.IsHidden(LayerSettings.LayerOf(o, _controller.Palette)));
            _interpolator.Push(_frame);
        }

        Scroll(delta);
        if (_world is not null)
        {
            var size = (Vector2I)Size;
            Ghost? ghost = null;
            if (_controller.Tool == EditorTool.Place && _controller.Ruleset.EntityTypeId(_controller.PlaceKey) is >= 0 and var type)
            {
                MapObject candidate = PlaceTools.Placement(_controller.Ruleset, _controller.PlaceKey, _controller.PlaceOwner, _controller.Hover.TileX, _controller.Hover.TileY);
                ghost = new Ghost((ushort)type, _controller.Hover.TileX, _controller.Hover.TileY, new Occupancy(doc, _controller.Ruleset).Check(candidate).Ok);
            }

            _world.Render(_frame, true, _interpolator, 1000, _pan, Vector2I.Zero, size, (long)(_time * 1000), Array.Empty<EntityId>(), 0, new WorldOptions { Fog = false, DayNight = false }, 0f, ghost);
        }

        _overlay.Position = -(Vector2)_pan;
        _overlay.QueueRedraw();
    }

    /// <inheritdoc/>
    public override void _GuiInput(InputEvent @event)
    {
        MapDocument doc = _controller.Document;
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } mb:
                _panning = mb.Pressed;
                _panFrom = mb.Position;
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel when wheel.Pressed:
                _pan = ClampPan(_pan + new Vector2I(0, wheel.ButtonIndex == MouseButton.WheelUp ? -96 : 96));
                CameraMoved?.Invoke();
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                GrabFocus();
                if (mb.Pressed)
                {
                    _buttonDown = true;
                    _controller.PointerDown(PointAt(mb.Position), Mods(mb));
                }
                else if (_buttonDown)
                {
                    _buttonDown = false;
                    _controller.PointerUp(PointAt(mb.Position), Mods(mb));
                }

                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }:
                _controller.Select([]);
                AcceptEvent();
                break;
            case InputEventMouseMotion motion:
                if (_panning)
                {
                    _pan = ClampPan(_pan - (Vector2I)(motion.Position - _panFrom));
                    _panFrom = motion.Position;
                    CameraMoved?.Invoke();
                }
                else if (_buttonDown)
                {
                    _controller.PointerDrag(PointAt(motion.Position));
                }
                else
                {
                    _controller.PointerHover(PointAt(motion.Position));
                }

                break;
            case InputEventKey { Pressed: true } key:
                string name = key.Keycode switch
                {
                    Godot.Key.Delete or Godot.Key.Backspace => "Delete",
                    Godot.Key.Tab => "Tab",
                    Godot.Key.Escape => "Escape",
                    >= Godot.Key.Key1 and <= Godot.Key.Key8 when !key.CtrlPressed => ((char)('1' + (key.Keycode - Godot.Key.Key1))).ToString(),
                    _ => string.Empty,
                };
                if (name.Length > 0 && _controller.Key(name, Mods(key)))
                {
                    AcceptEvent();
                }

                break;
        }

        _ = doc;
    }

    /// <summary>The map point under a local position.</summary>
    public MapPoint PointAt(Vector2 local)
    {
        int px = (int)local.X + _pan.X, py = (int)local.Y + _pan.Y;
        MapDocument doc = _controller.Document;
        int x = Math.Clamp(px * UnitsPerPixel, 0, (doc.Width * MapData.UnitsPerTile) - 1);
        int y = Math.Clamp(py * UnitsPerPixel, 0, (doc.Height * MapData.UnitsPerTile) - 1);
        return MapPoint.FromWorld(x, y);
    }

    /// <summary>The terrain grid the composer draws: the painted corners with a set, else the derived presentation.</summary>
    private TerrainGrid Grid(MapDocument doc)
    {
        if (doc.TerrainSet is not null && _set is not null && string.Equals(doc.TerrainSet, _set.Id, StringComparison.OrdinalIgnoreCase))
        {
            var grid = new TerrainGrid(doc.Width, doc.Height, 0);
            for (int y = 0; y <= doc.Height; y++)
            {
                for (int x = 0; x <= doc.Width; x++)
                {
                    grid[x, y] = doc.PaintedAt(x, y);
                }
            }

            return grid;
        }

        return TerrainPresentation.Build(doc.BuildMap(), _controller.Ruleset, _set!);
    }

    private static EditorModifiers Mods(InputEventWithModifiers e)
        => (e.ShiftPressed ? EditorModifiers.Shift : 0) | (e.CtrlPressed || e.MetaPressed ? EditorModifiers.Ctrl : 0) | (e.AltPressed ? EditorModifiers.Alt : 0);

    private Vector2I ClampPan(Vector2I pan)
    {
        MapDocument doc = _controller.Document;
        int maxX = Math.Max(0, (doc.Width * TilePixels) - (int)Size.X), maxY = Math.Max(0, (doc.Height * TilePixels) - (int)Size.Y);
        return new Vector2I(Math.Clamp(pan.X, 0, maxX), Math.Clamp(pan.Y, 0, maxY));
    }

    // Arrow keys scroll; while painting or dragging near the edge the map scrolls by itself (§18.2 auto-scroll).
    private void Scroll(double delta)
    {
        int speed = (int)(900 * delta);
        var move = Vector2I.Zero;
        if (HasFocus())
        {
            move.X += Input.IsKeyPressed(Godot.Key.Left) ? -speed : Input.IsKeyPressed(Godot.Key.Right) ? speed : 0;
            move.Y += Input.IsKeyPressed(Godot.Key.Up) ? -speed : Input.IsKeyPressed(Godot.Key.Down) ? speed : 0;
        }

        if (_buttonDown)
        {
            Vector2 m = GetLocalMousePosition();
            const int edge = 24;
            move.X += m.X < edge ? -speed : m.X > Size.X - edge ? speed : 0;
            move.Y += m.Y < edge ? -speed : m.Y > Size.Y - edge ? speed : 0;
        }

        if (move != Vector2I.Zero)
        {
            _pan = ClampPan(_pan + move);
            if (_buttonDown)
            {
                _controller.PointerDrag(PointAt(GetLocalMousePosition()));
            }

            CameraMoved?.Invoke();
        }
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        _world?.Dispose();
        _bank?.Dispose();
    }

    /// <summary>The editor marks, drawn in world pixels above the renderer.</summary>
    private sealed partial class Overlay(MapView view) : Node2D
    {
        public override void _Draw()
        {
            EditorController c = view._controller;
            MapDocument doc = c.Document;
            int t = TilePixels;
            Vector2I pan = view._pan;
            Vector2 size = view.Size;
            int x0 = Math.Max(0, pan.X / t), y0 = Math.Max(0, pan.Y / t);
            int x1 = Math.Min(doc.Width - 1, (pan.X + (int)size.X) / t), y1 = Math.Min(doc.Height - 1, (pan.Y + (int)size.Y) / t);
            if (view._world is null)
            {
                // No terrain art in the package: flat colours.
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        DrawRect(new Rect2(x * t, y * t, t, t), doc.IsWaterTile(x, y) ? new Color(0.15f, 0.25f, 0.55f) : doc.IsLandTile(x, y) ? new Color(0.3f, 0.45f, 0.18f) : new Color(0.55f, 0.5f, 0.3f));
                    }
                }

            }

            // Object marks: the footprint outline of structures and an owner dot, so every object is visible and
            // pickable even where the package has no sprite for it.
            var cull = new Rect2(pan.X - t, pan.Y - t, size.X + (2 * t), size.Y + (2 * t));
            foreach (EditorObject o in doc.State.Objects)
            {
                var foot = new Vector2(o.X / UnitsPerPixel, o.Y / UnitsPerPixel);
                if (!cull.HasPoint(foot) || c.Layers.IsHidden(LayerSettings.LayerOf(o, c.Palette)))
                {
                    continue;
                }

                Color colour = o.Owner < 8 ? Colours.Player(o.Owner) : new Color(0.15f, 0.35f, 0.12f);
                if (c.Ruleset.EntityTypeId(o.TypeKey) is >= 0 and var type && c.Ruleset.EntityTypes[type].HasFootprint)
                {
                    (int fx, int fy, int fw, int fh) = Occupancy.Footprint(c.Ruleset.EntityTypes[type], o.Placement);
                    DrawRect(new Rect2(fx * t, fy * t, fw * t, fh * t), new Color(colour, 0.55f), filled: false, width: 1);
                }
                else
                {
                    DrawCircle(foot, 3, colour);
                }
            }

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (!doc.IsDrawableTile(x, y))
                    {
                        DrawRect(new Rect2(x * t, y * t, t, t), new Color(1, 0, 0, 0.45f));
                    }

                    if (doc.IsCliff(x, y))
                    {
                        DrawRect(new Rect2((x * t) + 2, (y * t) + 2, t - 4, t - 4), new Color(0.35f, 0.25f, 0.15f, 0.8f), filled: false, width: 2);
                    }

                    if (view.ShowGrid)
                    {
                        DrawRect(new Rect2(x * t, y * t, t, t), new Color(1, 1, 1, 0.08f), filled: false);
                    }
                }
            }

            foreach (MapDecoration d in doc.State.Decorations)
            {
                if (!c.Layers.IsHidden(EditorLayer.Decorations))
                {
                    DrawCircle(new Vector2((d.TileX * t) + (t / 2), (d.TileY * t) + (t / 2)), 3, new Color(0.9f, 0.85f, 0.4f, 0.8f));
                }
            }

            // Selection.
            foreach (int id in c.Selection)
            {
                if (doc.State.Find(id) is not { } o)
                {
                    continue;
                }

                Rect2 r = Box(c, o);
                (int dx, int dy) = c.DragOffset;
                DrawRect(new Rect2(r.Position + new Vector2(dx * t, dy * t), r.Size), new Color(1, 1, 0.3f), filled: false, width: 2);
            }

            if (!c.Layers.IsHidden(EditorLayer.Markers))
            {
                foreach (KingdomDef k in doc.State.Scenario.Kingdoms)
                {
                    if (k.StartTileX >= 0)
                    {
                        var at = new Vector2((k.StartTileX * t) + (t / 2), (k.StartTileY * t) + (t / 2));
                        DrawCircle(at, 10, Colours.Player(k.Number - 1));
                        DrawString(ThemeDB.FallbackFont, at + new Vector2(-4, 5), k.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), HorizontalAlignment.Left, -1, 14, Colors.White);
                    }
                }

                foreach (CustomSoundDef s in doc.State.Scenario.Sounds)
                {
                    if (s.SourceObject is null)
                    {
                        DrawCircle(new Vector2((s.X * t) + (t / 2), (s.Y * t) + (t / 2)), 6, new Color(0.4f, 0.8f, 1f, 0.8f));
                    }
                }

                foreach ((RectArea a, string label, Color colour) in view.Areas)
                {
                    var r = new Rect2(a.Left * t, a.Top * t, (a.Right - a.Left + 1) * t, (a.Bottom - a.Top + 1) * t);
                    DrawRect(r, colour, filled: false, width: 2);
                    DrawString(ThemeDB.FallbackFont, r.Position + new Vector2(3, 14), label, HorizontalAlignment.Left, -1, 12, colour);
                }
            }

            (bool active, int l, int tp, int rt, int b) = c.DragRect;
            if (active)
            {
                DrawRect(new Rect2(l * t, tp * t, (rt - l + 1) * t, (b - tp + 1) * t), new Color(0.6f, 0.9f, 1f), filled: false, width: 1);
            }

            // Brush outline.
            MapPoint h = c.Hover;
            if (c.Tool is EditorTool.Terrain or EditorTool.Cliff)
            {
                int side = Brushes.TerrainSizes[Math.Clamp(c.BrushSize, 0, Brushes.TerrainSizes.Length - 1)];
                int bx = c.Tool == EditorTool.Terrain ? h.CornerX - ((side - 1) / 2) : h.TileX - ((side - 1) / 2);
                int by = c.Tool == EditorTool.Terrain ? h.CornerY - ((side - 1) / 2) : h.TileY - ((side - 1) / 2);
                Vector2 offset = c.Tool == EditorTool.Terrain ? new Vector2(-t / 2, -t / 2) : Vector2.Zero;
                DrawRect(new Rect2(new Vector2(bx * t, by * t) + offset, new Vector2(side * t, side * t)), new Color(1, 1, 1, 0.7f), filled: false, width: 1);
            }
            else if (c.Tool == EditorTool.Trees)
            {
                DrawArc(new Vector2((h.TileX * t) + (t / 2), (h.TileY * t) + (t / 2)), ((Brushes.TreeRadii[Math.Clamp(c.TreeBrush.Radius, 0, 4)] * 2) + 1) * t / 2f, 0, MathF.Tau, 32, new Color(0.5f, 1, 0.5f, 0.8f));
            }
            else
            {
                DrawRect(new Rect2(h.TileX * t, h.TileY * t, t, t), new Color(1, 1, 1, 0.35f), filled: false);
            }
        }

        private static Rect2 Box(EditorController c, EditorObject o)
        {
            int t = TilePixels;
            if (c.Ruleset.EntityTypeId(o.TypeKey) is >= 0 and var type && c.Ruleset.EntityTypes[type].HasFootprint)
            {
                (int x, int y, int w, int h) = Occupancy.Footprint(c.Ruleset.EntityTypes[type], o.Placement);
                return new Rect2(x * t, y * t, w * t, h * t);
            }

            return new Rect2((o.X / UnitsPerPixel) - 12, (o.Y / UnitsPerPixel) - 20, 24, 28);
        }
    }
}

/// <summary>Player colours for editor marks.</summary>
public static class Colours
{
    private static readonly Color[] Ramp =
    [
        new(0.85f, 0.15f, 0.15f), new(0.2f, 0.35f, 0.9f), new(0.2f, 0.75f, 0.25f), new(0.95f, 0.85f, 0.2f),
        new(0.65f, 0.25f, 0.8f), new(0.95f, 0.55f, 0.15f), new(0.2f, 0.8f, 0.85f), new(0.9f, 0.9f, 0.9f),
    ];

    /// <summary>The mark colour of player slot <paramref name="slot"/>.</summary>
    public static Color Player(int slot) => (uint)slot < (uint)Ramp.Length ? Ramp[slot] : new Color(0.6f, 0.6f, 0.6f);
}
