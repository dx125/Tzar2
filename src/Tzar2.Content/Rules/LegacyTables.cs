using System;
using System.Collections.Generic;
using System.Text.Json;
using Tzar2.Content.Vfs;

namespace Tzar2.Content.Rules;

/// <summary>An INI section of an imported table: name and entries in order (duplicate keys kept, docs/formats/ini-tsv.md §2).</summary>
internal sealed record LegacyIniSection(string? Name, IReadOnlyList<KeyValuePair<string?, string>> Entries)
{
    /// <summary>The first value of <paramref name="key"/> (case-insensitive), or null.</summary>
    public string? Get(string key)
    {
        foreach (KeyValuePair<string?, string> e in Entries)
        {
            if (e.Key is not null && string.Equals(e.Key.Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                return e.Value.Trim();
            }
        }

        return null;
    }
}

/// <summary>An imported INI table.</summary>
internal sealed record LegacyIni(string Path, IReadOnlyList<LegacyIniSection> Sections)
{
    /// <summary>The first section named <paramref name="name"/> (case-insensitive), or null.</summary>
    public LegacyIniSection? Section(string name)
    {
        foreach (LegacyIniSection s in Sections)
        {
            if (s.Name is not null && string.Equals(s.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }

        return null;
    }
}

/// <summary>An imported TSV table (cells trimmed; blank separator rows dropped).</summary>
internal sealed record LegacyTsv(string Path, IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>A footprint grid of a sprite's <c>.DAT</c> (docs/formats/dat.md §3).</summary>
internal sealed record LegacyFootprint(int Width, int Height, IReadOnlyList<int> Grid);

/// <summary>
/// Read access to the original tables as the importer stores them in the base package (docs/formats/t2pkg.md):
/// <c>tables/&lt;source&gt;.json</c> for INI/TSV files (as data, order and duplicates kept), the sprite index
/// (<c>manifests/sprite-index.json</c>, every <c>IMAGES.INI</c> entry with its properties) and the DAT footprints
/// (<c>manifests/footprints.json</c>). Lookups by name are case-insensitive, like the original.
/// </summary>
internal sealed class LegacyTables
{
    /// <summary>Package folder of the imported tables.</summary>
    public const string TablesRoot = "tables/data/";

    private readonly VirtualFileSystem _vfs;

    // Lookup only, never iterated (T2D004).
    private readonly Dictionary<string, LegacyIni> _iniCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LegacyIni> _objectsByClass = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _sprites = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LegacyFootprint> _footprints = new(StringComparer.OrdinalIgnoreCase);

    private LegacyTables(VirtualFileSystem vfs)
    {
        _vfs = vfs;
    }

    /// <summary>Whether the VFS holds imported original tables.</summary>
    public static bool Available(VirtualFileSystem vfs)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        return vfs.Enumerate(TablesRoot.TrimEnd('/')).Count > 0;
    }

    /// <summary>Indexes the object INIs, the sprite index and the footprints.</summary>
    public static LegacyTables Load(VirtualFileSystem vfs)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        var tables = new LegacyTables(vfs);
        foreach (string path in vfs.Enumerate(TablesRoot + "objects"))
        {
            if (!path.EndsWith(".ini.json", StringComparison.Ordinal) || path.IndexOf('/', (TablesRoot + "objects/").Length) >= 0)
            {
                continue;
            }

            LegacyIni ini = tables.ReadIni(path);
            string? cls = ini.Section("general")?.Get("class");
            if (!string.IsNullOrEmpty(cls))
            {
                tables._objectsByClass.TryAdd(cls, ini); // the first file in path order wins for duplicate classes
            }
        }

        try
        {
            tables.LoadManifests(vfs);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IndexOutOfRangeException)
        {
            throw new RulesException("legacy:manifests", 0, "malformed sprite index or footprints: " + ex.Message);
        }

        return tables;
    }

    private void LoadManifests(VirtualFileSystem vfs)
    {
        LegacyTables tables = this;
        if (vfs.Exists("manifests/sprite-index.json"))
        {
            using JsonDocument doc = JsonDocument.Parse(vfs.ReadAllBytes("manifests/sprite-index.json"));
            foreach (JsonElement entry in doc.RootElement.EnumerateArray())
            {
                string? name = entry.GetProperty("name").GetString();
                if (name is null || tables._sprites.ContainsKey(name))
                {
                    continue;
                }

                var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (entry.TryGetProperty("image", out JsonElement image) && image.GetString() is { } imagePath)
                {
                    properties["image"] = imagePath;
                }

                if (entry.TryGetProperty("properties", out JsonElement props))
                {
                    foreach (JsonProperty p in props.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0)
                        {
                            properties[p.Name] = p.Value[0].GetString() ?? string.Empty;
                        }
                    }
                }

                tables._sprites.Add(name, properties);
            }
        }

        if (vfs.Exists("manifests/footprints.json"))
        {
            using JsonDocument doc = JsonDocument.Parse(vfs.ReadAllBytes("manifests/footprints.json"));
            foreach (JsonElement entry in doc.RootElement.EnumerateArray())
            {
                string? source = entry.GetProperty("source").GetString();
                int w = entry.GetProperty("gridWidth").GetInt32();
                int h = entry.GetProperty("gridHeight").GetInt32();
                var grid = new List<int>();
                foreach (JsonElement cell in entry.GetProperty("grid").EnumerateArray())
                {
                    grid.Add(cell.GetInt32());
                }

                if (source is not null && w > 0 && h > 0 && w <= 64 && h <= 64 && grid.Count == w * h)
                {
                    tables._footprints.TryAdd(source, new LegacyFootprint(w, h, grid));
                }
            }
        }
    }

