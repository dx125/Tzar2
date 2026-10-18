using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Tzar2.Content.Compiled.Scenario;

/// <summary>Canonical JSON of a <see cref="CampaignData"/> (the <c>campaign.json</c> part of a <c>.t2camp</c>, ADR 0121).</summary>
public static class CampaignJson
{
    /// <summary>Format version written into the document.</summary>
    public const int FormatVersion = 1;

    /// <summary>Encodes <paramref name="campaign"/> (indented, LF newlines).</summary>
    public static byte[] Write(CampaignData campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("format", FormatVersion);
            w.WriteString("id", campaign.Id);
            w.WriteString("name", campaign.Name);
            w.WriteString("description", campaign.Description);
            w.WriteStartArray("maps");
            foreach (CampaignMap m in campaign.Maps)
            {
                w.WriteStartObject();
                w.WriteString("id", m.Id);
                w.WriteString("mapName", m.MapName);
                w.WriteString("file", m.FileName);
                w.WriteString("description", m.Description);
                w.WriteBoolean("entry", m.IsEntry);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("edges");
            foreach (CampaignEdge e in campaign.Edges)
            {
                w.WriteStartObject();
                w.WriteString("from", e.FromMapId);
                w.WriteString("toName", e.ToMapName);
                if (e.ToMapId is null)
                {
                    w.WriteNull("to");
                }
                else
                {
                    w.WriteString("to", e.ToMapId);
                }

                w.WriteString("kind", e.Kind.ToString());
                w.WriteNumber("action", e.ActionIndex);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return ScenarioJson.NormaliseNewlines(buffer.WrittenSpan);
    }

    /// <summary>Decodes a document written by <see cref="Write"/>.</summary>
    /// <exception cref="FormatException">The document is malformed.</exception>
    public static CampaignData Read(ReadOnlySpan<byte> json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json.ToArray());
            JsonElement root = doc.RootElement;
            if (root.GetProperty("format").GetInt32() != FormatVersion)
            {
                throw new FormatException("unsupported campaign format.");
            }

            var maps = ImmutableArray.CreateBuilder<CampaignMap>();
            foreach (JsonElement m in root.GetProperty("maps").EnumerateArray())
            {
                maps.Add(new CampaignMap(S(m, "id"), S(m, "mapName"), S(m, "file"), S(m, "description"), m.GetProperty("entry").GetBoolean()));
            }

            var edges = ImmutableArray.CreateBuilder<CampaignEdge>();
            foreach (JsonElement e in root.GetProperty("edges").EnumerateArray())
            {
                JsonElement to = e.GetProperty("to");
                CampaignEdgeKind kind = Enum.TryParse(S(e, "kind"), out CampaignEdgeKind k) && Enum.IsDefined(k) ? k : throw new FormatException("bad edge kind.");
                edges.Add(new CampaignEdge(S(e, "from"), S(e, "toName"), to.ValueKind == JsonValueKind.Null ? null : to.GetString(), kind, e.GetProperty("action").GetInt32()));
            }

            return new CampaignData(S(root, "id"), S(root, "name"), S(root, "description"), maps.ToImmutable(), edges.ToImmutable());
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new FormatException("Invalid campaign document: " + e.Message, e);
        }

        static string S(JsonElement e, string name) => e.GetProperty(name).GetString() ?? string.Empty;
    }
}
