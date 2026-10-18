using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;

namespace Tzar2.Content.Editing;

/// <summary>The editor's tools (TDD §18.2 "Tools", "Terrain", "Trees", "Decorations", "Buildings", "Units", "Items").</summary>
public enum EditorTool : byte
{
    /// <summary>Select (click, rubber band, Shift adds), Alt-drag move, Ctrl-drag copy.</summary>
    Select,

    /// <summary>Paint terrain.</summary>
    Terrain,

    /// <summary>Paint or erase cliffs.</summary>
    Cliff,

    /// <summary>Plant or delete trees.</summary>
    Trees,

    /// <summary>Place objects of the palette (walls by drag).</summary>
    Place,

    /// <summary>Place decorations.</summary>
    Decoration,

    /// <summary>Set a kingdom's starting location.</summary>
    Start,

    /// <summary>Delete objects and decorations under the pointer.</summary>
    Erase,

    /// <summary>Draw a rectangle (named areas, trigger areas).</summary>
    Area,
}

/// <summary>Keyboard modifiers of a pointer or key event.</summary>
[Flags]
public enum EditorModifiers : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Shift.</summary>
    Shift = 1,

    /// <summary>Ctrl (Cmd on macOS).</summary>
    Ctrl = 2,

    /// <summary>Alt.</summary>
    Alt = 4,
}

/// <summary>A point of the map under the pointer: the tile and the nearest corner.</summary>
/// <param name="TileX">Tile column.</param>
/// <param name="TileY">Tile row.</param>
/// <param name="CornerX">Nearest corner column.</param>
/// <param name="CornerY">Nearest corner row.</param>
public readonly record struct MapPoint(int TileX, int TileY, int CornerX, int CornerY)
{
    /// <summary>The point of world coordinates (world units).</summary>
    public static MapPoint FromWorld(int x, int y) => new(
        Math.Max(0, x) / MapData.UnitsPerTile,
        Math.Max(0, y) / MapData.UnitsPerTile,
        (Math.Max(0, x) + (MapData.UnitsPerTile / 2)) / MapData.UnitsPerTile,
        (Math.Max(0, y) + (MapData.UnitsPerTile / 2)) / MapData.UnitsPerTile);
}

/// <summary>
/// The editor's interaction model (ADR 0150): it turns pointer and key events on the map into document edits with the
/// original's semantics (brush strokes are one undo step, Alt-drag moves, Ctrl-drag copies, Ctrl/Alt turn gates and
/// bridges, TAB turns units, keys 1–8 set starting locations, walls by drag). The Godot map view only converts screen
/// positions to <see cref="MapPoint"/>s and draws; the logic here is tested headlessly.
/// </summary>
public sealed class EditorController
{
    private EditTransaction? _stroke;
    private Occupancy? _occupancy;
    private MapPoint _down;
    private MapPoint _last;
    private bool _dragging;
    private DragMode _drag;
    private ulong _strokes;

