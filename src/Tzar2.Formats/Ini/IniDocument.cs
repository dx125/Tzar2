using System;
using System.Collections.Generic;
using Tzar2.Formats.Text;

namespace Tzar2.Formats.Ini;

/// <summary>One line of a section: a <c>key = value</c> pair, or a bare value (<see cref="Key"/> is null).</summary>
/// <param name="Key">The key as written (case preserved), or null for a key-less line such as <c>allunits</c>.</param>
/// <param name="Value">The value, trimmed, with any <c>;</c> comment removed. May be empty.</param>
/// <param name="Line">1-based source line.</param>
public sealed record IniEntry(string? Key, string Value, int Line);

/// <summary>A section in file order. Duplicate sections are kept as separate instances.</summary>
public sealed class IniSection
{
    internal IniSection(string? name, int line)
    {
        Name = name;
        Line = line;
    }

    /// <summary>The section name as written, or null for lines before the first section header.</summary>
    public string? Name { get; }

    /// <summary>1-based line of the header (0 for the implicit leading section).</summary>
    public int Line { get; }

    /// <summary>Entries in file order, including duplicate keys.</summary>
    public IReadOnlyList<IniEntry> Entries => EntryList;

    internal List<IniEntry> EntryList { get; } = [];

    /// <summary>All values of <paramref name="key"/> (case-insensitive) in file order; duplicate keys are multi-values.</summary>
    public IReadOnlyList<string> GetValues(string key)
    {
        var values = new List<string>();
        foreach (IniEntry entry in EntryList)
        {
            if (entry.Key is not null && string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                values.Add(entry.Value);
            }
        }

        return values;
    }

    /// <summary>The last value of <paramref name="key"/> (the prototype's rule), or null.</summary>
    public string? GetValue(string key)
    {
        for (int i = EntryList.Count - 1; i >= 0; i--)
        {
            IniEntry entry = EntryList[i];
            if (entry.Key is not null && string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        return null;
    }

    /// <summary>The key-less lines (e.g. <c>[accept] allunits</c>, hero names).</summary>
    public IReadOnlyList<string> BareValues()
    {
        var values = new List<string>();
        foreach (IniEntry entry in EntryList)
        {
            if (entry.Key is null)
            {
                values.Add(entry.Value);
            }
        }

        return values;
    }
}

/// <summary>
/// Tolerant reader for the original INI files (docs/formats/ini-tsv.md §2): <c>[section]</c>, <c>key = value</c> split
/// at the first <c>=</c>, bare values, <c>;</c> comments anywhere, case-insensitive names, duplicate sections and keys
/// preserved in order. Parsing never fails; irregular lines produce <see cref="Warnings"/>.
/// </summary>
public sealed class IniDocument
{
    private IniDocument(List<IniSection> sections, List<FormatWarning> warnings)
    {
        Sections = sections;
        Warnings = warnings;
    }

    /// <summary>Sections in file order. A leading implicit section (name null) exists only when it has entries.</summary>
    public IReadOnlyList<IniSection> Sections { get; }

    /// <summary>Irregularities (e.g. an unterminated section header).</summary>
    public IReadOnlyList<FormatWarning> Warnings { get; }

    /// <summary>Parses raw bytes in the edition's code page.</summary>
    public static IniDocument Parse(ReadOnlySpan<byte> data, CodePage codePage) => Parse(LegacyText.Decode(data, codePage));

    /// <summary>Parses decoded text.</summary>
    public static IniDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sections = new List<IniSection>();
        var warnings = new List<FormatWarning>();
        var current = new IniSection(null, 0);
        sections.Add(current);
        List<string> lines = TextLines.Split(text);
        for (int i = 0; i < lines.Count; i++)
        {
            int lineNumber = i + 1;
            string raw = lines[i];
            int comment = TextLines.IndexOf(raw, ';');
            ReadOnlySpan<char> line = (comment >= 0 ? raw.AsSpan(0, comment) : raw.AsSpan()).Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            if (line[0] == '[')
            {
                ReadOnlySpan<char> name;
                if (line[^1] == ']')
                {
                    name = line[1..^1];
                }
                else
                {
                    warnings.Add(new FormatWarning(lineNumber, "unterminated section header"));
                    name = line[1..];
                }

                current = new IniSection(name.Trim().ToString(), lineNumber);
                sections.Add(current);
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals >= 0)
            {
                current.EntryList.Add(new IniEntry(line[..equals].Trim().ToString(), line[(equals + 1)..].Trim().ToString(), lineNumber));
            }
            else
            {
                current.EntryList.Add(new IniEntry(null, line.ToString(), lineNumber));
            }
        }

        if (sections[0].EntryList.Count == 0)
        {
            sections.RemoveAt(0);
        }

        return new IniDocument(sections, warnings);
    }

    /// <summary>All sections named <paramref name="name"/> (case-insensitive), in file order.</summary>
    public IReadOnlyList<IniSection> FindSections(string name)
    {
        var result = new List<IniSection>();
        foreach (IniSection section in Sections)
        {
            if (section.Name is not null && string.Equals(section.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(section);
            }
        }

        return result;
    }

    /// <summary>The first section named <paramref name="name"/>, or null.</summary>
    public IniSection? FindSection(string name)
    {
        foreach (IniSection section in Sections)
        {
            if (section.Name is not null && string.Equals(section.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return section;
            }
        }

        return null;
    }

    /// <summary>Names of sections that occur more than once (case-insensitive), in order of their second occurrence.</summary>
    public IReadOnlyList<string> DuplicateSectionNames()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (IniSection section in Sections)
        {
            if (section.Name is not null && !seen.Add(section.Name) && reported.Add(section.Name))
            {
                result.Add(section.Name);
            }
        }

        return result;
    }
}
