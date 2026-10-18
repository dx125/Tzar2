using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Tzar2.Content.Compiled;
using Tzar2.Content.Vfs;

namespace Tzar2.Content.Rules;

/// <summary>Options of <see cref="RulesetCompiler.Compile"/>.</summary>
public sealed record RulesetCompileOptions
{
    /// <summary>
    /// Extra rules layers applied after the ruleset chain, in order (the override mechanism of ADR 0031; mods add theirs
    /// here): each is a VFS path (<c>rules/my-mod.yaml</c>).
    /// </summary>
    public IReadOnlyList<string> OverrideFiles { get; init; } = [];

    /// <summary>Extra rules layers given as text (source name, YAML), applied after <see cref="OverrideFiles"/>.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> OverrideTexts { get; init; } = [];
}

/// <summary>The result of a compilation.</summary>
/// <param name="Ruleset">The compiled ruleset.</param>
/// <param name="Diagnostics">Non-fatal notes (skipped producers, unsatisfiable requirements...), in a deterministic order.</param>
/// <param name="Warnings">
/// Problems worth showing the player, e.g. a modified installation (the imported tables differ from the reference build).
/// </param>
/// <param name="LegacyDataHash">
/// xxHash64 of what the compiler read from the original tables (the imported layer, BONUS.INI and RES.INI), or null for a
/// ruleset that imports nothing. Equal to <c>legacy.referenceDataHash</c> for an unmodified reference build (ADR 0032).
/// </param>
public sealed record CompiledRuleset(Ruleset Ruleset, IReadOnlyList<string> Diagnostics, IReadOnlyList<string> Warnings, ulong? LegacyDataHash);

/// <summary>
/// The ruleset compiler (TDD §11.2, REQ-DATA-04/05/06; ADRs 0031-0033): reads <c>rules/&lt;name&gt;.yaml</c> and its
/// <c>base</c> chain from the VFS, imports the original tables named by the <c>legacy:</c> section as the bottom layer,
/// applies the YAML layers and overrides, resolves inheritance and compiles every non-template entry into an immutable
/// <see cref="Ruleset"/> with dense ids (entry keys in ordinal order). Deterministic: no floating point, no hash-order
/// iteration (ADR 0012); the same files give the same <see cref="Ruleset.ContentHash"/> on every peer.
/// </summary>
public static class RulesetCompiler
{
    /// <summary>VFS folder of ruleset files.</summary>
    public const string RulesFolder = "rules/";

    private static readonly string[] Sections = ["ruleset", "resources", "gameplay", "terrain", "legacy"];

