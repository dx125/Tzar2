using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Tzar2.Content.Compiled;
using Tzar2.Content.Vfs;

namespace Tzar2.Content.Rules;

/// <summary>
/// Synthetic stand-ins for the imported original tables (REQ-LEGAL-03: public CI never sees original data): the same
/// shape the importer writes (<c>tables/data/*.json</c>, the sprite index, DAT footprints) for one civilization
/// (<c>euro</c>) and the common objects. Every number is invented and differs from the original in every column (a
/// test checks this against a local install when <c>TZAR2_ORIGIN</c> is set). The technology tree, command cards and
/// unit groups are a reduced mix: some relations are the ones the manual describes in its own words (Architecture
/// unlocks stone walls, gates and bridges; Domestication cows), others are deliberately permuted (Halberd after Scale
/// Armor, galleons after Usury) so the fixture is not a copy of the original tree; the guard test covers numbers only.
/// The keys and display-name conventions are the original's stable identifiers (REQ-DATA-06; TDD Appendix E lists the
/// 87 technologies and 12 upgrades). The <c>classic</c> ruleset compiles from it exactly as from a real install, so
/// tests, benchmarks and the determinism CI run the real compiler and rules (ADRs 0032, 0040).
/// </summary>
public static class SyntheticLegacyContent
{
    /// <summary>The 87 technology keys of the original (TDD Appendix E.3).</summary>
    public static IReadOnlyList<string> TechnologyKeys { get; } =
    [
        "te_fields", "te_livestock", "te_feudalism", "te_architecture", "te_longbow", "te_platearmor", "te_woodplus", "te_stoneplus",
        "te_goldplus", "te_ironplus", "te_halberd", "te_learning", "te_temperiron", "te_crossbow", "te_scalearmor", "te_chainmail",
        "te_engineering", "te_cannontower", "te_heroism", "te_nobility", "te_honor", "te_warfare", "te_tactics", "te_mercenaries",
        "te_heroesquest", "te_fame", "te_siegetactics", "te_bodybuilding", "te_fanaticism", "te_crusade", "te_espionage",
        "te_inquisition", "te_healing", "te_medicine", "te_bless", "te_traderoutes", "te_mapmaking", "te_usury", "te_gambling",
        "te_supplystorage", "te_bribery", "te_tradetaxes", "te_bat", "te_invinsibility", "te_lightning", "te_freeze",
        "te_stonegolem", "te_landingtactics", "te_cowpoaching", "te_slavery", "te_ridinghorses", "te_janissary", "te_jihad",
        "te_magiccarpet", "te_jin", "te_witchcrafts", "te_rainoffire", "te_magicvision", "te_necromancy", "te_horsemanship",
        "te_scimitar", "te_jewelry", "te_orefloatation", "te_greatwall", "te_budhism", "te_irrigation", "te_armoredchariot",
        "te_bushido", "te_suntzubook", "te_meditation", "te_philosophy", "te_kungfu", "te_ninjas", "te_mastery", "te_tournaments",
        "te_banking", "te_chemistry", "te_gunpowder", "te_rocketarrow", "te_seafood", "te_concentration", "te_berserk",
        "te_flamebolt", "te_whirlpool", "te_dragon", "te_turntostone", "te_artofriding",
    ];

    /// <summary>The 12 upgrade keys of the original and what each turns into (null = a level upgrade).</summary>
    public static IReadOnlyList<(string Key, string? Into)> UpgradeKeys { get; } =
    [
        ("to_wtower2", "tower_wood2"), ("to_stower", "tower_stone"), ("to_stower2_1", "tower_stone2"), ("to_stower2_2", "tower_stone2"),
        ("to_wall_stone", "wall_stone"), ("to_gate_stone", "gate_stone"), ("to_ship_level2", null), ("to_ship_level3", null),
        ("to_ship_level4", null), ("to_ship_level5", null), ("to_ship_level6", null), ("to_convertres", null),
    ];

