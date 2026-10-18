using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tzar2.Net.Protocol;
using Tzar2.Net.Relay;

namespace Tzar2.Server;

/// <summary>
/// The minimal HTTP side of the service (§19.7): <c>GET /lobbies</c> (public lobbies as JSON, for web pages and
/// community tools), <c>GET /healthz</c>, and <c>GET /metrics</c> in the Prometheus text format. Read-only: lobbies are
/// created and joined over the relay's UDP protocol. Request bodies are never read.
/// </summary>
internal sealed class HttpApi : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly RelayServer _relay;
    private readonly Func<int> _desyncReports;
    private readonly long _startedTicks = Environment.TickCount64;
    private Task? _loop;

    public HttpApi(string host, int port, RelayServer relay, Func<int> desyncReports)
    {
        _relay = relay;
        _desyncReports = desyncReports;
        _listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://{host}:{port}/"));
    }

    /// <summary>Starts serving.</summary>
    /// <exception cref="HttpListenerException">The prefix cannot be bound.</exception>
    public void Start(CancellationToken cancellation)
    {
        _listener.Start();
        _loop = Task.Run(() => Serve(cancellation), cancellation);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _listener.Close();
        _inFlight.Dispose();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The loop ends with the listener.
        }
    }

    private async Task Serve(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            // At most MaxConcurrent requests are served at once; beyond that the answer is 503 at once, so slow
            // clients cannot pile up work (review M2). Holding many idle sockets open is a job for a reverse proxy.
            if (!_inFlight.Wait(0))
            {
                Reject(context);
                continue;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    Respond(context);
                }
                catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // The client went away.
                }
                finally
                {
                    _inFlight.Release();
                }
            }, cancellation);
        }
    }

    /// <summary>Most requests served at the same time.</summary>
    public const int MaxConcurrent = 32;

    private readonly SemaphoreSlim _inFlight = new(MaxConcurrent, MaxConcurrent);

    private static void Reject(HttpListenerContext context)
    {
        try
        {
            context.Response.Headers["Retry-After"] = "1";
            Send(context.Response, 503, "text/plain", "busy\n");
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The client went away.
        }
    }

    private void Respond(HttpListenerContext context)
    {
        HttpListenerResponse response = context.Response;
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Access-Control-Allow-Origin"] = "*"; // public, read-only data for community pages
        if (context.Request.HttpMethod != "GET")
        {
            Send(response, 405, "text/plain", "method not allowed\n");
            return;
        }

        switch (context.Request.Url?.AbsolutePath)
        {
            case "/healthz":
                Send(response, 200, "text/plain", "ok\n");
                break;
            case "/metrics":
                Send(response, 200, "text/plain; version=0.0.4", Metrics());
                break;
            case "/lobbies":
                Send(response, 200, "application/json", Lobbies());
                break;
            default:
                Send(response, 404, "text/plain", "not found\n");
                break;
        }
    }

    /// <summary>The public lobbies as a JSON array.</summary>
    public string Lobbies()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartArray();
            foreach (LobbySummary l in _relay.PublicLobbies)
            {
                json.WriteStartObject();
                json.WriteString("code", l.Code);
                json.WriteString("name", l.Name);
                json.WriteNumber("players", l.Players);
                json.WriteNumber("maxPlayers", l.MaxPlayers);
                json.WriteString("phase", l.Phase.ToString().ToLowerInvariant());
                json.WriteString("version", l.GameVersion);
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The counters in the Prometheus text exposition format.</summary>
    public string Metrics()
    {
        RelayMetrics m = _relay.Metrics;
        var text = new StringBuilder();
        void Metric(string name, string type, string help, long value)
        {
            text.Append(CultureInfo.InvariantCulture, $"# HELP tzar2_{name} {help}\n# TYPE tzar2_{name} {type}\ntzar2_{name} {value}\n");
        }

        Metric("lobbies", "gauge", "Open lobbies.", m.Lobbies);
        Metric("matches", "gauge", "Running matches.", m.Matches);
        Metric("peers", "gauge", "Connected players.", m.Peers);
        Metric("udp_received_bytes_total", "counter", "UDP bytes received.", m.BytesReceived);
        Metric("udp_sent_bytes_total", "counter", "UDP bytes sent.", m.BytesSent);
        Metric("lobbies_created_total", "counter", "Lobbies created.", m.LobbiesCreated);
        Metric("matches_started_total", "counter", "Matches started.", m.MatchesStarted);
        Metric("ticks_closed_total", "counter", "Ticks closed over all matches.", m.TicksClosed);
        Metric("desyncs_total", "counter", "Desyncs detected.", m.Desyncs);
        Metric("desync_reports_written_total", "counter", "Desync reports written to disk.", _desyncReports());
        Metric("rejoins_total", "counter", "Rejoins served.", m.Rejoins);
        Metric("takeovers_total", "counter", "Dropped kingdoms taken over.", m.Takeovers);
        Metric("refused_hellos_total", "counter", "Connection requests refused.", m.RefusedHellos);
        Metric("rate_limited_total", "counter", "Requests or members stopped by a rate limit.", m.RateLimited);
        Metric("malformed_messages_total", "counter", "Malformed messages.", m.Malformed);
        Metric("late_batches_total", "counter", "Command batches that missed their tick.", m.LateBatches);
        Metric("chat_lines_total", "counter", "Chat lines delivered.", m.ChatLines);
        Metric("saves_total", "counter", "Multiplayer saves agreed.", m.Saves);
        Metric("uptime_seconds", "gauge", "Seconds since the service started.", (Environment.TickCount64 - _startedTicks) / 1000);
        return text.ToString();
    }

    private static void Send(HttpListenerResponse response, int status, string type, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = type;
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes);
        response.Close();
    }
}
