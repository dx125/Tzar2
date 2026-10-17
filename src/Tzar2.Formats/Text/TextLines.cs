using System;
using System.Collections.Generic;

namespace Tzar2.Formats.Text;

/// <summary>Splits decoded text into lines at CRLF, LF or CR (the original files use CRLF).</summary>
internal static class TextLines
{
    public static List<string> Split(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '\r' or '\n')
            {
                lines.Add(text[start..i]);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    /// <summary>Trims spaces, tabs and other white space, like the original reader's tolerance.</summary>
    public static string Trim(ReadOnlySpan<char> value) => value.Trim().ToString();

    public static bool IsBlank(string value) => value.AsSpan().Trim().IsEmpty;

    public static int IndexOf(string value, char c) => value.IndexOf(c, StringComparison.Ordinal);
}