    /// <summary>A VFS with the engine defaults and the synthetic tables mounted.</summary>
    public static VirtualFileSystem CreateFileSystem()
    {
        var vfs = new VirtualFileSystem();
        vfs.Mount(new EngineDefaultsSource());
        vfs.Mount(CreateTables());
        return vfs;
    }

    /// <summary>Compiles the <c>classic</c> ruleset (or <paramref name="name"/>) from the synthetic tables.</summary>
    public static Ruleset CompileRuleset(string name = "classic", RulesetCompileOptions? options = null)
        => RulesetCompiler.Compile(CreateFileSystem(), name, options).Ruleset;

    /// <summary>The synthetic tables as a package-shaped source.</summary>
    public static MemorySource CreateTables()
    {
        var source = new MemorySource("synthetic legacy tables");
        string[] unitHeader = ["UNIT", "CLASS", "COST", "TIME", "SIGHT", "RANGE", "HEALTH", "ATTACK", "DEFENCE", "SPEED", "RATE", "SPECIAL", "DEPEND ON"];
        string[] buildingHeader = ["BUILDING", "CLASS", "COST", "TIME", "SIGHT", "RESISTANCE", "HEALTH", "EMIT (own/other)", "DEPENDS ON"];
        source.Add("tables/data/euro units.txt.json", Tsv("DATA/EURO UNITS.TXT", unitHeader,
        [
            ["Peon", "peon", "45f", "9", "7", "", "44", "3", "1", "5", "2", "Castle", "*"],
            ["Footman", "footman", "66f", "11", "6", "", "70", "9", "2", "5", "6", "Barracks", "*"],
            ["Archer", "archer", "35f, 25w", "13", "9", "7", "38", "7", "1", "5", "3", "Barracks", "*"],
            [],
            ["Maceman", "maceman", "110f", "15", "7", "", "90", "11", "1", "2", "2", "Barracks", "*"],
            ["Knight", "knight", "130f, 60g", "21", "6", "", "130", "13", "5", "7", "3", "Barracks", "te_platearmor"],
            ["Pikeman", "pikeman", "95f, 35g", "17", "6", "", "73", "9", "3", "3", "3", "Barracks", "te_halberd"],
            ["Mage", "mage", "140g", "29", "14", "9", "58", "4", "1", "5", "2", "Mage Tower", "*"],
        ]));
        source.Add("tables/data/euro buildings.txt.json", Tsv("DATA/EURO BUILDINGS.TXT", buildingHeader,
        [
            ["House", "house", "55w", "16", "3", "40", "420", "*", "*"],
            ["Lumber Mill", "lumber", "85w", "21", "3", "60", "610", "*", "*"],
            ["Storage", "storage", "160w", "26", "4", "70", "630", "*", "*"],
            ["Farm", "farm", "170w", "27", "6", "90", "640", "*", "*"],
            ["Barracks", "barrack", "160s,140f", "42", "7", "300", "1250", "*", "*"],
            ["Harbor", "harbor", "190w,210f", "59", "7", "190", "1350", "+90,-40", "*"],
            ["Blacksmith", "blacksmith", "160w,140f", "41", "6", "85", "790", "*", "*"],
            ["Workshop", "workshop", "140w,160f", "39", "4", "75", "810", "*", "blacksmith"],
            ["Tavern", "tavern", "210w,190f", "41", "6", "140", "990", "-40,-60", "*"],
            ["Stable", "stable", "190w,190f", "39", "6", "70", "820", "*", "*"],
            ["Market", "market", "210w,210f", "61", "7", "210", "1450", "*", "tcguild"],
            [],
            ["Castle", "castle", "900s,1100w", "90", "11", "700", "5500", "+300,-150", "*"],
            ["Cathedral", "cathedral", "1900s,2100g", "89", "9", "690", "4900", "+290,-60", "*"],
            ["Mage Guild", "magetower", "2050s,1950g", "92", "7", "680", "5200", "+320,-70", "*"],
            ["Merchants Guild", "tcguild", "1950s,2050g", "88", "9", "720", "5050", "+280,-45", "tavern"],
            ["Warriors Guild", "academy", "2100s,1900g", "91", "7", "710", "5100", "+310,-40", "blacksmith"],
        ]));
        source.Add("tables/data/comm buildings.txt.json", Tsv("DATA/COMM BUILDINGS.TXT", buildingHeader,
        [
            ["Field", "field", "110w", "17", "2", "*", "130", "*", "*"],
            [],
            ["Wood wall", "wall_wood", "23w", "7", "2", "*", "2100", "*", "*"],
            ["Stone wall", "wall_stone", "24s", "5", "3", "*", "3900", "*", "te_architecture"],
            ["Wood Gate", "gate_wood", "190w", "24", "7", "*", "1450", "*", "*"],
            ["Stone Gate", "gate_stone", "210s", "34", "11", "*", "3100", "*", "te_architecture"],
            ["Bridge", "bridge", "310s", "21", "2", "*", "9900", "*", "te_architecture"],
            [],
            ["Watch tower", "tower_wood", "315w", "11", "14", "110", "", "*", "*"],
            ["Guard tower", "tower_wood2", "320w", "16", "14", "140", "", "*", "*"],
            ["Keep tower", "tower_stone", "330w", "21", "16", "310", "", "*", "*"],
            ["Cannon tower", "tower_stone2", "340w", "26", "16", "290", "", "*", "te_cannontower"],
        ]));
        source.Add("tables/data/comm units.txt.json", Tsv("DATA/COMM UNITS.TXT", unitHeader,
        [
            ["Catapult", "catapult", "610w", "31", "19", "15", "105", "11", "1", "1", "3", "Workshop", "*"],
            [],
            ["Galleon", "galleon", "810f,1190w", "49", "15", "11", "1150", "33", "1", "5", "5", "Harbor", "te_mapmaking"],
            ["Fishing Boat", "fishing_boat", "110f,190w", "19", "11", "7", "55", "2", "1", "5", "5", "Harbor", "*"],
            [],
            ["Watch tower", "tower_wood", "310w", "29", "11", "7", "390", "6", "1", "", "4", "", "*"],
            ["Guard tower", "tower_wood2", "305w", "34", "15", "11", "990", "11", "1", "", "4", "", "*"],
            ["Keep tower", "tower_stone", "320w", "39", "17", "13", "1900", "15", "1", "", "3", "", "*"],
            ["Cannon tower", "tower_stone2", "330w", "44", "17", "9", "2400", "39", "1", "", "4", "", "te_cannontower"],
            [],
            ["Cow", "cow", "290f", "21", "4", "", "75", "", "", "2", "", "Farm", "te_cattle"],
        ]));
        source.Add("tables/data/res.ini.json", Ini("DATA/RES.INI",
        [
            ("Low", [("gold", "10"), ("stone", "20"), ("iron", "5"), ("food", "210"), ("wood", "220")]),
            ("Medium", [("gold", "500"), ("stone", "500"), ("iron", "500"), ("food", "500"), ("wood", "500")]),
            ("High", [("gold", "1000"), ("stone", "1000"), ("iron", "1000"), ("food", "1000"), ("wood", "1000")]),
            ("Others", [("golden rocks", "5000"), ("rocks", "3000"), ("fish patch", "333")]),
        ]));
        source.Add("tables/data/bonus.ini.json", Ini("DATA/BONUS.INI",
        [
            ("groups",
            [
                ("mounted", "e knight"), ("arrow_unit", "archer"), ("infantry_units", "footman, archer, pikeman, maceman"),
                ("siege", "catapult"), ("ships", "galleon, fishing_boat"),
            ]),
            ("footman", [("mounted", "+55%")]),
            ("arrow_unit", [("footman", "-25%")]),
        ]));
        AddTechnologyTables(source);
        AddObjects(source);
        source.Add("manifests/sprite-index.json", SpriteIndex(
        [
            ("e_castle", @"building\euro castle.bmp", "object", "0,0,160,160"),
            ("e_house", @"building\euro houses.bmp", "object", "0,0,64,64"),
            ("e_barrack", @"building\euro barrack.bmp", "object", "0,0,96,96"),
            ("e_storage", @"building\euro storage.bmp", "object", "0,0,64,64"),
            ("e_lumber", @"building\euro lumbermill.bmp", "object", "0,0,96,96"),
            ("e_farm", @"building\euro farm.bmp", "object", "0,0,96,96"),
            ("e_harbor", @"building\euro harbor.bmp", "object", "0,0,96,96"),
            ("e_blacksmith", @"building\euro blacksmith.bmp", "object", "0,0,96,96"),
            ("e_workshop", @"building\euro workshop.bmp", "object", "0,0,96,96"),
            ("e_tavern", @"building\euro tavern.bmp", "object", "0,0,96,96"),
            ("e_stable", @"building\euro stable.bmp", "object", "0,0,96,96"),
            ("e_market", @"building\euro market.bmp", "object", "0,0,96,96"),
            ("e_cathedral", @"building\euro cathedral.bmp", "object", "0,0,128,128"),
            ("e_magetower", @"building\euro mage.bmp", "object", "0,0,128,128"),
            ("e_tcguild", @"building\euro guild.bmp", "object", "0,0,128,128"),
            ("e_academy", @"building\euro academy.bmp", "object", "0,0,128,128"),
            ("field", @"building\fields.bmp", "selection", "0,0,96,96"),
            ("wall_wood", @"building\walls\wood.bmp", "object", "0,0,32,32"),
            ("wall_stone", @"building\walls\stone.bmp", "object", "0,0,32,32"),
            ("bridge", @"building\bridge\bridge.bmp", "object", "0,0,160,64"),
            ("tower_wood", @"building\tower wood.bmp", "object", "0,0,64,64"),
            ("tower_wood2", @"building\tower wood 2.bmp", "object", "0,0,64,64"),
            ("tower_stone", @"building\tower stone.bmp", "object", "0,0,64,64"),
            ("tower_stone2", @"building\tower stone 2.bmp", "object", "0,0,64,64"),
            ("res_gold", @"scenery\res gold.bmp", "object", "0,0,64,64"),
            ("res_stone", @"scenery\res stone.bmp", "object", "0,0,64,64"),
            ("e_peon", @"unit\euro peasant.bmp", "object", "10,10,30,30"),
        ]));

        // The castle's DAT grid cuts its four corners (2 = free), like most large buildings (docs/formats/dat.md §3).
        source.Add("manifests/footprints.json", Json(w =>
        {
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteString("source", "images/building/euro castle.dat");
            w.WriteStartArray("footprint");
            foreach (int v in new[] { 0, 0, 159, 159 })
            {
                w.WriteNumberValue(v);
            }

            w.WriteEndArray();
            w.WriteNumber("gridWidth", 5);
            w.WriteNumber("gridHeight", 5);
            w.WriteStartArray("grid");
            for (int y = 0; y < 5; y++)
            {
                for (int x = 0; x < 5; x++)
                {
                    w.WriteNumberValue((x is 0 or 4) && (y is 0 or 4) ? 2 : 0);
                }
            }

            w.WriteEndArray();
            w.WriteNumber("frameBoxes", -1);
            w.WriteEndObject();
            w.WriteEndArray();
        }));
        return source;
    }

