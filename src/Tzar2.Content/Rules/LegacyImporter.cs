using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Tzar2.Content.Rules;

/// <summary>
/// Turns the original data tables into the bottom rules layer (ADRs 0032, 0040): units and buildings
/// (<c>DATA/* UNITS.TXT</c>, <c>* BUILDINGS.TXT</c>, <c>OBJECTS/*.INI</c>, <c>IMAGES.INI</c> boxes, DAT footprints),
/// resource nodes (<c>RES.INI</c>), technologies (<c>TECHNOLOGIES.INI</c>), upgrades (<c>UPGRADES.INI</c>), the per-civ
/// technology trees (<c>TECHTREE &lt;civ&gt;.INI</c>) and what every building researches (its command card:
/// <c>OBJECTS</c> <c>menu</c> → <c>MENU.INI</c> → <c>ORDERS.INI</c> <c>ACTION = TECHNOLOGY | UPGRADE</c>). The ruleset's
/// <c>legacy:</c> section says which classes to import and how to map columns; YAML layers then patch the generated
/// entries. Column semantics follow docs/formats/ini-tsv.md §3 (columns by position) and TDD Appendix E.
/// </summary>
internal sealed partial class LegacyImporter
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
    private const int ColBuildingHealth = 6;
    private const int ColEmit = 7;
    private const int ColBuildingDepends = 8;

    private readonly LegacyTables _tables;
    private readonly YamlMapping _config;
    private readonly string _configSource;
    private readonly Func<string, bool> _templateExists;
    private readonly List<string> _diagnostics;

    // Lookup only, never iterated (T2D004): order key -> (action, class name); menu -> order keys in file order.
    private readonly Dictionary<string, (string Action, string ClassName)> _orders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _menus = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _upgradeTargets = new(StringComparer.Ordinal); // upgrade key -> UPGRADE target

    // Sorted for deterministic iteration.
    private readonly SortedSet<string> _technologies = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _upgrades = new(StringComparer.Ordinal);
    private readonly List<TechTree> _trees = [];

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
        YamlMapping suffixes = Mapping("costSuffixes");
        YamlMapping producers = Mapping("producers");
        ReadResearchTables();

        // Pass 1: the tables and which classes are imported per civ (requirements and producers refer to them).
        var buildingTables = new List<(string Civ, LegacyTsv Table)>();
        var unitTables = new List<(string Civ, LegacyTsv Table)>();
        foreach (string civ in civs)
        {
            if (_tables.Has($"data/{civ} buildings.txt"))
            {
                buildingTables.Add((civ, _tables.Tsv($"data/{civ} buildings.txt")));
            }

            if (_tables.Has($"data/{civ} units.txt"))
            {
                unitTables.Add((civ, _tables.Tsv($"data/{civ} units.txt")));
            }
        }

        var buildingClasses = new SortedSet<string>(StringComparer.Ordinal);
        foreach ((string _, LegacyTsv table) in buildingTables)
        {
            foreach (IReadOnlyList<string> row in table.Rows)
            {
                string cls = Cell(row, ColClass);
                if (cls.Length > 0 && Selected("buildings", cls))
                {
                    buildingClasses.Add(cls);
                }
            }
        }

        var imported = new SortedSet<string>(StringComparer.Ordinal);
        foreach ((string civ, LegacyTsv table) in buildingTables)
        {
            foreach (IReadOnlyList<string> row in table.Rows)
            {
                if (buildingClasses.Contains(Cell(row, ColClass)))
                {
                    imported.Add(civ + "." + Cell(row, ColClass));
                }
            }
        }

        // Armed buildings (towers) take their armament and hit points from the unit table row of the same class.
        var armedRows = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal); // lookup only (T2D004)
        foreach ((string civ, LegacyTsv table) in unitTables)
        {
            foreach (IReadOnlyList<string> row in table.Rows)
            {
                string cls = Cell(row, ColClass);
                if (buildingClasses.Contains(cls))
                {
                    armedRows.TryAdd(civ + "." + cls, row);
                }
            }
        }

        foreach ((string civ, LegacyTsv table) in buildingTables)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (IReadOnlyList<string> row in table.Rows)
            {
                string cls = Cell(row, ColClass);
                if (!buildingClasses.Contains(cls) || !seen.Add(cls))
                {
                    continue;
                }

                armedRows.TryGetValue(civ + "." + cls, out IReadOnlyList<string>? armed);
                AddBuilding(document, civ, cls, row, armed, table.Path, imported, suffixes);
            }
        }

        foreach ((string civ, LegacyTsv table) in unitTables)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (IReadOnlyList<string> row in table.Rows)
            {
                string cls = Cell(row, ColClass);
                if (cls.Length == 0 || buildingClasses.Contains(cls) || !Selected("units", cls) || !seen.Add(cls))
                {
                    continue;
                }

                AddUnit(document, civ, cls, row, table.Path, imported, suffixes, producers);
            }
        }

        AddResourceNodes(document);
        AddResearch(document);
        AddCombatEntries(document);
    }

    private void AddUnit(RulesDocument document, string civ, string cls, IReadOnlyList<string> row, string table, SortedSet<string> imported, YamlMapping suffixes, YamlMapping producers)
    {
        string source = "legacy:" + table;
        string key = civ + "." + cls;
        var body = new Body();
        body.Inherits("^Unit", civ, cls, _templateExists);
        body.Trait("Legacy", ("class", cls), ("civ", civ), ("name", Cell(row, ColName)));
        AddCostAndTime(body, row, suffixes, source, key);
        AddInt(body, "RevealsShroud", "sight", row, ColSight, source, key);
        AddInt(body, "Health", "max", row, ColHealth, source, key);
        AddInt(body, "Armor", "defence", row, ColDefence, source, key);
        AddArmament(body, row, civ, cls, source, key);
        if (Cell(row, ColSpeed).Length > 0)
        {
            AddInt(body, "Mobile", "speed", row, ColSpeed, source, key);
        }

        string producer = Cell(row, ColProducer);
        if (producer.Length > 0 && producers.Get(producer) is YamlScalar { IsNull: false } producerClass)
        {
            // Common units (siege, ships, cows) are trained in every civilization's building of that class.
            var by = new List<string>();
            foreach (string candidate in imported)
            {
                int dot = candidate.IndexOf('.', StringComparison.Ordinal);
                if (string.Equals(candidate[(dot + 1)..], producerClass.Value, StringComparison.Ordinal) && (civ == "comm" || candidate[..dot] == civ))
                {
                    by.Add(candidate);
                }
            }

            if (by.Count > 0)
            {
                body.Trait("Producible", ("by", "[" + string.Join(", ", by) + "]"));
            }
            else
            {
                _diagnostics.Add($"{key}: producer '{civ}.{producerClass.Value}' is not imported; the unit cannot be trained");
            }
        }

        AddObjectTraits(body, civ, cls, source, key, building: false);
        AddRequirements(body, civ, cls, Cell(row, ColUnitDepends), imported, key);
        document.ApplyLayer(body.ToEntry(key), source, declared: false);
    }

    private void AddBuilding(RulesDocument document, string civ, string cls, IReadOnlyList<string> row, IReadOnlyList<string>? armedRow, string table, SortedSet<string> imported, YamlMapping suffixes)
    {
        string source = "legacy:" + table;
        string key = civ + "." + cls;
        var body = new Body();
        body.Inherits("^Building", civ, cls, _templateExists);
        body.Trait("Legacy", ("class", cls), ("civ", civ), ("name", Cell(row, ColName)));
        AddCostAndTime(body, row, suffixes, source, key);
        AddInt(body, "RevealsShroud", "sight", row, ColSight, source, key);
        int health = Int(row, ColBuildingHealth, source, key, 0);
        if (health <= 0 && armedRow is not null)
        {
            // Towers: the buildings table holds 0, the unit table their hit points.
            health = Int(armedRow, ColHealth, source, key, 0);
        }

        if (health > 0)
        {
            body.Trait("Health", ("max", Num(health)));
        }

        if (int.TryParse(Cell(row, ColResistance), NumberStyles.None, CultureInfo.InvariantCulture, out int resistance))
        {
            body.Trait("Armor", ("resistance", Num(resistance))); // "*" (walls, fields...) = none
        }

        if (armedRow is not null)
        {
            AddArmament(body, armedRow, civ, cls, source, key);
        }

        // EMIT (own/other): "+400,-200" (⚠ HYPOTHESIS UNK-19: capture influence, ADR 0044).
        string emit = Cell(row, ColEmit);
        if (emit.Length > 0 && emit != "*")
        {
            string[] parts = emit.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                throw new RulesException(source, 0, $"{key}: cannot read EMIT '{emit}'");
            }

            body.Trait("InfluenceEmitter", ("own", Num(ParseInt(parts[0], source, key + ".emit"))), ("other", Num(ParseInt(parts[1], source, key + ".emit"))));
        }

        AddFootprint(body, civ, cls, source, key);
        if (ObjectSection(civ, cls)?.Get("pop_count") is { Length: > 0 } pop)
        {
            body.Trait("PopulationProvider", ("amount", Num(ParseInt(pop, source, key + ".pop_count"))));
        }

        AddObjectTraits(body, civ, cls, source, key, building: true);
        AddRequirements(body, civ, cls, Cell(row, ColBuildingDepends), imported, key);
        document.ApplyLayer(body.ToEntry(key), source, declared: false);
    }

    private void AddArmament(Body body, IReadOnlyList<string> row, string civ, string cls, string source, string key)
    {
        int attack = Int(row, ColAttack, source, key, 0);
        int rate = Int(row, ColRate, source, key, 0);
        if (attack > 0)
        {
            // Some rows have an ATTACK but no RATE (priests, Jihad warriors): the rules must give one (ADR 0091).
            var fields = new List<(string, string)> { ("attack", Num(attack)) };
            if (rate > 0)
            {
                fields.Add(("rate", Num(rate)));
            }

            if (Cell(row, ColRange).Length > 0)
            {
                fields.Add(("range", Num(Int(row, ColRange, source, key, 0))));
            }

            if (HitPermille(civ, cls) is { } hit)
            {
                fields.Add(("hitPermille", Num(hit)));
            }

            body.Trait("Armament", [.. fields]);
        }
    }

    // WAR-014: the blow lands at the frame of the attack animation that IMAGES.INI marks (actionrow, counted from 0), so at
    // the share of the swing the frames before it take (their delays, or their number when there are none); in thousandths.
    private int? HitPermille(string civ, string cls)
    {
        if (ObjectSection(civ, cls)?.Get("image_attack") is not { Length: > 0 } image || _tables.Sprite(image) is not { } sprite
            || !sprite.TryGetValue("rows", out string? rowsText) || !int.TryParse(rowsText, NumberStyles.None, CultureInfo.InvariantCulture, out int rows)
            || !sprite.TryGetValue("actionrow", out string? actionText) || !int.TryParse(actionText, NumberStyles.None, CultureInfo.InvariantCulture, out int action)
            || rows <= 0 || action >= rows)
        {
            return null;
        }

        long before = action, total = rows;
        if (sprite.TryGetValue("delay", out string? delays))
        {
            string[] parts = delays.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var ms = new long[parts.Length];
            bool ok = parts.Length == rows;
            for (int i = 0; ok && i < parts.Length; i++)
            {
                ok = int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int d);
                ms[i] = d;
            }

            if (ok && ms.Sum() > 0)
            {
                before = ms.Take(action).Sum();
                total = ms.Sum();
            }
        }

        return (int)(before * 1000 / total);
    }

    // OBJECTS/<class>.INI: "type = building, capture" (GAME-04), max_manna (mana pools), and the command card's research.
    private void AddObjectTraits(Body body, string civ, string cls, string source, string key, bool building)
    {
        LegacyIni? ini = _tables.ObjectOf(cls);
        if (ini?.Section("general")?.Get("type") is { } type)
        {
            foreach (string part in type.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(part, "capture", StringComparison.OrdinalIgnoreCase) && building)
                {
                    body.Trait("Capturable", ("instant", "false"));
                }
                else if (string.Equals(part, "mounted", StringComparison.OrdinalIgnoreCase) && !building)
                {
                    body.Trait("Mobile", ("locomotor", "mounted"));
                }
            }
        }

        // teren: 0 = flies, 2 = water (ships, fish), 1 = land.
        if (!building && ini?.Section("general")?.Get("teren") is { } teren)
        {
            if (teren == "0")
            {
                body.Trait("Mobile", ("locomotor", "air"));
            }
            else if (teren == "2")
            {
                body.Trait("Mobile", ("locomotor", "ship"));
            }
        }

        LegacyIniSection? section = ObjectSection(civ, cls);
        if (section?.Get("max_manna") is { Length: > 0 } mana)
        {
            int amount = ParseInt(mana, source, key + ".max_manna");
            if (Names("milkClasses").Contains(cls))
            {
                // ⚠ HYPOTHESIS (ADR 0042): cows keep their milk as "mana" (max_manna); it is the food they hold.
                body.Trait("ResourceNode", ("resource", "food"), ("amount", Num(amount)));
            }
            else
            {
                body.Trait("ManaPool", ("max", Num(amount)));
            }
        }

        // What the entity's command card researches and upgrades.
        var techs = new List<string>();
        var upgrades = new List<string>();
        if (section?.Get("menu") is { Length: > 0 } menu)
        {
            CollectResearch(menu, techs, upgrades, []);
        }

        if (techs.Count > 0 || upgrades.Count > 0)
        {
            body.Trait("Researcher", ("technologies", "[" + string.Join(", ", techs) + "]"), ("upgrades", "[" + string.Join(", ", upgrades) + "]"));
        }

        AddCombatObjectTraits(body, ini, section, source, key, building);
    }

    // Walks a command card and its submenus (bounded: menus are untrusted data) for research and upgrade orders.
    private void CollectResearch(string menu, List<string> techs, List<string> upgrades, List<string> visited)
    {
        if (visited.Count >= 8 || visited.Contains(menu) || !_menus.TryGetValue(menu, out List<string>? orders))
        {
            return;
        }

        visited.Add(menu);
        foreach (string order in orders)
        {
            if (!_orders.TryGetValue(order, out (string Action, string ClassName) o))
            {
                continue;
            }

            if (string.Equals(o.Action, "TECHNOLOGY", StringComparison.OrdinalIgnoreCase) && _technologies.Contains(o.ClassName.ToLowerInvariant()))
            {
                AddOnce(techs, o.ClassName.ToLowerInvariant());
            }
            else if (o.Action.StartsWith("UPGRADE ", StringComparison.OrdinalIgnoreCase) && _upgrades.Contains(UpgradeKey(order, o.ClassName)))
            {
                AddOnce(upgrades, UpgradeKey(order, o.ClassName)); // the order [to_swall] names upgrade to_wall_stone
            }
            else if (o.Action.StartsWith("SUBMENU ", StringComparison.Ordinal))
            {
                CollectResearch(o.Action["SUBMENU ".Length..].Trim(), techs, upgrades, visited);
            }
        }
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
            bool footprint = spec.Get("footprint") is not { } f || RulesYaml.AsBool(f, "footprint");
            string? amount = res?.Section("Others")?.Get(amountKey);
            if (amount is null)
            {
                throw new RulesException(source, 0, $"{key}: RES.INI [Others] has no '{amountKey}'");
            }

            var body = new Body();
            body.Inherits("^Resource", "comm", cls, _templateExists);
            body.Trait("Legacy", ("class", cls), ("civ", "comm"));
            body.Trait("ResourceNode", ("resource", resource), ("amount", Num(ParseInt(amount, source, key + ".amount"))));
            if (footprint)
            {
                AddFootprint(body, "comm", cls, source, key);
            }

            document.ApplyLayer(body.ToEntry(key), source, declared: false);
        }
    }

    // TECHNOLOGIES.INI and UPGRADES.INI entries with their per-civ prerequisites.
    private void AddResearch(RulesDocument document)
    {
        foreach ((string table, string trait, SortedSet<string> keys) in new[] { ("data/technologies.ini", "Technology", _technologies), ("data/upgrades.ini", "Upgrade", _upgrades) })
        {
            if (!_tables.Has(table))
            {
                continue;
            }

            string source = "legacy:" + table;
            foreach (LegacyIniSection section in _tables.Ini(table).Sections)
            {
                string? key = section.Name?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(key) || !keys.Contains(key) || document.Find(key) is not null)
                {
                    continue;
                }

                var body = new Body();
                body.Inherits("^" + trait, string.Empty, key, _templateExists);
                body.Trait("Legacy", ("class", key), ("name", section.Get("displayname") ?? string.Empty));
                var cost = new List<(string, string)>();
                foreach (KeyValuePair<string?, string> e in section.Entries)
                {
                    string field = e.Key?.Trim().ToLowerInvariant() ?? string.Empty;
                    if (field is "displayname" or "time" or "")
                    {
                        continue;
                    }

                    cost.Add((field, Num(ParseInt(e.Value, source, $"{key}.{field}"))));
                }

                body.Trait("Cost", [.. cost]);
                body.Trait("BuildTime", ("ms", Num(ParseInt(section.Get("time") ?? throw new RulesException(source, 0, $"{key}: no time"), source, key + ".time"))));
                if (trait == "Upgrade")
                {
                    string? into = _upgradeTargets.TryGetValue(key, out string? target) ? target : null;
                    // "UPGRADE tower_wood2" converts into that class; "UPGRADE to_ship_level2" names itself: a level upgrade.
                    if (into is not null && into != key)
                    {
                        body.Trait("Upgrade", ("into", into));
                    }
                    else
                    {
                        body.Trait("Upgrade");
                    }
                }
                else
                {
                    body.Trait("Technology");
                }

                AddTechTree(body, key);
                document.ApplyLayer(body.ToEntry(key), source, declared: false);
            }
        }
    }

    private void ReadResearchTables()
    {
        foreach ((string table, SortedSet<string> keys) in new[] { ("data/technologies.ini", _technologies), ("data/upgrades.ini", _upgrades) })
        {
            if (_tables.Has(table))
            {
                foreach (LegacyIniSection section in _tables.Ini(table).Sections)
                {
                    if (!string.IsNullOrWhiteSpace(section.Name))
                    {
                        keys.Add(section.Name.Trim().ToLowerInvariant());
                    }
                }
            }
        }

        if (_tables.Has("data/orders.ini"))
        {
            foreach (LegacyIniSection section in _tables.Ini("data/orders.ini").Sections)
            {
                if (!string.IsNullOrWhiteSpace(section.Name))
                {
                    string action = section.Get("action") ?? string.Empty;
                    if (action.Length == 0 && section.Get("submenu") is { Length: > 0 } submenu)
                    {
                        action = "SUBMENU " + submenu;
                    }

                    _orders.TryAdd(section.Name.Trim(), (action, section.Get("classname") ?? string.Empty));
                    if (action.StartsWith("UPGRADE ", StringComparison.OrdinalIgnoreCase))
                    {
                        _upgradeTargets.TryAdd(UpgradeKey(section.Name.Trim(), section.Get("classname") ?? string.Empty), action["UPGRADE ".Length..].Trim().ToLowerInvariant());
                    }
                }
            }
        }

        if (_tables.Has("data/menu.ini"))
        {
            foreach (LegacyIniSection section in _tables.Ini("data/menu.ini").Sections)
            {
                if (string.IsNullOrWhiteSpace(section.Name) || _menus.ContainsKey(section.Name.Trim()))
                {
                    continue;
                }

                var orders = new List<string>();
                foreach (KeyValuePair<string?, string> e in section.Entries)
                {
                    // "te_heroism = H" or a bare "te_heroism" line (guild menus).
                    string order = (e.Key ?? e.Value).Trim();
                    if (order.Length > 0)
                    {
                        orders.Add(order);
                    }
                }

                _menus.Add(section.Name.Trim(), orders);
            }
        }

        foreach (KeyValuePair<string, YamlNode> tree in Mapping("techTrees").Entries)
        {
            string table = RulesYaml.AsString(tree.Value, $"legacy.techTrees.{tree.Key}");
            if (!_tables.Has(table))
            {
                _diagnostics.Add($"legacy.techTrees.{tree.Key}: '{table}' is not in the imported package");
                continue;
            }

            LegacyIni ini = _tables.Ini(table);
            var t = new TechTree(tree.Key);
            foreach ((string sectionName, List<(string, string)> into) in new[] { ("depend", t.Depend), ("required", t.Required), ("orders", t.Orders) })
            {
                foreach (KeyValuePair<string?, string> e in ini.Section(sectionName)?.Entries ?? [])
                {
                    if (e.Key is not null && e.Value.Trim().Length > 0)
                    {
                        into.Add((e.Key.Trim().ToLowerInvariant(), e.Value.Trim().ToLowerInvariant()));
                    }
                }
            }

            _trees.Add(t);
        }
    }

    // TSV DEPENDS ON plus TECHTREE [depend]/[required] (ADR 0040). A [depend] entry of the civ replaces the table's
    // technology (the tables name some that do not exist, e.g. te_domestication for cows where the tree says te_livestock).
    private void AddRequirements(Body body, string civ, string cls, string depends, SortedSet<string> imported, string key)
    {
        bool treeDefinesTech = false;
        foreach (TechTree t in _trees)
        {
            if ((civ == "comm" || t.Civ == civ) && t.Depend.Exists(d => d.Key == cls))
            {
                treeDefinesTech = true;
            }
        }

        if (depends.Length > 0 && depends != "*")
        {
            string lower = depends.ToLowerInvariant();
            if (lower.StartsWith("te_", StringComparison.Ordinal))
            {
                if (_technologies.Contains(lower) || !treeDefinesTech)
                {
                    body.Trait("Requires", ("technology", lower));
                }
                else
                {
                    _diagnostics.Add($"{key}: the table requires unknown '{lower}'; the technology tree's prerequisite applies");
                }
            }
            else
            {
                string building = BuildingKey(civ, lower, imported);
                if (building.Length > 0)
                {
                    body.Trait("Requires", ("buildings", "[" + building + "]"));
                }
                else if (_trees.Exists(t => (civ == "comm" || t.Civ == civ) && t.Required.Exists(r => r.Key == cls)))
                {
                    // The Asian and Arabian tables say churches need a cathedral; their trees name the shaolinn/mosque.
                    _diagnostics.Add($"{key}: the table requires '{civ}.{lower}', which is not imported; the technology tree's building applies");
                }
                else
                {
                    // A building that is not imported can never exist; keep the requirement unsatisfiable rather than drop it.
                    body.Trait("Requires", ("technology", "building:" + lower));
                    _diagnostics.Add($"{key}: requires '{civ}.{lower}', which is not imported");
                }
            }
        }

        var perCiv = new List<KeyValuePair<string, YamlNode>>();
        foreach (TechTree t in _trees)
        {
            if (civ != "comm" && t.Civ != civ)
            {
                continue;
            }

            var techs = new List<YamlNode>();
            var buildings = new List<YamlNode>();
            foreach ((string k, string v) in t.Depend)
            {
                if (k == cls)
                {
                    techs.Add(new YamlScalar(v, quoted: true));
                }
            }

            foreach ((string k, string v) in t.Required)
            {
                if (k == cls)
                {
                    string building = BuildingKey(t.Civ, v, imported);
                    if (building.Length > 0)
                    {
                        buildings.Add(new YamlScalar(building, quoted: true));
                    }
                    else
                    {
                        techs.Add(new YamlScalar("building:" + v, quoted: true));
                        _diagnostics.Add($"{key}: the {t.Civ} technology tree requires '{v}', which is not imported");
                    }
                }
            }

            if (techs.Count > 0 || buildings.Count > 0)
            {
                perCiv.Add(new KeyValuePair<string, YamlNode>(t.Civ, new YamlMapping(
                [
                    new KeyValuePair<string, YamlNode>("technologies", new YamlSequence(techs)),
                    new KeyValuePair<string, YamlNode>("buildings", new YamlSequence(buildings)),
                ])));
            }
        }

        if (perCiv.Count > 0)
        {
            body.Raw("TechTree", new YamlMapping(perCiv));
        }
    }

    // Technologies and upgrades: TECHTREE [depend] te_x = te_y per civ; and [orders] gates (spells, loans...).
    private void AddTechTree(Body body, string key)
    {
        var perCiv = new List<KeyValuePair<string, YamlNode>>();
        foreach (TechTree t in _trees)
        {
            var techs = new List<YamlNode>();
            foreach ((string k, string v) in t.Depend)
            {
                if (k == key)
                {
                    techs.Add(new YamlScalar(v, quoted: true));
                }
            }

            var orders = new List<YamlNode>();
            foreach ((string k, string v) in t.Orders)
            {
                if (v == key)
                {
                    orders.Add(new YamlScalar(k, quoted: true));
                }
            }

            if (techs.Count > 0 || orders.Count > 0)
            {
                perCiv.Add(new KeyValuePair<string, YamlNode>(t.Civ, new YamlMapping(
                [
                    new KeyValuePair<string, YamlNode>("technologies", new YamlSequence(techs)),
                    new KeyValuePair<string, YamlNode>("orders", new YamlSequence(orders)),
                ])));
            }
        }

        if (perCiv.Count > 0)
        {
            body.Raw("TechTree", new YamlMapping(perCiv));
        }
    }

    private static string UpgradeKey(string order, string className) => (className.Length > 0 ? className : order).ToLowerInvariant();

    private static string BuildingKey(string civ, string cls, SortedSet<string> imported)
        => imported.Contains(civ + "." + cls) ? civ + "." + cls : imported.Contains("comm." + cls) ? "comm." + cls : string.Empty;

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
            long ms = (long)Int(row, ColTime, source, key, 0) * LegacyTime.MsPerTimeUnit;
            body.Trait("BuildTime", ("ms", ms is >= 0 and <= int.MaxValue ? Num((int)ms) : throw new RulesException(source, 0, $"{key}: TIME {ms / LegacyTime.MsPerTimeUnit} is out of range")));
        }
    }

    private void AddFootprint(Body body, string civ, string cls, string source, string key)
    {
        string? image = ObjectSection(civ, cls)?.Get("image");
        IReadOnlyDictionary<string, string>? sprite = image is null ? null : _tables.Sprite(image);
        string? box = sprite is null ? null : sprite.TryGetValue("object", out string? o) ? o : sprite.TryGetValue("selection", out string? s) ? s : null;
        if (sprite is null || box is null)
        {
            // Gates draw from animations without a box: the YAML gives their footprint (ADR 0043).
            _diagnostics.Add($"{key}: no footprint in the data (OBJECTS [{civ}] image '{image}' has no IMAGES.INI object/selection box)");
            return;
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

    private static void AddOnce(List<string> list, string item)
    {
        if (!list.Contains(item))
        {
            list.Add(item);
        }
    }

    private bool Selected(string field, string cls)
    {
        if (_config.Get(field) is YamlScalar { Value: "all" })
        {
            return Array.IndexOf([.. Names(field + "Except")], cls) < 0;
        }

        return Names(field).Contains(cls);
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
        => _config.Get(field) is { } node and not YamlScalar { Value: "all" } ? RulesDocument.StringList(node, _configSource, "legacy." + field) : [];

    private YamlMapping Mapping(string field)
        => _config.Get(field) is { } node ? RulesYaml.AsMapping(node, "legacy." + field) : new YamlMapping([]);

    private RulesException Error(int line, string message) => new(_configSource, line, message);

    /// <summary>One civilization's <c>TECHTREE</c>: [depend] (prerequisite technologies), [required] (buildings), [orders].</summary>
    private sealed class TechTree(string civ)
    {
        public string Civ { get; } = civ;

        public List<(string Key, string Value)> Depend { get; } = [];

        public List<(string Key, string Value)> Required { get; } = [];

        public List<(string Key, string Value)> Orders { get; } = [];
    }

    /// <summary>Builds one generated entry as a YAML mapping (so imported and authored rules share one merge path).</summary>
    private sealed class Body
    {
        private readonly List<KeyValuePair<string, YamlNode>> _entries = [];

        /// <summary>Parents: the base template, then <c>^class.&lt;cls&gt;</c> and <c>^&lt;civ&gt;.&lt;cls&gt;</c> when they exist.</summary>
        public void Inherits(string baseTemplate, string civ, string cls, Func<string, bool> templateExists)
        {
            var parents = new List<YamlNode>();
            if (templateExists(baseTemplate))
            {
                parents.Add(new YamlScalar(baseTemplate, false));
            }

            string[] templates = civ.Length > 0 ? ["^class." + cls, "^" + civ + "." + cls] : ["^class." + cls];
            foreach (string template in templates)
            {
                if (templateExists(template) && !parents.Exists(p => ((YamlScalar)p).Value == template))
                {
                    parents.Add(new YamlScalar(template, false));
                }
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

        public void Raw(string name, YamlMapping fields) => _entries.Add(new KeyValuePair<string, YamlNode>(name, fields));

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