    /// <summary>Creates a controller over <paramref name="document"/>.</summary>
    public EditorController(MapDocument document, Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ruleset);
        Document = document;
        Ruleset = ruleset;
        Palette = EditorPalette.Build(ruleset);
        TreeBrush = new TreeBrush(Palette.Of(PaletteKind.Tree) is { IsEmpty: false } t ? t[0].Key : "comm.tree", TreeChoice.RandomOfType, 2, 60, false, 1);
    }

    private enum DragMode : byte
    {
        None,
        Band,
        Move,
        Copy,
        Wall,
        Area,
    }

    /// <summary>Raised when the selection changes.</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised with a status-line message (refused placements, results).</summary>
    public event Action<string>? Status;

    /// <summary>Raised when an area drawn with <see cref="EditorTool.Area"/> is finished.</summary>
    public event Action<RectArea>? AreaDrawn;

    /// <summary>The document.</summary>
    public MapDocument Document { get; private set; }

    /// <summary>The ruleset.</summary>
    public Ruleset Ruleset { get; }

    /// <summary>The palette of the ruleset.</summary>
    public EditorPalette Palette { get; }

    /// <summary>Layer visibility and locks.</summary>
    public LayerSettings Layers { get; } = new();

    /// <summary>The current tool.</summary>
    public EditorTool Tool { get; set; } = EditorTool.Select;

    /// <summary>Selected object ids, in selection order.</summary>
    public ImmutableArray<int> Selection { get; private set; } = [];

    /// <summary>Painted terrain value of the terrain tool.</summary>
    public int TerrainValue { get; set; }

    /// <summary>Brush size index (0–5) of the terrain and cliff tools.</summary>
    public int BrushSize { get; set; } = 1;

    /// <summary>Whether the cliff tool erases.</summary>
    public bool EraseCliffs { get; set; }

    /// <summary>Tree brush settings.</summary>
    public TreeBrush TreeBrush { get; set; }

    /// <summary>Whether the tree tool deletes.</summary>
    public bool DeleteTrees { get; set; }

    /// <summary>The type the place tool puts down.</summary>
    public string PlaceKey { get; set; } = string.Empty;

    /// <summary>Owner of placed objects (slot 0–7 or <see cref="MapObject.Nature"/>).</summary>
    public byte PlaceOwner { get; set; }

    /// <summary>Facing of placed units (TAB turns it while the place tool is active).</summary>
    public byte PlaceFacing { get; set; }

    /// <summary>Place a random type of the entry's group ("random within a group").</summary>
    public bool PlaceRandomOfGroup { get; set; }

    /// <summary>Class of placed decorations.</summary>
    public string DecorationClass { get; set; } = "ground_patch";

    /// <summary>Image of placed decorations.</summary>
    public string DecorationImage { get; set; } = "patch";

    /// <summary>The shapes TAB cycles through for the current decoration group.</summary>
    public IReadOnlyList<string> DecorationShapes { get; set; } = [];

    /// <summary>Kingdom (1–8) the start tool sets.</summary>
    public int StartKingdom { get; set; } = 1;

    /// <summary>The tile under the pointer.</summary>
    public MapPoint Hover { get; private set; }

    /// <summary>Whether a drag is in progress, and its rectangle (tiles, inclusive) for the band, wall and area previews.</summary>
    public (bool Active, int Left, int Top, int Right, int Bottom) DragRect
        => _dragging && _drag is DragMode.Band or DragMode.Area or DragMode.Wall
            ? (true, Math.Min(_down.TileX, _last.TileX), Math.Min(_down.TileY, _last.TileY), Math.Max(_down.TileX, _last.TileX), Math.Max(_down.TileY, _last.TileY))
            : (false, 0, 0, 0, 0);

    /// <summary>The move/copy offset of a drag in progress (tiles).</summary>
    public (int Dx, int Dy) DragOffset => _dragging && _drag is DragMode.Move or DragMode.Copy ? (_last.TileX - _down.TileX, _last.TileY - _down.TileY) : (0, 0);

    /// <summary>Replaces the document (new, open); the selection is cleared.</summary>
    public void SetDocument(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        CancelStroke();
        Document = document;
        _occupancy = null;
        Select([]);
    }

    /// <summary>Selects <paramref name="ids"/> (only editable layers).</summary>
    public void Select(IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Selection = Layers.Editable(Document.State, Palette, ids);
        SelectionChanged?.Invoke();
    }

    /// <summary>Drops selected ids that no longer exist (after undo/redo).</summary>
    public void PruneSelection()
    {
        var kept = ImmutableArray.CreateBuilder<int>();
        foreach (int id in Selection)
        {
            if (Document.State.IndexOf(id) >= 0)
            {
                kept.Add(id);
            }
        }

        if (kept.Count != Selection.Length)
        {
            Selection = kept.ToImmutable();
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>The topmost editable object at a tile (units and items before structures), or null.</summary>
    public EditorObject? ObjectAt(int tileX, int tileY)
    {
        EditorObject? structure = null;
        DocumentState s = Document.State;
        for (int i = s.Objects.Count - 1; i >= 0; i--)
        {
            EditorObject o = s.Objects[i];
            if (!Layers.IsEditable(o, Palette))
            {
                continue;
            }

            int typeId = Ruleset.EntityTypeId(o.TypeKey);
            if (typeId >= 0 && Ruleset.EntityTypes[typeId].HasFootprint)
            {
                (int x, int y, int w, int h) = Occupancy.Footprint(Ruleset.EntityTypes[typeId], o.Placement);
                if (structure is null && tileX >= x && tileX < x + w && tileY >= y && tileY < y + h)
                {
                    structure = o;
                }
            }
            else if (o.TileX == tileX && o.TileY == tileY)
            {
                return o;
            }
        }

        return structure;
    }

    /// <summary>The pointer moved (no button).</summary>
    public void PointerHover(MapPoint p) => Hover = p;

    /// <summary>A button went down on the map.</summary>
    public void PointerDown(MapPoint p, EditorModifiers mods)
    {
        Hover = p;
        _down = _last = p;
        _dragging = true;
        _drag = DragMode.None;
        switch (Tool)
        {
            case EditorTool.Terrain or EditorTool.Cliff or EditorTool.Trees:
                _strokes++;
                _stroke = Document.Begin(Tool switch { EditorTool.Terrain => "Paint terrain", EditorTool.Cliff => EraseCliffs ? "Erase cliffs" : "Paint cliffs", _ => DeleteTrees ? "Delete trees" : "Plant trees" });
                _occupancy = Tool == EditorTool.Trees ? new Occupancy(Document, Ruleset) : null;
                Brush(p);
                break;
            case EditorTool.Place when IsWall(PlaceKey):
                _drag = DragMode.Wall;
                break;
            case EditorTool.Place:
                PlaceOne(p, mods);
                _dragging = false;
                break;
            case EditorTool.Decoration:
                Decorate(p);
                _dragging = false;
                break;
            case EditorTool.Start:
                Document.Apply($"Starting location of kingdom {StartKingdom}", s => s.WithScenario(m => m.SetStart(StartKingdom, p.TileX, p.TileY)));
                _dragging = false;
                break;
            case EditorTool.Erase:
                EraseAt(p);
                _dragging = false;
                break;
            case EditorTool.Area:
                _drag = DragMode.Area;
                break;
            default:
                EditorObject? hit = ObjectAt(p.TileX, p.TileY);
                if (hit is not null && (mods & EditorModifiers.Alt) != 0)
                {
                    if (!Selection.Contains(hit.Id))
                    {
                        Select([hit.Id]);
                    }

                    _drag = DragMode.Move;
                }
                else if (hit is not null && (mods & EditorModifiers.Ctrl) != 0)
                {
                    if (!Selection.Contains(hit.Id))
                    {
                        Select([hit.Id]);
                    }

                    _drag = DragMode.Copy;
                }
                else
                {
                    _drag = DragMode.Band;
                }

                break;
        }
    }

    /// <summary>The pointer moved with a button down.</summary>
    public void PointerDrag(MapPoint p)
    {
        Hover = p;
        if (!_dragging)
        {
            return;
        }

        bool moved = p != _last;
        _last = p;
        if (_stroke is not null && moved)
        {
            Brush(p);
        }
    }

    /// <summary>The button went up.</summary>
    public void PointerUp(MapPoint p, EditorModifiers mods)
    {
        Hover = p;
        if (!_dragging)
        {
            return;
        }

        _last = p;
        _dragging = false;
        if (_stroke is not null)
        {
            _stroke.Commit();
            _stroke = null;
            _occupancy = null;
            return;
        }

        int dx = p.TileX - _down.TileX, dy = p.TileY - _down.TileY;
        switch (_drag)
        {
            case DragMode.Move when dx != 0 || dy != 0:
                Document.Apply($"Move {Selection.Length} object(s)", s => s.MoveBy(Selection, dx * MapData.UnitsPerTile, dy * MapData.UnitsPerTile, Document.Width, Document.Height));
                break;
            case DragMode.Copy when dx != 0 || dy != 0:
                ImmutableArray<int> copies = [];
                Document.Apply($"Copy {Selection.Length} object(s)", s => s.CopyBy(Selection, dx * MapData.UnitsPerTile, dy * MapData.UnitsPerTile, Document.Width, Document.Height, out copies));
                Select(copies);
                break;
            case DragMode.Band:
                bool add = (mods & EditorModifiers.Shift) != 0;
                ImmutableArray<int> picked;
                if (_down.TileX == p.TileX && _down.TileY == p.TileY)
                {
                    picked = ObjectAt(p.TileX, p.TileY) is { } hit ? [hit.Id] : [];
                }
                else
                {
                    picked = ObjectLocator.InRect(Document.State, _down.TileX, _down.TileY, p.TileX, p.TileY);
                }

                var next = new List<int>(add ? Selection : []);
                foreach (int id in picked)
                {
                    if (!next.Contains(id))
                    {
                        next.Add(id);
                    }
                }

                Select(next);
                break;
            case DragMode.Wall:
            {
                using EditTransaction tx = Document.Begin("Wall");
                var occupancy = new Occupancy(Document, Ruleset);
                ImmutableArray<int> segments = WallTool.Drag(tx, occupancy, PlaceKey, PlaceOwner, _down.TileX, _down.TileY, p.TileX, p.TileY);
                tx.Commit();
                Status?.Invoke($"{segments.Length} wall segment(s) placed.");
                break;
            }

            case DragMode.Area:
                AreaDrawn?.Invoke(new RectArea(Math.Min(_down.TileX, p.TileX), Math.Min(_down.TileY, p.TileY), Math.Max(_down.TileX, p.TileX), Math.Max(_down.TileY, p.TileY)));
                break;
        }
    }

    /// <summary>Abandons a brush stroke in progress (rolled back).</summary>
    public void CancelStroke()
    {
        _stroke?.Dispose();
        _stroke = null;
        _dragging = false;
    }

    /// <summary>Handles an editor key on the map; returns whether it was used.</summary>
    /// <param name="key">The key: <c>Delete</c>, <c>Tab</c>, <c>1</c>–<c>8</c>, <c>Escape</c>.</param>
    /// <param name="mods">Modifiers.</param>
    public bool Key(string key, EditorModifiers mods)
    {
        ArgumentNullException.ThrowIfNull(key);
        bool back = (mods & EditorModifiers.Shift) != 0;
        switch (key)
        {
            case "Delete" when !Selection.IsEmpty:
                DeleteSelection();
                return true;
            case "Tab" when Tool == EditorTool.Place:
                PlaceFacing = (byte)((PlaceFacing + (back ? 7 : 1)) & 7);
                Status?.Invoke($"Facing {PlaceFacing}.");
                return true;
            case "Tab" when Tool == EditorTool.Decoration:
                DecorationImage = DecorationEdits.NextShape(DecorationShapes, DecorationImage, back);
                Status?.Invoke($"Shape {DecorationImage}.");
                return true;
            case "Tab" when !Selection.IsEmpty:
                Document.Apply("Turn", s => s.Rotate(Selection, back));
                return true;
            case "Escape":
                CancelStroke();
                Select([]);
                return true;
            case { Length: 1 } when key[0] is >= '1' and <= '8':
                int k = key[0] - '0';
                Document.Apply($"Starting location of kingdom {k}", s => s.WithScenario(m => m.SetStart(k, Hover.TileX, Hover.TileY)));
                return true;
            default:
                return false;
        }
    }

    /// <summary>Deletes the selection.</summary>
    public void DeleteSelection()
    {
        if (Selection.IsEmpty)
        {
            return;
        }

        int n = Selection.Length;
        ImmutableArray<int> ids = Selection;
        Document.Apply($"Delete {n} object(s)", s => s.Remove(ids));
        Select([]);
    }

    /// <summary>The selection as clipboard text.</summary>
    public string CopySelection() => ObjectClipboard.Copy(Document.State, Selection);

    /// <summary>Pastes clipboard text at the hovered tile; returns how many objects were placed.</summary>
    public int Paste(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ImmutableArray<int> pasted = [];
        try
        {
            Document.Apply("Paste", s => ObjectClipboard.Paste(s, text, Hover.TileX, Hover.TileY, Document.Width, Document.Height, new Occupancy(Document, Ruleset), out pasted));
        }
        catch (FormatException e)
        {
            Status?.Invoke(e.Message);
            return 0;
        }

        Select(pasted);
        return pasted.Length;
    }

    private bool IsWall(string key) => Ruleset.EntityTypeId(key) is >= 0 and var id && Ruleset.EntityTypes[id].Traits.IsWall;

    private void Brush(MapPoint p)
    {
        if (_stroke is null)
        {
            return;
        }

        switch (Tool)
        {
            case EditorTool.Terrain:
                TerrainTools.Paint(_stroke, p.CornerX, p.CornerY, BrushSize, TerrainValue);
                break;
            case EditorTool.Cliff:
                TerrainTools.Cliffs(_stroke, p.TileX, p.TileY, BrushSize, !EraseCliffs);
                break;
            case EditorTool.Trees when DeleteTrees:
                TreeTools.Delete(_stroke, _occupancy!, Palette, p.TileX, p.TileY, TreeBrush.Radius);
                break;
            case EditorTool.Trees:
                TreeTools.Plant(_stroke, _occupancy!, Palette, p.TileX, p.TileY, TreeBrush with { Seed = TreeBrush.Seed ^ (_strokes * 0x9E3779B97F4A7C15UL) });
                break;
        }

        _stroke.Flush();
    }

    private void PlaceOne(MapPoint p, EditorModifiers mods)
    {
        if (PlaceKey.Length == 0 || Ruleset.EntityTypeId(PlaceKey) < 0)
        {
            Status?.Invoke("Choose a type in the palette first.");
            return;
        }

        string key = PlaceKey;
        if (PlaceRandomOfGroup && Palette.Find(PlaceKey) is { } entry)
        {
            key = PlaceTools.RandomOfGroup(Palette, entry, ++_strokes ^ ((ulong)(uint)p.TileX << 20) ^ (uint)p.TileY);
        }

        MapObject placement = PlaceTools.Placement(Ruleset, key, PlaceOwner, p.TileX, p.TileY, (mods & EditorModifiers.Ctrl) != 0, (mods & EditorModifiers.Alt) != 0, PlaceFacing);
        using EditTransaction tx = Document.Begin("Place " + key);
        int id = PlaceTools.Place(tx, new Occupancy(Document, Ruleset), placement, null, out string reason);
        tx.Commit();
        if (id == 0)
        {
            Status?.Invoke($"Cannot place {key} here: {reason}.");
        }
        else
        {
            Select([id]);
        }
    }

    private void Decorate(MapPoint p)
    {
        bool added = false;
        Document.Apply("Place decoration", s => s.AddDecoration(Document, new MapDecoration(DecorationClass, DecorationImage, p.TileX, p.TileY, -1, -1), out added));
        if (!added)
        {
            Status?.Invoke($"A {DecorationClass} cannot lie here.");
        }
    }

    private void EraseAt(MapPoint p)
    {
        if (ObjectAt(p.TileX, p.TileY) is { } o)
        {
            Document.Apply("Delete " + o.Label, s => s.Remove([o.Id]));
            PruneSelection();
            return;
        }

        Document.Apply("Delete decorations", s => s.RemoveDecorationsAt(p.TileX, p.TileY));
    }
}
