using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Tzar2.Net.Relay;

namespace Tzar2.Server;

/// <summary>Structured logs as JSON lines (§19.7): one object per relay event with a UTC timestamp.</summary>
internal sealed class JsonLog(TextWriter output, Func<DateTimeOffset> clock)
{
    private readonly object _gate = new();

    /// <summary>Writes one relay event.</summary>
    public void Write(RelayLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Write(entry.Event, entry.Lobby, entry.Slot, entry.Detail);
    }

    /// <summary>Writes one event.</summary>
    public void Write(string name, string lobby, int slot, string detail)
    {
        using var stream = new MemoryStream(256);
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("ts", clock().UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            json.WriteString("event", name);
            if (lobby.Length > 0)
            {
                json.WriteString("lobby", lobby);
            }

            if (slot >= 0)
            {
                json.WriteNumber("slot", slot);
            }

            if (detail.Length > 0)
            {
                json.WriteString("detail", detail);
            }

            json.WriteEndObject();
        }

        string line = Encoding.UTF8.GetString(stream.ToArray());
        lock (_gate)
        {
            output.WriteLine(line);
            output.Flush();
        }
    }
}
