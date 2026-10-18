using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.Rules;

/// <summary>Technology and upgrade ids by key (entries sorted by key, ordinal), known before the entities compile.</summary>
internal sealed class ResearchIds
{
    private readonly string[] _technologies;
    private readonly string[] _upgrades;

    public ResearchIds(List<ResolvedEntry> technologies, List<ResolvedEntry> upgrades)
    {
        _technologies = Keys(technologies);
        _upgrades = Keys(upgrades);
    }

    public ushort? Technology(string key) => Find(_technologies, key);

    public ushort? Upgrade(string key) => Find(_upgrades, key);

    private static ushort? Find(string[] keys, string key)
    {
        int i = Array.BinarySearch(keys, key, StringComparer.Ordinal);
        return i >= 0 ? (ushort)i : null;
    }

    private static string[] Keys(List<ResolvedEntry> entries)
    {
        string[] keys = new string[entries.Count];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = entries[i].Key;
        }

        return keys;
    }
}

/// <summary>
/// Compiles technology and upgrade entries (GAME-06; ADR 0040): cost, time, per-civ prerequisites (<c>Requires</c>,
/// <c>TechTree</c>), effects (<c>Technology.effects</c>, <c>Upgrade.effects</c>: modifiers whose target filters resolve
/// to entity type ids here), conversions (<c>Upgrade.into</c>: the class an upgraded entity of the same civilization
/// becomes) and the order gates of <c>TECHTREE [orders]</c>.
/// </summary>
internal sealed class ResearchCompiler
{
    private static readonly string[] TechTraits = ["Legacy", "Cost", "BuildTime", "Technology", "Upgrade", "Requires", "TechTree"];
    private static readonly string[] EffectFields = ["stat", "add", "percent", "classes", "groups", "types", "damage", "category", "locomotor", "civ"];

    private readonly List<EntityTypeDefinition> _types;
    private readonly List<string> _resources;
    private readonly LegacyGroups _groups;
    private readonly ResearchIds _ids;
    private readonly List<string> _diagnostics;

    public ResearchCompiler(List<EntityTypeDefinition> types, List<string> resources, LegacyGroups groups, ResearchIds ids, List<string> diagnostics)
    {
        _types = types;
        _resources = resources;
        _groups = groups;
        _ids = ids;
        _diagnostics = diagnostics;
    }

    public List<TechnologyDefinition> Technologies(List<ResolvedEntry> entries, List<OrderGate> gates)
    {
        var list = new List<TechnologyDefinition>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            ResolvedEntry e = entries[i];
            Check(e);
            (ImmutableArray<RequirementSet> requirements, List<(string Civ, string Order)> orders) = Requirements(e);
            foreach ((string civ, string order) in orders)
            {
                gates.Add(new OrderGate(order, civ, (ushort)i));
            }

            list.Add(new TechnologyDefinition((ushort)i, e.Key, Name(e), Cost(e), Time(e), requirements, Effects(e, e.Find("Technology"))));
        }

