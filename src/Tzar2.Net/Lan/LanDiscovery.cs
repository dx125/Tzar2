using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Tzar2.Core.Serialization;
using Tzar2.Net.Protocol;

namespace Tzar2.Net.Lan;

/// <summary>A lobby found on the LAN.</summary>
/// <param name="Relay">Where its embedded relay listens.</param>
/// <param name="Lobby">The lobby.</param>
/// <param name="SeenUs">When the last answer arrived (<see cref="MonotonicClock"/>).</param>
public sealed record LanLobby(IPEndPoint Relay, LobbySummary Lobby, long SeenUs);

/// <summary>
/// LAN discovery by UDP broadcast (REQ-NET-01, ADR 0160). A browser broadcasts a query to the discovery port (and to
/// loopback, for a host on the same machine); every LAN host answers unicast with its relay port and its lobbies.
/// Datagrams are tiny and decoded with bounds; answers from anywhere are only ever displayed, never trusted.
/// </summary>
public static class LanDiscovery
{
    /// <summary>The default discovery port.</summary>
    public const int DefaultPort = 47624;

    /// <summary>The default relay port of a LAN host (falls back to any free port when taken).</summary>
    public const int DefaultRelayPort = 47625;

    /// <summary>"T2LN" little-endian.</summary>
    internal const uint Magic = 0x4E4C3254;

    /// <summary>Largest datagram either side sends or accepts.</summary>
    internal const int MaxDatagram = 1400;

    internal static byte[] Query(uint nonce)
    {
        var w = new BinaryBufferWriter(16);
        w.WriteUInt32(Magic);
        w.WriteUInt16(Wire.ProtocolVersion);
        w.WriteByte(1);
        w.WriteUInt32(nonce);
        return w.ToArray();
    }

