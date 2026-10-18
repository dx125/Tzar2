using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Maps;
using Tzar2.Content.Packages;

namespace Tzar2.Content.Editing;

/// <summary>
/// Reads and writes <c>.t2map</c> packages for the editor (TDD §18.4, ADR 0121, ADR 0151) with the package writers the
/// converter uses: <see cref="PackageWriter"/> (sorted, stored, fixed-timestamp ZIP), <see cref="MapBinary"/> and
/// <see cref="ScenarioJson"/>. Saving is deterministic, and <c>Write(Read(p)) == p</c> byte for byte for every package
/// the editor or the converter wrote: entries the editor does not own (provenance, conversion report, placeholders,
/// strings) are kept verbatim, and <c>editor.json</c> is only written when it holds something.
/// </summary>
/// <remarks>
/// Owned entries: <c>package.yaml</c>, <c>map.bin</c>, <c>scenario.json</c>, <c>decorations.json</c>,
/// <c>editor.json</c> (object ids when they differ from index + 1, named areas, object groups, source language when it
/// differs from the provenance's) and <c>assets/…</c> (user resources).
/// </remarks>
public static class MapPackage
{
    /// <summary>Format version of <c>editor.json</c>.</summary>
    public const int EditorFormat = 1;

    /// <summary>Largest package the editor opens (1 GiB, the reader's entry limit).</summary>
    public const long MaxPackageBytes = PackageReader.MaxEntrySize;

    private static readonly JsonWriterOptions Indented = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Whether <paramref name="path"/> (an entry path) is written by the editor rather than kept verbatim.</summary>
    public static bool IsOwned(string path)
        => path is PackageWriter.ManifestPath or "map.bin" or "scenario.json" or "decorations.json" or "editor.json"
            || path.StartsWith("assets/", StringComparison.Ordinal);

    /// <summary>Encodes <paramref name="document"/>.</summary>
    /// <exception cref="ArgumentException">The document does not build (a reference the validator reports as an error).</exception>
    public static byte[] Write(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        DocumentState s = document.State;
        MapData map = document.BuildMap();
        ScenarioData scenario = document.BuildScenario();
        var writer = new PackageWriter();
        PackageManifest manifest = document.Manifest is { } kept
            ? kept with { Name = scenario.Properties.Title }
            : new PackageManifest
            {
                Id = "map." + Slug(s.Name),
                Name = scenario.Properties.Title,
                Version = "1.0.0",
                Kind = PackageKind.Map,
                Provides = ["maps"],
            };
        writer.AddText(PackageWriter.ManifestPath, manifest.ToYaml());
        writer.Add("map.bin", MapBinary.Write(map));
        writer.Add("scenario.json", ScenarioJson.Write(scenario));
        writer.Add("decorations.json", Json(w =>
        {
            w.WriteStartArray();
            foreach (MapDecoration d in s.Decorations)
            {
                w.WriteStartObject();
                w.WriteString("class", d.Class);
                w.WriteString("image", d.Image);
                w.WriteNumber("x", d.TileX);
                w.WriteNumber("y", d.TileY);
                w.WriteNumber("px", d.PixelX);
                w.WriteNumber("py", d.PixelY);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }));
        if (EditorJson(document) is { } editor)
        {
            writer.Add("editor.json", editor);
        }

        foreach (KeyValuePair<string, ImmutableArray<byte>> asset in s.Assets)
        {
            writer.Add("assets/" + asset.Key, asset.Value.AsSpan().ToArray());
        }

        foreach (KeyValuePair<string, ImmutableArray<byte>> entry in document.Preserved)
        {
            if (!writer.Contains(entry.Key))
            {
                writer.Add(entry.Key, entry.Value.AsSpan().ToArray());
            }
        }

        using var ms = new MemoryStream();
        writer.WriteTo(ms);
        return ms.ToArray();
    }

    /// <summary>Decodes a <c>.t2map</c>.</summary>
    /// <param name="package">The package bytes.</param>
    /// <param name="rules">Painting rules for the map's terrain set id (null = no set).</param>
    /// <param name="name">Display name for errors.</param>
    /// <exception cref="InvalidDataException">The package is malformed or not a map.</exception>
    public static MapDocument Read(byte[] package, Func<string?, TerrainRules> rules, string name = "map")
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(rules);
        using var stream = new MemoryStream(package, writable: false);
        using PackageReader reader = PackageReader.Open(stream, name);
        return Read(reader, rules);
    }

