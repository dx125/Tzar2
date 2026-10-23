using System;
using System.Collections.Generic;
using System.Globalization;

namespace Tzar2.Client.Logic;

/// <summary>A line of the credits: the font it is drawn in (an index of <c>CREDITSFNT.INI</c>) and its text (may be empty).</summary>
/// <param name="Font">The font index.</param>
/// <param name="Text">The text.</param>
public readonly record struct CreditsLine(int Font, string Text);

/// <summary>
/// The credits screen's text (SHELL-020): the package's <c>text/local/credits.txt</c>, where a line starting with
/// <c>@N</c> switches to font N of <c>CREDITSFNT.INI [credits]</c> for it and the lines after it.
/// </summary>
public static class CreditsText
{
    /// <summary>Where the import puts the credits file.</summary>
    public const string PackagePath = "text/local/credits.txt";

    /// <summary>Splits <paramref name="text"/> into lines with their fonts (font 0 until the first switch).</summary>
    public static List<CreditsLine> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<CreditsLine>();
        int font = 0;
        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            string line = raw;
            if (line.Length >= 2 && line[0] == '@' && char.IsAsciiDigit(line[1]))
            {
                int end = 1;
                while (end < line.Length && char.IsAsciiDigit(line[end]))
                {
                    end++;
                }

                font = int.Parse(line.AsSpan(1, end - 1), NumberStyles.None, CultureInfo.InvariantCulture);
                line = line[end..];
            }

            lines.Add(new CreditsLine(font, line.TrimEnd()));
        }

        while (lines.Count > 0 && lines[^1].Text.Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>
    /// The package path of a <c>CREDITSFNT.INI</c> font (<c>LOCAL\FONTS\Arial14B_aa.fnt</c> → <c>fonts/arial14b_aa.json</c>),
    /// or null for an empty entry.
    /// </summary>
    public static string? FontAsset(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return null;
        }

        string name = entry.Trim().Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        int dot = name.LastIndexOf('.');
        return "fonts/" + (dot > 0 ? name[..dot] : name).ToLowerInvariant() + ".json";
    }
}
