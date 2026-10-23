using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Tzar2.Content.Terrain;

/// <summary>A terrain type of a set (<c>[terrainname]</c>).</summary>
/// <param name="Index">Terrain index (0 = water).</param>
/// <param name="Name">Display name.</param>
public readonly record struct TerrainType(int Index, string Name);

/// <summary>
/// <c>[terrainsize]</c> value <c>cols, rows, frames</c> (docs/formats/bmp.md §3): base textures are atlases of
/// <c>cols × rows</c> 32-px tiles; transition files are <c>2 × 14</c> (variants × shapes) with <c>frames</c> animation
/// frames per variant (shores) or 0.
/// </summary>
/// <param name="Columns">Tile columns (transitions: variants).</param>
/// <param name="Rows">Tile rows (transitions: 14 shapes).</param>
/// <param name="Frames">Animation frames per variant, 0 when static.</param>
public readonly record struct TerrainSize(int Columns, int Rows, int Frames)
{
    /// <summary>Parses <c>"2,14,4"</c>; null when malformed.</summary>
    public static TerrainSize? Parse(string? value)
    {
        if (value is null)
        {
            return null;
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        int[] n = new int[3];
        for (int i = 0; i < Math.Min(3, parts.Length); i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i]))
            {
                return null;
            }
        }

        return n[0] > 0 && n[1] > 0 ? new TerrainSize(n[0], n[1], n[2]) : null;
    }
}

/// <summary>One <c>[terrainfiles]</c> entry: terrain pair <c>AB</c> → bitmap, with its declared size.</summary>
/// <param name="A">First terrain (lower index).</param>
/// <param name="B">Second terrain; equal to <paramref name="A"/> for base textures.</param>
/// <param name="Asset">Package path of the <c>.t2img</c>, or null when the file is missing.</param>
/// <param name="Size">Declared <c>[terrainsize]</c>, or the size declared for another pair that uses the same file.</param>
/// <param name="SizeInherited">The size was not declared for this pair but taken from another pair with the same file.</param>
public sealed record TerrainFile(int A, int B, string? Asset, TerrainSize? Size, bool SizeInherited)
{
    /// <summary>The pair as written in the INI (<c>"12"</c>).</summary>
    public string Pair => string.Create(CultureInfo.InvariantCulture, $"{A}{B}");

    /// <summary>A base texture (<c>AA</c>).</summary>
    public bool IsBase => A == B;

    /// <summary>
    /// ⚠ UNK-10: pairs with pseudo-terrains 8/9 (cliff/hill outlines, bmp.md §3.4) or without a usable size; the viewer
    /// shows their bitmaps raw because their placement rules are not known.
    /// </summary>
    public bool IsUnresolved => A >= 8 || B >= 8 || Size is null;
}

/// <summary>
/// A terrain set from <c>manifests/terrain.json</c> (imported <c>&lt;SET&gt;.TERRAIN.INI</c>, docs/formats/bmp.md §3).
/// </summary>
public sealed class TerrainSet
{
    /// <summary>Package path of the terrain manifest.</summary>
    public const string PackagePath = "manifests/terrain.json";

    private TerrainSet(string id, string name, IReadOnlyList<TerrainType> terrains, IReadOnlyList<TerrainFile> files, string? light, string? trees, string? treeShadows, string? table)
    {
        Id = id;
        Table = table;
        Name = name;
        Terrains = terrains;
        Files = files;
        Light = light;
        Trees = trees;
        TreeShadows = treeShadows;
    }

    /// <summary>Set id (<c>grass</c>, <c>summer</c>, …).</summary>
    public string Id { get; }

    /// <summary>Display name.</summary>
    public string Name { get; }

    /// <summary>Terrain types.</summary>
    public IReadOnlyList<TerrainType> Terrains { get; }

    /// <summary>All <c>[terrainfiles]</c> entries in INI order.</summary>
    public IReadOnlyList<TerrainFile> Files { get; }

    /// <summary>Light map asset (<c>LIGHT.BMP</c>).</summary>
    public string? Light { get; }

    /// <summary>Tree strip asset.</summary>
    public string? Trees { get; }

    /// <summary>Tree shadow strip asset.</summary>
    public string? TreeShadows { get; }

    /// <summary>The imported <c>.TERRAIN.INI</c> table of the set (its other sections: minimap colours...), or null.</summary>
    public string? Table { get; }

    /// <summary>The base texture of terrain <paramref name="terrain"/>, or null.</summary>
    public TerrainFile? BaseOf(int terrain) => Find(terrain, terrain);

    /// <summary>The transition between two terrains (any order), or null.</summary>
    public TerrainFile? TransitionOf(int a, int b) => a == b ? null : Find(Math.Min(a, b), Math.Max(a, b));

    /// <summary>Display name of terrain <paramref name="index"/>.</summary>
    public string TerrainName(int index)
    {
        foreach (TerrainType t in Terrains)
        {
            if (t.Index == index)
            {
                return t.Name;
            }
        }

        return index.ToString(CultureInfo.InvariantCulture);
    }

    private TerrainFile? Find(int a, int b)
    {
        foreach (TerrainFile f in Files)
        {
            if (f.A == a && f.B == b)
            {
                return f;
            }
        }

        return null;
    }

    /// <summary>Parses <c>manifests/terrain.json</c> (one object per set).</summary>
    /// <exception cref="InvalidDataException">The JSON is malformed.</exception>
    public static IReadOnlyList<TerrainSet> ParseAll(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            var sets = new List<TerrainSet>();
            foreach (JsonElement s in doc.RootElement.EnumerateArray())
            {
                var terrains = new List<TerrainType>();
                foreach (JsonElement t in s.GetProperty("terrains").EnumerateArray())
                {
                    if (int.TryParse(t.GetProperty("index").GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                    {
                        terrains.Add(new TerrainType(index, t.GetProperty("name").GetString() ?? string.Empty));
                    }
                }

                var raw = new List<(int A, int B, string? Asset, TerrainSize? Size)>();
                foreach (JsonElement f in s.GetProperty("files").EnumerateArray())
                {
                    string pair = f.GetProperty("pair").GetString() ?? string.Empty;
                    if (pair.Length != 2 || !char.IsAsciiDigit(pair[0]) || !char.IsAsciiDigit(pair[1]))
                    {
                        continue;
                    }

                    raw.Add((pair[0] - '0', pair[1] - '0', Text(f, "asset"), TerrainSize.Parse(Text(f, "size"))));
                }

                var files = new List<TerrainFile>();
                foreach ((int a, int b, string? asset, TerrainSize? size) in raw)
                {
                    TerrainSize? resolved = size;
                    if (resolved is null && asset is not null)
                    {
                        foreach ((int _, int _, string? otherAsset, TerrainSize? otherSize) in raw)
                        {
                            if (otherSize is not null && string.Equals(otherAsset, asset, StringComparison.Ordinal))
                            {
                                resolved = otherSize;
                                break;
                            }
                        }
                    }

                    files.Add(new TerrainFile(a, b, asset, resolved, size is null && resolved is not null));
                }

                sets.Add(new TerrainSet(
                    Text(s, "set") ?? string.Empty,
                    Text(s, "name") ?? string.Empty,
                    terrains,
                    files,
                    Text(s, "light"),
                    Text(s, "trees"),
                    Text(s, "treeShadows"),
                    Text(s, "table")));
            }

            return sets;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("terrain.json: " + e.Message, e);
        }
    }

    private static string? Text(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
