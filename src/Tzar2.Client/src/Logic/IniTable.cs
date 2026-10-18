using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Tzar2.Client.Logic;

/// <summary>
/// An imported INI table (<c>tables/**.ini.json</c>, docs/formats/ini-tsv.md): sections in file order with their
/// entries; names compare case-insensitively like the original's lookups. Duplicate sections are all kept; lookups
/// return the first.
/// </summary>
public sealed class IniTable
{
    private readonly List<IniSection> _sections = [];
    private readonly Dictionary<string, IniSection> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An empty table.</summary>
    public static IniTable Empty { get; } = new();

    /// <summary>All sections in file order.</summary>
    public IReadOnlyList<IniSection> Sections => _sections;

    /// <summary>The section named <paramref name="name"/>, or null.</summary>
    public IniSection? Section(string? name) => name is not null && _byName.TryGetValue(name, out IniSection? s) ? s : null;

    /// <summary>The value of <paramref name="key"/> in <paramref name="section"/>, or null.</summary>
    public string? Get(string section, string key) => Section(section)?.Get(key);

    /// <summary>Parses the importer's JSON form.</summary>
    /// <exception cref="JsonException">Malformed JSON.</exception>
    public static IniTable Parse(ReadOnlySpan<byte> json)
    {
        var table = new IniTable();
        var reader = new Utf8JsonReader(json);
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        if (!doc.RootElement.TryGetProperty("sections", out JsonElement sections) || sections.ValueKind != JsonValueKind.Array)
        {
            return table;
        }

        foreach (JsonElement s in sections.EnumerateArray())
        {
            string name = s.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty;
            var entries = new List<KeyValuePair<string?, string>>();
            if (s.TryGetProperty("entries", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement pair in list.EnumerateArray())
                {
                    if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2)
                    {
                        continue;
                    }

                    string? key = pair[0].ValueKind == JsonValueKind.String ? pair[0].GetString() : null;
                    string value = pair[1].ValueKind == JsonValueKind.String ? pair[1].GetString() ?? string.Empty : string.Empty;
                    entries.Add(new(key, value));
                }
            }

            var section = new IniSection(name, entries);
            table._sections.Add(section);
            table._byName.TryAdd(name, section);
        }

        return table;
    }
}

/// <summary>One INI section.</summary>
/// <param name="Name">Section name.</param>
/// <param name="Entries">Key/value pairs in file order (a null key is a bare value line).</param>
public sealed record IniSection(string Name, IReadOnlyList<KeyValuePair<string?, string>> Entries)
{
    /// <summary>The first value of <paramref name="key"/> (case-insensitive), or null.</summary>
    public string? Get(string key)
    {
        foreach (KeyValuePair<string?, string> e in Entries)
        {
            if (e.Key is not null && string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return e.Value;
            }
        }

        return null;
    }
}
