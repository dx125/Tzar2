using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Packages;

namespace Tzar2.Content.Editing;

/// <summary>
/// Reads and writes <c>.t2camp</c> packages for the campaign editor (TDD §18.4, ADR 0121, ADR 0155) with the writers the
/// converter uses: <c>campaign.json</c> (<see cref="CampaignJson"/>), <c>maps/&lt;id&gt;.t2map</c> (<see cref="MapPackage"/>),
/// <c>assets/…</c> for resources with the same path and bytes in every map that has them (stored once, REQ-EDT-09), and
/// <c>editor.json</c> for graph positions when there are any. Deterministic; <c>Write(Read(p)) == p</c> for campaigns the
/// editor or the converter wrote.
/// </summary>
public static class CampaignPackage
{
    /// <summary>Encodes a campaign.</summary>
    /// <exception cref="ArgumentException">A map does not build (the validator reports it first).</exception>
    public static byte[] Write(CampaignDocument campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        CampaignData data = campaign.Build();
        CampaignState s = campaign.State;

        // Shared resources: the campaign's own plus every path whose bytes are identical in all maps that have it.
        var byPath = new SortedDictionary<string, List<ImmutableArray<byte>>>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ImmutableArray<byte>> a in campaign.SharedAssets)
        {
            byPath[a.Key] = [a.Value];
        }

        foreach (CampaignEntry m in s.Maps)
        {
            foreach (KeyValuePair<string, ImmutableArray<byte>> a in m.Document.State.Assets)
            {
                if (!byPath.TryGetValue(a.Key, out List<ImmutableArray<byte>>? list))
                {
                    byPath[a.Key] = list = [];
                }

                list.Add(a.Value);
            }
        }

