using System;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Editing;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>The prefab library, the fair-starts check and building a map from one half (ADR 0292).</summary>
public class MapToolTests
{
    private const int Tile = MapData.UnitsPerTile;

    private static EditorController Editor(int width = 48, int height = 40)
        => new(MapDocument.CreateEmpty("Tools", width, height, Fixtures.RulesFor(null)), Fixtures.Classic);

    [Fact]
    public void The_ready_made_prefabs_are_built_from_traits_and_paste_whole_on_open_land()
    {
        var names = PrefabLibrary.BuiltIn(Fixtures.Classic, "euro", 2).Select(p => p.Name).ToArray();
        Assert.Equal(["Village", "Walled yard", "Watch post", "War band", "Mining camp"], names);

        foreach (Prefab p in PrefabLibrary.BuiltIn(Fixtures.Classic, "euro", 2))
        {
            EditorController c = Editor();
            Assert.Equal(p.Count, c.PasteAt(p.Text, 10, 10, "Place " + p.Name));
            Assert.All(c.Document.State.Objects, o => Assert.True(o.Owner is 2 or MapObject.Nature, $"{p.Name}: {o.TypeKey} of {o.Owner}"));
        }

        // The yard: a ring of walls with the gate in the middle of its south side.
        Prefab yard = PrefabLibrary.BuiltIn(Fixtures.Classic, "euro", 0).Single(p => p.Name == "Walled yard");
        EditorController e = Editor();
        e.PasteAt(yard.Text, 10, 10);
        EditorObject gate = Assert.Single(e.Document.State.Objects, o => Fixtures.Classic.EntityTypes[Fixtures.Classic.EntityTypeId(o.TypeKey)].Traits.Gate is not null);
        int south = e.Document.State.Objects.Max(o => o.TileY);
        Assert.Equal(south, gate.TileY);
        Assert.Contains(e.Document.State.Objects, o => o.TileY == south && o.TileX < gate.TileX);
        Assert.Contains(e.Document.State.Objects, o => o.TileY == south && o.TileX > gate.TileX);

        // A civilization the ruleset gives no houses or soldiers gets what it can.
        string[] arab = [.. PrefabLibrary.BuiltIn(Fixtures.Classic, "arab", 0).Select(p => p.Name)];
        Assert.DoesNotContain("Village", arab);
        Assert.Contains("Walled yard", arab);
    }

    [Fact]
    public void A_prefab_goes_to_the_chosen_kingdom_and_nature_keeps_its_own()
    {
        Prefab camp = PrefabLibrary.BuiltIn(Fixtures.Classic, "euro", 0).Single(p => p.Name == "Mining camp");
        EditorController c = Editor();
        c.PasteAt(PrefabLibrary.WithOwner(camp.Text, 5), 8, 8);
        Assert.Single(c.Document.State.Objects, o => o.Owner == MapObject.Nature);
        Assert.Equal(4, c.Document.State.Objects.Count(o => o.Owner == 5));

        Prefab mine = PrefabLibrary.FromFile("C:/user/prefabs/gate house" + PrefabLibrary.Extension, ObjectClipboard.Copy(c.Document.State, [.. c.Document.State.Objects.Select(o => o.Id)]));
        Assert.Equal(("gate house", 5), (mine.Name, mine.Count));
        Assert.Equal("Camp", PrefabLibrary.FromFile("x.json", ObjectClipboard.Copy(c.Document.State, [c.Document.State.Objects[0].Id], "Camp")).Name);
        Assert.Throws<FormatException>(() => PrefabLibrary.FromFile("x.json", "{ \"kind\": \"something else\" }"));
    }

    // Two starts, with gold and stone placed for each, and a forest in the middle.
    private static MapDocument Duel(int goldX2 = 38)
    {
        MapDocument doc = MapDocument.CreateEmpty("Duel", 48, 24, Fixtures.RulesFor(null));
        doc.Apply("Setup", s => s
            .Add(MapObject.AtTile("comm.res_gold", MapObject.Nature, 8, 10), null, out _)
            .Add(MapObject.AtTile("comm.res_gold", MapObject.Nature, goldX2, 10), null, out _)
            .Add(MapObject.AtTile("comm.res_stone", MapObject.Nature, 4, 16), null, out _)
            .Add(MapObject.AtTile("comm.res_stone", MapObject.Nature, 42, 16), null, out _)
            .Add(MapObject.AtTile("comm.tree", MapObject.Nature, 24, 11), null, out _)
            .WithScenario(m => m.SetStart(1, 4, 10).SetStart(2, 43, 10).UpdateKingdom(2, k => k with { Active = true })));
        return doc;
    }