    internal static bool TryReadQuery(ReadOnlySpan<byte> data, out uint nonce)
    {
        nonce = 0;
        var r = new BinaryBufferReader(data);
        try
        {
            if (data.Length != 11 || r.ReadUInt32() != Magic || r.ReadUInt16() != Wire.ProtocolVersion || r.ReadByte() != 1)
            {
                return false;
            }

            nonce = r.ReadUInt32();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    internal static byte[] Answer(uint nonce, int relayPort, IReadOnlyList<LobbySummary> lobbies)
    {
        var w = new BinaryBufferWriter(256);
        w.WriteUInt32(Magic);
        w.WriteUInt16(Wire.ProtocolVersion);
        w.WriteByte(2);
        w.WriteUInt16((ushort)relayPort);
        Wire.Write(w, new BrowseReply(nonce, lobbies.Count, lobbies.Take(8).ToArray()));
        return w.ToArray();
    }

    internal static bool TryReadAnswer(ReadOnlySpan<byte> data, uint nonce, out int relayPort, out LobbySummary[] lobbies)
    {
        relayPort = 0;
        lobbies = [];
        if (data.Length > MaxDatagram || data.Length < 9)
        {
            return false;
        }

        var r = new BinaryBufferReader(data);
        try
        {
            if (r.ReadUInt32() != Magic || r.ReadUInt16() != Wire.ProtocolVersion || r.ReadByte() != 2)
            {
                return false;
            }

            relayPort = r.ReadUInt16();
            BrowseReply reply = Wire.ReadBrowseReply(data[r.Position..]);
            if (reply.Nonce != nonce || relayPort == 0)
            {
                return false;
            }

            lobbies = reply.Lobbies;
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}

/// <summary>The host side of LAN discovery: answers queries with the embedded relay's port and public lobbies.</summary>
public sealed class LanBeacon : IDisposable
{
    private readonly Socket _socket;
    private readonly Func<IReadOnlyList<LobbySummary>> _lobbies;
    private readonly int _relayPort;
    private readonly byte[] _buffer = new byte[LanDiscovery.MaxDatagram];

    /// <summary>Listens on <paramref name="discoveryPort"/> for the relay on <paramref name="relayPort"/>.</summary>
    /// <param name="discoveryPort">The discovery port (shared with other hosts on the machine where the OS allows).</param>
    /// <param name="relayPort">The embedded relay's UDP port.</param>
    /// <param name="lobbies">The lobbies to announce (called on the polling thread).</param>
    /// <exception cref="SocketException">The port cannot be bound.</exception>
    public LanBeacon(int discoveryPort, int relayPort, Func<IReadOnlyList<LobbySummary>> lobbies)
    {
        ArgumentNullException.ThrowIfNull(lobbies);
        _relayPort = relayPort;
        _lobbies = lobbies;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { Blocking = false, EnableBroadcast = true };
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
    }

    /// <summary>The bound discovery port.</summary>
    public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    /// <summary>Answers pending queries (call regularly, e.g. with the relay's update).</summary>
    public void Poll()
    {
        for (int i = 0; i < 64 && _socket.Available > 0; i++)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int length;
            try
            {
                length = _socket.ReceiveFrom(_buffer, ref from);
            }
            catch (SocketException)
            {
                return;
            }

            if (LanDiscovery.TryReadQuery(_buffer.AsSpan(0, length), out uint nonce))
            {
                try
                {
                    _socket.SendTo(LanDiscovery.Answer(nonce, _relayPort, _lobbies()), from);
                }
                catch (SocketException)
                {
                    // The querier is gone; nothing to do.
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _socket.Dispose();
}

/// <summary>The browser side of LAN discovery.</summary>
public sealed class LanBrowser : IDisposable
{
    private const int MaxLobbies = 64;

    private readonly Socket _socket;
    private readonly int _discoveryPort;
    private readonly byte[] _buffer = new byte[LanDiscovery.MaxDatagram + 1];
    private readonly Dictionary<(string Code, int Port), LanLobby> _found = [];
    private uint _nonce;

    /// <summary>Opens a browser for hosts on <paramref name="discoveryPort"/>.</summary>
    public LanBrowser(int discoveryPort = LanDiscovery.DefaultPort)
    {
        _discoveryPort = discoveryPort;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { Blocking = false, EnableBroadcast = true };
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
    }

    /// <summary>The lobbies seen within the last <paramref name="maxAgeMs"/> milliseconds, by name.</summary>
    public IReadOnlyList<LanLobby> Lobbies(int maxAgeMs = 4_000)
    {
        long now = MonotonicClock.NowMicroseconds();
        return _found.Values.Where(l => now - l.SeenUs <= maxAgeMs * 1000L)
            .OrderBy(l => l.Lobby.Name, StringComparer.Ordinal).ThenBy(l => l.Lobby.Code, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Broadcasts a query (call about once a second while the LAN browser is open).</summary>
    public void Query()
    {
        _nonce = (uint)Random.Shared.Next() | 1;
        byte[] query = LanDiscovery.Query(_nonce);
        foreach (IPAddress target in new[] { IPAddress.Broadcast, IPAddress.Loopback })
        {
            try
            {
                _socket.SendTo(query, new IPEndPoint(target, _discoveryPort));
            }
            catch (SocketException)
            {
                // No broadcast route (e.g. offline): loopback still finds a host on this machine.
            }
        }
    }

    /// <summary>Collects answers.</summary>
    public void Poll()
    {
        for (int i = 0; i < 256 && _socket.Available > 0; i++)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int length;
            try
            {
                length = _socket.ReceiveFrom(_buffer, ref from);
            }
            catch (SocketException)
            {
                return;
            }

            if (from is IPEndPoint sender && LanDiscovery.TryReadAnswer(_buffer.AsSpan(0, length), _nonce, out int port, out LobbySummary[] lobbies))
            {
                var relay = new IPEndPoint(sender.Address, port);
                long now = MonotonicClock.NowMicroseconds();
                foreach (LobbySummary lobby in lobbies)
                {
                    // A host on this machine answers on loopback and on its LAN address: keep one entry, the LAN one.
                    (string, int) key = (lobby.Code, port);
                    if (_found.TryGetValue(key, out LanLobby? known))
                    {
                        IPEndPoint address = IPAddress.IsLoopback(known.Relay.Address) ? relay : known.Relay;
                        _found[key] = new LanLobby(address, lobby, now);
                    }
                    else if (_found.Count < MaxLobbies)
                    {
                        _found[key] = new LanLobby(relay, lobby, now);
                    }
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _socket.Dispose();
}
