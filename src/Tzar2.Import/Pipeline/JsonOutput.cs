using System;
using System.IO;
using System.Text.Json;

namespace Tzar2.Import.Pipeline;

/// <summary>
/// Deterministic JSON for manifests: <see cref="Utf8JsonWriter"/> (no reflection, AOT-safe), compact, caller-defined
/// key order, so the bytes are identical on every OS (indented output would use the platform's newline).
/// </summary>
internal static class JsonOutput
{
    public static byte[] Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            body(writer);
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    public static void IntArray(Utf8JsonWriter w, string name, params int[] values)
    {
        w.WriteStartArray(name);
        foreach (int v in values)
        {
            w.WriteNumberValue(v);
        }

        w.WriteEndArray();
    }
}
