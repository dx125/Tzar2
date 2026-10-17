using System;
using System.Collections.Generic;
using System.IO;

namespace Tzar2.Content.Vfs;

/// <summary>An in-memory source (tests, generated content, embedded map packages).</summary>
public sealed class MemorySource : IContentSource
{
    private readonly SortedDictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private List<string>? _paths;

    /// <summary>Creates an empty source.</summary>
    public MemorySource(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> Paths => _paths ??= [.. _files.Keys];

    /// <summary>Adds or replaces a file (the path is normalised; the bytes are copied).</summary>
    public MemorySource Add(string path, ReadOnlySpan<byte> data)
    {
        _files[ContentPath.Normalize(path)] = data.ToArray();
        _paths = null;
        return this;
    }

    /// <inheritdoc />
    public bool Contains(string path) => _files.ContainsKey(path);

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path)
        => _files.TryGetValue(path, out byte[]? data) ? (byte[])data.Clone() : throw new FileNotFoundException($"'{path}' is not in {Name}.", path);

    /// <inheritdoc />
    public long GetSize(string path)
        => _files.TryGetValue(path, out byte[]? data) ? data.LongLength : throw new FileNotFoundException($"'{path}' is not in {Name}.", path);
}