    /// <summary>Decodes a <c>.t2map</c> from an open reader.</summary>
    /// <exception cref="InvalidDataException">The package is malformed or not a map.</exception>
    public static MapDocument Read(PackageReader reader, Func<string?, TerrainRules> rules)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(rules);
        try
        {
            PackageManifest manifest = PackageManifest.Parse(reader.ReadText(PackageWriter.ManifestPath));
            if (manifest.Kind != PackageKind.Map)
            {
                throw new InvalidDataException($"{reader.Name}: the package is a {manifest.Kind} package, not a map.");
            }

            MapData map = MapBinary.Read(reader.ReadAllBytes("map.bin"));
            ScenarioData? scenario = reader.Contains("scenario.json") ? ScenarioJson.Read(reader.ReadAllBytes("scenario.json")) : null;
            JsonDocument? editor = reader.Contains("editor.json") ? JsonDocument.Parse(reader.ReadAllBytes("editor.json")) : null;
            using (editor)
            {
                JsonElement? root = editor?.RootElement;
                if (root is { } r && r.GetProperty("format").GetInt32() != EditorFormat)
                {
                    throw new InvalidDataException($"{reader.Name}: unsupported editor.json format.");
                }

                int[]? ids = root is { } e && e.TryGetProperty("objectIds", out JsonElement idList) ? Ints(idList) : null;
                MapDocument doc = MapDocument.FromCompiled(map, scenario, rules(map.TerrainSet), ids);
                doc.Manifest = manifest;
                var preserved = ImmutableSortedDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
                var assets = ImmutableSortedDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
                foreach (PackageEntry entry in reader.Entries)
                {
                    if (entry.Path.StartsWith("assets/", StringComparison.Ordinal))
                    {
                        assets[entry.Path["assets/".Length..]] = ImmutableArray.Create(reader.ReadAllBytes(entry.Path));
                    }
                    else if (!IsOwned(entry.Path))
                    {
                        preserved[entry.Path] = ImmutableArray.Create(reader.ReadAllBytes(entry.Path));
                    }
                }

                doc.Preserved = preserved.ToImmutable();
                var decorations = ImmutableList.CreateBuilder<MapDecoration>();
                if (reader.Contains("decorations.json"))
                {
                    using JsonDocument json = JsonDocument.Parse(reader.ReadAllBytes("decorations.json"));
                    foreach (JsonElement d in json.RootElement.EnumerateArray())
                    {
                        decorations.Add(new MapDecoration(
                            d.GetProperty("class").GetString() ?? string.Empty,
                            d.GetProperty("image").GetString() ?? string.Empty,
                            d.GetProperty("x").GetInt32(),
                            d.GetProperty("y").GetInt32(),
                            d.GetProperty("px").GetInt32(),
                            d.GetProperty("py").GetInt32()));
                    }
                }

                EditorExtras extras = EditorExtras.Default with { Language = DefaultLanguage(doc) };
                if (root is { } x)
                {
                    var areas = new List<NamedArea>();
                    if (x.TryGetProperty("areas", out JsonElement areaList))
                    {
                        foreach (JsonElement a in areaList.EnumerateArray())
                        {
                            areas.Add(new NamedArea(
                                a.GetProperty("name").GetString() ?? string.Empty,
                                new RectArea(a.GetProperty("left").GetInt32(), a.GetProperty("top").GetInt32(), a.GetProperty("right").GetInt32(), a.GetProperty("bottom").GetInt32())));
                        }
                    }

                    var groups = new List<ObjectGroup>();
                    if (x.TryGetProperty("groups", out JsonElement groupList))
                    {
                        foreach (JsonElement g in groupList.EnumerateArray())
                        {
                            groups.Add(new ObjectGroup(g.GetProperty("name").GetString() ?? string.Empty, Ints(g.GetProperty("objects"))));
                        }
                    }

                    extras = new EditorExtras(
                        EquatableArray<NamedArea>.From(areas),
                        EquatableArray<ObjectGroup>.From(groups),
                        x.TryGetProperty("language", out JsonElement lang) ? lang.GetString() ?? extras.Language : extras.Language);
                }

                doc.SetState(doc.State with { Decorations = decorations.ToImmutable(), Assets = assets.ToImmutable(), Extras = extras });
                return doc;
            }
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException($"{reader.Name}: {e.Message}", e);
        }
    }

    /// <summary>A package id fragment: lower-case letters, digits and dashes.</summary>
    public static string Slug(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sb = new StringBuilder();
        foreach (char c in name.ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        string slug = sb.ToString().Trim('-');
        return slug.Length > 0 ? slug : "untitled";
    }

    // The language a map's texts are in when editor.json does not say: the converter's provenance, else English.
    private static string DefaultLanguage(MapDocument doc)
    {
        if (doc.Preserved.TryGetValue("provenance.json", out ImmutableArray<byte> bytes))
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(bytes.AsSpan().ToArray());
                if (json.RootElement.TryGetProperty("language", out JsonElement lang) && lang.GetString() is { Length: > 0 } l)
                {
                    return l;
                }
            }
            catch (JsonException)
            {
            }
        }

        return EditorExtras.Default.Language;
    }

    private static byte[]? EditorJson(MapDocument document)
    {
        DocumentState s = document.State;
        bool defaultIds = true;
        for (int i = 0; i < s.Objects.Count && defaultIds; i++)
        {
            defaultIds = s.Objects[i].Id == i + 1;
        }

        string language = DefaultLanguage(document);
        if (defaultIds && s.Extras.Areas.Length == 0 && s.Extras.Groups.Length == 0 && s.Extras.Language == language)
        {
            return null;
        }

        return Json(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("format", EditorFormat);
            if (!defaultIds)
            {
                w.WriteStartArray("objectIds");
                foreach (EditorObject o in s.Objects)
                {
                    w.WriteNumberValue(o.Id);
                }

                w.WriteEndArray();
            }

            w.WriteStartArray("areas");
            foreach (NamedArea a in s.Extras.Areas)
            {
                w.WriteStartObject();
                w.WriteString("name", a.Name);
                w.WriteNumber("left", a.Area.Left);
                w.WriteNumber("top", a.Area.Top);
                w.WriteNumber("right", a.Area.Right);
                w.WriteNumber("bottom", a.Area.Bottom);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("groups");
            foreach (ObjectGroup g in s.Extras.Groups)
            {
                w.WriteStartObject();
                w.WriteString("name", g.Name);
                w.WriteStartArray("objects");
                foreach (int id in g.ObjectIds)
                {
                    w.WriteNumberValue(id);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteString("language", s.Extras.Language);
            w.WriteEndObject();
        });
    }

    private static int[] Ints(JsonElement list)
    {
        var values = new List<int>();
        foreach (JsonElement v in list.EnumerateArray())
        {
            values.Add(v.GetInt32());
        }

        return values.ToArray();
    }

    /// <summary>Indented JSON with LF newlines and a final LF (the converter's formatting, so bytes match).</summary>
    internal static byte[] Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, Indented))
        {
            write(w);
        }

        return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(buffer.WrittenSpan).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }
}