    // TECHNOLOGIES.INI, UPGRADES.INI (invented times and costs), a reduced, partly permuted euro tree, and the command cards
    // (MENU.INI, ORDERS.INI) that say which building researches what.
    private static void AddTechnologyTables(MemorySource source)
    {
        var technologies = new List<(string, List<(string, string)>)>();
        for (int i = 0; i < TechnologyKeys.Count; i++)
        {
            // Invented: times end in 3 ms and gold amounts in 4, which no original value does.
            technologies.Add((TechnologyKeys[i], [("displayname", "Synthetic " + i.ToString(CultureInfo.InvariantCulture)),
                ("time", (10_003 + (i * 1000)).ToString(CultureInfo.InvariantCulture)), ("gold", (104 + (i * 10)).ToString(CultureInfo.InvariantCulture))]));
        }

        source.Add("tables/data/technologies.ini.json", Ini("DATA/TECHNOLOGIES.INI", [.. technologies]));
        var upgrades = new List<(string, List<(string, string)>)>();
        for (int i = 0; i < UpgradeKeys.Count; i++)
        {
            upgrades.Add((UpgradeKeys[i].Key, [("displayname", "Synthetic upgrade " + i.ToString(CultureInfo.InvariantCulture)),
                ("time", (2_003 + (i * 500)).ToString(CultureInfo.InvariantCulture)), (i < 4 ? "wood" : "stone", (21 + (i * 10)).ToString(CultureInfo.InvariantCulture))]));
        }

        source.Add("tables/data/upgrades.ini.json", Ini("DATA/UPGRADES.INI", [.. upgrades]));
        source.Add("tables/data/techtree euro.ini.json", Ini("DATA/TECHTREE EURO.INI",
        [
            ("depend",
            [
                ("cow", "te_livestock"), ("galleon", "te_usury"), ("bridge", "te_architecture"), ("wall_stone", "te_architecture"),
                ("gate_stone", "te_architecture"), ("to_wall_stone", "te_architecture"), ("to_gate_stone", "te_architecture"),
                ("to_stower", "te_architecture"), ("to_stower2_1", "te_cannontower"), ("to_stower2_2", "te_cannontower"),
                ("academy", "te_learning"), ("te_halberd", "te_scalearmor"), ("te_chainmail", "te_platearmor"), ("te_tactics", "te_bodybuilding"),
                ("te_irrigation", "te_livestock"),
            ]),
            ("required", [("knight", "stable"), ("workshop", "blacksmith"), ("market", "tcguild")]),
            ("orders", [("spell_heal", "te_healing"), ("loan", "te_usury")]),
        ]));
        source.Add("tables/data/menu.ini.json", Ini("DATA/MENU.INI",
        [
            ("e_castle", [("e_peon", "P"), ("te_feudalism", "D"), ("te_architecture", "A"), ("te_learning", "L")]),
            ("e_farm", [("cow", "C"), ("te_fields", "F"), ("te_irrigation", "I"), ("te_livestock", "D"), ("te_cowpoaching", "P")]),
            ("e_blacksmith", [("te_temperiron", "T"), ("te_halberd", "H"), ("te_scalearmor", "S"), ("te_chainmail", "C"), ("te_platearmor", "P")]),
            ("e_lumber", [("te_woodplus", "S"), ("te_crossbow", "C"), ("te_longbow", "L")]),
            ("e_storage", [("te_goldplus", "G"), ("te_stoneplus", "S")]),
            ("e_workshop", [("te_engineering", "E"), ("te_cannontower", "C")]),
            ("e_academy", [("te_heroism", "H"), ("te_warfare", "W"), ("te_tactics", "T"), ("te_bodybuilding", "B")]),
            ("e_cathedral", [("te_fanaticism", "F"), ("te_budhism", "B"), ("te_healing", "H")]),
            ("e_tcguild", [("te_supplystorage", "S"), ("te_usury", "U"), ("te_mapmaking", "M")]),
            ("e_magetower", [("te_concentration", "C")]),
            ("e_harbor", [("te_landingtactics", "L"), ("te_greatwall", "G")]),
            ("tower_wood", [("attack", "A"), ("to_wtower2", "G")]),
            ("tower_wood2", [("to_stower", "G"), ("to_stower2_1", "C")]),
            ("tower_stone", [("to_stower2_2", "C")]),
            ("wall_wood", [("to_wall_stone", "S")]),
            ("gate_wood_00", [("gateopen", "O"), ("to_gate_stone", "S")]),
            ("e_galleon", [("to_ship_level2", "L")]),
        ]));
        var orders = new List<(string, List<(string, string)>)>();
        foreach (string tech in TechnologyKeys)
        {
            orders.Add((tech, [("ACTION", "TECHNOLOGY"), ("CLASSNAME", tech), ("IMG", "1")]));
        }

        foreach ((string key, string? into) in UpgradeKeys)
        {
            orders.Add((key, [("ACTION", "UPGRADE " + (into ?? key)), ("CLASSNAME", key), ("IMG", "2")]));
        }

        orders.Add(("e_peon", [("ACTION", "CREATE peon"), ("CLASSNAME", "peon"), ("IMG", "3")]));
        orders.Add(("gateopen", [("ACTION", "gateopen"), ("IMG", "4")]));
        source.Add("tables/data/orders.ini.json", Ini("DATA/ORDERS.INI", [.. orders]));
    }

