using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Maps;
using Tzar2.Content.Packages;

namespace Tzar2.Import.Maps;

/// <summary>
/// Writes converted maps and campaigns as <c>.t2map</c> / <c>.t2camp</c> packages (TDD §18.4, ADR 0121). The bytes
/// depend only on the conversion (sorted, stored ZIP entries via <see cref="PackageWriter"/>; JSON with fixed key order
/// and LF), so the same source and converter version give a byte-identical package (REQ-EDT-05).
/// </summary>
/// <remarks>
/// <c>.t2map</c>: <c>package.yaml</c>, <c>map.bin</c> (<see cref="MapBinary"/>), <c>scenario.json</c>
/// (<see cref="ScenarioJson"/>), <c>decorations.json</c>, <c>legacy.json</c> (placeholders), <c>strings/&lt;lang&gt;.po</c>,
/// <c>assets/…</c>, <c>provenance.json</c>, <c>conversion-report.json</c>.
/// <c>.t2camp</c>: <c>package.yaml</c>, <c>campaign.json</c> (<see cref="CampaignJson"/>), <c>maps/&lt;id&gt;.t2map</c>,
/// <c>assets/…</c> shared by several maps with identical bytes (deduplicated, REQ-EDT-09), <c>provenance.json</c>,
/// <c>conversion-report.json</c>.
/// </remarks>
public static class LegacyPackageWriter
{
    /// <summary>Writes a converted map, or returns null when the conversion produced no map.</summary>
    /// <param name="conversion">The conversion.</param>
    /// <param name="id">Package id suffix (e.g. the map's slug).</param>
    public static byte[]? WriteMap(MapConversion conversion, string id) => WriteMap(conversion, id, null);

    /// <summary>Writes a converted campaign, or returns null when the campaign graph could not be built.</summary>
    public static byte[]? WriteCampaign(CampaignConversion conversion)
    {
        ArgumentNullException.ThrowIfNull(conversion);
        if (conversion.Campaign is not CampaignData campaign)
        {
            return null;
        }

        // Assets with the same path and bytes in every map that has them are stored once at campaign level.
        var byPath = new SortedDictionary<string, List<byte[]>>(StringComparer.Ordinal);
        foreach (MapConversion m in conversion.Maps)
        {
            foreach (UserResource r in m.Resources)
            {
                if (!byPath.TryGetValue(r.Path, out List<byte[]>? list))
                {
                    byPath[r.Path] = list = [];
                }

                list.Add(r.Data);
            }
        }

        var shared = new HashSet<string>(StringComparer.Ordinal);
        long saved = 0;
        foreach (KeyValuePair<string, List<byte[]>> p in byPath)
        {
            if (p.Value.All(d => d.AsSpan().SequenceEqual(p.Value[0])))
            {
                shared.Add(p.Key);
                saved += (long)p.Value[0].Length * (p.Value.Count - 1);
            }
        }

        conversion.Report.Count("sharedAssets", shared.Count);
        conversion.Report.Count("dedupKilobytes", (int)Math.Min(int.MaxValue, saved / 1024));

        var writer = new PackageWriter();
        var manifest = new PackageManifest
        {
            Id = "campaign." + campaign.Id,
            Name = campaign.Name,
            Version = "1.0.0",
            Kind = PackageKind.Campaign,
            Provides = ["campaigns", "maps"],
        };
        writer.AddText(PackageWriter.ManifestPath, manifest.ToYaml());
        writer.Add("campaign.json", CampaignJson.Write(campaign));
        for (int i = 0; i < campaign.Maps.Length; i++)
        {
            CampaignMap map = campaign.Maps[i];
            byte[]? package = WriteMap(conversion.Maps[i], map.Id, shared);
            if (package is not null)
            {
                writer.Add(map.FileName, package);
            }
        }

        foreach (string path in shared)
        {
            writer.Add("assets/" + path, byPath[path][0]);
        }

        writer.Add("provenance.json", Provenance(conversion.Report));
        writer.Add("conversion-report.json", conversion.Report.ToJson());
        return Zip(writer);
    }

