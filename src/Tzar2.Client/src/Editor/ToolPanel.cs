using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Godot;
using Tzar2.Content.Compiled;
using Tzar2.Content.Editing;

namespace Tzar2.Client.Editor;

/// <summary>
/// The tool dock (TDD §18.2): terrain with six brush sizes and cliffs, trees (specific / random of a type / random of
/// all, five brushes, density, accumulation, delete), objects by palette section and civilization with the owner and
/// random-within-group, decorations with TAB shapes, starting locations, select and erase.
/// </summary>
public partial class ToolPanel : TabContainer
{
    private static readonly string[] Owners = ["Kingdom 1", "Kingdom 2", "Kingdom 3", "Kingdom 4", "Kingdom 5", "Kingdom 6", "Kingdom 7", "Kingdom 8", "Nature"];
    private readonly EditorController _c;
    private readonly ItemList _objects = new() { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 240) };
    private readonly List<PaletteEntry> _shown = [];
    private OptionButton? _owner;
    private PaletteKind _kind = PaletteKind.Building;
    private string? _civ;

    /// <summary>Creates the dock.</summary>
    /// <param name="controller">The controller.</param>
    /// <param name="decorations">Decoration images by class (from the package's sprite index), for the decoration tab.</param>
    public ToolPanel(EditorController controller, IReadOnlyDictionary<string, IReadOnlyList<string>> decorations)
    {
        _c = controller;
        CustomMinimumSize = new Vector2(260, 0);
        SizeFlagsVertical = SizeFlags.ExpandFill;
        Terrain();
        Trees();
        Objects();
        Decorations(decorations);
        Starts();
        TabChanged += tab => Activate((int)tab);
        Activate(0);
    }

    /// <summary>Re-reads the palette of the document's terrain rules (new map with another set).</summary>
    public void RefreshTerrain()
    {
        Control page = GetTabControl(0);
        Forms.Clear(page);
        FillTerrain(page);
    }

    private void Activate(int tab)
    {
        _c.Tool = tab switch
        {
            0 => _c.EraseCliffs || _c.Tool == EditorTool.Cliff ? _c.Tool : EditorTool.Terrain,
            1 => EditorTool.Trees,
            2 => _c.Tool == EditorTool.Select || _c.Tool == EditorTool.Erase ? _c.Tool : EditorTool.Place,
            3 => EditorTool.Decoration,
            _ => EditorTool.Start,
        };
    }

    private VBoxContainer Page(string title)
    {
        var page = new VBoxContainer { Name = title };
        AddChild(page);
        return page;
    }

    private void Terrain()
    {
        VBoxContainer page = Page("Terrain");
        FillTerrain(page);
    }

    private void FillTerrain(Control page)
    {
        Forms.Heading(page, "Paint terrain");
        var terrains = new ItemList { CustomMinimumSize = new Vector2(0, 140), SelectMode = ItemList.SelectModeEnum.Single };
        IReadOnlyList<PaletteTerrain> palette = _c.Document.Rules.Palette;
        foreach (PaletteTerrain t in palette)
        {
            terrains.AddItem(t.Name + (t.IsWater ? " (water)" : string.Empty));
        }

        terrains.ItemSelected += i =>
        {
            _c.TerrainValue = palette[(int)i].Index;
            _c.Tool = EditorTool.Terrain;
        };
        int current = palette.ToList().FindIndex(p => p.Index == _c.TerrainValue);
        if (current >= 0)
        {
            terrains.Select(current);
        }

        page.AddChild(terrains);
        GridContainer grid = Forms.Grid(page);
        Forms.Choice(grid, "Brush", Brushes.TerrainSizes.Select(s => $"{s} × {s}").ToArray(), _c.BrushSize, i => _c.BrushSize = i);
        Forms.Heading(page, "Cliffs");
        var row = new HBoxContainer();
        Forms.Button(row, "Paint cliffs", () =>
        {
            _c.EraseCliffs = false;
            _c.Tool = EditorTool.Cliff;
        });
        Forms.Button(row, "Erase cliffs", () =>
        {
            _c.EraseCliffs = true;
            _c.Tool = EditorTool.Cliff;
        });
        page.AddChild(row);
        Forms.Label(page, "Cliffs stand on land only; painting terrain over a cliff removes it. Red tiles have no transition in the terrain set.").AutowrapMode = TextServer.AutowrapMode.WordSmart;
    }

    private void Trees()
    {
        VBoxContainer page = Page("Trees");
        GridContainer grid = Forms.Grid(page);
        ImmutableArray<PaletteEntry> trees = _c.Palette.Of(PaletteKind.Tree);
        Forms.Choice(grid, "Tree", trees.Select(t => t.Key).ToArray(), 0, i => _c.TreeBrush = _c.TreeBrush with { TypeKey = trees[i].Key });
        Forms.Enum(grid, "Choice", _c.TreeBrush.Choice, v => _c.TreeBrush = _c.TreeBrush with { Choice = v });
        Forms.Choice(grid, "Brush", Brushes.TreeRadii.Select(r => r == 0 ? "1 tree" : $"radius {r}").ToArray(), _c.TreeBrush.Radius, i => _c.TreeBrush = _c.TreeBrush with { Radius = i });
        Forms.Number(grid, "Density %", _c.TreeBrush.DensityPercent, 1, 100, v => _c.TreeBrush = _c.TreeBrush with { DensityPercent = v });
        Forms.Check(grid, "Accumulate", _c.TreeBrush.Accumulate, v => _c.TreeBrush = _c.TreeBrush with { Accumulate = v });
        Forms.Check(grid, "Delete trees", _c.DeleteTrees, v => _c.DeleteTrees = v);
    }

    private void Objects()
    {
        VBoxContainer page = Page("Objects");
        var tools = new HBoxContainer();
        Forms.Button(tools, "Select", () => _c.Tool = EditorTool.Select);
        Forms.Button(tools, "Place", () => _c.Tool = EditorTool.Place);
        Forms.Button(tools, "Erase", () => _c.Tool = EditorTool.Erase);
        page.AddChild(tools);
        GridContainer grid = Forms.Grid(page);
        Forms.Enum(grid, "Section", _kind, v =>
        {
            _kind = v;
            FillObjects();
        });
        string[] civs = ["(all)", .. _c.Ruleset.Civilizations];
        Forms.Choice(grid, "Civilization", civs, 0, i =>
        {
            _civ = i == 0 ? null : civs[i];
            FillObjects();
        });
        _owner = Forms.Choice(grid, "Owner", Owners, 0, i => _c.PlaceOwner = i == 8 ? MapObject.Nature : (byte)i);
        Forms.Check(grid, "Random within group", _c.PlaceRandomOfGroup, v => _c.PlaceRandomOfGroup = v);
        _objects.ItemSelected += i =>
        {
            PaletteEntry e = _shown[(int)i];
            _c.PlaceKey = e.Key;
            _c.Tool = EditorTool.Place;
            if (e.NatureByDefault)
            {
                _c.PlaceOwner = MapObject.Nature;
                _owner.Selected = 8;
            }
        };
        page.AddChild(_objects);
        Forms.Label(page, "Gates: Ctrl turns, Alt flips the side, Ctrl+Alt places open. Bridges: Alt turns. TAB / Shift+TAB turn units. Walls: drag.").AutowrapMode = TextServer.AutowrapMode.WordSmart;
        FillObjects();
    }

    private void FillObjects()
    {
        _objects.Clear();
        _shown.Clear();
        foreach (PaletteEntry e in _c.Palette.Of(_kind, _civ))
        {
            _shown.Add(e);
            _objects.AddItem(e.Key);
        }
    }

    private void Decorations(IReadOnlyDictionary<string, IReadOnlyList<string>> catalog)
    {
        VBoxContainer page = Page("Decorations");
        GridContainer grid = Forms.Grid(page);
        string[] classes = ["ground_patch", "ground_object", "water_object", "animation"];
        var images = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 200) };
        void Fill(string cls)
        {
            images.Clear();
            IReadOnlyList<string> shapes = catalog.TryGetValue(cls, out IReadOnlyList<string>? list) && list.Count > 0 ? list : [cls == "ground_patch" ? "patch" : cls];
            foreach (string s in shapes)
            {
                images.AddItem(s);
            }

            _c.DecorationClass = cls;
            _c.DecorationShapes = shapes;
            _c.DecorationImage = shapes[0];
        }

        Forms.Choice(grid, "Kind", classes, 0, i => Fill(classes[i]));
        images.ItemSelected += i =>
        {
            _c.DecorationImage = images.GetItemText((int)i);
            _c.Tool = EditorTool.Decoration;
        };
        page.AddChild(images);
        Forms.Label(page, "Patches stack anywhere on land; ground objects need land, water objects water. TAB cycles shapes.").AutowrapMode = TextServer.AutowrapMode.WordSmart;
        Fill(classes[0]);
    }

    private void Starts()
    {
        VBoxContainer page = Page("Starts");
        GridContainer grid = Forms.Grid(page);
        Forms.Choice(grid, "Kingdom", Enumerable.Range(1, 8).Select(k => k.ToString(CultureInfo.InvariantCulture)).ToArray(), 0, i => _c.StartKingdom = i + 1);
        Forms.Label(page, "Click the map to set the kingdom's starting location, or press 1–8 over the map in any tool.").AutowrapMode = TextServer.AutowrapMode.WordSmart;
    }
}
