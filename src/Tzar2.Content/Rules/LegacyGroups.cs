using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.Rules;

/// <summary>
/// The unit groups of <c>BONUS.INI [groups]</c> (<c>infantry_units = archer, footman, ...</c>; an optional civilization
/// prefix <c>e </c>/<c>a </c>/<c>r </c> restricts a class to one civilization) and the class-spec matching shared by the
/// damage bonuses (ADR 0038) and the technology effects (ADR 0040).
/// </summary>
internal sealed class LegacyGroups
{
    private readonly List<(string Name, List<string> Members)> _groups = [];
    private readonly List<(string Prefix, string Civ)> _prefixes = [];

    /// <summary>No groups (a ruleset without imported tables).</summary>
    public static LegacyGroups Empty { get; } = new();

    /// <summary>Reads the groups of <paramref name="bonus"/> (may be null) with the prefixes of <c>legacy.bonusCivPrefixes</c>.</summary>
    public static LegacyGroups Read(LegacyIni? bonus, YamlMapping? legacyConfig, string source)
    {
        var groups = new LegacyGroups();
        if (legacyConfig?.Get("bonusCivPrefixes") is YamlMapping map)
        {
            foreach (KeyValuePair<string, YamlNode> p in map.Entries)
            {
                groups._prefixes.Add((p.Key, RulesetCompiler.Str(p.Value, source, "legacy.bonusCivPrefixes")));
            }
        }

        if (bonus?.Section("groups") is { } section)
        {
            foreach (KeyValuePair<string?, string> e in section.Entries)
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

                groups._groups.Add((e.Key.Trim().ToLowerInvariant(), members));
            }
        }

        return groups;
    }

    /// <summary>Whether a group of that name exists.</summary>
    public bool Contains(string name)
    {
        string lower = name.ToLowerInvariant();
        return _groups.Exists(g => g.Name == lower);
    }

    /// <summary>Adds the ids of the types a spec (a group, or a class with an optional civ prefix) matches.</summary>
    public void Match(string spec, List<EntityTypeDefinition> types, List<ushort> output)
    {
        string lower = spec.ToLowerInvariant();
        foreach ((string name, List<string> members) in _groups)
        {
            if (name == lower)
            {
                foreach (string m in members)
                {
                    MatchClass(m, types, output);
                }

                return;
            }
        }

        MatchClass(spec, types, output);
    }

    private void MatchClass(string spec, List<EntityTypeDefinition> types, List<ushort> output)
    {
        string[] parts = spec.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? civ = null;
        string cls = parts.Length == 0 ? string.Empty : parts[^1];
        if (parts.Length == 2)
        {
            foreach ((string prefix, string c) in _prefixes)
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
}
