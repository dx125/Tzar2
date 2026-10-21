using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tzar2.Client.Logic;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Content.Terrain;
using Tzar2.Content.Vfs;
using Xunit;

namespace Tzar2.Client.Tests;

public sealed class PresentationCatalogTests
{
    private static readonly Lazy<(Ruleset Rules, MemorySource Tables)> Data = new(() =>
    {
        MemorySource tables = SyntheticLegacyContent.CreateTables();
        tables.Add("tables/data/menu.ini.json", Ini(("e_peon", [("default", "move"), ("move", "M"), ("e_build", "B"), ("harvest", "H"), ("bribe", "I")]), ("e_build", [("e_house", "H"), ("e_rare", "")])));
        tables.Add("tables/data/orders.ini.json", Ini(
            ("move", [("ACTION", "move"), ("IMG", "7")]),
            ("harvest", [("ACTION", "harvest"), ("IMG", "9")]),
            ("bribe", [("ACTION", "bribe"), ("IMG", "1")]),
            ("e_build", [("ACTION", string.Empty), ("SUBMENU", "e_build"), ("IMG", "24")]),
            ("e_house", [("ACTION", "BUILD house"), ("IMG", "74")])));
        tables.Add("tables/data/sounds.ini.json", Ini(("foot_cmd", [("command\\Foot1.wav", "100"), ("command\\foot2.wav", "300")]), ("priority", [("obj_select", "255")])));
        tables.Add("tables/data/keys.ini.json", Ini(("Ctrl", [("C", "castle"), ("T", "tower_wood,tower_stone2")])));
        tables.Add("tables/data/icons.ini.json", Ini(("e_peon", [("image", "ICONS\\icons.bmp"), ("index", "33")])));
        tables.Add("tables/local/strings.ini.json", Ini(("objects", [("peon e", "Peasant"), ("castle", "Castle")]), ("orders_names", [("move", "Move")]), ("mainscr", [("single", "SINGLE PLAYER| Single player game")])));
        var vfs = new VirtualFileSystem();
        vfs.Mount(new EngineDefaultsSource());
        vfs.Mount(tables);
        return (RulesetCompiler.Compile(vfs, "classic").Ruleset, tables);
    });

    private static PresentationCatalog Catalog()
    {
        (Ruleset rules, MemorySource tables) = Data.Value;
        return new PresentationCatalog(rules, p => tables.Contains(p) ? tables.ReadAllBytes(p) : null, tables.Paths.Where(p => p.StartsWith(PresentationCatalog.ObjectsFolder, StringComparison.Ordinal)));
    }

    [Fact]
    public void Types_get_their_images_names_menus_and_icons_from_the_object_tables()
    {
        PresentationCatalog catalog = Catalog();
        TypeVisual peon = catalog.Type((ushort)catalog.Ruleset.EntityTypeId("euro.peon"));
        Assert.Equal(("peon", "euro", "Peasant"), (peon.Class, peon.Civilization, peon.Name));
        Assert.Equal("e_peon", peon.Image(UnitAnim.Base));
        Assert.Equal("e_peon", peon.Image(UnitAnim.Chop));
        Assert.Equal(33, peon.IconIndex);
        Assert.Equal("Castle", catalog.Type((ushort)catalog.Ruleset.EntityTypeId("euro.castle")).Name);
        Assert.Equal("SINGLE PLAYER", catalog.Text("mainscr", "single", "x"));
        Assert.Equal("fallback", catalog.Text("mainscr", "missing", "fallback"));
    }

    [Fact]
    public void Cards_classify_orders_and_keep_menu_letters()
    {
        PresentationCatalog catalog = Catalog();
        IReadOnlyList<CardButton> card = catalog.Card("E_PEON");
        Assert.Equal(["move", "e_build", "harvest", "bribe"], card.Select(b => b.Order));
        Assert.Equal(['M', 'B', 'H', 'I'], card.Select(b => b.Hotkey));
        Assert.Equal([CardAction.Move, CardAction.Submenu, CardAction.Harvest, CardAction.Bribe], card.Select(b => b.Action));
        Assert.Equal("e_build", card[1].Argument);
        Assert.Equal(7, card[0].ImageIndex);
        Assert.Equal("Move", card[0].Tooltip);
        CardButton house = catalog.Card("e_build")[0];
        Assert.Equal((CardAction.Build, "house"), (house.Action, house.Argument));
        Assert.Equal(catalog.Ruleset.EntityTypeId("euro.house"), catalog.TypeOfClass("euro", "house"));
        Assert.Equal((CardAction.Train, "peon"), PresentationCatalog.Classify("CREATE peon", null));
        Assert.Equal((CardAction.Stop, string.Empty), PresentationCatalog.Classify("STOP", null));
    }

