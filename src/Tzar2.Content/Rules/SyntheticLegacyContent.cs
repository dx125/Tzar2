using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Tzar2.Content.Compiled;
using Tzar2.Content.Vfs;

namespace Tzar2.Content.Rules;

/// <summary>
/// Synthetic stand-ins for the imported original tables (REQ-LEGAL-03: public CI never sees original data): the same
/// shape the importer writes (<c>tables/data/*.json</c>, the sprite index, DAT footprints) for one civilization
/// (<c>euro</c>) and the common field. Every number is invented and differs from the original in every column (a test
/// checks this against a local install when <c>TZAR2_ORIGIN</c> is set). The <c>classic</c> ruleset compiles from it
/// exactly as from a real install, so tests, benchmarks and the determinism CI run the real compiler and rules
/// (ADR 0032).
/// </summary>
public static class SyntheticLegacyContent
{
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
        ]));
        source.Add("tables/data/euro buildings.txt.json", Tsv("DATA/EURO BUILDINGS.TXT", buildingHeader,
        [
            ["House", "house", "55w", "16", "3", "40", "420", "*", "*"],
            ["Lumber Mill", "lumber", "85w", "21", "3", "60", "610", "*", "*"],
            ["Storage", "storage", "160w", "26", "4", "70", "630", "*", "*"],
            ["Farm", "farm", "170w", "27", "6", "90", "640", "*", "*"],
            ["Barracks", "barrack", "160s,140f", "42", "7", "300", "1250", "*", "*"],
            [],
            ["Castle", "castle", "900s,1100w", "90", "11", "700", "5500", "+300,-150", "*"],
        ]));
        source.Add("tables/data/comm buildings.txt.json", Tsv("DATA/COMM BUILDINGS.TXT", buildingHeader,
        [
            ["Field", "field", "110w", "17", "2", "*", "130", "*", "*"],
        ]));
        source.Add("tables/data/res.ini.json", Ini("DATA/RES.INI",
        [
            ("Low", [("gold", "10"), ("stone", "20"), ("iron", "5"), ("food", "210"), ("wood", "220")]),
            ("Medium", [("gold", "500"), ("stone", "500"), ("iron", "500"), ("food", "500"), ("wood", "500")]),
            ("High", [("gold", "1000"), ("stone", "1000"), ("iron", "1000"), ("food", "1000"), ("wood", "1000")]),
            ("Others", [("golden rocks", "5000"), ("rocks", "3000")]),
        ]));
        source.Add("tables/data/bonus.ini.json", Ini("DATA/BONUS.INI",
        [
            ("groups", [("mounted", "e knight"), ("arrow_unit", "archer")]),
            ("footman", [("mounted", "+55%")]),
            ("arrow_unit", [("footman", "-25%")]),
        ]));
        AddObject(source, "peon", "unit", "euro", "e_peon", null);
        AddObject(source, "footman", "unit", "euro", "e_footman", null);
        AddObject(source, "archer", "unit", "euro", "e_archer1", null);
        AddObject(source, "maceman", "unit", "euro", "e_maceman", null);
        AddObject(source, "knight", "unit", "euro", "e_knight", null);
        AddObject(source, "castle", "building, capture", "euro", "e_castle", "24");
        AddObject(source, "house", "building, capture", "euro", "e_house", "12");
        AddObject(source, "barrack", "building, capture", "euro", "e_barrack", null);
        AddObject(source, "storage", "building, capture", "euro", "e_storage", null);
        AddObject(source, "lumber", "building, capture", "euro", "e_lumber", null);
        AddObject(source, "farm", "building, capture", "euro", "e_farm", null);
        AddObject(source, "field", "building", "comm", "field", null);
        AddObject(source, "res_gold", null, "comm", "res_gold", null, file: "res gold");
        AddObject(source, "res_stone", null, "comm", "res_stone", null, file: "res stone");
        source.Add("manifests/sprite-index.json", SpriteIndex(
        [
            ("e_castle", @"building\euro castle.bmp", "object", "0,0,160,160"),
            ("e_house", @"building\euro houses.bmp", "object", "0,0,64,64"),
            ("e_barrack", @"building\euro barrack.bmp", "object", "0,0,96,96"),
            ("e_storage", @"building\euro storage.bmp", "object", "0,0,64,64"),
            ("e_lumber", @"building\euro lumbermill.bmp", "object", "0,0,96,96"),
            ("e_farm", @"building\euro farm.bmp", "object", "0,0,96,96"),
            ("field", @"building\fields.bmp", "selection", "0,0,96,96"),
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

    private static void AddObject(MemorySource source, string cls, string? type, string civ, string image, string? popCount, string? file = null)
    {
        var general = new List<(string, string)> { ("class", cls) };
        if (type is not null)
        {
            general.Add(("type", type));
        }

        var section = new List<(string, string)> { ("image", image) };
        if (popCount is not null)
        {
            section.Add(("pop_count", popCount));
        }

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