    [Fact]
    public void The_fair_starts_check_measures_each_starts_walk_to_the_resources()
    {
        FairnessReport fair = FairStarts.Check(Duel(), Fixtures.Classic);
        Assert.Equal([1, 2], fair.Starts.Select(s => s.Kingdom).ToArray());
        Assert.Equal(fair.Starts[0].Gold, fair.Starts[1].Gold);
        Assert.Equal(fair.Starts[0].Stone, fair.Starts[1].Stone);
        Assert.All(fair.Starts, s => Assert.Single(s.Reaches));
        Assert.Empty(fair.Findings);

        // Kingdom 2's gold far off: a finding names it.
        FairnessReport unfair = FairStarts.Check(Duel(goldX2: 26), Fixtures.Classic);
        Assert.Contains(unfair.Findings, f => f.StartsWith("Gold: kingdom 2 walks", StringComparison.Ordinal));
    }

    [Fact]
    public void Water_between_the_starts_and_a_missing_resource_are_findings()
    {
        MapDocument doc = Duel();
        int water = doc.Rules.Palette.First(p => p.IsWater).Index;
        using (EditTransaction tx = doc.Begin("Channel"))
        {
            for (int y = 0; y <= doc.Height; y++)
            {
                for (int x = 22; x <= 26; x++)
                {
                    tx.Paint(x, y, water);
                }
            }

            tx.Commit();
        }

        FairnessReport report = FairStarts.Check(doc, Fixtures.Classic);
        Assert.Contains(report.Findings, f => f.StartsWith("Kingdom 1 cannot walk to kingdom 2", StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f == "Kingdom 2 reaches no wood by land." || f == "Kingdom 1 reaches no wood by land.");
    }

    [Fact]
    public void A_map_built_from_its_left_half_gives_the_partner_kingdom_the_mirrored_start()
    {
        MapDocument doc = MapDocument.CreateEmpty("Half", 40, 30, Fixtures.RulesFor(null));
        int water = doc.Rules.Palette.First(p => p.IsWater).Index;
        using (EditTransaction tx = doc.Begin("Lake"))
        {
            tx.Paint(6, 6, water);
            tx.Commit();
        }

        doc.Apply("Left half", s => s
            .Add(MapObject.AtTile("comm.res_gold", MapObject.Nature, 4, 20), null, out _)
            .Add(new MapObject("euro.peon", 0, (5 * Tile) + 1024, (10 * Tile) + 1024), null, out _)
            .Add(new MapObject("euro.peon", 3, (30 * Tile) + 1024, (10 * Tile) + 1024), null, out _) // on the right: replaced
            .WithScenario(m => m.SetStart(1, 5, 10)));
        long steps = doc.History.Version;

        Assert.Equal(2, MapMirror.Apply(doc, Fixtures.Classic, MirrorKind.LeftToRight));
        Assert.Equal(water, doc.PaintedAt(34, 6));
        Assert.Contains(doc.State.Objects, o => o.TypeKey == "comm.res_gold" && o.TileX == 34 && o.TileY == 20 && o.Owner == MapObject.Nature);
        Assert.Contains(doc.State.Objects, o => o.TypeKey == "euro.peon" && o.TileX == 34 && o.TileY == 10 && o.Owner == 1);
        Assert.DoesNotContain(doc.State.Objects, o => o.Owner == 3);
        Assert.Equal((34, 10), (doc.State.Scenario.Kingdom(2)!.StartTileX, doc.State.Scenario.Kingdom(2)!.StartTileY));

        // One step, undone at once.
        doc.Undo();
        Assert.Equal(steps, doc.History.Version);
        Assert.Contains(doc.State.Objects, o => o.Owner == 3);

        // Turned half round: a structure's footprint lands in the opposite corner.
        Assert.Equal(2, MapMirror.Apply(doc, Fixtures.Classic, MirrorKind.LeftTurned));
        Assert.Contains(doc.State.Objects, o => o.TypeKey == "comm.res_gold" && o.TileX == 34 && o.TileY == 8);
        Assert.Equal((34, 19), (doc.State.Scenario.Kingdom(2)!.StartTileX, doc.State.Scenario.Kingdom(2)!.StartTileY));
        Assert.Equal(1, MapMirror.Partner(0));
        Assert.Equal(6, MapMirror.Partner(7));
        Assert.Equal(MapObject.Nature, MapMirror.Partner(MapObject.Nature));
    }
}
