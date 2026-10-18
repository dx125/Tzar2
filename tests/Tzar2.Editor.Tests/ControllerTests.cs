using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>The editor's interaction model: pointer and key events become document edits with the original's semantics.</summary>
public class ControllerTests
{
    private static EditorController Controller(int size = 32)
        => new(MapDocument.CreateEmpty("Ctl", size, size, Fixtures.RulesFor("grass")), Fixtures.Classic);

    private static MapPoint P(int x, int y) => new(x, y, x, y);

    [Fact]
    public void A_brush_stroke_is_one_undo_step_and_escape_cancels_it()
    {
        EditorController c = Controller();
        c.Tool = EditorTool.Terrain;
        c.TerrainValue = 0;
        c.BrushSize = 0;
        c.PointerDown(P(2, 2), EditorModifiers.None);
        c.PointerDrag(P(3, 2));
        c.PointerDrag(P(4, 2));
        c.PointerUp(P(4, 2), EditorModifiers.None);
        Assert.Single(c.Document.History.UndoSteps);
        Assert.Equal(0, c.Document.PaintedAt(3, 2));

        c.PointerDown(P(10, 10), EditorModifiers.None);
        Assert.Equal(0, c.Document.PaintedAt(10, 10));
        c.Key("Escape", EditorModifiers.None);
        Assert.Equal(1, c.Document.PaintedAt(10, 10));
        Assert.Single(c.Document.History.UndoSteps);
    }

    [Fact]
    public void Place_select_alt_drag_moves_and_ctrl_drag_copies()
    {
        EditorController c = Controller();
        c.Tool = EditorTool.Place;
        c.PlaceKey = "euro.footman";
        c.PointerDown(P(5, 5), EditorModifiers.None);
        int id = c.Selection.Single();
        c.Tool = EditorTool.Select;
        c.PointerDown(P(5, 5), EditorModifiers.Alt);
        c.PointerDrag(P(8, 6));
        Assert.Equal((3, 1), c.DragOffset);
        c.PointerUp(P(8, 6), EditorModifiers.Alt);
        Assert.Equal((8, 6), (c.Document.State.Find(id)!.TileX, c.Document.State.Find(id)!.TileY));
        c.PointerDown(P(8, 6), EditorModifiers.Ctrl);
        c.PointerUp(P(9, 9), EditorModifiers.Ctrl);
        Assert.Equal(2, c.Document.State.Objects.Count);
        Assert.NotEqual(id, c.Selection.Single());

        // Rubber band (Shift adds) and click.
        c.PointerDown(P(0, 0), EditorModifiers.None);
        c.PointerUp(P(20, 20), EditorModifiers.None);
        Assert.Equal(2, c.Selection.Length);
        c.PointerDown(P(8, 6), EditorModifiers.None);
        c.PointerUp(P(8, 6), EditorModifiers.None);
        Assert.Equal([id], c.Selection.ToArray());
        c.Key("Delete", EditorModifiers.None);
        Assert.Single(c.Document.State.Objects);
    }

    [Fact]
    public void Placement_refusals_report_a_reason_and_tab_turns()
    {
        EditorController c = Controller();
        string? status = null;
        c.Status += s => status = s;
        c.Tool = EditorTool.Place;
        c.PlaceKey = "euro.castle";
        c.PointerDown(P(30, 30), EditorModifiers.None);
        Assert.Contains("does not fit", status, System.StringComparison.Ordinal);
        Assert.Empty(c.Document.State.Objects);
        c.PlaceKey = "euro.knight";
        c.Key("Tab", EditorModifiers.None);
        c.Key("Tab", EditorModifiers.None);
        c.PointerDown(P(3, 3), EditorModifiers.None);
        Assert.Equal(2, c.Document.State.Objects[0].Placement.Facing);
        c.Tool = EditorTool.Select;
        c.Key("Tab", EditorModifiers.Shift);
        Assert.Equal(1, c.Document.State.Objects[0].Placement.Facing);
    }

