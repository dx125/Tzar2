using System;
using System.Collections.Generic;
using System.IO;
using Tzar2.Content.Packages;

namespace Tzar2.Content.Vfs;

/// <summary>A mounted <c>.t2pkg</c> package. Entry paths are normalised; non-normalisable entries are skipped.</summary>
public sealed class PackageSource : IContentSource, IDisposable
{
    private readonly PackageReader _reader;
    private readonly Dictionary<string, PackageEntry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _paths;

    /// <summary>Wraps an open package. The source takes ownership of <paramref name="reader"/>.</summary>
    public PackageSource(PackageReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        foreach (PackageEntry entry in reader.Entries)
        {
            if (entry.Path.EndsWith('/') || !ContentPath.IsNormalized(entry.Path))
            {
                continue;
            }

            _entries.TryAdd(entry.Path, entry);
        }

        _paths = [.. _entries.Keys];
        _paths.Sort(StringComparer.Ordinal);
    }

    /// <summary>Opens a package file.</summary>
    public static PackageSource Open(string path) => new(PackageReader.Open(path));

    /// <inheritdoc/>
    public string Name => _reader.Name;

    /// <inheritdoc/>
    public IReadOnlyList<string> Paths => _paths;

    /// <summary>The package's manifest, or null when <c>package.yaml</c> is missing.</summary>
    public PackageManifest? ReadManifest()
        => Contains(PackageWriter.ManifestPath) ? PackageManifest.Parse(_reader.ReadText(PackageWriter.ManifestPath)) : null;

    /// <inheritdoc/>
    public bool Contains(string path) => _entries.ContainsKey(path);

    /// <inheritdoc/>
    public byte[] ReadAllBytes(string path)
        => _entries.ContainsKey(path) ? _reader.ReadAllBytes(path) : throw new FileNotFoundException($"'{path}' is not in {Name}.", path);

    /// <inheritdoc/>
    public long GetSize(string path)
        => _entries.TryGetValue(path, out PackageEntry? entry) ? entry.Size : throw new FileNotFoundException($"'{path}' is not in {Name}.", path);

    /// <inheritdoc/>
    public void Dispose() => _reader.Dispose();
}