    private static byte[]? WriteMap(MapConversion conversion, string id, HashSet<string>? sharedAssets)
    {
        ArgumentNullException.ThrowIfNull(conversion);
        if (conversion.Map is null || conversion.Scenario is null)
        {
            return null;
        }

        var writer = new PackageWriter();
        var manifest = new PackageManifest
        {
            Id = "map." + LegacyCampaignConverter.Slug(id),
            Name = conversion.Scenario.Properties.Title,
            Version = "1.0.0",
            Kind = PackageKind.Map,
            Provides = ["maps"],
        };
        writer.AddText(PackageWriter.ManifestPath, manifest.ToYaml());
        writer.Add("map.bin", MapBinary.Write(conversion.Map));
        writer.Add("scenario.json", ScenarioJson.Write(conversion.Scenario));
        writer.Add("decorations.json", Json(w =>
        {
            w.WriteStartArray();
            foreach (Decoration d in conversion.Decorations)
            {
                w.WriteStartObject();
                w.WriteString("class", d.ClassName);
                w.WriteString("image", d.Image);
                w.WriteNumber("x", d.TileX);
                w.WriteNumber("y", d.TileY);
                w.WriteNumber("px", d.PixelX);
                w.WriteNumber("py", d.PixelY);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }));
        writer.Add("legacy.json", Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("mapName", conversion.MapName);
            w.WriteStartArray("placeholders");
            foreach (Placeholder p in conversion.Placeholders)
            {
                w.WriteStartObject();
                w.WriteNumber("index", p.LegacyIndex);
                w.WriteString("class", p.ClassName);
                w.WriteNumber("race", p.Race);
                w.WriteNumber("owner", p.Owner);
                w.WriteNumber("x", p.TileX);
                w.WriteNumber("y", p.TileY);
                w.WriteNumber("id", p.Id);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }));
        writer.AddText("strings/" + conversion.Report.Language + ".po", Po(conversion));
        foreach (UserResource r in conversion.Resources)
        {
            if (sharedAssets is null || !sharedAssets.Contains(r.Path))
            {
                writer.Add("assets/" + r.Path, r.Data);
            }
        }

        writer.Add("provenance.json", Provenance(conversion.Report));
        writer.Add("conversion-report.json", conversion.Report.ToJson());
        return Zip(writer);
    }

    private static byte[] Zip(PackageWriter writer)
    {
        using var ms = new MemoryStream();
        writer.WriteTo(ms);
        return ms.ToArray();
    }

    private static byte[] Provenance(ConversionReport report) => Json(w =>
    {
        w.WriteStartObject();
        w.WriteString("source", report.Source);
        w.WriteNumber("sourceSize", report.SourceSize);
        w.WriteString("sourceMd5", report.SourceMd5);
        w.WriteNumber("codePage", report.CodePage);
        w.WriteString("language", report.Language);
        w.WriteString("converterVersion", ConversionReport.ConverterVersion);
        w.WriteEndObject();
    });

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            write(w);
        }

        return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(buffer.WrittenSpan).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    // gettext catalogue of the map's texts: msgctxt = stable context, msgid = source text, msgstr empty (REQ-EDT-07).
    private static string Po(MapConversion conversion)
    {
        var sb = new StringBuilder();
        sb.Append("msgid \"\"\nmsgstr \"\"\n\"Content-Type: text/plain; charset=UTF-8\\n\"\n\"Language: ")
            .Append(conversion.Report.Language).Append("\\n\"\n");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapString s in conversion.Strings)
        {
            if (!seen.Add(s.Context))
            {
                continue;
            }

            sb.Append("\nmsgctxt ").Append(Quote(s.Context)).Append("\nmsgid ").Append(Quote(s.Text)).Append("\nmsgstr \"\"\n");
        }

        return sb.ToString();
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.Append('"').ToString();
    }
}