    private static void AddObjects(MemorySource source)
    {
        AddObject(source, "peon", "unit", "euro", "e_peon");
        AddObject(source, "footman", "unit", "euro", "e_footman");
        AddObject(source, "archer", "unit", "euro", "e_archer1");
        AddObject(source, "maceman", "unit", "euro", "e_maceman");
        AddObject(source, "knight", "unit", "euro", "e_knight");
        AddObject(source, "pikeman", "unit", "euro", "e_pikeman");
        AddObject(source, "mage", "unit", "euro", "e_mage", extra: [("max_manna", "90")]);
        AddObject(source, "castle", "building, capture", "euro", "e_castle", extra: [("pop_count", "24"), ("menu", "e_castle")]);
        AddObject(source, "house", "building, capture", "euro", "e_house", extra: [("pop_count", "12")]);
        AddObject(source, "barrack", "building, capture", "euro", "e_barrack");
        AddObject(source, "storage", "building, capture", "euro", "e_storage", extra: [("menu", "e_storage")]);
        AddObject(source, "lumber", "building, capture", "euro", "e_lumber", extra: [("menu", "e_lumber")]);
        AddObject(source, "farm", "building, capture", "euro", "e_farm", extra: [("menu", "e_farm")]);
        AddObject(source, "harbor", "building, capture", "euro", "e_harbor", extra: [("menu", "e_harbor")]);
        AddObject(source, "blacksmith", "building, capture", "euro", "e_blacksmith", extra: [("menu", "e_blacksmith")]);
        AddObject(source, "workshop", "building, capture", "euro", "e_workshop", extra: [("menu", "e_workshop")]);
        AddObject(source, "tavern", "building, capture", "euro", "e_tavern");
        AddObject(source, "stable", "building, capture", "euro", "e_stable");
        AddObject(source, "market", "building, capture", "euro", "e_market");
        AddObject(source, "cathedral", "building, capture", "euro", "e_cathedral", extra: [("menu", "e_cathedral")]);
        AddObject(source, "magetower", "building, capture", "euro", "e_magetower", extra: [("menu", "e_magetower")]);
        AddObject(source, "tcguild", "building, capture", "euro", "e_tcguild", extra: [("menu", "e_tcguild")]);
        AddObject(source, "academy", "building, capture", "euro", "e_academy", extra: [("menu", "e_academy")]);
        AddObject(source, "field", "building", "comm", "field");
        AddObject(source, "wall_wood", "building", "comm", "wall_wood", extra: [("menu", "wall_wood")]);
        AddObject(source, "wall_stone", "building", "comm", "wall_stone");
        AddObject(source, "gate_wood", "building", "comm", "anim_none", extra: [("menu", "gate_wood_00")]);
        AddObject(source, "gate_stone", "building", "comm", "anim_none");
        AddObject(source, "bridge", "building", "comm", "bridge");
        AddObject(source, "tower_wood", "building, capture", "comm", "tower_wood", file: "tower wood", extra: [("menu", "tower_wood")]);
        AddObject(source, "tower_wood2", "building, capture", "comm", "tower_wood2", file: "tower wood2", extra: [("menu", "tower_wood2")]);
        AddObject(source, "tower_stone", "building, capture", "comm", "tower_stone", file: "tower stone", extra: [("menu", "tower_stone")]);
        AddObject(source, "tower_stone2", "building, capture", "comm", "tower_stone2", file: "tower stone2");
        AddObject(source, "catapult", "unit", "comm", "catapult", teren: "1");
        AddObject(source, "galleon", "unit", "comm", "galleon", teren: "2", extra: [("menu", "e_galleon")]);
        AddObject(source, "fishing_boat", "unit", "comm", "boat", teren: "2");
        AddObject(source, "cow", "unit, animal", "comm", "cow", teren: "1", extra: [("max_manna", "40")]);
        AddObject(source, "fishpatch", null, "comm", "FISH_1", teren: "2");
        AddObject(source, "res_gold", null, "comm", "res_gold", file: "res gold");
        AddObject(source, "res_stone", null, "comm", "res_stone", file: "res stone");
    }