        return list;
    }

    public List<UpgradeDefinition> Upgrades(List<ResolvedEntry> entries)
    {
        var list = new List<UpgradeDefinition>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            ResolvedEntry e = entries[i];
            Check(e);
            ResolvedTrait upgrade = e.Find("Upgrade")!;
            (ImmutableArray<RequirementSet> requirements, List<(string Civ, string Order)> _) = Requirements(e);
            var after = new SortedSet<ushort>();
            if (upgrade.Get("after") is { } a)
            {
                foreach (string key in RulesDocument.StringList(a.Node, a.Source, $"{e.Key}.Upgrade.after"))
                {
                    after.Add(_ids.Upgrade(key) ?? throw new RulesException(a.Source, a.Node.Line, $"{e.Key}.Upgrade.after: unknown upgrade '{key}'"));
                }
            }

            // Conversions: every type that can buy the upgrade becomes the class named by `into` of its own
            // civilization (or the common one).
            var conversions = new List<(ushort, ushort)>();
            string? into = upgrade.Get("into") is { } v ? RulesetCompiler.Str(v.Node, v.Source, $"{e.Key}.Upgrade.into") : null;
            if (into is not null)
            {
                foreach (EntityTypeDefinition t in _types)
                {
                    if (t.Traits.Producer is not { } p || !p.CanUpgrade((ushort)i))
                    {
                        continue;
                    }

                    int target = FindClass(into, t.Traits.Civilization);
                    if (target < 0)
                    {
                        _diagnostics.Add($"{e.Key}: '{t.Key}' cannot become '{into}' (not imported); the upgrade is not available there");
                        continue;
                    }

                    if (target != t.Id)
                    {
                        conversions.Add((t.Id, (ushort)target));
                    }
                }

                if (conversions.Count == 0)
                {
                    _diagnostics.Add($"{e.Key}: no imported type can be upgraded into '{into}'");
                }
            }

            list.Add(new UpgradeDefinition((ushort)i, e.Key, Name(e), Cost(e), Time(e), requirements, [.. conversions], [.. after], Effects(e, upgrade)));
        }

        return list;
    }

    private int FindClass(string cls, string civilization)
    {
        int common = -1;
        foreach (EntityTypeDefinition t in _types)
        {
            if (t.Traits.LegacyClass != cls)
            {
                continue;
            }

            if (t.Traits.Civilization == civilization)
            {
                return t.Id;
            }

            if (t.Traits.Civilization == "comm")
            {
                common = t.Id;
            }
        }

        return common;
    }

    private static void Check(ResolvedEntry e)
    {
        foreach (ResolvedTrait trait in e.Traits)
        {
            if (Array.IndexOf(TechTraits, trait.Name) < 0)
            {
                throw new RulesException(trait.Source, trait.Line, $"{e.Key}: trait '{trait.Name}' is not allowed on a technology or upgrade (allowed: {string.Join(", ", TechTraits)})");
            }
        }
    }

    private static string Name(ResolvedEntry e) => e.Find("Legacy")?.Get("name") is { } n ? RulesetCompiler.Str(n.Node, n.Source, $"{e.Key}.Legacy.name") : e.Key;

    private static int Time(ResolvedEntry e)
    {
        RuleValue v = e.Find("BuildTime")?.Get("ms") ?? throw new RulesException(e.Source, e.Line, $"{e.Key}: a technology or upgrade needs BuildTime.ms");
        int ms = RulesetCompiler.Int(v.Node, v.Source, $"{e.Key}.BuildTime.ms");
        return ms >= 1 ? ms : throw new RulesException(v.Source, v.Node.Line, $"{e.Key}.BuildTime.ms must be positive");
    }

    private ImmutableArray<int> Cost(ResolvedEntry e)
    {
        int[] amounts = new int[_resources.Count];
        if (e.Find("Cost") is { } cost)
        {
            foreach (KeyValuePair<string, RuleValue> f in cost.Fields)
            {
                int r = _resources.IndexOf(f.Key);
                if (r < 0)
                {
                    throw new RulesException(f.Value.Source, f.Value.Node.Line, $"{e.Key}.Cost: unknown resource '{f.Key}'");
                }

                amounts[r] = RulesetCompiler.Int(f.Value.Node, f.Value.Source, $"{e.Key}.Cost.{f.Key}");
                if (amounts[r] < 0)
                {
                    throw new RulesException(f.Value.Source, f.Value.Node.Line, $"{e.Key}.Cost.{f.Key} must not be negative");
                }
            }
        }

        return [.. amounts];
    }

    private (ImmutableArray<RequirementSet> Sets, List<(string Civ, string Order)> Orders) Requirements(ResolvedEntry e)
    {
        var sets = new List<RequirementSet>();
        var orders = new List<(string, string)>();
        if (e.Find("Requires") is { } requires)
        {
            (SortedSet<ushort> techs, SortedSet<ushort> types, bool unsatisfiable) = Prerequisites(e.Key, requires.Get("technologies"), requires.Get("buildings"));
            sets.Add(new RequirementSet(string.Empty, [.. techs], [.. types], unsatisfiable));
        }

        if (e.Find("TechTree") is { } tree)
        {
            foreach (KeyValuePair<string, RuleValue> civ in tree.Fields)
            {
                YamlMapping spec = RulesetCompiler.Map(civ.Value.Node, civ.Value.Source, $"{e.Key}.TechTree.{civ.Key}");
                RuleValue? techList = null;
                RuleValue? buildingList = null;
                foreach (KeyValuePair<string, YamlNode> f in spec.Entries)
                {
                    var value = new RuleValue(f.Value, civ.Value.Source);
                    switch (f.Key)
                    {
                        case "technologies":
                            techList = value;
                            break;
                        case "buildings":
                            buildingList = value;
                            break;
                        case "orders":
                            foreach (string order in RulesDocument.StringList(f.Value, civ.Value.Source, $"{e.Key}.TechTree.{civ.Key}.orders"))
                            {
                                orders.Add((civ.Key, order));
                            }

                            break;
                        default:
                            throw new RulesException(civ.Value.Source, f.Value.Line, $"{e.Key}.TechTree.{civ.Key}: unknown field '{f.Key}' (technologies, buildings, orders)");
                    }
                }

                (SortedSet<ushort> techs, SortedSet<ushort> types, bool unsatisfiable) = Prerequisites(e.Key, techList, buildingList);
                if (techs.Count > 0 || types.Count > 0 || unsatisfiable)
                {
                    sets.Add(new RequirementSet(civ.Key, [.. techs], [.. types], unsatisfiable));
                }
            }
        }

        return ([.. sets], orders);
    }

    private (SortedSet<ushort> Techs, SortedSet<ushort> Types, bool Unsatisfiable) Prerequisites(string key, RuleValue? techList, RuleValue? buildingList)
    {
        var techs = new SortedSet<ushort>();
        var types = new SortedSet<ushort>();
        bool unsatisfiable = false;
        if (techList is { } t)
        {
            foreach (string name in RulesDocument.StringList(t.Node, t.Source, $"{key}: technologies"))
            {
                if (_ids.Technology(name) is { } id)
                {
                    techs.Add(id);
                }
                else
                {
                    unsatisfiable = true;
                    _diagnostics.Add($"{key}: requires '{name}', which does not exist; it can never be researched");
                }
            }
        }

        if (buildingList is { } b)
        {
            foreach (string name in RulesDocument.StringList(b.Node, b.Source, $"{key}: buildings"))
            {
                int id = FindKey(name);
                if (id < 0 || !_types[id].HasFootprint)
                {
                    throw new RulesException(b.Source, b.Node.Line, $"{key}: requires unknown building '{name}'");
                }

                types.Add((ushort)id);
            }
        }

        return (techs, types, unsatisfiable);
    }

    private int FindKey(string key)
    {
        foreach (EntityTypeDefinition t in _types)
        {
            if (t.Key == key)
            {
                return t.Id;
            }
        }

        return -1;
    }

    // Technology.effects / Upgrade.effects: a list of { stat, add, percent, <filters> }.
    private ImmutableArray<ModifierDefinition> Effects(ResolvedEntry e, ResolvedTrait? trait)
    {
        if (trait?.Get("effects") is not { } effects)
        {
            return [];
        }

        var list = new List<ModifierDefinition>();
        foreach (YamlNode item in RulesetCompiler.Seq(effects.Node, effects.Source, $"{e.Key}.effects").Items)
        {
            YamlMapping m = RulesetCompiler.Map(item, effects.Source, $"{e.Key}.effects[]");
            foreach (KeyValuePair<string, YamlNode> f in m.Entries)
            {
                if (Array.IndexOf(EffectFields, f.Key) < 0)
                {
                    throw new RulesException(effects.Source, f.Value.Line, $"{e.Key}.effects: unknown field '{f.Key}' (known: {string.Join(", ", EffectFields)})");
                }
            }

            string statKey = RulesetCompiler.Str(m.Get("stat"), effects.Source, $"{e.Key}.effects[].stat");
            if (!ModifierStats.TryParse(statKey, out ModifierStat stat))
            {
                throw new RulesException(effects.Source, item.Line, $"{e.Key}.effects: unknown stat '{statKey}' (known: {ModifierStats.KnownKeys})");
            }

            int add = m.Get("add") is { } a ? RulesetCompiler.Int(a, effects.Source, $"{e.Key}.effects[].add") : 0;
            int percent = m.Get("percent") is { } p ? RulesetCompiler.Int(p, effects.Source, $"{e.Key}.effects[].percent") : 0;
            if (percent is < -100 or > 10_000 || add is < -1_000_000 or > 1_000_000)
            {
                throw new RulesException(effects.Source, item.Line, $"{e.Key}.effects: percent must be -100..10000 and add within ±1,000,000");
            }

            ImmutableArray<ushort> types = Filter(e.Key, m, effects.Source);
            if (types.IsEmpty)
            {
                _diagnostics.Add($"{e.Key}: an effect on {statKey} matches no imported type");
            }

            list.Add(new ModifierDefinition(stat, add, percent * 100, types));
        }

        return [.. list];
    }

    internal ImmutableArray<ushort> Filter(string key, YamlMapping m, string source)
    {
        var selected = new SortedSet<ushort>();
        bool anySelector = false;
        if (m.Get("classes") is { } classes)
        {
            anySelector = true;
            foreach (string cls in RulesDocument.StringList(classes, source, $"{key}.effects[].classes"))
            {
                foreach (EntityTypeDefinition t in _types)
                {
                    if (t.Traits.LegacyClass == cls)
                    {
                        selected.Add(t.Id);
                    }
                }
            }
        }

        if (m.Get("groups") is { } groups)
        {
            anySelector = true;
            var ids = new List<ushort>();
            foreach (string group in RulesDocument.StringList(groups, source, $"{key}.effects[].groups"))
            {
                if (!_groups.Contains(group))
                {
                    throw new RulesException(source, groups.Line, $"{key}.effects[].groups: unknown group '{group}' (BONUS.INI [groups])");
                }

                ids.Clear();
                _groups.Match(group, _types, ids);
                selected.UnionWith(ids);
            }
        }

        if (m.Get("types") is { } types)
        {
            anySelector = true;
            foreach (string typeKey in RulesDocument.StringList(types, source, $"{key}.effects[].types"))
            {
                int id = FindKey(typeKey);
                selected.Add(id >= 0 ? (ushort)id : throw new RulesException(source, types.Line, $"{key}.effects[].types: unknown entity '{typeKey}'"));
            }
        }

        if (!anySelector)
        {
            foreach (EntityTypeDefinition t in _types)
            {
                selected.Add(t.Id);
            }
        }

        string? damage = m.Get("damage") is { } d ? RulesetCompiler.Str(d, source, $"{key}.effects[].damage") : null;
        string? category = m.Get("category") is { } c ? RulesetCompiler.Str(c, source, $"{key}.effects[].category") : null;
        Locomotor? locomotor = m.Get("locomotor") is { } l ? RulesetCompiler.ParseLocomotor(RulesetCompiler.Str(l, source, $"{key}.effects[].locomotor"), source, l.Line) : null;
        string? civ = m.Get("civ") is { } v ? RulesetCompiler.Str(v, source, $"{key}.effects[].civ") : null;
        DamageClass? damageClass = damage switch
        {
            null => null,
            "sword" => DamageClass.Sword,
            "arrow" => DamageClass.Arrow,
            "cannon" => DamageClass.Cannon,
            _ => throw new RulesException(source, m.Line, $"{key}.effects[].damage: '{damage}' is not sword/arrow/cannon"),
        };
        EntityCategory? wanted = category switch
        {
            null => null,
            "unit" => EntityCategory.Unit,
            "building" => EntityCategory.Building,
            "resource" => EntityCategory.Resource,
            _ => throw new RulesException(source, m.Line, $"{key}.effects[].category: '{category}' is not unit/building/resource"),
        };

        var result = ImmutableArray.CreateBuilder<ushort>();
        foreach (ushort id in selected)
        {
            EntityTypeDefinition t = _types[id];
            if ((damageClass is { } dc && t.Traits.Armament?.DamageClass != dc)
                || (wanted is { } w && t.Traits.Category != w)
                || (locomotor is { } lo && (!t.IsMobile || t.Locomotor != lo))
                || (civ is not null && t.Traits.Civilization != civ))
            {
                continue;
            }

            result.Add(id);
        }

        return result.ToImmutable();
    }
}
