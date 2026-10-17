using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Tzar2.Content.Assets;

/// <summary>
/// One <c>IMAGES.INI</c> entry as resolved by the importer (<c>manifests/sprite-index.json</c>, docs/formats/ini-tsv.md §4):
/// the section name, the INI it came from, the asset it resolves to (null when the referenced file does not exist) and
/// every key with all its values, in file order.
/// </summary>
public sealed class SpriteIndexEntry
{
    private readonly KeyValuePair<string, string[]>[] _properties;

    /// <summary>Creates an entry.</summary>
    public SpriteIndexEntry(string name, string ini, string? asset, IReadOnlyList<KeyValuePair<string, string[]>> properties)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(ini);
        ArgumentNullException.ThrowIfNull(properties);
        Name = name;
        Ini = ini;
        Asset = asset;
        _properties = [.. properties];
    }

    /// <summary>Section name, e.g. <c>e_knight_attack</c>.</summary>
    public string Name { get; }

    /// <summary>Source INI (<c>data/images.ini</c> or a terrain set's <c>.images.ini</c>).</summary>
    public string Ini { get; }

    /// <summary>Package path of the resolved <c>.t2spr</c>/<c>.t2img</c>, or null.</summary>
    public string? Asset { get; }

    /// <summary>All keys in file order.</summary>
    public IReadOnlyList<KeyValuePair<string, string[]>> Properties => _properties;

    /// <summary>The first value of <paramref name="key"/>, or null.</summary>
    public string? Get(string key)
    {
        foreach (KeyValuePair<string, string[]> p in _properties)
        {
            if (string.Equals(p.Key, key, StringComparison.Ordinal) && p.Value.Length > 0)
            {
                return p.Value[0];
            }
        }

        return null;
    }

    /// <summary>The first value of <paramref name="key"/> as a list of integers (<c>"43,55,32,32"</c>), or null.</summary>
    public int[]? GetInts(string key)
    {
        string? value = Get(key);
        if (value is null)
        {
            return null;
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        int[] result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result[i]))
            {
                return null;
            }
        }

        return result;
    }

    /// <summary>The first integer of <paramref name="key"/>, or <paramref name="fallback"/>.</summary>
    public int GetInt(string key, int fallback)
        => GetInts(key) is { Length: > 0 } values ? values[0] : fallback;
}

/// <summary>
/// All <c>IMAGES.INI</c> entries of a package (<c>manifests/sprite-index.json</c>) with the naming conventions that link
/// sprites together: <c>&lt;name&gt;_s</c> / <c>&lt;name&gt;_shadow</c> are shadow sprites and <c>&lt;name&gt;_c</c> are
/// building player-colour overlays (docs/formats/bmp.md §2, Appendix B).
/// </summary>
public sealed class SpriteIndex
{
    /// <summary>Package path of the index.</summary>
    public const string PackagePath = "manifests/sprite-index.json";

    private static readonly string[] ShadowSuffixes = ["_s", "_shadow"];
    private readonly List<SpriteIndexEntry> _entries;
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);

    /// <summary>Creates an index; for duplicate names the first entry wins in <see cref="Find(string)"/>.</summary>
    public SpriteIndex(IEnumerable<SpriteIndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = [.. entries];
        for (int i = 0; i < _entries.Count; i++)
        {
            _byName.TryAdd(_entries[i].Name, i);
        }
    }

    /// <summary>Entries in manifest order.</summary>
    public IReadOnlyList<SpriteIndexEntry> Entries => _entries;

    /// <summary>The entry named <paramref name="name"/>, or null.</summary>
    public SpriteIndexEntry? Find(string name)
        => _byName.TryGetValue(name, out int i) ? _entries[i] : null;

    /// <summary>The shadow sprite of <paramref name="entry"/> (<c>_s</c> or <c>_shadow</c> suffix), or null.</summary>
    public SpriteIndexEntry? ShadowOf(SpriteIndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        foreach (string suffix in ShadowSuffixes)
        {
            if (Find(entry.Name + suffix) is { Asset: not null } shadow)
            {
                return shadow;
            }
        }

        return null;
    }

    /// <summary>The player-colour overlay of <paramref name="entry"/> (<c>_c</c> suffix), or null.</summary>
    public SpriteIndexEntry? OverlayOf(SpriteIndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Find(entry.Name + "_c") is { Asset: not null } overlay ? overlay : null;
    }

    /// <summary>
    /// Whether <paramref name="entry"/> is a companion (shadow or overlay) of another existing entry and is therefore shown
    /// together with it rather than on its own.
    /// </summary>
    public bool IsCompanion(SpriteIndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string name = entry.Name;
        foreach (string suffix in (ReadOnlySpan<string>)["_s", "_shadow", "_c"])
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal)
                && Find(name[..^suffix.Length]) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses <c>manifests/sprite-index.json</c>.</summary>
    /// <exception cref="InvalidDataException">The JSON is malformed.</exception>
    public static SpriteIndex Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            var entries = new List<SpriteIndexEntry>();
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                var properties = new List<KeyValuePair<string, string[]>>();
                if (e.TryGetProperty("properties", out JsonElement props))
                {
                    foreach (JsonProperty p in props.EnumerateObject())
                    {
                        var values = new List<string>();
                        foreach (JsonElement v in p.Value.EnumerateArray())
                        {
                            values.Add(v.GetString() ?? string.Empty);
                        }

                        properties.Add(new KeyValuePair<string, string[]>(p.Name, [.. values]));
                    }
                }

                entries.Add(new SpriteIndexEntry(
                    e.GetProperty("name").GetString() ?? string.Empty,
                    e.TryGetProperty("ini", out JsonElement ini) ? ini.GetString() ?? string.Empty : string.Empty,
                    e.TryGetProperty("asset", out JsonElement asset) ? asset.GetString() : null,
                    properties));
            }

            return new SpriteIndex(entries);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("sprite-index.json: " + e.Message, e);
        }
    }
}