    private static void AddObject(MemorySource source, string cls, string? type, string civ, string image, string? file = null, string? teren = null, (string Key, string Value)[]? extra = null)
    {
        var general = new List<(string, string)> { ("class", cls) };
        if (type is not null)
        {
            general.Add(("type", type));
        }

        if (teren is not null)
        {
            general.Add(("teren", teren));
        }

        var section = new List<(string, string)> { ("image", image) };
        section.AddRange(extra ?? []);
        source.Add($"tables/data/objects/{file ?? cls}.ini.json", Ini($"DATA/OBJECTS/{(file ?? cls).ToUpperInvariant()}.INI", [("general", general), (civ, section)]));
    }

    private static byte[] Tsv(string name, string[] header, string[][] rows) => Json(w =>
    {
        w.WriteStartObject();
        w.WriteString("source", name);
        w.WriteNumber("codePage", 1252);
        w.WriteStartArray("header");
        foreach (string h in header)
        {
            w.WriteStringValue(h);
        }

        w.WriteEndArray();
        w.WriteStartArray("rows");
        foreach (string[] row in rows)
        {
            w.WriteStartArray();
            foreach (string cell in row)
            {
                w.WriteStringValue(cell);
            }

            w.WriteEndArray();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    });

    private static byte[] Ini(string name, (string Section, List<(string Key, string Value)> Entries)[] sections) => Json(w =>
    {
        w.WriteStartObject();
        w.WriteString("source", name);
        w.WriteNumber("codePage", 1252);
        w.WriteStartArray("sections");
        int line = 1;
        foreach ((string section, List<(string Key, string Value)> entries) in sections)
        {
            w.WriteStartObject();
            w.WriteString("name", section);
            w.WriteNumber("line", line++);
            w.WriteStartArray("entries");
            foreach ((string key, string value) in entries)
            {
                w.WriteStartArray();
                w.WriteStringValue(key);
                w.WriteStringValue(value);
                w.WriteEndArray();
                line++;
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    });

    private static byte[] SpriteIndex((string Name, string Image, string BoxKey, string Box)[] entries) => Json(w =>
    {
        w.WriteStartArray();
        foreach ((string name, string image, string boxKey, string box) in entries)
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteString("ini", "data/images.ini");
            w.WriteString("image", image);
            w.WriteNull("asset");
            w.WriteStartObject("properties");
            w.WriteStartArray("image");
            w.WriteStringValue(image);
            w.WriteEndArray();
            w.WriteStartArray(boxKey);
            w.WriteStringValue(box);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        w.WriteEndArray();
    });

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            write(writer);
        }

        return stream.ToArray();
    }
}
