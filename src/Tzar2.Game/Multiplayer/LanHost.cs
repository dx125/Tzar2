using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using Tzar2.Net.Lan;
using Tzar2.Net.Lockstep;
using Tzar2.Net.Protocol;
using Tzar2.Net.Relay;

namespace Tzar2.Game.Multiplayer;

/// <summary>A lobby found by the LAN browser or the internet lobby list.</summary>
/// <param name="Host">Relay address.</param>
/// <param name="Port">Relay port.</param>
/// <param name="Code">Join code.</param>
/// <param name="Name">Lobby name.</param>
/// <param name="Players">Occupied slots.</param>
/// <param name="MaxPlayers">Slots.</param>
/// <param name="InMatch">Whether its match has started.</param>
/// <param name="Version">The host's game version.</param>
public sealed record LobbyListing(string Host, int Port, string Code, string Name, int Players, int MaxPlayers, bool InMatch, string Version);

/// <summary>
/// LAN mode (REQ-NET-01): this machine hosts an embedded relay — the same <c>RelayServer</c> the internet service runs
/// (ADR 0160) — on a background thread, announces its lobbies to LAN browsers, and keeps desync reports locally. The
/// host then joins its own relay like any other player (<see cref="NetworkMatch.Create"/> on <c>127.0.0.1</c>).
/// </summary>
public sealed class LanHost : IDisposable
{
    private readonly RelayServer _relay;
    private readonly LanBeacon? _beacon;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();

    private LanHost(RelayServer relay, LanBeacon? beacon)
    {
        _relay = relay;
        _beacon = beacon;
        _thread = new Thread(Run) { IsBackground = true, Name = "LAN relay" };
        _thread.Start();
    }

    /// <summary>Starts the embedded relay and the LAN beacon.</summary>
    /// <param name="relayPort">Relay port; when taken, any free port (then only found through loopback or by address).</param>
    /// <param name="discoveryPort">The LAN discovery port.</param>
    /// <param name="desyncDirectory">Where desync reports are written (null: not kept).</param>
    public static LanHost Start(int relayPort = LanDiscovery.DefaultRelayPort, int discoveryPort = LanDiscovery.DefaultPort, string? desyncDirectory = null)
    {
        RelayServer relay = Bind(relayPort, desyncDirectory);
        LanBeacon? beacon = null;
        try
        {
            beacon = new LanBeacon(discoveryPort, relay.Port, () => relay.PublicLobbies);
        }
        catch (SocketException)
        {
            // Another host on this machine owns the discovery port: this one is reachable by address.
        }

        return new LanHost(relay, beacon);
    }

    private static RelayServer Bind(int port, string? desyncDirectory)
    {
        foreach (int candidate in new[] { port, 0 })
        {
            var relay = new RelayServer(new RelayOptions { Port = candidate, DesyncSink = desyncDirectory is null ? null : new DesyncFiles(desyncDirectory) });
            try
            {
                relay.Start();
                return relay;
            }
            catch (InvalidOperationException) when (candidate != 0)
            {
                relay.Dispose();
            }
        }

        throw new InvalidOperationException("No UDP port for the LAN relay.");
    }

    /// <summary>The relay port.</summary>
    public int Port => _relay.Port;

    /// <summary>Whether LAN browsers can find this host.</summary>
    public bool Discoverable => _beacon is not null;

    /// <summary>Desyncs the embedded relay detected.</summary>
    public long Desyncs => _relay.Metrics.Desyncs;

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(TimeSpan.FromSeconds(3));
        _beacon?.Dispose();
        _relay.Dispose();
        _stop.Dispose();
    }

    private void Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            _relay.Update();
            _beacon?.Poll();
            Thread.Sleep(1);
        }

        _relay.Shutdown();
    }

    private sealed class DesyncFiles(string directory) : IDesyncSink
    {
        public void Store(DesyncBundle bundle)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string name = FormattableString.Invariant($"desync-{RelayServer.NormalizeCode(bundle.Lobby)}-{bundle.MatchId:X16}-t{bundle.Tick}.t2desync");
                File.WriteAllBytes(Path.Combine(directory, name), bundle.ToBytes());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning("Desync report not written: " + e.Message);
            }
        }
    }
}

/// <summary>The LAN browser (REQ-NET-08): broadcasts a query about once a second and lists the hosts that answer.</summary>
public sealed class LanBrowserService : IDisposable
{
    private readonly LanBrowser _browser;
    private long _nextQueryUs;

    /// <summary>Opens the browser.</summary>
    public LanBrowserService(int discoveryPort = LanDiscovery.DefaultPort) => _browser = new LanBrowser(discoveryPort);

    /// <summary>Queries and collects answers; call every frame.</summary>
    public void Poll()
    {
        long now = Net.MonotonicClock.NowMicroseconds();
        if (now >= _nextQueryUs)
        {
            _nextQueryUs = now + 1_000_000;
            _browser.Query();
        }

        _browser.Poll();
    }

    /// <summary>The lobbies seen in the last few seconds.</summary>
    public IReadOnlyList<LobbyListing> Lobbies => _browser.Lobbies().Select(l => Listing(l.Relay.Address.ToString(), l.Relay.Port, l.Lobby)).ToArray();

    /// <inheritdoc />
    public void Dispose() => _browser.Dispose();

    internal static LobbyListing Listing(string host, int port, LobbySummary l) =>
        new(host, port, l.Code, l.Name, l.Players, l.MaxPlayers, l.Phase != LobbyPhase.Lobby, l.GameVersion);
}

/// <summary>The internet lobby list of a relay server (one datagram; ADR 0160).</summary>
public static class LobbyDirectory
{
    /// <summary>The public lobbies of the relay at <paramref name="host"/>:<paramref name="port"/>, or null if it does not answer.</summary>
    public static IReadOnlyList<LobbyListing>? Query(string host, int port, int timeoutMs = 2_000)
    {
        BrowseReply? reply = LobbyBrowser.Query(host, port, 0, timeoutMs);
        return reply?.Lobbies.Select(l => LanBrowserService.Listing(host, port, l)).ToArray();
    }
}