    [Theory]
    [InlineData("TECHNOLOGY", "te_feudalism", CardAction.Research, "te_feudalism")]
    [InlineData("UPGRADE gate_stone", "to_gate_stone", CardAction.Upgrade, "to_gate_stone")]
    [InlineData("capture", null, CardAction.Capture, "")]
    [InlineData("move_advance", null, CardAction.MoveAdvance, "")]
    [InlineData("stand_ground", null, CardAction.Stance, "1")]
    [InlineData("gate_subai_on", null, CardAction.Gate, "auto")]
    [InlineData("die", null, CardAction.Delete, "")]
    [InlineData("emerge", null, CardAction.Unload, "")]
    [InlineData("become_priest", null, CardAction.Become, "priest")]
    [InlineData("next_idle_peon", null, CardAction.NextIdle, "")]
    [InlineData("TECHNOLOGY", null, CardAction.Unsupported, "TECHNOLOGY")]
    [InlineData("spell spell_heal", null, CardAction.Spell, "spell_heal")]
    [InlineData("raisedead", null, CardAction.Spell, "raisedead")]
    [InlineData("murder", null, CardAction.Spell, "murder")]
    [InlineData("chop", null, CardAction.Harvest, "")]
    [InlineData("fishing", null, CardAction.Harvest, "")]
    [InlineData("loan gold", null, CardAction.Loan, "gold")]
    [InlineData("loan_return", null, CardAction.Loan, "return")]
    [InlineData("bribe", null, CardAction.Bribe, "")]
    [InlineData("select_crusaders", null, CardAction.Crusade, "")]
    [InlineData("pick_item", null, CardAction.PickItem, "")]
    [InlineData("item_use", null, CardAction.UseItem, "")]
    [InlineData("item_use_instant", null, CardAction.UseItem, "self")]
    [InlineData("item_drop", null, CardAction.DropItem, "")]
    [InlineData("gamble", null, CardAction.Gamble, "")]
    [InlineData("trade1", null, CardAction.Exchange, "")]
    [InlineData("trade", null, CardAction.Trade, "")]
    [InlineData("impersonate", null, CardAction.Unsupported, "impersonate")]
    public void Card_orders_map_onto_the_sims_commands(string action, string? className, CardAction expected, string argument)
        => Assert.Equal((expected, argument), PresentationCatalog.Classify(action, null, className));

    [Fact]
    public void Sound_sets_and_key_classes_resolve_to_package_paths()
    {
        PresentationCatalog catalog = Catalog();
        Assert.Equal([("sound/command/foot1.wav", 100), ("sound/command/foot2.wav", 300)], catalog.Sounds("FOOT_CMD"));
        Assert.Equal([("sound/buildings/e_castle1.wav", 1)], catalog.Sounds("buildings\\e_castle1.wav"));
        Assert.Empty(catalog.Sounds(null));
        Assert.Equal(255, catalog.SoundPriority("obj_select"));
        Assert.Equal(128, catalog.SoundPriority("unknown"));
        Assert.Equal(["tower_wood", "tower_stone2"], catalog.ClassesOfKey('T'));
        Assert.Empty(catalog.ClassesOfKey('Q'));
    }

    internal static byte[] Ini(params (string Name, (string Key, string Value)[] Entries)[] sections)
    {
        var sb = new StringBuilder("{\"source\":\"test\",\"codePage\":1252,\"sections\":[");
        sb.Append(string.Join(",", sections.Select(s =>
            $"{{\"name\":\"{s.Name}\",\"line\":1,\"entries\":[{string.Join(",", s.Entries.Select(e => $"[\"{Escape(e.Key)}\",\"{Escape(e.Value)}\"]"))}]}}")));
        sb.Append("]}");
        return Encoding.UTF8.GetBytes(sb.ToString());

        static string Escape(string v) => v.Replace("\\", "\\\\", StringComparison.Ordinal);
    }
}

public sealed class TerrainAndSpriteRuleTests
{
    [Fact]
    public void Facings_map_onto_the_sheet_columns()
    {
        // Sim east (0) → column 2 (E); south (2) → 0 (S); north (6) → 4 (N); south-west (3) → 7 (SW).
        Assert.Equal([2, 1, 0, 7, 6, 5, 4, 3], Enumerable.Range(0, 8).Select(f => SpriteRules.Direction(f, 8)));
        Assert.Equal(0, SpriteRules.Direction(5, 1));
    }

    [Fact]
    public void Sites_and_rocks_pick_stages()
    {
        Assert.Equal(0, SpriteRules.SiteFrame(0, 4));
        Assert.Equal(1, SpriteRules.SiteFrame(250, 4));
        Assert.Equal(3, SpriteRules.SiteFrame(1000, 4));
        Assert.Equal(0, SpriteRules.DepletionFrame(1000, 1000, 4));
        Assert.Equal(3, SpriteRules.DepletionFrame(10, 1000, 4));
        Assert.Equal(0, SpriteRules.DepletionFrame(5, 0, 4));
    }

