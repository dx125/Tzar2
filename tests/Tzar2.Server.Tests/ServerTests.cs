using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using Tzar2.Net.Lockstep;
using Tzar2.Net.Protocol;
using Tzar2.Net.Relay;
using Xunit;

namespace Tzar2.Server.Tests;

public sealed class ServerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tzar2-server-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Options_come_from_the_environment_then_the_arguments()
    {
        ServerOptions o = ServerOptions.Parse(["--http-port", "0", "--max-lobbies", "9"], name => name == "TZAR2_UDP_PORT" ? "5000" : name == "TZAR2_HTTP_PORT" ? "81" : null);
        Assert.Equal(5000, o.UdpPort);
        Assert.Equal(0, o.HttpPort);
        Assert.Equal(9, o.MaxLobbies);
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--udp-port", "70000"], _ => null));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--nope", "1"], _ => null));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--udp-port"], _ => null));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--http-host", "a/b"], _ => null));
        Assert.True(ServerOptions.Parse(["-h"], _ => null).Help);
    }

    [Fact]
    public void Bad_options_exit_with_the_usage()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(Program.UsageError, Program.Run(["--udp-port", "x"], output, error, _ => null, CancellationToken.None));
        Assert.Contains("Usage: Tzar2.Server", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, Program.Run(["--help"], output, error, _ => null, CancellationToken.None));
    }

    [Fact]
    public void Log_lines_are_json_objects()
    {
        var output = new StringWriter();
        var log = new JsonLog(output, () => new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        log.Write(new RelayLogEntry("lobby.create", "ABCDEF", 0, "say \"hi\"\n"));
        using JsonDocument doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("lobby.create", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("say \"hi\"\n", doc.RootElement.GetProperty("detail").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("slot").GetInt32());
        Assert.StartsWith("2026-10-18T12:00:00", doc.RootElement.GetProperty("ts").GetString(), StringComparison.Ordinal);
        Assert.Single(output.ToString().Trim().Split('\n'));
    }

    [Fact]
    public void Desync_reports_are_written_under_relay_generated_names_and_pruned()
    {
        using var dir = new DesyncDirectory(_dir, max: 2, _ => { });
        for (int i = 0; i < 3; i++)
        {
            dir.Store(new DesyncBundle("ab/../c", (ulong)i, 10 + i, 1, [0], [], [], []));
        }

        var watch = Stopwatch.StartNew();
        while (dir.Written < 3 && watch.ElapsedMilliseconds < 5_000)
        {
            Thread.Sleep(10);
        }

        dir.Dispose();
        string[] files = Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        Assert.Equal(2, files.Length);
        Assert.All(files, f => Assert.Matches("^desync-ABC-[0-9A-F]{16}-t1[0-2]\\.t2desync$", f));
        DesyncBundle back = DesyncBundle.Parse(File.ReadAllBytes(Path.Combine(_dir, files[0])));
        Assert.Equal("ab/../c", back.Lobby);
    }

    [Fact]
    public async System.Threading.Tasks.Task The_service_relays_lobbies_and_serves_the_http_api()
    {
        int httpPort = FreeTcpPort();
        var output = new StringWriter();
        var options = ServerOptions.Parse(["--udp-port", "0", "--http-port", httpPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "--http-host", "localhost", "--desync-dir", _dir], _ => null);
        using var host = new ServerHost(options, new JsonLog(TextWriter.Synchronized(output), () => DateTimeOffset.UtcNow));
        using var stop = new CancellationTokenSource();
        host.Start(stop.Token);

        using LockstepClient client = LockstepClient.Create(
            new LockstepClientOptions { Host = "127.0.0.1", Port = host.UdpPort, GameVersion = "1.0", Name = "ana" },
            new LobbyCreate("Public game", 4, true));
        // Issue #179: poll until a generous deadline instead of fixed waits (a loaded Windows runner missed a 5 s Welcome
        // and a 400 ms listing refresh); the happy path takes as long as before.
        var watch = Stopwatch.StartNew();
        var total = Stopwatch.StartNew();
        while (client.Welcome is null && watch.ElapsedMilliseconds < 30_000)
        {
            client.Poll();
            Thread.Sleep(2);
        }

        long welcomeMs = watch.ElapsedMilliseconds, listedMs = -1;
        // Issue #179: on a failure, say which phase took how long, where the client stands and what the relay logged.
        string Diagnostics() => $"welcome after {welcomeMs} ms, listed after {listedMs} ms, {total.ElapsedMilliseconds} ms in all; client {client.Phase}; relay log:\n{output}";
        Assert.True(client.Welcome is not null, Diagnostics());
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{httpPort}/") };
        Assert.Equal("ok\n", await http.GetStringAsync("healthz"));
        string listing = "[]", metrics = string.Empty;
        for (watch.Restart(); watch.ElapsedMilliseconds < 30_000; await System.Threading.Tasks.Task.Delay(50))
        {
            client.Poll(); // the client keeps its connection while the listing refreshes (every 250 ms)
            listing = await http.GetStringAsync("lobbies");
            metrics = await http.GetStringAsync("metrics");
            using JsonDocument now = JsonDocument.Parse(listing);
            if (now.RootElement.GetArrayLength() == 1 && metrics.Contains("tzar2_lobbies 1\n", StringComparison.Ordinal))
            {
                listedMs = watch.ElapsedMilliseconds;
                break;
            }
        }

        using JsonDocument lobbies = JsonDocument.Parse(listing);
        Assert.True(lobbies.RootElement.GetArrayLength() == 1, Diagnostics());
        JsonElement lobby = lobbies.RootElement.EnumerateArray().Single();
        Assert.Equal(client.Welcome!.Code, lobby.GetProperty("code").GetString());
        Assert.Equal("lobby", lobby.GetProperty("phase").GetString());
        Assert.Contains("tzar2_lobbies 1\n", metrics, StringComparison.Ordinal);
        Assert.Contains("# TYPE tzar2_desyncs_total counter", metrics, StringComparison.Ordinal);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync("nothing")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, (await http.PostAsync("lobbies", new StringContent("x"))).StatusCode);

        client.Poll();
        BrowseReply? reply = LobbyBrowser.Query("127.0.0.1", host.UdpPort);
        Assert.True(reply is { Lobbies.Length: 1 }, $"browse reply: {(reply is null ? "none" : reply.Lobbies.Length + " lobbies")}; " + Diagnostics());
        Assert.Equal(client.Welcome.Code, reply!.Lobbies[0].Code);
        Assert.Contains("\"event\":\"lobby.create\"", output.ToString(), StringComparison.Ordinal);
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>Talks to a relay started elsewhere (the CI container) when <c>TZAR2_RELAY</c> is <c>host:port</c>.</summary>
public sealed class ContainerProbe
{
    [Fact]
    public void A_running_relay_answers_a_browse_query_and_hosts_a_lobby()
    {
        string? target = Environment.GetEnvironmentVariable("TZAR2_RELAY");
        if (string.IsNullOrEmpty(target))
        {
            return; // only in the server-docker CI job
        }

        string host = target[..target.LastIndexOf(':')];
        int port = int.Parse(target[(target.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        using LockstepClient client = LockstepClient.Create(new LockstepClientOptions { Host = host, Port = port, GameVersion = "ci" }, new LobbyCreate("ci", 2, false));
        var watch = Stopwatch.StartNew();
        while (client.Welcome is null && watch.ElapsedMilliseconds < 5_000)
        {
            client.Poll();
            Thread.Sleep(2);
        }

        Assert.NotNull(client.Welcome);
        Assert.NotNull(LobbyBrowser.Query(host, port, timeoutMs: 5_000));
    }
}
