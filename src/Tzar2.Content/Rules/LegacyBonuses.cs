using System;
using System.Collections.Generic;
using System.Globalization;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.Rules;

/// <summary>
/// Compiles <c>BONUS.INI</c> (TDD Appendix E.2.2) into per-type-pair attack bonuses. <c>[groups]</c> defines named
/// groups of class specs; every other section is an attacker spec (a group, or a class with an optional civilization
/// prefix: <c>e knight</c>, <c>a maceman</c>, <c>archer</c>) whose entries map target specs to <c>+N%</c>/<c>-N%</c>.
/// How several matching bonuses combine is not known (UNK-08): they are summed, a placeholder (ADR 0038).
/// </summary>
internal static class LegacyBonuses
{
    private const string Source = "legacy:data/bonus.ini";

    public static List<DamageBonus> Compile(LegacyIni ini, YamlMapping legacyConfig, List<EntityTypeDefinition> types, List<string> diagnostics)
    {
        // Civilization prefixes of class specs (e/a/r).
        var prefixes = new List<(string Prefix, string Civ)>();
        if (legacyConfig.Get("bonusCivPrefixes") is YamlMapping map)
        {
            foreach (KeyValuePair<string, YamlNode> p in map.Entries)
            {
                prefixes.Add((p.Key, RulesetCompiler.Str(p.Value, Source, "legacy.bonusCivPrefixes")));
            }
        }

        var groups = new List<(string Name, List<string> Members)>();
        if (ini.Section("groups") is { } groupSection)
        {
            foreach (KeyValuePair<string?, string> e in groupSection.Entries)
            {
                if (e.Key is null)
                {
                    continue;
                }

                var members = new List<string>();
                foreach (string m in e.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    members.Add(m);
                }

                groups.Add((e.Key.Trim().ToLowerInvariant(), members));
            }
        }

        // (attacker, target) -> summed basis points; a sorted key keeps the output order deterministic.
        var sums = new SortedDictionary<(ushort, ushort), int>();
        var attackers = new List<ushort>();
        var targets = new List<ushort>();
        foreach (LegacyIniSection section in ini.Sections)
        {
            if (section.Name is null || string.Equals(section.Name.Trim(), "groups", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            attackers.Clear();
            Match(section.Name.Trim(), groups, prefixes, types, attackers);
            foreach (KeyValuePair<string?, string> e in section.Entries)
            {
                if (e.Key is null)
                {
                    continue;
                }

                int bp = ParsePercent(e.Value, section.Name, e.Key);
                targets.Clear();
                Match(e.Key.Trim(), groups, prefixes, types, targets);
                foreach (ushort a in attackers)
                {
                    foreach (ushort t in targets)
                    {
                        sums[(a, t)] = (sums.TryGetValue((a, t), out int s) ? s : 0) + bp;
                    }
                }
            }
        }

        var list = new List<DamageBonus>(sums.Count);
        foreach (KeyValuePair<(ushort, ushort), int> kv in sums)
        {
            if (kv.Value != 0)
            {
                list.Add(new DamageBonus(kv.Key.Item1, kv.Key.Item2, kv.Value));
            }
        }

        diagnostics.Add($"BONUS.INI: {list.Count} attack bonuses between imported types");
        return list;
    }

    private static void Match(string spec, List<(string Name, List<string> Members)> groups, List<(string Prefix, string Civ)> prefixes, List<EntityTypeDefinition> types, List<ushort> output)
    {
        string lower = spec.ToLowerInvariant();
        foreach ((string name, List<string> members) in groups)
        {
            if (name == lower)
            {
                foreach (string m in members)
                {
                    MatchClass(m, prefixes, types, output);
                }

                return;
            }
        }

        MatchClass(spec, prefixes, types, output);
    }

    private static void MatchClass(string spec, List<(string Prefix, string Civ)> prefixes, List<EntityTypeDefinition> types, List<ushort> output)
    {
        string[] parts = spec.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? civ = null;
        string cls = parts.Length == 0 ? string.Empty : parts[^1];
        if (parts.Length == 2)
        {
            foreach ((string prefix, string c) in prefixes)
            {
                if (prefix == parts[0])
                {
                    civ = c;
                }
            }

            if (civ is null)
            {
                return;
            }
        }

        foreach (EntityTypeDefinition t in types)
        {
            if (string.Equals(t.Traits.LegacyClass, cls, StringComparison.Ordinal) && (civ is null || t.Traits.Civilization == civ) && !output.Contains(t.Id))
            {
                output.Add(t.Id);
            }
        }
    }

    private static int ParsePercent(string value, string section, string key)
    {
        string text = value.Trim();
        if (text.EndsWith('%'))
        {
            text = text[..^1];
        }

        return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int percent) && percent is >= -100_000 and <= 100_000
            ? percent * 100
            : throw new RulesException(Source, 0, $"[{section}] {key}: '{value}' is not a percentage");
    }
}