        var shared = new SortedDictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, List<ImmutableArray<byte>>> p in byPath)
        {
            bool same = true;
            foreach (ImmutableArray<byte> bytes in p.Value)
            {
                same &= bytes.AsSpan().SequenceEqual(p.Value[0].AsSpan());
            }

            if (same)
            {
                shared[p.Key] = p.Value[0];
            }
        }

        var writer = new PackageWriter();
        PackageManifest manifest = campaign.Manifest is { } kept
            ? kept with { Name = s.Name }
            : new PackageManifest { Id = "campaign." + s.Id, Name = s.Name, Version = "1.0.0", Kind = PackageKind.Campaign, Provides = ["campaigns", "maps"] };
        writer.AddText(PackageWriter.ManifestPath, manifest.ToYaml());
        writer.Add("campaign.json", CampaignJson.Write(data));
        foreach (CampaignEntry m in s.Maps)
        {
            MapDocument doc = m.Document;
            DocumentState own = doc.State;
            var assets = own.Assets;
            foreach (KeyValuePair<string, ImmutableArray<byte>> a in own.Assets)
            {
                if (shared.ContainsKey(a.Key))
                {
                    assets = assets.Remove(a.Key);
                }
            }

            // Write the map without the shared resources, then restore its state (the document itself is not changed).
            doc.SetState(own with { Assets = assets });
            try
            {
                writer.Add(m.FileName, MapPackage.Write(doc));
            }
            finally
            {
                doc.SetState(own);
            }
        }

        foreach (KeyValuePair<string, ImmutableArray<byte>> a in shared)
        {
            writer.Add("assets/" + a.Key, a.Value.AsSpan().ToArray());
        }

        if (s.Positions.Count > 0)
        {
            writer.Add("editor.json", MapPackage.Json(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("format", MapPackage.EditorFormat);
                w.WriteStartObject("positions");
                foreach (KeyValuePair<string, (int X, int Y)> p in s.Positions)
                {
                    w.WriteStartArray(p.Key);
                    w.WriteNumberValue(p.Value.X);
                    w.WriteNumberValue(p.Value.Y);
                    w.WriteEndArray();
                }

                w.WriteEndObject();
                w.WriteEndObject();
            }));
        }

        foreach (KeyValuePair<string, ImmutableArray<byte>> e in campaign.Preserved)
        {
            if (!writer.Contains(e.Key))
            {
                writer.Add(e.Key, e.Value.AsSpan().ToArray());
            }
        }

        using var ms = new MemoryStream();
        writer.WriteTo(ms);
        return ms.ToArray();
    }

    /// <summary>One map of the campaign as a stand-alone <c>.t2map</c> (Export map): the shared resources it lacks are added.</summary>
    /// <exception cref="ArgumentException">There is no map <paramref name="id"/>.</exception>
    public static byte[] WriteMap(CampaignDocument campaign, string id)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        CampaignEntry m = campaign.Find(id) ?? throw new ArgumentException($"The campaign has no map '{id}'.", nameof(id));
        DocumentState own = m.Document.State;
        var assets = own.Assets;
        foreach (KeyValuePair<string, ImmutableArray<byte>> a in campaign.SharedAssets)
        {
            if (!assets.ContainsKey(a.Key))
            {
                assets = assets.Add(a.Key, a.Value);
            }
        }

        m.Document.SetState(own with { Assets = assets });
        try
        {
            return MapPackage.Write(m.Document);
        }
        finally
        {
            m.Document.SetState(own);
        }
    }

    /// <summary>Decodes a <c>.t2camp</c>; the maps' shared resources stay at campaign level.</summary>
    /// <exception cref="InvalidDataException">The package is malformed or not a campaign.</exception>
    public static CampaignDocument Read(byte[] package, Func<string?, TerrainRules> rules, string name = "campaign")
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(rules);
        using var stream = new MemoryStream(package, writable: false);
        using PackageReader reader = PackageReader.Open(stream, name);
        try
        {
            PackageManifest manifest = PackageManifest.Parse(reader.ReadText(PackageWriter.ManifestPath));
            if (manifest.Kind != PackageKind.Campaign)
            {
                throw new InvalidDataException($"{name}: the package is a {manifest.Kind} package, not a campaign.");
            }

            CampaignData data = CampaignJson.Read(reader.ReadAllBytes("campaign.json"));
            var maps = ImmutableList.CreateBuilder<CampaignEntry>();
            var owned = new HashSet<string>(StringComparer.Ordinal) { PackageWriter.ManifestPath, "campaign.json", "editor.json" };
            foreach (CampaignMap m in data.Maps)
            {
                owned.Add(m.FileName);
                if (m.FileName != "maps/" + m.Id + ".t2map")
                {
                    throw new InvalidDataException($"{name}: map '{m.Id}' is stored as '{m.FileName}', expected 'maps/{m.Id}.t2map'.");
                }

                MapDocument doc = MapPackage.Read(reader.ReadAllBytes(m.FileName), rules, m.FileName);
                maps.Add(new CampaignEntry(m.Id, m.MapName, m.Description, m.IsEntry, doc));
            }

            var shared = ImmutableSortedDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
            var preserved = ImmutableSortedDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
            foreach (PackageEntry e in reader.Entries)
            {
                if (e.Path.StartsWith("assets/", StringComparison.Ordinal))
                {
                    shared[e.Path["assets/".Length..]] = ImmutableArray.Create(reader.ReadAllBytes(e.Path));
                }
                else if (!owned.Contains(e.Path))
                {
                    preserved[e.Path] = ImmutableArray.Create(reader.ReadAllBytes(e.Path));
                }
            }

            var positions = ImmutableSortedDictionary.CreateBuilder<string, (int, int)>(StringComparer.Ordinal);
            if (reader.Contains("editor.json"))
            {
                using JsonDocument json = JsonDocument.Parse(reader.ReadAllBytes("editor.json"));
                if (json.RootElement.TryGetProperty("positions", out JsonElement p))
                {
                    foreach (JsonProperty pos in p.EnumerateObject())
                    {
                        positions[pos.Name] = (pos.Value[0].GetInt32(), pos.Value[1].GetInt32());
                    }
                }
            }

            return new CampaignDocument(new CampaignState(data.Id, data.Name, data.Description, maps.ToImmutable(), positions.ToImmutable()))
            {
                Manifest = manifest,
                SharedAssets = shared.ToImmutable(),
                Preserved = preserved.ToImmutable(),
            };
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException($"{name}: {e.Message}", e);
        }
    }
}
