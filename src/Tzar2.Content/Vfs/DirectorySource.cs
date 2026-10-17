using System;
using System.Collections.Generic;
using System.IO;

namespace Tzar2.Content.Vfs;

/// <summary>
/// A read-only folder mounted case-insensitively (TDD REQ-DATA-03: legacy loose-file layouts, unpacked mods).
/// Files are indexed once at construction. When several files differ only in case, the ordinally smallest relative
/// path wins and the others are listed in <see cref="CaseCollisions"/>.
/// </summary>
public sealed class DirectorySource : IContentSource
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly List<string> _paths;

    /// <summary>Indexes <paramref name="root"/> recursively.</summary>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    public DirectorySource(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Folder '{root}' does not exist.");
        }

        Root = Path.GetFullPath(root);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.None };
        var relatives = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Root, "*", options))
        {
            relatives.Add(Path.GetRelativePath(Root, file).Replace('\\', '/'));
        }

        relatives.Sort(StringComparer.Ordinal);
        var collisions = new List<string>();
        foreach (string relative in relatives)
        {
            string key;
            try
            {
                key = ContentPath.Normalize(relative);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!_files.TryAdd(key, relative))
            {
                collisions.Add(relative);
            }
        }

        _paths = [.. _files.Keys];
        _paths.Sort(StringComparer.Ordinal);
        CaseCollisions = collisions;
    }

    /// <summary>The absolute folder path.</summary>
    public string Root { get; }

    /// <inheritdoc/>
    public string Name => Root;

    /// <inheritdoc/>
    public IReadOnlyList<string> Paths => _paths;

    /// <summary>Files hidden because another file differs from them only in case.</summary>
    public IReadOnlyList<string> CaseCollisions { get; }

    /// <inheritdoc/>
    public bool Contains(string path) => _files.ContainsKey(path);

    /// <summary>The on-disk relative path (original case) of a normalised path.</summary>
    public string GetRelativePath(string path)
        => _files.TryGetValue(path, out string? relative) ? relative : throw new FileNotFoundException($"'{path}' is not in {Root}.", path);

    /// <summary>The absolute file path of a normalised path.</summary>
    public string GetFullPath(string path) => Path.Combine(Root, GetRelativePath(path));

    /// <inheritdoc/>
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(GetFullPath(path));

    /// <inheritdoc/>
    public long GetSize(string path) => new FileInfo(GetFullPath(path)).Length;
}