    /// <summary>Compiles the ruleset <paramref name="name"/> (e.g. <c>classic</c>).</summary>
    /// <exception cref="RulesException">A file is missing or malformed, or an entry is invalid.</exception>
    public static CompiledRuleset Compile(VirtualFileSystem vfs, string name, RulesetCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentException.ThrowIfNullOrEmpty(name);
        options ??= new RulesetCompileOptions();
        var diagnostics = new List<string>();

        // 1. The chain base → derived, then the override layers.
        var layers = new List<(string Source, YamlMapping Root)>();
        LoadChain(vfs, name, layers, new List<string>());
        int chainLength = layers.Count;
        foreach (string file in options.OverrideFiles)
        {
            layers.Add((file, Parse(file, ReadText(vfs, file))));
        }

        foreach (KeyValuePair<string, string> text in options.OverrideTexts)
        {
            layers.Add((text.Key, Parse(text.Key, text.Value)));
        }

        // 2. Ruleset-wide sections: later layers override field by field.
        var settings = new SectionSet();
        var templates = new SortedSet<string>(StringComparer.Ordinal);
        foreach ((string source, YamlMapping root) in layers)
        {
            foreach (KeyValuePair<string, YamlNode> e in root.Entries)
            {
                if (Array.IndexOf(Sections, e.Key) >= 0)
                {
                    settings.Apply(e.Key, e.Value, source);
                }
                else if (e.Key.StartsWith('^'))
                {
                    templates.Add(e.Key);
                }
            }
        }

        string rulesetName = settings.Scalar("ruleset", "name") ?? name;
        if (!string.Equals(rulesetName, name, StringComparison.Ordinal))
        {
            throw new RulesException(layers[chainLength - 1].Source, 0, $"ruleset.name is '{rulesetName}', expected '{name}'");
        }

        List<string> resources = settings.List("resources") ?? [];
        var gameplay = new GameplayReader(settings, resources);

        // 3. Layer 0: the original tables.
        var document = new RulesDocument();
        var warnings = new List<string>();
        LegacyTables? legacy = null;
        ulong? legacyHash = null;
        if (settings.Mapping("legacy") is { } legacyConfig)
        {
            if (!LegacyTables.Available(vfs))
            {
                throw new RulesException(settings.SourceOf("legacy"), 0, $"ruleset '{name}' is built from the original data tables: mount the imported base package (t2 import)");
            }

            legacy = LegacyTables.Load(vfs);
            new LegacyImporter(legacy, legacyConfig, settings.SourceOf("legacy"), templates.Contains, diagnostics).ImportInto(document);
            legacyHash = LegacyDataHash(document, legacy);
            if (settings.Scalar("legacy", "referenceDataHash") is { } reference
                && !string.Equals(reference, legacyHash.Value.ToString("X16", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
            {
                // Owner decision: a warning, not an error (modded installs stay playable; lobbies compare ruleset hashes).
                warnings.Add($"modified installation: the imported tables differ from the {settings.Scalar("legacy", "referenceEdition") ?? "reference build"} "
                    + $"(data hash {legacyHash.Value:X16}, expected {reference})");
            }
        }

        // 4. The YAML layers, then inheritance.
        foreach ((string source, YamlMapping root) in layers)
        {
            var entries = new List<KeyValuePair<string, YamlNode>>();
            foreach (KeyValuePair<string, YamlNode> e in root.Entries)
            {
                if (Array.IndexOf(Sections, e.Key) < 0)
                {
                    entries.Add(e);
                }
            }

            document.ApplyLayer(new YamlMapping(entries), source);
        }

        List<ResolvedEntry> resolved = document.Resolve();
        var compiled = new List<ResolvedEntry>();
        var technologyEntries = new List<ResolvedEntry>();
        var upgradeEntries = new List<ResolvedEntry>();
        foreach (ResolvedEntry e in resolved)
        {
            if (e.Key.StartsWith('^'))
            {
                continue;
            }

            // Technologies and upgrades (GAME-06) are entries with a Technology/Upgrade trait; ids of their own.
            (e.Find("Technology") is not null ? technologyEntries : e.Find("Upgrade") is not null ? upgradeEntries : compiled).Add(e);
        }

        compiled.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        technologyEntries.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        upgradeEntries.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        if (compiled.Count > Ruleset.MaxEntityTypes || technologyEntries.Count > Ruleset.MaxTechnologies || upgradeEntries.Count > Ruleset.MaxUpgrades)
        {
            throw new RulesException(layers[0].Source, 0, $"too many entity types, technologies or upgrades (at most {Ruleset.MaxEntityTypes}, {Ruleset.MaxTechnologies}, {Ruleset.MaxUpgrades})");
        }

        // 5. Traits, then technologies and upgrades (their effects name entity types).
        var researchIds = new ResearchIds(technologyEntries, upgradeEntries);
        var context = new EntityCompiler(compiled, resources, gameplay, researchIds, diagnostics);
        List<EntityTypeDefinition> types = context.CompileAll();
        List<TerrainTypeDefinition> terrain = ReadTerrain(settings);
        GameplaySettings gameplaySettings = gameplay.Build(legacy, settings);
        YamlMapping? legacyMapping = settings.Mapping("legacy");
        LegacyIni? bonusIni = legacy is not null && legacy.Has("data/bonus.ini") ? legacy.Ini("data/bonus.ini") : null;
        LegacyGroups groups = legacy is null ? LegacyGroups.Empty : LegacyGroups.Read(bonusIni, legacyMapping, settings.SourceOf("legacy"));
        List<DamageBonus> bonuses = bonusIni is not null ? LegacyBonuses.Compile(bonusIni, groups, types, diagnostics) : [];
        var research = new ResearchCompiler(types, resources, groups, researchIds, diagnostics);
        var orderGates = new List<OrderGate>();
        List<TechnologyDefinition> technologies = research.Technologies(technologyEntries, orderGates);
        List<UpgradeDefinition> upgrades = research.Upgrades(upgradeEntries);
        var civilizations = new SortedSet<string>(StringComparer.Ordinal);
        foreach (EntityTypeDefinition t in types)
        {
            if (t.Traits.Civilization.Length > 0)
            {
                civilizations.Add(t.Traits.Civilization);
            }
        }

        foreach (OrderGate g in orderGates)
        {
            civilizations.Add(g.Civilization);
        }

        int hudMask = -1;
        if (settings.Field("ruleset", "hudResources") is { } hud)
        {
            hudMask = 0;
            foreach (string r in RulesDocument.StringList(hud, settings.FieldSource("ruleset", "hudResources"), "ruleset.hudResources"))
            {
                int id = resources.IndexOf(r);
                hudMask |= id >= 0 ? 1 << id : throw new RulesException(settings.FieldSource("ruleset", "hudResources"), hud.Line, $"ruleset.hudResources: unknown resource '{r}'");
            }
        }

        try
        {
            var ruleset = new Ruleset(
                name,
                terrain,
                types,
                occupancyCellsPerTileAxis: settings.Int("ruleset", "occupancyCellsPerTileAxis") ?? Ruleset.DefaultOccupancyCellsPerTileAxis,
                maxPlayers: settings.Int("ruleset", "maxPlayers") ?? Ruleset.DefaultMaxPlayers,
                corpseTtlMilliseconds: settings.Int("ruleset", "corpseTtlMs") ?? Ruleset.DefaultCorpseTtlMilliseconds,
                decalCap: settings.Int("ruleset", "decalCap") ?? Ruleset.DefaultDecalCap,
                resourceTypes: resources,
                gameplay: gameplaySettings,
                damageBonuses: bonuses,
                technologies: technologies,
                upgrades: upgrades,
                civilizations: [.. civilizations],
                orderGates: orderGates,
                hudResourceMask: hudMask);
            return new CompiledRuleset(ruleset, diagnostics, warnings, legacyHash);
        }
        catch (ArgumentException ex)
        {
            throw new RulesException(layers[chainLength - 1].Source, 0, ex.Message);
        }
    }

    // The imported layer as the importer generated it (keys, parents, trait fields in order) plus the raw BONUS.INI and
    // RES.INI entries: everything compiled from the user's tables, independent of the YAML on top.
    private static ulong LegacyDataHash(RulesDocument document, LegacyTables legacy)
    {
        var hasher = new Core.Hashing.StateHasher();
        foreach (RuleEntry entry in document.Entries)
        {
            Str(hasher, entry.Key).Add(entry.Inherits.Count);
            foreach (string parent in entry.Inherits)
            {
                Str(hasher, parent);
            }

            hasher.Add(entry.Changes.Count);
            foreach (TraitChange change in entry.Changes)
            {
                Str(hasher, change.Trait).Add(change.RemoveTrait).Add(change.Operations.Count);
                foreach (FieldOperation op in change.Operations)
                {
                    Str(hasher, op.Field).Add((byte)op.Op);
                    Node(hasher, op.Value.Node);
                }
            }
        }

        foreach (string table in new[] { "data/bonus.ini", "data/res.ini" })
        {
            if (!legacy.Has(table))
            {
                hasher.Add(-1);
                continue;
            }

            LegacyIni ini = legacy.Ini(table);
            hasher.Add(ini.Sections.Count);
            foreach (LegacyIniSection section in ini.Sections)
            {
                Str(hasher, section.Name ?? string.Empty).Add(section.Entries.Count);
                foreach (KeyValuePair<string?, string> e in section.Entries)
                {
                    Str(Str(hasher, e.Key ?? string.Empty), e.Value);
                }
            }
        }

        return hasher.GetCurrentHash();

        static Core.Hashing.StateHasher Str(Core.Hashing.StateHasher h, string s) => h.Add(Encoding.UTF8.GetBytes(s));

        static void Node(Core.Hashing.StateHasher h, YamlNode node)
        {
            switch (node)
            {
                case YamlScalar s:
                    Str(h.Add((byte)1), s.Value);
                    break;
                case YamlSequence q:
                    h.Add((byte)2).Add(q.Items.Count);
                    foreach (YamlNode item in q.Items)
                    {
                        Node(h, item);
                    }

                    break;
                case YamlMapping m:
                    h.Add((byte)3).Add(m.Entries.Count);
                    foreach (KeyValuePair<string, YamlNode> e in m.Entries)
                    {
                        Str(h, e.Key);
                        Node(h, e.Value);
                    }

                    break;
            }
        }
    }

    private static void LoadChain(VirtualFileSystem vfs, string name, List<(string, YamlMapping)> layers, List<string> visiting)
    {
        if (visiting.Contains(name))
        {
            throw new RulesException(RulesFolder + name + ".yaml", 0, "ruleset base chain has a cycle");
        }

        if (visiting.Count >= 16)
        {
            throw new RulesException(RulesFolder + name + ".yaml", 0, "ruleset base chain is longer than 16 rulesets");
        }

        visiting.Add(name);
        string path = RulesFolder + name + ".yaml";
        YamlMapping root = Parse(path, ReadText(vfs, path));
        if (root.Get("ruleset") is YamlMapping header && header.Get("base") is YamlScalar { IsNull: false } baseName)
        {
            LoadChain(vfs, baseName.Value, layers, visiting);
        }

        layers.Add((path, root));
    }

    private static string ReadText(VirtualFileSystem vfs, string path)
    {
        if (!vfs.Exists(path))
        {
            throw new RulesException(path, 0, "rules file not found");
        }

        return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(vfs.ReadAllBytes(path));
    }

    private static YamlMapping Parse(string source, string text)
    {
        try
        {
            return RulesYaml.Parse(text) as YamlMapping ?? throw new RulesException(source, 1, "a rules file must be a mapping");
        }
        catch (YamlException ex)
        {
            throw new RulesException(source, ex.SourceLine, ex.Message);
        }
    }

    private static List<TerrainTypeDefinition> ReadTerrain(SectionSet settings)
    {
        var list = new List<TerrainTypeDefinition>();
        if (settings.Node("terrain") is not { } node)
        {
            throw new RulesException("rules", 0, "the ruleset defines no terrain types");
        }

        string source = settings.SourceOf("terrain");
        foreach (YamlNode item in Seq(node, source, "terrain").Items)
        {
            YamlMapping t = Map(item, source, "terrain[]");
            string key = Str(t.Get("key"), source, "terrain[].key");
            LocomotorMask mask = LocomotorMask.None;
            foreach (string loco in RulesDocument.StringList(t.Get("passable") ?? new YamlSequence([]), source, $"terrain.{key}.passable"))
            {
                mask |= ParseLocomotor(loco, source, t.Line).ToMask();
            }

            bool water = t.Get("water") is { } w && Bool(w, source, $"terrain.{key}.water");
            if (list.Count > byte.MaxValue)
            {
                throw new RulesException(source, item.Line, "more than 256 terrain types");
            }

            list.Add(new TerrainTypeDefinition((byte)list.Count, key, mask, water));
        }

        return list;
    }

    internal static Locomotor ParseLocomotor(string text, string source, int line) => text switch
    {
        "foot" => Locomotor.Foot,
        "mounted" => Locomotor.Mounted,
        "large" => Locomotor.Large,
        "ship" => Locomotor.Ship,
        "air" => Locomotor.Air,
        _ => throw new RulesException(source, line, $"unknown locomotor '{text}' (foot, mounted, large, ship, air)"),
    };

    internal static YamlMapping Map(YamlNode? node, string source, string what) => node as YamlMapping
        ?? throw new RulesException(source, node?.Line ?? 0, $"{what}: expected a mapping");

    internal static YamlSequence Seq(YamlNode? node, string source, string what) => node as YamlSequence
        ?? throw new RulesException(source, node?.Line ?? 0, $"{what}: expected a list");

    internal static string Str(YamlNode? node, string source, string what) => node is YamlScalar { IsNull: false } s
        ? s.Value
        : throw new RulesException(source, node?.Line ?? 0, $"{what}: expected a value");

    internal static int Int(YamlNode? node, string source, string what)
    {
        string text = Str(node, source, what).Replace("_", string.Empty, StringComparison.Ordinal);
        return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new RulesException(source, node!.Line, $"{what}: '{text}' is not an integer");
    }

    internal static bool Bool(YamlNode? node, string source, string what) => Str(node, source, what) switch
    {
        "true" or "yes" => true,
        "false" or "no" => false,
        { } other => throw new RulesException(source, node!.Line, $"{what}: '{other}' is not true/false"),
    };

    /// <summary>The ruleset-wide sections, merged field by field over the layers (later wins).</summary>
    internal sealed class SectionSet
    {
        private readonly List<(string Section, string Field, YamlNode Value, string Source)> _fields = [];
        private readonly List<(string Section, YamlNode Value, string Source)> _whole = [];

        public void Apply(string section, YamlNode value, string source)
        {
            if (section is "ruleset" or "gameplay" or "legacy")
            {
                YamlMapping map = Map(value, source, section);
                foreach (KeyValuePair<string, YamlNode> f in map.Entries)
                {
                    _fields.RemoveAll(x => x.Section == section && x.Field == f.Key);
                    _fields.Add((section, f.Key, f.Value, source));
                }
            }
            else
            {
                _whole.RemoveAll(x => x.Section == section);
                _whole.Add((section, value, source));
            }
        }

        public YamlNode? Field(string section, string field)
        {
            foreach ((string s, string f, YamlNode v, string _) in _fields)
            {
                if (s == section && f == field)
                {
                    return v;
                }
            }

            return null;
        }

        public string FieldSource(string section, string field)
        {
            foreach ((string s, string f, YamlNode _, string src) in _fields)
            {
                if (s == section && f == field)
                {
                    return src;
                }
            }

            return SourceOf(section);
        }

        public YamlNode? Node(string section)
        {
            foreach ((string s, YamlNode v, string _) in _whole)
            {
                if (s == section)
                {
                    return v;
                }
            }

            return null;
        }

        public string SourceOf(string section)
        {
            foreach ((string s, YamlNode _, string src) in _whole)
            {
                if (s == section)
                {
                    return src;
                }
            }

            foreach ((string s, string _, YamlNode _, string src) in _fields)
            {
                if (s == section)
                {
                    return src;
                }
            }

            return "rules";
        }

        /// <summary>The merged fields of a mapping section, or null if no layer defines it.</summary>
        public YamlMapping? Mapping(string section)
        {
            var entries = new List<KeyValuePair<string, YamlNode>>();
            foreach ((string s, string f, YamlNode v, string _) in _fields)
            {
                if (s == section)
                {
                    entries.Add(new KeyValuePair<string, YamlNode>(f, v));
                }
            }

            return entries.Count == 0 ? null : new YamlMapping(entries);
        }

        public string? Scalar(string section, string field) => Field(section, field) is YamlScalar { IsNull: false } s ? s.Value : null;

        public int? Int(string section, string field) => Field(section, field) is { } node ? RulesetCompiler.Int(node, FieldSource(section, field), $"{section}.{field}") : null;

        public List<string>? List(string section) => Node(section) is { } node ? RulesDocument.StringList(node, SourceOf(section), section) : null;
    }
}
