using System;
using System.Collections.Generic;
using System.Globalization;

namespace Tzar2.Content.Rules;

/// <summary>
/// Turns the original data tables (<c>DATA/* UNITS.TXT</c>, <c>* BUILDINGS.TXT</c>, <c>OBJECTS/*.INI</c>,
/// <c>IMAGES.INI</c> boxes, DAT footprints, <c>RES.INI</c>) into the bottom rules layer (ADR 0032). The ruleset's
/// <c>legacy:</c> section says which classes to import and how to map columns; YAML layers then patch the generated
/// entries. Column semantics follow docs/formats/ini-tsv.md §3 (columns by position) and TDD Appendix E.
/// </summary>
internal sealed class LegacyImporter
{
    // Unit table columns (by position, docs/formats/ini-tsv.md §3).
    private const int ColName = 0;
    private const int ColClass = 1;
    private const int ColCost = 2;
    private const int ColTime = 3;
    private const int ColSight = 4;
    private const int ColRange = 5;
    private const int ColHealth = 6;
    private const int ColAttack = 7;
    private const int ColDefence = 8;
    private const int ColSpeed = 9;
    private const int ColRate = 10;
    private const int ColProducer = 11;
    private const int ColUnitDepends = 12;

    // Building table columns.
    private const int ColResistance = 5;
    private const int ColBuildingDepends = 8;

    private readonly LegacyTables _tables;
    private readonly YamlMapping _config;
    private readonly string _configSource;
    private readonly Func<string, bool> _templateExists;
    private readonly List<string> _diagnostics;

    public LegacyImporter(LegacyTables tables, YamlMapping config, string configSource, Func<string, bool> templateExists, List<string> diagnostics)
    {
        _tables = tables;
        _config = config;
        _configSource = configSource;
        _templateExists = templateExists;
        _diagnostics = diagnostics;
    }

    /// <summary>Adds the imported entries to <paramref name="document"/> (as its first layer).</summary>
    public void ImportInto(RulesDocument document)
    {
        List<string> civs = Names("civilizations");
        var units = new SortedSet<string>(Names("units"), StringComparer.Ordinal);
        var buildings = new SortedSet<string>(Names("buildings"), StringComparer.Ordinal);
        YamlMapping suffixes = Mapping("costSuffixes");
        YamlMapping producers = Mapping("producers");

        // Pass 1: which building classes exist per civ (requirements and producers refer to them).
        var buildingTables = new List<(string Civ, LegacyTsv Table)>();
        var unitTables = new List<(string Civ, LegacyTsv Table)>();
        var imported = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string civ in civs)
        {
            string b = $"data/{civ} buildings.txt";
            string u = $"data/{civ} units.txt";
            if (_tables.Has(b))
            {
                LegacyTsv table = _tables.Tsv(b);
                buildingTables.Add((civ, table));
                foreach (IReadOnlyList<string> row in table.Rows)
                {
                    if (buildings.Contains(Cell(row, ColClass)))
                    {
                        imported.Add(civ + "." + Cell(row, ColClass));
                    }
                }
            }

            if (_tables.Has(u))
            {
                unitTables.Add((civ, _tables.Tsv(u)));
            }
        }

