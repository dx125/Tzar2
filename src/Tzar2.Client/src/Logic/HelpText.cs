using System;
using System.Collections.Generic;

namespace Tzar2.Client.Logic;

/// <summary>
/// The files of the online help as the import stores them (HUD-020): <c>HELP/ITEMS.TXT</c>, the topics, in the INI form
/// of the original (sections, <c>key = value</c>, <c>;</c> comments); <c>HELP/HK.TXT</c>, the hot keys, where
/// <c>@1</c> starts the highlighted part of a line and <c>@2</c> ends it; <c>HELP/HELP_STR.INI</c>, the labels.
/// </summary>
public static class HelpText
{
    /// <summary>Where the import puts ITEMS.TXT.</summary>
    public const string ItemsPath = "text/help/items.txt";

    /// <summary>Where the import puts HK.TXT.</summary>
    public const string HotKeysPath = "text/help/hk.txt";

    /// <summary>Where the import puts HELP_STR.INI.</summary>
    public const string LabelsPath = "tables/help/help_str.ini.json";

    /// <summary>The sections of <paramref name="text"/> in file order, each with its keys (the first value of a key wins).</summary>
    public static List<(string Name, IReadOnlyDictionary<string, string> Keys)> Sections(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sections = new List<(string, IReadOnlyDictionary<string, string>)>();
        Dictionary<string, string>? keys = null;
        foreach (string raw in Lines(text))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == ';')
            {
                continue;
            }

            if (line[0] == '[' && line.IndexOf(']', StringComparison.Ordinal) is > 1 and var end)
            {
                keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections.Add((line[1..end].Trim(), keys));
                continue;
            }

            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (keys is not null && eq > 0)
            {
                keys.TryAdd(line[..eq].Trim(), line[(eq + 1)..].Trim());
            }
        }

        return sections;
    }

    /// <summary>The lines of the hot keys page, without the empty ones between them.</summary>
    public static List<string> HotKeyLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        foreach (string line in Lines(text))
        {
            if (line.Trim().Length > 0)
            {
                lines.Add(line.TrimEnd());
            }
        }

        return lines;
    }

    /// <summary>The parts of a hot keys line: the highlighted ones (after <c>@1</c>) and the plain ones.</summary>
    public static List<(bool Highlight, string Text)> Parts(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var parts = new List<(bool, string)>();
        bool highlight = false;
        int start = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '@' && i + 1 < line.Length && line[i + 1] is '1' or '2')
            {
                if (i > start)
                {
                    parts.Add((highlight, line[start..i]));
                }

                highlight = line[i + 1] == '1';
                start = i + 2;
                i++;
            }
        }

        if (start < line.Length)
        {
            parts.Add((highlight, line[start..]));
        }

        return parts;
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
}