    [Fact]
    public void Gate_modifiers_walls_by_drag_and_random_items()
    {
        EditorController c = Controller();
        c.Tool = EditorTool.Place;
        c.PlaceKey = "comm.gate_stone";
        c.PointerDown(P(4, 4), EditorModifiers.Ctrl | EditorModifiers.Alt);
        Assert.Equal(MapObjectFlags.Open, c.Document.State.Objects[0].Placement.Flags);
        c.PlaceKey = "comm.wall_wood";
        c.PointerDown(P(1, 10), EditorModifiers.None);
        Assert.True(c.DragRect.Active);
        c.PointerUp(P(9, 10), EditorModifiers.None);
        Assert.Equal(9, c.Document.State.Objects.Count(o => o.TypeKey == "comm.wall_wood"));
        c.PlaceKey = "comm.item_armor";
        c.PlaceRandomOfGroup = true;
        c.PlaceOwner = MapObject.Nature;
        c.PointerDown(P(20, 20), EditorModifiers.None);
        Assert.StartsWith("comm.item", c.Document.State.Objects[^1].TypeKey, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Keys_1_to_8_set_starting_locations_under_the_pointer()
    {
        EditorController c = Controller();
        c.PointerHover(P(7, 9));
        Assert.True(c.Key("3", EditorModifiers.None));
        Assert.Equal((7, 9), (c.Document.State.Scenario.Kingdom(3)!.StartTileX, c.Document.State.Scenario.Kingdom(3)!.StartTileY));
        c.Tool = EditorTool.Start;
        c.StartKingdom = 5;
        c.PointerDown(P(2, 3), EditorModifiers.None);
        Assert.Equal(2, c.Document.State.Scenario.Kingdom(5)!.StartTileX);
    }

    [Fact]
    public void Trees_cliffs_decorations_erase_and_areas()
    {
        EditorController c = Controller();
        c.Tool = EditorTool.Trees;
        c.TreeBrush = c.TreeBrush with { Radius = 2, DensityPercent = 100 };
        c.PointerDown(P(10, 10), EditorModifiers.None);
        c.PointerUp(P(10, 10), EditorModifiers.None);
        int trees = c.Document.State.Objects.Count;
        Assert.True(trees > 5);
        c.DeleteTrees = true;
        c.PointerDown(P(10, 10), EditorModifiers.None);
        c.PointerUp(P(10, 10), EditorModifiers.None);
        Assert.Empty(c.Document.State.Objects);

        c.Tool = EditorTool.Cliff;
        c.BrushSize = 0;
        c.PointerDown(P(3, 3), EditorModifiers.None);
        c.PointerUp(P(3, 3), EditorModifiers.None);
        Assert.True(c.Document.IsCliff(3, 3));

        c.Tool = EditorTool.Decoration;
        c.DecorationShapes = ["a", "b"];
        c.DecorationImage = "a";
        c.Key("Tab", EditorModifiers.None);
        c.PointerDown(P(6, 6), EditorModifiers.None);
        Assert.Equal("b", c.Document.State.Decorations.Single().Image);
        c.Tool = EditorTool.Erase;
        c.PointerDown(P(6, 6), EditorModifiers.None);
        Assert.Empty(c.Document.State.Decorations);

        RectArea? drawn = null;
        c.AreaDrawn += a => drawn = a;
        c.Tool = EditorTool.Area;
        c.PointerDown(P(8, 2), EditorModifiers.None);
        c.PointerUp(P(4, 5), EditorModifiers.None);
        Assert.Equal(new RectArea(4, 2, 8, 5), drawn);
    }

    [Fact]
    public void Copy_paste_through_text_and_locked_layers_are_not_selectable()
    {
        EditorController c = Controller();
        c.Tool = EditorTool.Place;
        c.PlaceKey = "euro.peon";
        c.PointerDown(P(2, 2), EditorModifiers.None);
        string text = c.CopySelection();
        c.PointerHover(P(12, 12));
        Assert.Equal(1, c.Paste(text));
        Assert.Equal(12, c.Document.State.Objects[1].TileX);
        Assert.Equal(0, c.Paste("not a clipboard"));
        c.Layers.SetLocked(EditorLayer.Units, true);
        Assert.Null(c.ObjectAt(2, 2));
        c.Select(c.Document.State.Objects.Select(o => o.Id));
        Assert.Empty(c.Selection);
    }
}
