using System;
using System.Collections.Generic;
using System.Globalization;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.Rules;

/// <summary>
/// Compiles <c>BONUS.INI</c> (TDD Appendix E.2.2) into per-type-pair attack bonuses. <c>[groups]</c> defines named
/// groups of class specs (<see cref="LegacyGroups"/>); every other section is an attacker spec (a group, or a class with
/// an optional civilization prefix: <c>e knight</c>, <c>a maceman</c>, <c>archer</c>) whose entries map target specs to
/// <c>+N%</c>/<c>-N%</c>. How several matching bonuses combine is not known (UNK-08): they are summed, a placeholder
/// (ADR 0038).
/// </summary>
internal static class LegacyBonuses
{
    private const string Source = "legacy:data/bonus.ini";

    public static List<DamageBonus> Compile(LegacyIni ini, LegacyGroups groups, List<EntityTypeDefinition> types, List<string> diagnostics)
    {
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
            groups.Match(section.Name.Trim(), types, attackers);
            foreach (KeyValuePair<string?, string> e in section.Entries)
            {
                if (e.Key is null)
                {
                    continue;
                }

                int bp = ParsePercent(e.Value, section.Name, e.Key);
                targets.Clear();
                groups.Match(e.Key.Trim(), types, targets);
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
