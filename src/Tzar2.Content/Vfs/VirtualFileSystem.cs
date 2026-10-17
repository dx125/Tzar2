using System;
using System.Collections.Generic;
using System.IO;

namespace Tzar2.Content.Vfs;

/// <summary>
/// The virtual file system (TDD §11.1, REQ-DATA-02): sources are mounted in order (engine defaults → base →
/// expansions → mods → map), and a later source overrides an earlier one for the same logical path.
/// Paths are normalised with <see cref="ContentPath.Normalize"/> on every call, so lookups are case-insensitive and
/// accept <c>\</c>. Enumeration is deterministic (ordinal order).
/// </summary>
public sealed class VirtualFileSystem
{
    private readonly List<IContentSource> _sources = [];

    /// <summary>Mounted sources, lowest priority first.</summary>
    public IReadOnlyList<IContentSource> Sources => _sources;

    /// <summary>Mounts <paramref name="source"/> on top of the existing ones.</summary>
    public void Mount(IContentSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _sources.Add(source);
    }

    /// <summary>Whether any source holds the path.</summary>
    public bool Exists(string path) => Resolve(path) is not null;

    /// <summary>The source that provides <paramref name="path"/> (the last mounted one holding it), or null.</summary>
    public IContentSource? Resolve(string path)
    {
        string key = ContentPath.Normalize(path);
        for (int i = _sources.Count - 1; i >= 0; i--)
        {
            if (_sources[i].Contains(key))
            {
                return _sources[i];
            }
        }

        return null;
    }

    /// <summary>Reads a file from the highest-priority source holding it.</summary>
    /// <exception cref="FileNotFoundException">No source holds the path.</exception>
    public byte[] ReadAllBytes(string path)
    {
        string key = ContentPath.Normalize(path);
        IContentSource source = Resolve(key) ?? throw new FileNotFoundException($"'{key}' is not in the virtual file system.", key);
        return source.ReadAllBytes(key);
    }

    /// <summary>All logical paths under <paramref name="prefix"/> (a folder path, or empty for all), ordinal order, no duplicates.</summary>
    public IReadOnlyList<string> Enumerate(string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(prefix);
        string folder = prefix.Length == 0 ? string.Empty : ContentPath.Normalize(prefix) + "/";
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (IContentSource source in _sources)
        {
            foreach (string path in source.Paths)
            {
                if (path.StartsWith(folder, StringComparison.Ordinal))
                {
                    set.Add(path);
                }
            }
        }

        return [.. set];
    }
}