/// <summary>
/// Frame timing of one animation from its <c>IMAGES.INI</c> keys (docs/formats/ini-tsv.md §4): per-frame <c>delay</c>
/// list when it has one value per frame, else <c>anim_time</c> split evenly, else <c>fps</c>, else
/// <see cref="DefaultDelayMs"/>. Integer milliseconds only.
/// </summary>
public sealed class SpriteAnimation
{
    /// <summary>Frame delay when the entry has no timing keys (⚠ assumption, not from the original).</summary>
    public const int DefaultDelayMs = 100;

    private readonly int[] _delays;
    private readonly long[] _ends;

    /// <summary>Creates an animation from explicit per-frame delays (each ≥ 1 ms).</summary>
    public SpriteAnimation(IReadOnlyList<int> delaysMs, string source)
    {
        ArgumentNullException.ThrowIfNull(delaysMs);
        if (delaysMs.Count == 0)
        {
            throw new ArgumentException("An animation needs at least one frame.", nameof(delaysMs));
        }

        _delays = new int[delaysMs.Count];
        _ends = new long[delaysMs.Count];
        long t = 0;
        for (int i = 0; i < _delays.Length; i++)
        {
            _delays[i] = Math.Max(1, delaysMs[i]);
            t += _delays[i];
            _ends[i] = t;
        }

        Source = source;
    }

    /// <summary>Number of frames.</summary>
    public int Frames => _delays.Length;

    /// <summary>Per-frame delays in ms.</summary>
    public IReadOnlyList<int> DelaysMs => _delays;

    /// <summary>Length of one cycle in ms.</summary>
    public long CycleMs => _ends[^1];

    /// <summary>Which key the timing came from (<c>delay</c>, <c>anim_time</c>, <c>fps</c> or <c>default</c>).</summary>
    public string Source { get; }

    /// <summary>The frame shown <paramref name="timeMs"/> after the start of a looping animation.</summary>
    public int FrameAt(long timeMs)
    {
        long t = ((timeMs % CycleMs) + CycleMs) % CycleMs;
        int lo = 0, hi = _ends.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (t < _ends[mid])
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }

    /// <summary>Timing for <paramref name="frames"/> frames from <paramref name="entry"/>'s keys (or the default).</summary>
    public static SpriteAnimation For(SpriteIndexEntry? entry, int frames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        if (entry?.GetInts("delay") is { } delays && delays.Length == frames)
        {
            return new SpriteAnimation(delays, "delay");
        }

        int animTime = entry?.GetInt("anim_time", 0) ?? 0;
        if (animTime <= 0)
        {
            animTime = entry?.GetInt("anime_time", 0) ?? 0;
        }

        if (animTime > 0)
        {
            // Spread the remainder over the first frames so the cycle is exactly anim_time.
            int[] even = new int[frames];
            for (int i = 0; i < frames; i++)
            {
                even[i] = (animTime / frames) + (i < animTime % frames ? 1 : 0);
            }

            return new SpriteAnimation(even, "anim_time");
        }

        int fps = entry?.GetInt("fps", 0) ?? 0;
        int delay = fps > 0 ? Math.Max(1, 1000 / fps) : DefaultDelayMs;
        int[] constant = new int[frames];
        Array.Fill(constant, delay);
        return new SpriteAnimation(constant, fps > 0 ? "fps" : "default");
    }
}
