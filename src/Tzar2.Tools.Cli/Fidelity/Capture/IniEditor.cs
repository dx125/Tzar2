using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Tzar2.Tools.Cli.Fidelity.Capture;

/// <summary>
/// Sets <c>KEY=value</c> lines in a Windows INI text (<c>tzar.ini</c>, <c>ddraw.ini</c>) while keeping every other
/// line, comment and the line endings as they are. Section and key names match case-insensitively, like
/// <c>GetPrivateProfileString</c>.
/// </summary>
internal static class IniEditor
{
    /// <summary>Applies <c>SECTION.KEY → value</c> settings.</summary>
    public static string Apply(string text, IReadOnlyDictionary<string, string> settings)
    {
        string newline = text.Contains("\r\n", StringComparison.Ordinal) || text.Length == 0 ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        foreach ((string qualified, string value) in settings)
        {
            int dot = qualified.IndexOf('.', StringComparison.Ordinal);
            if (dot <= 0 || dot == qualified.Length - 1)
            {
                throw new ArgumentException($"INI setting '{qualified}' must be SECTION.KEY.", nameof(settings));
            }

            Set(lines, qualified[..dot], qualified[(dot + 1)..], value);
        }

        var sb = new StringBuilder();
        foreach (string line in lines)
        {
            sb.Append(line).Append(newline);
        }

        return sb.ToString();
    }

    /// <summary>Reads <c>[section] key</c>, or null.</summary>
    public static string? Get(string text, string section, string key)
    {
        string? current = null;
        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line[1..^1].Trim();
                continue;
            }

            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (current is not null && eq > 0 && string.Equals(current, section, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(line[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                return line[(eq + 1)..].Trim();
            }
        }

        return null;
    }

    private static void Set(List<string> lines, string section, string key, string value)
    {
        int sectionLine = -1;
        int sectionEnd = lines.Count;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Trim();
            if (!(line.StartsWith('[') && line.EndsWith(']')))
            {
                continue;
            }

            if (sectionLine >= 0)
            {
                sectionEnd = i;
                break;
            }

            if (string.Equals(line[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase))
            {
                sectionLine = i;
            }
        }

        if (sectionLine < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length != 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            return;
        }

        for (int i = sectionLine + 1; i < sectionEnd; i++)
        {
            string line = lines[i];
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && !line.TrimStart().StartsWith(';') && string.Equals(line[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{line[..eq].TrimEnd()}={value}";
                return;
            }
        }

        // Insert after the section's last non-blank line.
        int insert = sectionEnd;
        while (insert > sectionLine + 1 && lines[insert - 1].Trim().Length == 0)
        {
            insert--;
        }

        lines.Insert(insert, $"{key}={value}");
    }
}
