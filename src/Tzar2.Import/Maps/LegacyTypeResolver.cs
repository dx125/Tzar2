using System;
using System.Collections.Generic;
using System.Globalization;
using Tzar2.Content.Compiled;

namespace Tzar2.Import.Maps;

/// <summary>
/// Maps legacy object classes to entity type keys (REQ-EDT-10). A user mapping table wins; then, with a ruleset, the
/// first key it defines among <c>&lt;civ&gt;.&lt;class&gt;</c>, <c>comm.&lt;class&gt;</c> and the other civilizations;
/// without a ruleset the key is formed by name (<c>&lt;civ&gt;.&lt;class&gt;</c>, <c>comm</c> for race 0). Unresolved
/// classes return null and become flagged placeholders, never silently dropped.
/// </summary>
public sealed class LegacyTypeResolver
{
    private static readonly string[] Civs = ["comm", "euro", "arab", "asia"];

    private readonly Ruleset? _ruleset;
    private readonly Dictionary<string, string> _mapping;

    /// <summary>Creates a resolver.</summary>
    /// <param name="ruleset">The target ruleset, or null to form keys by name.</param>
    /// <param name="mapping">User mapping: legacy <c>class</c> or <c>civ.class</c> → type key (empty value = drop silently is not allowed; use a key).</param>
    public LegacyTypeResolver(Ruleset? ruleset = null, IReadOnlyDictionary<string, string>? mapping = null)
    {
        _ruleset = ruleset;
        _mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (mapping is not null)
        {
            foreach (KeyValuePair<string, string> m in mapping)
            {
                _mapping[m.Key] = m.Value;
            }
        }
    }

    /// <summary>The ruleset, or null.</summary>
    public Ruleset? Ruleset => _ruleset;

    /// <summary>The civilization prefix of a legacy race (0 comm, 1 euro, 2 arab, 3 asia).</summary>
    public static string CivOf(int race) => (uint)race < (uint)Civs.Length ? Civs[race] : "comm";

    /// <summary>Resolves <paramref name="className"/> of <paramref name="race"/>, or null when the ruleset lacks it.</summary>
    public string? Resolve(string className, int race)
    {
        string civ = CivOf(race);
        if (_mapping.TryGetValue(civ + "." + className, out string? mapped) || _mapping.TryGetValue(className, out mapped))
        {
            return mapped;
        }

        if (_ruleset is null)
        {
            return civ + "." + className;
        }

        foreach (string candidate in Candidates(civ, className))
        {
            if (_ruleset.EntityTypeId(candidate) >= 0)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Ruleset terrain id of a terrain-set index (0 water, others land); defaults to the classic ids.</summary>
    public byte TerrainId(int setIndex)
    {
        string key = setIndex == 0 ? "water" : "grass";
        if (_ruleset is not null)
        {
            for (int i = 0; i < _ruleset.TerrainTypes.Count; i++)
            {
                if (_ruleset.TerrainTypes[i].Key == key)
                {
                    return (byte)i;
                }
            }
        }

        return setIndex == 0 ? (byte)1 : (byte)0;
    }

    /// <summary>
    /// Parses a mapping table: one <c>legacy: key</c> per line (a YAML mapping of scalars; <c>#</c> starts a comment).
    /// </summary>
    /// <exception cref="FormatException">A line is not <c>name: value</c>.</exception>
    public static Dictionary<string, string> ParseMapping(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0)
            {
                line = line[..hash];
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            string key = colon > 0 ? line[..colon].Trim().Trim('"', '\'') : string.Empty;
            string value = colon > 0 ? line[(colon + 1)..].Trim().Trim('"', '\'') : string.Empty;
            if (key.Length == 0 || value.Length == 0)
            {
                throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"mapping line {i + 1}: expected 'legacy: key'."));
            }

            result[key] = value;
        }

        return result;
    }

    private static IEnumerable<string> Candidates(string civ, string className)
    {
        yield return civ + "." + className;
        foreach (string other in Civs)
        {
            if (other != civ)
            {
                yield return other + "." + className;
            }
        }
    }
}
