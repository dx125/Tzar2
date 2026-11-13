using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Tzar2.Content.Editing;

/// <summary>
/// The editor's recently opened and saved files (ADR 0291): maps, campaigns and playtest recordings, newest first, at
/// most <see cref="Max"/>, kept in the user data as one path per line. A path appears once (compared ignoring case, as
/// Windows does); a file that no longer opens is dropped when the editor finds it gone.
/// </summary>
public sealed class RecentFiles
{
    /// <summary>The most paths kept.</summary>
    public const int Max = 12;

    /// <summary>The file of the user data that keeps the list.</summary>
    public const string FileName = "editor-recent.txt";

    private RecentFiles(ImmutableArray<string> paths) => Paths = paths;

    /// <summary>An empty list.</summary>
    public static RecentFiles Empty { get; } = new([]);

    /// <summary>The paths, newest first.</summary>
    public ImmutableArray<string> Paths { get; }

    /// <summary>Reads the list (one path per line; blank lines and repeats skipped).</summary>
    public static RecentFiles Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var paths = new List<string>();
        foreach (string line in text.Split('\n'))
        {
            string path = line.Trim();
            if (path.Length > 0 && !paths.Contains(path, StringComparer.OrdinalIgnoreCase) && paths.Count < Max)
            {
                paths.Add(path);
            }
        }

        return new RecentFiles([.. paths]);
    }

    /// <summary>The text the list is kept as.</summary>
    public string Write() => string.Concat(Paths.Select(p => p + "\n"));

    /// <summary>The list with <paramref name="path"/> first.</summary>
    public RecentFiles Add(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string trimmed = path.Trim();
        return new RecentFiles([trimmed, .. Paths.Where(p => !string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase)).Take(Max - 1)]);
    }

    /// <summary>The list without <paramref name="path"/>.</summary>
    public RecentFiles Remove(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new RecentFiles([.. Paths.Where(p => !string.Equals(p, path.Trim(), StringComparison.OrdinalIgnoreCase))]);
    }

    /// <summary>What a path opens as, by its extension.</summary>
    public static RecentKind KindOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.EndsWith(".t2camp", StringComparison.OrdinalIgnoreCase) ? RecentKind.Campaign
            : path.EndsWith(".t2play", StringComparison.OrdinalIgnoreCase) ? RecentKind.PlaytestRecording
            : RecentKind.Map;
    }
}

/// <summary>What a recent file opens as.</summary>
public enum RecentKind : byte
{
    /// <summary>A <c>.t2map</c>, in the editor.</summary>
    Map,

    /// <summary>A <c>.t2camp</c>, in the campaign editor.</summary>
    Campaign,

    /// <summary>A <c>.t2play</c>, in the replay viewer.</summary>
    PlaytestRecording,
}
