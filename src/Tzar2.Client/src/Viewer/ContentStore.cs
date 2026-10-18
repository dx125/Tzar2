using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Tzar2.Content.Assets;
using Tzar2.Content.Packages;
using Tzar2.Content.Terrain;
using Tzar2.Content.Vfs;

namespace Tzar2.Client.Viewer;

/// <summary>A bitmap font listed in <c>manifests/fonts.json</c>.</summary>
public sealed record FontEntry(string Asset, string Strip, string Face);

/// <summary>A sound or music file listed in <c>manifests/sounds.json</c> / <c>music.json</c>.</summary>
public sealed record AudioEntry(string Asset, string Detail);

/// <summary>
/// The mounted package (or unpacked folder) the viewer browses: the VFS plus the parsed manifests, with caches for
/// decoded sheets and images. Loading is lazy per asset; only the manifests are read up front.
/// </summary>
public sealed class ContentStore : IDisposable
{
    private readonly IContentSource _source;
    private readonly Dictionary<string, SpriteSheet?> _sheets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageAsset?> _images = new(StringComparer.Ordinal);

    private ContentStore(string path, IContentSource source)
    {
        Path = path;
        _source = source;
        Vfs = new VirtualFileSystem();
        Vfs.Mount(source);
        Manifest = source is PackageSource package ? package.ReadManifest() : null;
        Palette = PlayerPalette.Parse(Read(PlayerPalette.PackagePath));
        Index = SpriteIndex.Parse(Read(SpriteIndex.PackagePath));
        TerrainSets = Vfs.Exists(TerrainSet.PackagePath) ? TerrainSet.ParseAll(Read(TerrainSet.PackagePath)) : [];
        Fonts = ReadList("manifests/fonts.json", e => new FontEntry(Str(e, "asset"), Str(e, "strip"), Str(e, "face")));
        Sounds = ReadList("manifests/sounds.json", e => new AudioEntry(Str(e, "asset"), $"{Num(e, "durationMs")} ms, {Num(e, "sampleRate")} Hz"));
        Music = ReadList("manifests/music.json", e => new AudioEntry(Str(e, "asset"), $"SMF type {Num(e, "smfType")}, {Num(e, "tracks")} tracks"));
    }

    /// <summary>Package file or folder.</summary>
    public string Path { get; }

    /// <summary>The mounted source (the game compiles the ruleset from it).</summary>
    public IContentSource Source => _source;

    /// <summary>The virtual file system over the package.</summary>
    public VirtualFileSystem Vfs { get; }

    /// <summary><c>package.yaml</c>, when the source is a package.</summary>
    public PackageManifest? Manifest { get; }

    /// <summary>Player ramps.</summary>
    public PlayerPalette Palette { get; }

    /// <summary>All IMAGES.INI entries.</summary>
    public SpriteIndex Index { get; }

    /// <summary>Terrain sets.</summary>
    public IReadOnlyList<TerrainSet> TerrainSets { get; }

    /// <summary>Bitmap fonts.</summary>
    public IReadOnlyList<FontEntry> Fonts { get; }

    /// <summary>Sound effects.</summary>
    public IReadOnlyList<AudioEntry> Sounds { get; }

    /// <summary>MIDI music.</summary>
    public IReadOnlyList<AudioEntry> Music { get; }

    /// <summary>Opens a <c>.t2pkg</c> file or an unpacked folder.</summary>
    /// <exception cref="IOException">The path does not exist or the package is malformed.</exception>
    /// <exception cref="InvalidDataException">A manifest is malformed.</exception>
    public static ContentStore Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        IContentSource source = Directory.Exists(path)
            ? new DirectorySource(path)
            : File.Exists(path) ? PackageSource.Open(path) : throw new FileNotFoundException($"'{path}' does not exist.", path);
        try
        {
            return new ContentStore(path, source);
        }
        catch
        {
            (source as IDisposable)?.Dispose();
            throw;
        }
    }

    /// <summary>Reads a file.</summary>
    public byte[] Read(string path) => Vfs.ReadAllBytes(path);

    /// <summary>A <c>.t2img</c>, cached; null when missing or malformed.</summary>
    public ImageAsset? LoadImage(string asset)
    {
        if (!_images.TryGetValue(asset, out ImageAsset? image))
        {
            image = Vfs.Exists(asset) ? ImageAsset.Parse(Read(asset)) : null;
            _images[asset] = image;
        }

        return image;
    }

    /// <summary>
    /// The sheet of <paramref name="asset"/> as used by <paramref name="entry"/>: <c>.t2spr</c> as stored; <c>.t2img</c>
    /// cut into the entry's <c>cols × rows</c> grid, as a shadow mask for <c>_s</c>/<c>_shadow</c> entries and with the
    /// player range for <c>_c</c> overlays. Cached; null when the asset is missing.
    /// </summary>
    public SpriteSheet? LoadSheet(string asset, SpriteIndexEntry? entry)
    {
        bool shadow = entry is not null && (entry.Name.EndsWith("_s", StringComparison.Ordinal) || entry.Name.EndsWith("_shadow", StringComparison.Ordinal));
        bool overlay = entry is not null && entry.Name.EndsWith("_c", StringComparison.Ordinal);
        int cols = entry?.GetInt("cols", 1) ?? 1, rows = entry?.GetInt("rows", 1) ?? 1;
        string key = $"{asset}|{cols}|{rows}|{shadow}|{overlay}";
        if (!_sheets.TryGetValue(key, out SpriteSheet? sheet))
        {
            if (!Vfs.Exists(asset))
            {
                sheet = null;
            }
            else if (asset.EndsWith(SpriteSheet.Extension, StringComparison.Ordinal))
            {
                sheet = SpriteSheet.Parse(Read(asset));
            }
            else
            {
                ImageAsset image = LoadImage(asset) ?? throw new FileNotFoundException($"'{asset}' is missing.", asset);
                sheet = ImageSheets.FromImage(image, cols, rows, asShadowMask: shadow, playerColours: overlay);
            }

            _sheets[key] = sheet;
        }

        return sheet;
    }

    /// <summary>Drops the decoded-asset caches.</summary>
    public void ClearCaches()
    {
        _sheets.Clear();
        _images.Clear();
    }

    /// <inheritdoc/>
    public void Dispose() => (_source as IDisposable)?.Dispose();

    private IReadOnlyList<T> ReadList<T>(string path, Func<JsonElement, T> map)
    {
        if (!Vfs.Exists(path))
        {
            return [];
        }

        using JsonDocument doc = JsonDocument.Parse(Read(path));
        var list = new List<T>();
        foreach (JsonElement e in doc.RootElement.EnumerateArray())
        {
            list.Add(map(e));
        }

        return list;
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static string Num(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "?";
}
