using System;
using System.Collections.Immutable;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Content.Maps;
using Tzar2.Content.MapGen;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using GameType = Tzar2.Content.Compiled.Scenario.GameType;
using VictoryCondition = Tzar2.Content.Compiled.Scenario.VictoryCondition;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>The map tools of TDD §18.2: terrain, trees, decorations, buildings, units, items, properties and tools.</summary>
public class ToolTests
{
    private const int Tile = MapData.UnitsPerTile;

    private static readonly EditorPalette Palette = EditorPalette.Build(Fixtures.Classic);

    private static MapDocument Map(int size = 32) => MapDocument.CreateEmpty("Tools", size, size, Fixtures.RulesFor("grass"));

    // A lake in the middle: corners 10..20 are water, so tiles 10..19 are water and 9/20 are shore.
    private static MapDocument Lake()
    {
        MapDocument doc = Map();
        using EditTransaction tx = doc.Begin("Lake");
        for (int y = 10; y <= 20; y++)
        {
            for (int x = 10; x <= 20; x++)
            {
                tx.Paint(x, y, 0);
            }
        }

        tx.Commit();
        return doc;
    }

    [Fact]
    public void Map_sizes_and_terrain_set_are_fixed_at_creation()
    {
        Assert.Equal([64, 96, 128, 192, 256, 384, 512], MapDocument.ClassicSizes.ToArray());
        MapDocument big = MapDocument.CreateEmpty("Big", 1024, 1024, Fixtures.RulesFor("snow"));
        Assert.Equal(("snow", 1024), (big.BuildMap().TerrainSet, big.BuildMap().Width));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapDocument.CreateEmpty("Huge", 1025, 1025, Fixtures.RulesFor("snow")));
    }

    [Fact]
    public void Terrain_brushes_have_six_sizes_and_painting_over_cliffs_removes_them()
    {
        Assert.Equal(6, Brushes.TerrainSizes.Length);
        MapDocument doc = Map();
        using (EditTransaction tx = doc.Begin("Cliffs"))
        {
            Assert.Equal(25, TerrainTools.Cliffs(tx, 5, 5, 3, cliff: true));
            tx.Commit();
        }

        Assert.True(doc.IsCliff(5, 5));
        using (EditTransaction tx = doc.Begin("Paint"))
        {
            Assert.Equal(9, TerrainTools.Paint(tx, 5, 5, 2, 0));
            tx.Commit();
        }

        Assert.False(doc.IsCliff(5, 5));
        Assert.False(doc.IsCliff(4, 4));
        Assert.True(doc.IsCliff(7, 7));
        Assert.Equal(1, doc.TerrainAt(5, 5)); // the ruleset's water id
        Assert.Equal(1, doc.Rules.SimTerrainOf(0));
    }

    [Fact]
    public void Cliffs_are_only_allowed_on_land()
    {
        MapDocument doc = Lake();
        using EditTransaction tx = doc.Begin("Cliff");
        Assert.Equal(0, TerrainTools.Cliffs(tx, 15, 15, 0, cliff: true));
        Assert.Equal(1, TerrainTools.Cliffs(tx, 2, 2, 0, cliff: true));
    }

    [Fact]
    public void Invalid_transitions_are_red_with_a_terrain_set()
    {
        var set = Content.Terrain.TerrainSet.ParseAll("""
            [{"set":"t","name":"T","table":"t","folder":"f/","terrains":[{"index":"0","name":"Water"},{"index":"1","name":"Dirt"},{"index":"2","name":"Grass"}],
              "files":[{"pair":"00","asset":"a","size":"1,1,0"},{"pair":"11","asset":"a","size":"1,1,0"},{"pair":"22","asset":"a","size":"1,1,0"},{"pair":"01","asset":"a","size":"2,14,0"},{"pair":"12","asset":"a","size":"2,14,0"}],
              "light":null,"trees":null,"treeShadows":null}]
            """u8)[0];
        TerrainRules rules = TerrainRules.ForSet("t", set, Fixtures.Classic);
        Assert.Equal(["Water", "Dirt", "Grass"], rules.Palette.Select(p => p.Name).ToArray());
        MapDocument doc = MapDocument.CreateEmpty("Set", 8, 8, rules, fill: 2);
        using (EditTransaction tx = doc.Begin("Water next to grass"))
        {
            tx.Paint(4, 4, 0);
            tx.Commit();
        }

        // Water meets grass directly: the set has no 0/2 transition.
        Assert.Equal(4, TerrainTools.InvalidTiles(doc, 0, 0, 7, 7).Length);
        Assert.Equal(4, MapValidator.Validate(doc).WithCode("terrain.transition").Length);
        using (EditTransaction tx = doc.Begin("Dirt shore"))
        {
            TerrainTools.Paint(tx, 4, 4, 3, 1);
            tx.Paint(4, 4, 0);
            tx.Commit();
        }

        Assert.Empty(TerrainTools.InvalidTiles(doc, 0, 0, 7, 7));
    }

    [Fact]
    public void Tree_brushes_plant_deterministically_on_free_land_and_delete()
    {
        Assert.Equal(5, Brushes.TreeRadii.Length);
        MapDocument doc = Lake();
        var occupancy = new Occupancy(doc, Fixtures.Classic);
        var brush = new TreeBrush("comm.tree", TreeChoice.Specific, 4, 100, Accumulate: false, Seed: 7);
        ImmutableArray<int> planted;
        using (EditTransaction tx = doc.Begin("Forest"))
        {
            planted = TreeTools.Plant(tx, occupancy, Palette, 12, 12, brush);
            tx.Commit();
        }

        Assert.NotEmpty(planted);
        Assert.All(planted, id => Assert.True(doc.IsLandTile(doc.State.Find(id)!.TileX, doc.State.Find(id)!.TileY)));

        // Same seed, same forest.
        MapDocument again = Lake();
        using (EditTransaction tx = again.Begin("Forest"))
        {
            TreeTools.Plant(tx, new Occupancy(again, Fixtures.Classic), Palette, 12, 12, brush);
            tx.Commit();
        }

        Assert.Equal(doc.BuildMap().ContentHash, again.BuildMap().ContentHash);

        // Without accumulation a second pass adds nothing to a full brush; with it, the density applies again.
        using (EditTransaction tx = doc.Begin("Again"))
        {
            Assert.Empty(TreeTools.Plant(tx, occupancy, Palette, 12, 12, brush with { Seed = 8 }));
            tx.Commit();
        }

        using (EditTransaction tx = doc.Begin("Clear"))
        {
            Assert.Equal(planted.Length, TreeTools.Delete(tx, occupancy, Palette, 12, 12, 4));
            tx.Commit();
        }

        Assert.Empty(doc.State.Objects);
    }

    [Fact]
    public void Forest_density_and_tree_choices()
    {
        MapDocument doc = Map(64);
        var occupancy = new Occupancy(doc, Fixtures.Classic);
        using EditTransaction tx = doc.Begin("Sparse");
        ImmutableArray<int> sparse = TreeTools.Plant(tx, occupancy, Palette, 20, 20, new TreeBrush("comm.tree", TreeChoice.RandomOfAll, 4, 20, true, 1));
        ImmutableArray<int> dense = TreeTools.Plant(tx, occupancy, Palette, 45, 45, new TreeBrush("comm.tree", TreeChoice.RandomOfType, 4, 90, true, 1));
        Assert.InRange(sparse.Length, 1, dense.Length - 1);
        Assert.Equal(["comm.tree"], TreeTools.Candidates(Palette, new TreeBrush("comm.tree", TreeChoice.RandomOfAll, 0, 50, true, 0)).Select(e => e.Key).ToArray());
    }

    [Fact]
    public void Placement_rules_follow_the_layers()
    {
        MapDocument doc = Lake();
        var occ = new Occupancy(doc, Fixtures.Classic);
        Assert.True(occ.Check(PlaceTools.Placement(Fixtures.Classic, "euro.house", 0, 2, 2)).Ok);
        Assert.False(occ.Check(PlaceTools.Placement(Fixtures.Classic, "euro.house", 0, 14, 14)).Ok);
        Assert.False(occ.Check(PlaceTools.Placement(Fixtures.Classic, "euro.harbor", 0, 2, 2)).Ok); // not on the coast
        Assert.True(occ.Check(PlaceTools.Placement(Fixtures.Classic, "euro.harbor", 0, 6, 12)).Ok); // touches the lake
        Assert.True(occ.Check(PlaceTools.Placement(Fixtures.Classic, "comm.galleon", 0, 15, 15)).Ok);
        Assert.False(occ.Check(PlaceTools.Placement(Fixtures.Classic, "comm.galleon", 0, 2, 2)).Ok);
        Assert.True(occ.Check(PlaceTools.Placement(Fixtures.Classic, "comm.fishpatch", MapObject.Nature, 15, 15)).Ok);
        Assert.False(occ.Check(PlaceTools.Placement(Fixtures.Classic, "euro.footman", 0, 15, 15)).Ok);
        Assert.True(occ.Check(PlaceTools.Placement(Fixtures.Classic, "comm.dragon", 0, 15, 15)).Ok);
        Assert.False(occ.Check(PlaceTools.Placement(Fixtures.Classic, "comm.item_armor", MapObject.Nature, 15, 15)).Ok);

        // Stacking: one unit per tile and layer; air over ground is fine; nothing inside a structure.
        using EditTransaction tx = doc.Begin("Place");
        Assert.NotEqual(0, PlaceTools.Place(tx, occ, PlaceTools.Placement(Fixtures.Classic, "euro.footman", 0, 3, 8), null, out _));
        Assert.Equal(0, PlaceTools.Place(tx, occ, PlaceTools.Placement(Fixtures.Classic, "euro.peon", 0, 3, 8), null, out string reason));
        Assert.Contains("layer", reason, StringComparison.Ordinal);
        Assert.NotEqual(0, PlaceTools.Place(tx, occ, PlaceTools.Placement(Fixtures.Classic, "comm.bird", MapObject.Nature, 3, 8), null, out _));
        Assert.NotEqual(0, PlaceTools.Place(tx, occ, PlaceTools.Placement(Fixtures.Classic, "euro.castle", 0, 0, 0), null, out _));
        Assert.Equal(0, PlaceTools.Place(tx, occ, PlaceTools.Placement(Fixtures.Classic, "euro.peon", 0, 2, 2), null, out _));
    }

    [Fact]
    public void Bridges_cross_water_and_turn_with_alt()
    {
        MapDocument doc = Map();
        using (EditTransaction tx = doc.Begin("River"))
        {
            for (int y = 0; y <= 32; y++)
            {
                for (int x = 12; x <= 13; x++)
                {
                    tx.Paint(x, y, 0);
                }
            }

            tx.Commit();
        }

        var occ = new Occupancy(doc, Fixtures.Classic);
        MapObject across = PlaceTools.Placement(Fixtures.Classic, "comm.bridge", 0, 10, 5);
        Assert.Equal(MapObjectFlags.None, across.Flags);
        Assert.True(occ.Check(across).Ok);
        MapObject turned = PlaceTools.Placement(Fixtures.Classic, "comm.bridge", 0, 10, 5, alt: true);
        Assert.Equal(MapObjectFlags.Rotated, turned.Flags);
        Assert.False(occ.Check(turned).Ok); // 2 wide, 5 long along the river: no land at the ends
        Assert.False(occ.Check(PlaceTools.Placement(Fixtures.Classic, "comm.bridge", 0, 2, 5)).Ok); // no water to cross
    }

    [Fact]
    public void Gate_modifiers_and_walls_by_drag()
    {
        Assert.Equal(MapObjectFlags.Rotated, PlaceTools.Placement(Fixtures.Classic, "comm.gate_stone", 0, 4, 4, ctrl: true).Flags);
        Assert.Equal(MapObjectFlags.Mirrored, PlaceTools.Placement(Fixtures.Classic, "comm.gate_stone", 0, 4, 4, alt: true).Flags);
        Assert.Equal(MapObjectFlags.Open, PlaceTools.Placement(Fixtures.Classic, "comm.gate_stone", 0, 4, 4, ctrl: true, alt: true).Flags);
        Assert.Equal(11, WallTool.Line(0, 0, 10, 4).Length);
        MapDocument doc = Map();
        var occ = new Occupancy(doc, Fixtures.Classic);
        using EditTransaction tx = doc.Begin("Wall");
        PlaceTools.Place(tx, occ, MapObject.AtTile("euro.house", 0, 5, 0), null, out _);
        ImmutableArray<int> wall = WallTool.Drag(tx, occ, "comm.wall_stone", 0, 0, 0, 10, 0);
        Assert.Equal(9, wall.Length); // the house takes tiles 5 and 6
        tx.Commit();
    }

    [Fact]
    public void Gates_bridges_facing_and_amounts_reach_the_simulation()
    {
        MapDocument doc = Map();
        int gate = 0, peon = 0, gold = 0;
        doc.Apply("Place", s => s
            .Add(PlaceTools.Placement(Fixtures.Classic, "comm.gate_stone", 0, 4, 4, ctrl: true, alt: true) with { Flags = MapObjectFlags.Open | MapObjectFlags.Rotated | MapObjectFlags.Mirrored }, null, out gate)
            .Add(PlaceTools.Placement(Fixtures.Classic, "euro.peon", 0, 10, 10, facing: 3), null, out peon)
            .Add(MapObject.AtTile("comm.res_gold", MapObject.Nature, 20, 20) with { Amount = 1234 }, null, out gold));
        doc.Apply("Turn", s => s.Rotate([peon], counterClockwise: false));
        MapData map = doc.BuildMap();
        Assert.Equal(MapBinary.ExtrasVersion, BitConverter.ToUInt16(MapBinary.Write(map), 4));
        Assert.Equal(map.ContentHash, MapBinary.Read(MapBinary.Write(map)).ContentHash);
        Assert.True(map.Objects.AsSpan().SequenceEqual(MapBinary.Read(MapBinary.Write(map)).Objects.AsSpan()));

        var sim = new Simulation(Fixtures.Classic, map, new SimulationConfig { PlayerCount = 1 });
        World w = sim.World;
        EntityId g = w.MapObjectEntities[0], p = w.MapObjectEntities[1], r = w.MapObjectEntities[2];
        (int _, int _, int gw, int gh) = w.FootprintRect(g);
        Assert.Equal((1, 6), (gw, gh)); // a gate down (gs2_anim, CTRL-024)
        Assert.Equal(1, w.Footprints.Get(g).Mirrored); // and facing the other way
        Assert.Equal(1, w.Gates.Get(g).Open);
        Assert.Equal(4, w.Identities.Get(p).Facing);
        Assert.Equal(1234, w.ResourceStocks.Get(r).Amount);
    }

    [Fact]
    public void Maps_without_extras_keep_their_version_1_bytes_and_hash()
    {
        MapDocument doc = Map();
        doc.Apply("Place", s => s.Add(new MapObject("euro.peon", 0, 1024, 1024), null, out _));
        MapData map = doc.BuildMap();
        Assert.Equal(MapBinary.Version, BitConverter.ToUInt16(MapBinary.Write(map), 4));
        var plain = new MapData(map.Name, map.Width, map.Height, map.TerrainCorners, map.HeightCorners, map.CliffTiles, map.Objects, map.TerrainSet, map.SetTerrainCorners);
        Assert.Equal(plain.ContentHash, map.ContentHash);
    }

    [Fact]
    public void Units_turn_with_tab_and_resources_restore_their_default()
    {
        MapDocument doc = Map();
        int id = 0;
        doc.Apply("Place", s => s.Add(PlaceTools.Placement(Fixtures.Classic, "euro.knight", 0, 3, 3), null, out id));
        doc.Apply("Tab", s => s.Rotate([id], false).Rotate([id], false));
        Assert.Equal(2, doc.State.Find(id)!.Placement.Facing);
        doc.Apply("Shift+Tab", s => s.Rotate([id], true).Rotate([id], true).Rotate([id], true));
        Assert.Equal(7, doc.State.Find(id)!.Placement.Facing);
        doc.Apply("Amount", s => s.SetAmount([id], 500).SetAmount([id], 0));
        Assert.Equal(0, doc.State.Find(id)!.Placement.Amount);
    }

    [Fact]
    public void Palette_sections_are_mod_aware_and_items_have_groups()
    {
        Assert.Contains(Palette.Of(PaletteKind.Building, "euro"), e => e.Key == "euro.castle");
        Assert.Contains(Palette.Of(PaletteKind.Building, "comm"), e => e.Key == "comm.wall_stone");
        Assert.Contains(Palette.Of(PaletteKind.Unit, "euro"), e => e.Key == "euro.knight");
        Assert.Contains(Palette.Of(PaletteKind.Creature), e => e.Key == "comm.bear");
        Assert.DoesNotContain(Palette.Of(PaletteKind.Creature), e => e.Key == "comm.catapult");
        Assert.Contains(Palette.Of(PaletteKind.Tree), e => e.Key == "comm.tree");
        Assert.Contains(Palette.Of(PaletteKind.Resource), e => e.Key == "comm.res_gold");
        ImmutableArray<PaletteEntry> items = Palette.Of(PaletteKind.Item);
        Assert.True(items.Length >= 6, "6+ item types");
        PaletteEntry armor = Palette.Find("comm.item_armor")!;
        Assert.True(Palette.Siblings(armor).Length >= 6);
        Assert.Equal(PlaceTools.RandomOfGroup(Palette, armor, 3), PlaceTools.RandomOfGroup(Palette, armor, 3));
        Assert.True(armor.NatureByDefault);
    }

    [Fact]
    public void Object_properties_and_multi_edit()
    {
        MapDocument doc = Map();
        int a = 0, b = 0, house = 0;
        doc.Apply("Place", s => s
            .Add(new MapObject("euro.footman", 0, 1024, 1024), null, out a)
            .Add(new MapObject("euro.footman", 0, 5000, 1024), null, out b)
            .Add(MapObject.AtTile("euro.house", 1, 10, 10), null, out house));
        doc.Apply("Props", s => s.UpdateAll([a, b], o => o with { Info = o.Info with { Health = 50, MaxHealth = 80, Attack = 9, Mana = 4, Defence = 3, Level = 2, Flags = ObjectFlags.Persistent | ObjectFlags.NoAi, Icon = new ResourceRef("user/icon.png"), BigIcon = new ResourceRef("user/big.png") } }));
        doc.Apply("House", s => s.Update(house, o => o with { Info = o.Info with { Name = "Keep", Title = "The Keep", Health = 10, MaxHealth = 200, Resistance = 5, Flags = ObjectFlags.NotAppear }, Placement = o.Placement with { Owner = 2 } }));
        ScenarioData built = doc.BuildScenario();
        Assert.Equal(80, built.ObjectAt(1).MaxHealth);
        Assert.Equal(ObjectFlags.Persistent | ObjectFlags.NoAi, built.ObjectAt(0).Flags);
        Assert.Equal(("Keep", 5, ObjectFlags.NotAppear), (built.ObjectAt(2).Name, built.ObjectAt(2).Resistance!.Value, built.ObjectAt(2).Flags));
        Assert.Equal(2, doc.BuildMap().Objects[2].Owner);

        // "Use default" clears an override.
        doc.Apply("Default", s => s.Update(house, o => o with { Info = o.Info with { Health = null } }));
        Assert.Null(doc.BuildScenario().ObjectAt(2).Health);
    }

    [Fact]
    public void Item_properties_per_type()
    {
        MapDocument doc = Map();
        doc.Apply("Items", s => s
            .Add(new MapObject("comm.item_scroll", MapObject.Nature, 1024, 1024), ObjectInfo.Plain with { Item = new ItemInfo("scroll", Text: "Go north.", Image: new ResourceRef("user/map.png"), ScrollTimeMs: 9000, AutoClose: true) }, out _)
            .Add(new MapObject("comm.item_magic", MapObject.Nature, 3072, 1024), ObjectInfo.Plain with { Item = new ItemInfo("book", MagicType: 3, NeedsMana: true, CooldownSeconds: 30, Charges: 5) }, out _)
            .Add(new MapObject("comm.item_health", MapObject.Nature, 5120, 1024), ObjectInfo.Plain with { Item = new ItemInfo("potion", Value: 40) }, out _)
            .Add(new MapObject("comm.resitem", MapObject.Nature, 7168, 1024), ObjectInfo.Plain with { Item = new ItemInfo("gold", Amount: 300) }, out _));
        MapDocument back = MapPackage.Read(MapPackage.Write(doc), Fixtures.RulesFor);
        Assert.Equal("Go north.", back.State.Objects[0].Info.Item!.Text);
        Assert.Equal(5, back.State.Objects[1].Info.Item!.Charges);
        Assert.Equal(300, back.State.Objects[3].Info.Item!.Amount);
    }

    [Fact]
    public void Locator_filters_by_type_player_and_name()
    {
        MapDocument doc = Map();
        doc.Apply("Place", s => s
            .Add(new MapObject("euro.footman", 0, 1024, 1024), new ObjectInfo("Oren", "", ObjectFlags.None), out _)
            .Add(new MapObject("euro.footman", 1, 3000, 1024), null, out _)
            .Add(MapObject.AtTile("euro.house", 1, 10, 10), null, out _));
        Assert.Equal(2, ObjectLocator.Find(doc.State, new LocatorFilter(TypeKey: "euro.footman")).Length);
        Assert.Equal(2, ObjectLocator.Find(doc.State, new LocatorFilter(Owner: 1)).Length);
        Assert.Single(ObjectLocator.Find(doc.State, new LocatorFilter(NamedOnly: true)));
        Assert.Single(ObjectLocator.Find(doc.State, new LocatorFilter(Text: "ore")));
        Assert.Equal(2, ObjectLocator.InRect(doc.State, 0, 0, 1, 1).Length);
    }

    [Fact]
    public void Starting_locations_kingdoms_resources_and_diplomacy()
    {
        MapDocument doc = Map();
        doc.Apply("Kingdoms", s => s.WithScenario(m => m
            .SetStart(1, 3, 4)
            .SetStart(8, 20, 20)
            .UpdateKingdom(2, k => k with { Name = "North", TzarName = "Ivan", Civilization = Civilization.Asian, PlayerType = PlayerType.Rescue, Active = true, AiScript = "defend.ais", EconomyAi = false, WarAi = true })
            .Fill(Fixtures.Classic, 1, 2)
            .EqualizeColumn(KingdomResource.Gold, 1)
            .EqualizeRow(3, KingdomResource.Gold)
            .SetDiplomacy(new DiplomacyDef(1, 2, Stance.Ally, true, true, false))
            .UpdateKingdom(1, k => k with { SharedVictory = true, ForbiddenUnits = KingdomEdits.Toggle(k.ForbiddenUnits, "euro.knight", true), InitialResearches = KingdomEdits.Toggle(k.InitialResearches, "te_fields", true) })));
        ScenarioModel m = doc.State.Scenario;
        Assert.Equal((3, 4), (m.Kingdom(1)!.StartTileX, m.Kingdom(1)!.StartTileY));
        Assert.Equal((20, 20), (m.Kingdom(8)!.StartTileX, m.Kingdom(8)!.StartTileY));
        Assert.Equal(1000, m.Kingdom(1)!.Gold);
        Assert.Equal(1000, m.Kingdom(5)!.Gold);
        Assert.Equal(1000, m.Kingdom(3)!.Stone);
        Assert.Equal(Stance.Ally, m.DiplomacyOf(2, 1)!.Stance);
        Assert.Equal(Stance.Enemy, m.DiplomacyOrDefault(3, 4).Stance);
        Assert.Equal(["euro.knight"], m.Kingdom(1)!.ForbiddenUnits.Items.ToArray());
        Assert.Contains("euro.castle", KingdomEdits.BuildingKeys(Fixtures.Classic));
        Assert.Contains("te_fields", KingdomEdits.ResearchKeys(Fixtures.Classic));
        Assert.NotEmpty(KingdomEdits.UpgradeKeys(Fixtures.Classic));
        Assert.Equal(8, m.Kingdoms.Length);
        Assert.Throws<ArgumentException>(() => m.SetDiplomacy(new DiplomacyDef(1, 1, Stance.Ally, false, false, false)));
    }

    [Fact]
    public void Map_properties_round_trip()
    {
        MapDocument doc = Map();
        doc.Apply("Props", s => s.WithScenario(m => m with { Properties = new MapProperties("Title", "Me", "Desc", GameType.NotSpecified, VictoryCondition.ScoreLimit, 120, 2, true, true) }));
        MapDocument back = MapPackage.Read(MapPackage.Write(doc), Fixtures.RulesFor);
        Assert.Equal(doc.State.Scenario.Properties, back.State.Scenario.Properties);
    }

    [Fact]
    public void User_resources_custom_minimap_sounds_and_music()
    {
        Assert.Equal(AssetKind.Image, AssetEdits.KindOf("Hero.BMP"));
        Assert.Equal(AssetKind.Image, AssetEdits.KindOf("hero.png"));
        Assert.Equal(AssetKind.Sound, AssetEdits.KindOf("a.wav"));
        Assert.Equal(AssetKind.Sound, AssetEdits.KindOf("a.ogg"));
        Assert.Equal(AssetKind.Music, AssetEdits.KindOf("a.mid"));
        Assert.Null(AssetEdits.KindOf("a.exe"));
        Assert.Equal("user/hero.png", AssetEdits.PathFor("C:/x/Hero.png"));
        MapDocument doc = Map();
        doc.Apply("Import", s => s.Import("user/hero.png", [1, 2]).Import(AssetEdits.MinimapPath, [3]).Import("user/wind.wav", [4]).Import("user/theme.mid", [5]));
        doc.Apply("Sounds", s => s.WithScenario(m => m
            .AddSound(new CustomSoundDef("wind", new ResourceRef("user/wind.wav"), SoundRepeat.Random, 1000, 5000, null, 4, 4), out _)
            .AddSound(new CustomSoundDef("bell", new ResourceRef("user/wind.wav"), SoundRepeat.Delayed, 3000, 0, null, 6, 6), out _)
            .WithMusic([new ResourceRef("user/theme.mid")])));
        Assert.Equal(["user/hero.png", AssetEdits.MinimapPath], doc.State.Of(AssetKind.Image).ToArray());
        Assert.Equal([3], doc.State.Export(AssetEdits.MinimapPath));
        Assert.Empty(MapValidator.Validate(doc).WithCode("resource.missing"));
        doc.Apply("Delete", s => s.DeleteAsset("user/wind.wav"));
        Assert.Equal(2, MapValidator.Validate(doc).WithCode("resource.missing").Length);
    }

    [Fact]
    public void Sound_sources_follow_objects_and_fall_back_to_their_point()
    {
        MapDocument doc = Map();
        int bell = 0;
        doc.Apply("Bell", s => s.Add(MapObject.AtTile("euro.house", 0, 7, 7), null, out bell));
        doc.Apply("Sound", s => s.WithScenario(m => m.AddSound(new CustomSoundDef("ring", new ResourceRef("user/a.wav"), SoundRepeat.Every, 2000, 0, bell, 0, 0), out _)));
        Assert.Equal(0, doc.BuildScenario().Sounds[0].SourceObject);
        doc.Apply("Delete", s => s.Remove([bell]));
        CustomSoundDef sound = doc.State.Scenario.Sounds[0];
        Assert.Equal((null, 7, 7), (sound.SourceObject, sound.X, sound.Y));
    }

    [Fact]
    public void Decorations_stack_on_land_water_objects_need_water_and_tab_cycles()
    {
        MapDocument doc = Lake();
        DocumentState s = doc.State.AddDecoration(doc, new MapDecoration("ground_patch", "daisies", 2, 2, -1, -1), out bool a)
            .AddDecoration(doc, new MapDecoration("ground_patch", "ferns", 2, 2, -1, -1), out bool b)
            .AddDecoration(doc, new MapDecoration("ground_object", "sign", 15, 15, -1, -1), out bool c)
            .AddDecoration(doc, new MapDecoration("water_object", "reeds", 15, 15, -1, -1), out bool d);
        Assert.Equal((true, true, false, true), (a, b, c, d));
        Assert.Equal(2, s.RemoveDecorationsAt(2, 2).Decorations.Count + 1);
        Assert.Equal("c", DecorationEdits.NextShape(["a", "b", "c"], "b"));
        Assert.Equal("c", DecorationEdits.NextShape(["a", "b", "c"], "a", backwards: true));
    }

    [Fact]
    public void Copy_paste_across_maps_and_prefabs()
    {
        MapDocument source = Map();
        int a = 0, b = 0;
        source.Apply("Place", s => s
            .Add(new MapObject("euro.footman", 0, (3 * Tile) + 1024, (3 * Tile) + 1024) { Facing = 2 }, new ObjectInfo("Ann", "", ObjectFlags.Persistent, Level: 3), out a)
            .Add(MapObject.AtTile("euro.house", 0, 5, 3), null, out b));
        string clip = ObjectClipboard.Copy(source.State, [a, b], "outpost");
        MapDocument target = Map();
        target.Apply("Paste", s => ObjectClipboard.Paste(s, clip, 20, 20, target.Width, target.Height, new Occupancy(target, Fixtures.Classic), out _));
        EditorObject ann = target.State.Objects[0];
        Assert.Equal(((20 * Tile) + 1024, 2, 3, "Ann"), (ann.X, ann.Placement.Facing, ann.Info.Level!.Value, ann.Info.Name));
        Assert.Equal(22, target.State.Objects[1].TileX);
        Assert.Throws<FormatException>(() => ObjectClipboard.Paste(target.State, "{}", 0, 0, 32, 32, null, out _));
    }

    [Fact]
    public void Multi_select_transforms()
    {
        MapDocument doc = Map();
        int a = 0, b = 0;
        doc.Apply("Place", s => s
            .Add(new MapObject("euro.footman", 0, 4 * Tile, 4 * Tile), null, out a)
            .Add(new MapObject("euro.footman", 0, 8 * Tile, 4 * Tile), null, out b));
        doc.Apply("Turn", s => Transforms.Apply(s, [a, b], Transforms.Kind.RotateClockwise, doc.Width, doc.Height));
        Assert.Equal((6 * Tile, 2 * Tile), (doc.State.Find(a)!.X, doc.State.Find(a)!.Y));
        Assert.Equal((6 * Tile, 6 * Tile), (doc.State.Find(b)!.X, doc.State.Find(b)!.Y));
        Assert.Equal(2, doc.State.Find(a)!.Placement.Facing);
        doc.Apply("Mirror", s => Transforms.Apply(s, [a, b], Transforms.Kind.MirrorVertical, doc.Width, doc.Height));
        Assert.Equal(6 * Tile, doc.State.Find(a)!.Y);
    }

    [Fact]
    public void Layers_hide_and_lock()
    {
        MapDocument doc = Map();
        int tree = 0, man = 0;
        doc.Apply("Place", s => s.Add(MapObject.AtTile("comm.tree", MapObject.Nature, 1, 1), null, out tree).Add(new MapObject("euro.peon", 0, 9000, 9000), null, out man));
        var layers = new LayerSettings();
        layers.SetLocked(EditorLayer.Trees, true);
        Assert.Equal([man], layers.Editable(doc.State, Palette, [tree, man]).ToArray());
        layers.SetLocked(EditorLayer.Trees, false);
        layers.SetHidden(EditorLayer.Units, true);
        Assert.Equal([tree], layers.Editable(doc.State, Palette, [tree, man]).ToArray());
    }

    [Fact]
    public void New_empty_map_with_patches_rocks_and_animals()
    {
        MapDocument doc = NewMap.Empty(new NewMapOptions("Fresh", 64, "grass", PatchesPercent: 2, RocksPerKingdom: 2, Animals: 6, Seed: 5), Fixtures.RulesFor("grass"), Fixtures.Classic);
        Assert.False(doc.History.CanUndo);
        Assert.False(doc.IsDirty);
        Assert.NotEmpty(doc.State.Decorations);
        Assert.Equal(4, doc.State.Objects.Count(o => o.TypeKey == "comm.res_gold"));
        Assert.Equal(6, doc.State.Objects.Count(o => o.TypeKey is "comm.cow" or "comm.bear"));
        Assert.All(doc.State.Objects, o => Assert.Equal(MapObject.Nature, o.Owner));
        Assert.Equal("Fresh", doc.State.Scenario.Properties.Title);
    }

    [Fact]
    public void New_random_map_from_the_generator_with_per_player_kingdoms()
    {
        MapGenOptions options = new MapGenOptions { Size = RandomMapSize.Small, Players = 3, Seed = 11 }.BindTo(Fixtures.Classic);
        MapDocument doc = NewMap.Random(options, Fixtures.RulesFor(RandomMapNames.SetId(options.TerrainSet)), "Skirmish");
        Assert.Equal(3, doc.State.Scenario.Kingdoms.Length);
        Assert.True(doc.State.Scenario.Kingdom(3)!.StartTileX >= 0);
        Assert.NotEmpty(doc.State.Objects);
        Assert.False(MapValidator.Validate(doc, new ValidationOptions { Ruleset = Fixtures.Classic }).BlocksPlay);
    }
}
