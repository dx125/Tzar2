using System;
using System.Text;

namespace Tzar2.Content;

/// <summary>
/// Logical content paths (TDD §11.1): relative, <c>/</c>-separated, lower-case (invariant), no empty, <c>.</c> or
/// <c>..</c> segments. The VFS compares paths ordinally after normalisation, so lookups are case-insensitive like the
/// original Windows game.
/// </summary>
public static class ContentPath
{
    /// <summary>Normalises <paramref name="path"/> (backslashes to slashes, lower-case, trimmed separators).</summary>
    /// <exception cref="ArgumentException">The path is empty or contains <c>.</c>/<c>..</c> segments or control characters.</exception>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var sb = new StringBuilder(path.Length);
        foreach (string segment in path.Replace('\\', '/').Split('/'))
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
