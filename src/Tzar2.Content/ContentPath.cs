using System;
using System.Text;

namespace Tzar2.Content;

/// <summary>
/// Logical content paths (TDD §11.1): relative, <c>/</c>-separated, no empty, <c>.</c> or <c>..</c> segments,
/// NFC-composed and lower-cased. The VFS compares paths ordinally after normalisation, so lookups are case-insensitive
/// like the original Windows game. Two deliberate limits: composition covers the letters of the supported code pages
/// (CP1250/1251/1252, built-in table, so macOS NFD names and Windows NFC names agree without ICU), and lower-casing is
/// <see cref="string.ToLowerInvariant()"/> (simple invariant mapping), not the file system's case folding, so exotic
/// pairs such as Turkish dotted/dotless I or ẞ/ß do not fold together.
/// </summary>
public static class ContentPath
{
    /// <summary>Normalises <paramref name="path"/> (backslashes to slashes, lower-case, trimmed separators).</summary>
    /// <exception cref="ArgumentException">The path is empty or contains <c>.</c>/<c>..</c> segments or control characters.</exception>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var sb = new StringBuilder(path.Length);
        foreach (string segment in Compose(path).Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            if (segment is "." or "..")
            {
                throw new ArgumentException($"Path '{path}' contains a relative segment.", nameof(path));
            }

            foreach (char c in segment)
            {
                if (char.IsControl(c))
                {
                    throw new ArgumentException($"Path '{path}' contains a control character.", nameof(path));
                }
            }

            if (sb.Length > 0)
            {
                sb.Append('/');
            }

            sb.Append(segment.ToLowerInvariant());
        }

        if (sb.Length == 0)
        {
            throw new ArgumentException("Path is empty.", nameof(path));
        }

        return sb.ToString();
    }

    /// <summary>Composes base letter + combining mark pairs of the supported code pages (NFC for that repertoire).</summary>
    public static string Compose(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!HasCombiningMark(text))
        {
            return text;
        }

        ReadOnlySpan<char> pairs = NfcTable.Pairs;
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (sb.Length > 0 && c is >= '\u0300' and <= '\u036F')
            {
                char previous = sb[^1];
                bool composed = false;
                for (int i = 0; i < pairs.Length; i += 3)
                {
                    if (pairs[i] == previous && pairs[i + 1] == c)
                    {
                        sb[^1] = pairs[i + 2];
                        composed = true;
                        break;
                    }
                }

                if (composed)
                {
                    continue;
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool HasCombiningMark(string text)
    {
        foreach (char c in text)
        {
            if (c is >= '\u0300' and <= '\u036F')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="path"/> is already in normal form.</summary>
    public static bool IsNormalized(string path)
    {
        try
        {
            return path is not null && string.Equals(Normalize(path), path, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Replaces the extension of the last segment (or appends one).</summary>
    public static string ChangeExtension(string path, string extension)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(extension);
        int slash = path.LastIndexOf('/');
        int dot = path.LastIndexOf('.');
        string stem = dot > slash ? path[..dot] : path;
        return stem + extension;
    }
}