    [Fact]
    public void The_terrain_grid_never_needs_three_terrains_in_a_tile()
    {
        Ruleset rules = SyntheticLegacyContent.CompileRuleset();
        byte grass = (byte)rules.TerrainTypes.First(t => !t.IsWater).Id, water = (byte)rules.TerrainTypes.First(t => t.IsWater).Id;
        const int n = 40;
        byte[] corners = new byte[(n + 1) * (n + 1)];
        for (int y = 0; y <= n; y++)
        {
            for (int x = 0; x <= n; x++)
            {
                corners[(y * (n + 1)) + x] = (x - 20) * (x - 20) + ((y - 20) * (y - 20)) < 40 || (x > 30 && y < 4) ? water : grass;
            }
        }

        var map = new MapData("t", n, n, corners, [], []);
        TerrainSet set = Set();
        TerrainGrid grid = TerrainPresentation.Build(map, rules, set);
        Assert.Equal((0, 1, 2, 3), TerrainPresentation.Palette(set));
        bool variety = false;
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                (int a, int b, int c, int d) = grid.CornersOf(x, y);
                int[] distinct = new[] { a, b, c, d }.Distinct().ToArray();
                Assert.True(distinct.Length <= 2, $"tile {x},{y} has {string.Join(",", distinct)}");
                if (distinct.Length == 2)
                {
                    Assert.NotNull(set.TransitionOf(distinct.Min(), distinct.Max()));
                }

                variety |= distinct.Contains(3);
            }
        }

        Assert.True(variety);
        Assert.Equal(0, grid[20, 20]);
        Assert.Equal(1, grid[20, 27]);
    }

    private static TerrainSet Set()
    {
        string json = """
            [{"set":"t","name":"T","table":"t","folder":"art/terrain/t/","terrains":[{"index":"0","name":"Water"},{"index":"1","name":"Dirt"},{"index":"2","name":"Grass (Type 1)"},{"index":"3","name":"Grass (Type 2)"}],
              "files":[{"pair":"00","asset":"a","size":"1,1,0"},{"pair":"11","asset":"b","size":"1,1,0"},{"pair":"22","asset":"c","size":"1,1,0"},{"pair":"33","asset":"d","size":"1,1,0"},
                       {"pair":"01","asset":"e","size":"2,14,4"},{"pair":"12","asset":"f","size":"2,14,0"},{"pair":"23","asset":"g","size":"2,14,0"}],
              "light":null,"trees":null,"treeShadows":null}]
            """;
        return TerrainSet.ParseAll(Encoding.UTF8.GetBytes(json))[0];
    }
}

public sealed class GameArgsTests
{
    [Fact]
    public void Game_options_parse_and_imply_the_mode()
    {
        ViewerArgs a = ViewerArgs.Parse(["--package", "p", "--game-smoke", "--ticks", "120", "--map-size", "64", "--players", "4", "--seed", "9", "--terrain", "snow"]);
        Assert.Empty(a.Errors);
        Assert.True(a.GameSmoke && a.Skirmish && !a.IsViewerMode);
        Assert.Equal((120, 64, 4, 9UL, "snow"), (a.Ticks, a.MapSize, a.Players, a.Seed!.Value, a.Terrain));
        Assert.True(ViewerArgs.Parse(["--smoke"]).IsViewerMode);
        Assert.True(ViewerArgs.Parse(["--viewer"]).IsViewerMode);
        Assert.Equal("d", ViewerArgs.Parse(["--autoplay", "d"]).AutoplayDir);
        ViewerArgs r = ViewerArgs.Parse(["--map-type", "lakes", "--map-size", "large"]);
        Assert.Equal(("lakes", "large", 0), (r.MapType, r.MapSizeName, r.MapSize));
        Assert.NotEmpty(ViewerArgs.Parse(["--players", "12"]).Errors);
        Assert.NotEmpty(ViewerArgs.Parse(["--ticks", "x"]).Errors);
    }
}

public sealed class DisplayMathTests
{
    [Theory]
    [InlineData(1440, 900, 0, 1)]
    [InlineData(1920, 1080, 0, 1)]
    [InlineData(2560, 1440, 0, 2)]
    [InlineData(3840, 2160, 0, 3)]
    [InlineData(2560, 1440, 3, 2)] // 3x would make the logical screen 853 x 480: too small for the HUD
    [InlineData(3840, 2160, 2, 2)]
    [InlineData(1024, 720, 4, 1)]
    [InlineData(800, 600, 0, 1)]
    public void The_interface_scale_keeps_the_logical_screen_large_enough_for_the_hud(int width, int height, int setting, int expected)
    {
        int k = DisplayMath.Scale(width, height, setting);
        Assert.Equal(expected, k);
        (int w, int h) = DisplayMath.Logical(width, height, k);
        Assert.True(k == 1 || (w >= DisplayMath.MinWidth && h >= DisplayMath.MinHeight), $"{w} x {h}");
    }

    [Fact]
    public void The_logical_screen_is_the_window_over_the_scale()
    {
        Assert.Equal((1280, 720), DisplayMath.Logical(2560, 1440, 2));
        Assert.Equal((1920, 1080), DisplayMath.Logical(1920, 1080, 1));
        Assert.Equal((1024, 768), DisplayMath.Classic);
    }
}