    /// <summary>The object INI whose <c>[general] class</c> is <paramref name="cls"/>, or null.</summary>
    public LegacyIni? ObjectOf(string cls) => _objectsByClass.TryGetValue(cls, out LegacyIni? ini) ? ini : null;

    /// <summary>The properties of the <c>IMAGES.INI</c> entry <paramref name="name"/> (first value per key, plus <c>image</c>), or null.</summary>
    public IReadOnlyDictionary<string, string>? Sprite(string name) => _sprites.TryGetValue(name, out IReadOnlyDictionary<string, string>? p) ? p : null;

    /// <summary>The DAT footprint grid stored for <paramref name="datSource"/> (e.g. <c>images/building/euro barrack.dat</c>), or null.</summary>
    public LegacyFootprint? Footprint(string datSource) => _footprints.TryGetValue(datSource, out LegacyFootprint? f) ? f : null;

    /// <summary>Whether a table exists (<paramref name="source"/> like <c>data/res.ini</c>).</summary>
    public bool Has(string source) => _vfs.Exists("tables/" + source + ".json");

    /// <summary>Reads an INI table by its source path (<c>data/res.ini</c>).</summary>
    /// <exception cref="RulesException">The table is missing or malformed.</exception>
    public LegacyIni Ini(string source) => ReadIni("tables/" + source + ".json");

    /// <summary>Reads a TSV table by its source path (<c>data/euro units.txt</c>).</summary>
    /// <exception cref="RulesException">The table is missing or malformed.</exception>
    public LegacyTsv Tsv(string source)
    {
        string path = "tables/" + source + ".json";
        if (!_vfs.Exists(path))
        {
            throw new RulesException("legacy:" + source, 0, "table not found in the imported package");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(_vfs.ReadAllBytes(path));
            var header = new List<string>();
            foreach (JsonElement cell in doc.RootElement.GetProperty("header").EnumerateArray())
            {
                header.Add((cell.GetString() ?? string.Empty).Trim());
            }

            var rows = new List<IReadOnlyList<string>>();
            foreach (JsonElement row in doc.RootElement.GetProperty("rows").EnumerateArray())
            {
                var cells = new List<string>();
                bool blank = true;
                foreach (JsonElement cell in row.EnumerateArray())
                {
                    string text = (cell.GetString() ?? string.Empty).Trim();
                    blank &= text.Length == 0;
                    cells.Add(text);
                }

                if (!blank)
                {
                    rows.Add(cells);
                }
            }

            return new LegacyTsv(source, header, rows);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IndexOutOfRangeException)
        {
            throw new RulesException("legacy:" + source, 0, "malformed table: " + ex.Message);
        }
    }

    private LegacyIni ReadIni(string path)
    {
        if (_iniCache.TryGetValue(path, out LegacyIni? cached))
        {
            return cached;
        }

        string source = path.StartsWith("tables/", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal)
            ? path["tables/".Length..^".json".Length]
            : path;
        if (!_vfs.Exists(path))
        {
            throw new RulesException("legacy:" + source, 0, "table not found in the imported package");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(_vfs.ReadAllBytes(path));
            var sections = new List<LegacyIniSection>();
            foreach (JsonElement section in doc.RootElement.GetProperty("sections").EnumerateArray())
            {
                JsonElement nameElement = section.GetProperty("name");
                string? name = nameElement.ValueKind == JsonValueKind.Null ? null : nameElement.GetString();
                var entries = new List<KeyValuePair<string?, string>>();
                foreach (JsonElement entry in section.GetProperty("entries").EnumerateArray())
                {
                    JsonElement key = entry[0];
                    entries.Add(new KeyValuePair<string?, string>(key.ValueKind == JsonValueKind.Null ? null : key.GetString(), entry[1].GetString() ?? string.Empty));
                }

                sections.Add(new LegacyIniSection(name, entries));
            }

            var ini = new LegacyIni(source, sections);
            _iniCache.Add(path, ini);
            return ini;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IndexOutOfRangeException)
        {
            throw new RulesException("legacy:" + source, 0, "malformed table: " + ex.Message);
        }
    }
}