        foreach ((string civ, LegacyTsv table) in buildingTables)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (IReadOnlyList<string> row in table.Rows)
            {
                string cls = Cell(row, ColClass);
                if (!buildings.Contains(cls) || !seen.Add(cls))
                {
                    continue;
                }

                AddBuilding(document, civ, cls, row, table.Path, imported, suffixes);
            }
        }

        foreach ((string civ, LegacyTsv table) in unitTables)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (IReadOnlyList<string> row in table.Rows)
            {
                string cls = Cell(row, ColClass);
                if (!units.Contains(cls) || !seen.Add(cls))
                {
                    continue;
                }

                AddUnit(document, civ, cls, row, table.Path, imported, suffixes, producers);
            }
        }

        AddResourceNodes(document);
    }

    private void AddUnit(RulesDocument document, string civ, string cls, IReadOnlyList<string> row, string table, SortedSet<string> imported, YamlMapping suffixes, YamlMapping producers)
    {
        string source = "legacy:" + table;
        string key = civ + "." + cls;
        var body = new Body();
        body.Inherits("^Unit", cls, _templateExists);
        body.Trait("Legacy", ("class", cls), ("civ", civ), ("name", Cell(row, ColName)));
        AddCostAndTime(body, row, suffixes, source, key);
        AddInt(body, "RevealsShroud", "sight", row, ColSight, source, key);
        AddInt(body, "Health", "max", row, ColHealth, source, key);
        AddInt(body, "Armor", "defence", row, ColDefence, source, key);
        int attack = Int(row, ColAttack, source, key, 0);
        int rate = Int(row, ColRate, source, key, 0);
        if (attack > 0 && rate > 0)
        {
            var fields = new List<(string, string)> { ("attack", Num(attack)), ("rate", Num(rate)) };
            if (Cell(row, ColRange).Length > 0)
            {
                fields.Add(("range", Num(Int(row, ColRange, source, key, 0))));
            }

            body.Trait("Armament", [.. fields]);
        }

        if (Cell(row, ColSpeed).Length > 0)
        {
            AddInt(body, "Mobile", "speed", row, ColSpeed, source, key);
        }

        string producer = Cell(row, ColProducer);
        if (producer.Length > 0 && producers.Get(producer) is YamlScalar { IsNull: false } producerClass)
        {
            string producerKey = civ + "." + producerClass.Value;
            if (imported.Contains(producerKey))
            {
                body.Trait("Producible", ("by", "[" + producerKey + "]"));
            }
            else
            {
                _diagnostics.Add($"{key}: producer '{producerKey}' is not imported; the unit cannot be trained");
            }
        }

        AddRequirement(body, civ, Cell(row, ColUnitDepends), imported, key);
        document.ApplyLayer(body.ToEntry(key), source, declared: false);
    }

    private void AddBuilding(RulesDocument document, string civ, string cls, IReadOnlyList<string> row, string table, SortedSet<string> imported, YamlMapping suffixes)
    {
        string source = "legacy:" + table;
        string key = civ + "." + cls;
        var body = new Body();
        body.Inherits("^Building", cls, _templateExists);
        body.Trait("Legacy", ("class", cls), ("civ", civ), ("name", Cell(row, ColName)));
        AddCostAndTime(body, row, suffixes, source, key);
        AddInt(body, "RevealsShroud", "sight", row, ColSight, source, key);
        AddInt(body, "Health", "max", row, ColHealth, source, key);
        if (int.TryParse(Cell(row, ColResistance), NumberStyles.None, CultureInfo.InvariantCulture, out int resistance))
        {
            body.Trait("Armor", ("resistance", Num(resistance))); // "*" (walls, fields...) = none
        }

        AddFootprint(body, civ, cls, source, key);
        if (ObjectSection(civ, cls)?.Get("pop_count") is { Length: > 0 } pop)
        {
            body.Trait("PopulationProvider", ("amount", Num(ParseInt(pop, source, key + ".pop_count"))));
        }

        AddRequirement(body, civ, Cell(row, ColBuildingDepends), imported, key);
        document.ApplyLayer(body.ToEntry(key), source, declared: false);
    }

    private void AddResourceNodes(RulesDocument document)
    {
        YamlMapping nodes = Mapping("resourceNodes");
        LegacyIni? res = _tables.Has("data/res.ini") ? _tables.Ini("data/res.ini") : null;
        foreach (KeyValuePair<string, YamlNode> node in nodes.Entries)
        {
            string cls = node.Key;
            string key = "comm." + cls;
            string source = "legacy:data/res.ini";
            YamlMapping spec = RulesYaml.AsMapping(node.Value, $"legacy.resourceNodes.{cls}");
            string resource = RulesYaml.AsString(spec.Get("resource") ?? throw Error(node.Value.Line, $"legacy.resourceNodes.{cls}: missing 'resource'"), "resource");
            string amountKey = RulesYaml.AsString(spec.Get("amount") ?? throw Error(node.Value.Line, $"legacy.resourceNodes.{cls}: missing 'amount'"), "amount");
            string? amount = res?.Section("Others")?.Get(amountKey);
            if (amount is null)
            {
                throw new RulesException(source, 0, $"{key}: RES.INI [Others] has no '{amountKey}'");
            }

            var body = new Body();
            body.Inherits("^Resource", cls, _templateExists);
            body.Trait("Legacy", ("class", cls), ("civ", "comm"));
            body.Trait("ResourceNode", ("resource", resource), ("amount", Num(ParseInt(amount, source, key + ".amount"))));
            AddFootprint(body, "comm", cls, source, key);
            document.ApplyLayer(body.ToEntry(key), source, declared: false);
        }
    }

    private void AddCostAndTime(Body body, IReadOnlyList<string> row, YamlMapping suffixes, string source, string key)
    {
        string cost = Cell(row, ColCost);
        if (cost.Length > 0)
        {
            var fields = new List<(string, string)>();
            foreach (string part in cost.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // "40f", "200w": amount + resource suffix (GAME-01: f/w/s/g/m).
                int split = part.Length;
                while (split > 0 && !char.IsAsciiDigit(part[split - 1]))
                {
                    split--;
                }

                string suffix = part[split..].ToLowerInvariant();
                if (split == 0 || suffixes.Get(suffix) is not YamlScalar { IsNull: false } resource)
                {
                    throw new RulesException(source, 0, $"{key}: cannot read cost '{cost}' (unknown resource suffix '{suffix}')");
                }

                fields.Add((resource.Value, Num(ParseInt(part[..split], source, key + ".cost"))));
            }

            body.Trait("Cost", [.. fields]);
        }

        if (Cell(row, ColTime).Length > 0)
        {
            long ms = (long)Int(row, ColTime, source, key, 0) * 1000;
            body.Trait("BuildTime", ("ms", ms is >= 0 and <= int.MaxValue ? Num((int)ms) : throw new RulesException(source, 0, $"{key}: TIME {ms / 1000} is out of range")));
        }
    }

    private void AddRequirement(Body body, string civ, string depends, SortedSet<string> imported, string key)
    {
        if (depends.Length == 0 || depends == "*")
        {
            return;
        }

        if (depends.StartsWith("te_", StringComparison.OrdinalIgnoreCase))
        {
            body.Trait("Requires", ("technology", depends.ToLowerInvariant()));
            return;
        }

        string building = civ + "." + depends.ToLowerInvariant();
        if (imported.Contains(building))
        {
            body.Trait("Requires", ("buildings", "[" + building + "]"));
        }
        else
        {
            // A building that is not imported can never exist; keep the requirement unsatisfiable rather than drop it.
            body.Trait("Requires", ("technology", "building:" + depends.ToLowerInvariant()));
            _diagnostics.Add($"{key}: requires '{building}', which is not imported");
        }
    }

    private void AddFootprint(Body body, string civ, string cls, string source, string key)
    {
        string? image = ObjectSection(civ, cls)?.Get("image");
        IReadOnlyDictionary<string, string>? sprite = image is null ? null : _tables.Sprite(image);
        string? box = sprite is null ? null : sprite.TryGetValue("object", out string? o) ? o : sprite.TryGetValue("selection", out string? s) ? s : null;
        if (sprite is null || box is null)
        {
            throw new RulesException(source, 0, $"{key}: no footprint (OBJECTS [{civ}] image '{image}' has no IMAGES.INI object/selection box)");
        }

        string[] parts = box.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            throw new RulesException(source, 0, $"{key}: malformed box '{box}'");
        }

        // Building boxes are multiples of 32 px (one tile); round others to the nearest tile, at least one.
        int w = Math.Max(1, (ParseInt(parts[2], source, key) + 16) / 32);
        int h = Math.Max(1, (ParseInt(parts[3], source, key) + 16) / 32);
        var fields = new List<(string, string)> { ("width", Num(w)), ("height", Num(h)) };
        if (sprite.TryGetValue("image", out string? imagePath) && imagePath.Length > 0)
        {
            string dat = "images/" + ContentPath.ChangeExtension(ContentPath.Normalize(imagePath), ".dat");
            if (_tables.Footprint(dat) is { } grid && grid.Width == w && grid.Height == h)
            {
                // DAT grid: 0 = occupied, 2 = free (cut corners, courtyards), 1 = farms only, meaning unknown: treated as
                // occupied (docs/formats/dat.md §3).
                var rows = new List<string>(h);
                for (int y = 0; y < h; y++)
                {
                    var line = new char[w];
                    for (int x = 0; x < w; x++)
                    {
                        line[x] = grid.Grid[(y * w) + x] == 2 ? '0' : '1';
                    }

                    rows.Add(new string(line));
                }

                fields.Add(("mask", "[" + string.Join(", ", rows) + "]"));
            }
        }

        body.Trait("Building", [.. fields]);
    }

    private LegacyIniSection? ObjectSection(string civ, string cls) => _tables.ObjectOf(cls)?.Section(civ);

    private void AddInt(Body body, string trait, string field, IReadOnlyList<string> row, int column, string source, string key)
    {
        if (Cell(row, column).Length > 0)
        {
            body.Trait(trait, (field, Num(Int(row, column, source, key, 0))));
        }
    }

    private static string Cell(IReadOnlyList<string> row, int column) => column < row.Count ? row[column] : string.Empty;

    private static int Int(IReadOnlyList<string> row, int column, string source, string key, int fallback)
    {
        string text = Cell(row, column);
        return text.Length == 0 ? fallback : ParseInt(text, source, key);
    }

    private static int ParseInt(string text, string source, string key)
        => int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new RulesException(source, 0, $"{key}: '{text}' is not an integer");

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    private List<string> Names(string field)
        => _config.Get(field) is { } node ? RulesDocument.StringList(node, _configSource, "legacy." + field) : [];

    private YamlMapping Mapping(string field)
        => _config.Get(field) is { } node ? RulesYaml.AsMapping(node, "legacy." + field) : new YamlMapping([]);

    private RulesException Error(int line, string message) => new(_configSource, line, message);

    /// <summary>Builds one generated entry as a YAML mapping (so imported and authored rules share one merge path).</summary>
    private sealed class Body
    {
        private readonly List<KeyValuePair<string, YamlNode>> _entries = [];

        public void Inherits(string baseTemplate, string cls, Func<string, bool> templateExists)
        {
            var parents = new List<YamlNode> { new YamlScalar(baseTemplate, false) };
            string classTemplate = "^class." + cls;
            if (templateExists(classTemplate))
            {
                parents.Add(new YamlScalar(classTemplate, false));
            }

            _entries.Add(new KeyValuePair<string, YamlNode>("inherits", new YamlSequence(parents)));
        }

        public void Trait(string name, params (string Field, string Value)[] fields)
        {
            var list = new List<KeyValuePair<string, YamlNode>>(fields.Length);
            foreach ((string field, string value) in fields)
            {
                YamlNode node = value.StartsWith('[') && value.EndsWith(']')
                    ? new YamlSequence(SplitList(value))
                    : new YamlScalar(value, quoted: true);
                list.Add(new KeyValuePair<string, YamlNode>(field, node));
            }

            _entries.Add(new KeyValuePair<string, YamlNode>(name, new YamlMapping(list)));
        }

        public YamlMapping ToEntry(string key) => new([new KeyValuePair<string, YamlNode>(key, new YamlMapping(_entries))]);

        private static List<YamlNode> SplitList(string value)
        {
            var items = new List<YamlNode>();
            foreach (string item in value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                items.Add(new YamlScalar(item, quoted: true));
            }

            return items;
        }
    }
}
