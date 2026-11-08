using System;
using System.Collections.Generic;

namespace Tzar2.Game.Campaigns;

/// <summary>
/// A map's own resources (ADR 0284): the pictures, sounds and music its package holds under <c>user/</c>, which its
/// scenario names in story screens, conversations, sound sources, the music playlist and its objects' icons. Lookups
/// are case-insensitive, as the original's file names; a path is found as it is, or under <c>user/</c>. A campaign's
/// resources shared by its maps come after the map's own.
/// </summary>
public sealed class MapAssets
{
    private readonly IReadOnlyDictionary<string, byte[]> _files;
    private readonly MapAssets? _shared;

    /// <summary>Creates the resources of a map from its files by package path (<c>user/…</c>), with the campaign's shared ones.</summary>
    public MapAssets(IReadOnlyDictionary<string, byte[]> files, MapAssets? shared = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        var copy = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, byte[]> f in files)
        {
            copy[Normalize(f.Key)] = f.Value;
        }

        _files = copy;
        _shared = shared;
    }

    /// <summary>No resources.</summary>
    public static MapAssets None { get; } = new(new Dictionary<string, byte[]>());

    /// <summary>How many files the map itself holds.</summary>
    public int Count => _files.Count;

    /// <summary>The bytes of the resource a scenario names <paramref name="path"/>, or null.</summary>
    public byte[]? Find(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string key = Normalize(path);
        return _files.GetValueOrDefault(key) ?? _files.GetValueOrDefault("user/" + key) ?? _shared?.Find(path);
    }

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimStart('/');
}
