using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Tzar2.Spikes.S3.Lockstep;

namespace Tzar2.Spikes.S3.Host;

/// <summary>Network conditions applied in each direction.</summary>
/// <param name="OneWayDelayMs">Fixed one-way delay (RTT / 2).</param>
/// <param name="JitterMs">Uniform extra delay in [-jitter, +jitter] per packet (may reorder, as netem does).</param>
/// <param name="LossPercent">Independent drop probability per packet and direction.</param>
internal sealed record Impairment(int OneWayDelayMs, int JitterMs, double LossPercent)
{
    public static Impairment None { get; } = new(0, 0, 0);
}

/// <summary>
/// A per-client UDP forwarder between a client and the relay that delays, jitters and drops datagrams. LiteNetLib's
/// own simulation (SimulatePacketLoss/SimulateLatency) is compiled out of the NuGet release build
/// (<c>#if DEBUG || SIMULATE_NETWORK</c>), so the spike impairs real UDP traffic instead, like <c>tc netem</c>.
/// It also counts the bytes the client puts on and takes off the wire.
/// </summary>
internal sealed class UdpImpairmentProxy : IDisposable
{
    /// <summary>IPv4 (20) + UDP (8) header bytes per datagram, added to bandwidth figures.</summary>
    public const int IpUdpHeaderBytes = 28;

    private readonly Socket _front = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly Socket _back = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly PriorityQueue<(byte[] Data, bool ToRelay), long> _queue = new();
    private readonly object _gate = new();
    private readonly Random _random = new();
    private readonly Thread[] _threads;
    private volatile bool _stopping;
    private EndPoint? _client;
    private long _sequence;

    public UdpImpairmentProxy(IPEndPoint relay, Impairment impairment)
    {
        Impairment = impairment;
        _front.Bind(new IPEndPoint(IPAddress.Any, 0));
        _back.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _back.Connect(relay);

        // Blocking receives wake up regularly: disposing a socket blocked in a receive spins on macOS/Linux.
        _front.ReceiveTimeout = 100;
        _back.ReceiveTimeout = 100;
        _threads =
        [
            new Thread(FrontLoop) { IsBackground = true, Name = "proxy-front" },
            new Thread(BackLoop) { IsBackground = true, Name = "proxy-back" },
            new Thread(SchedulerLoop) { IsBackground = true, Name = "proxy-scheduler" },
        ];
        foreach (Thread thread in _threads)
        {
            thread.Start();
        }
    }

    /// <summary>The port clients connect to.</summary>
    public int Port => ((IPEndPoint)_front.LocalEndPoint!).Port;

    /// <summary>Current conditions; may be changed while running.</summary>
    public Impairment Impairment { get; set; }

    /// <summary>UDP payload bytes the client sent.</summary>
    public long UpBytes => Interlocked.Read(ref _upBytes);

    /// <summary>Datagrams the client sent.</summary>
    public long UpPackets => Interlocked.Read(ref _upPackets);

    /// <summary>UDP payload bytes delivered to the client.</summary>
    public long DownBytes => Interlocked.Read(ref _downBytes);

    /// <summary>Datagrams delivered to the client.</summary>
    public long DownPackets => Interlocked.Read(ref _downPackets);

    /// <summary>Datagrams dropped in either direction.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    private long _upBytes;
    private long _upPackets;
    private long _downBytes;
    private long _downPackets;
    private long _dropped;

    public void Dispose()
    {
        _stopping = true;
        foreach (Thread thread in _threads)
        {
            thread.Join(1_000);
        }

        _front.Dispose();
        _back.Dispose();
    }

    private void FrontLoop()
    {
        byte[] buffer = new byte[65536];
        while (!_stopping)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int length;
            try
            {
                length = _front.ReceiveFrom(buffer, ref from);
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _client = from; // a rejoining client arrives from a new port
            Interlocked.Add(ref _upBytes, length);
            Interlocked.Increment(ref _upPackets);
            Enqueue(buffer.AsSpan(0, length).ToArray(), toRelay: true);
        }
    }

    private void BackLoop()
    {
        byte[] buffer = new byte[65536];
        while (!_stopping)
        {
            int length;
            try
            {
                length = _back.Receive(buffer);
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            Enqueue(buffer.AsSpan(0, length).ToArray(), toRelay: false);
        }
    }

    private void Enqueue(byte[] data, bool toRelay)
    {
        Impairment impairment = Impairment;
        long delayUs;
        lock (_gate)
        {
            if (impairment.LossPercent > 0 && _random.NextDouble() * 100 < impairment.LossPercent)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }

            delayUs = (impairment.OneWayDelayMs * 1000L) + (impairment.JitterMs > 0 ? _random.NextInt64(-impairment.JitterMs * 1000L, (impairment.JitterMs * 1000L) + 1) : 0);
        }

        if (delayUs <= 0)
        {
            Send(data, toRelay);
            return;
        }

        lock (_gate)
        {
            // The sequence number keeps equal deadlines in arrival order.
            _queue.Enqueue((data, toRelay), ((Clock.NowMicroseconds() + delayUs) << 8) + (_sequence++ & 0xFF));
        }
    }

    private void SchedulerLoop()
    {
        var due = new List<(byte[] Data, bool ToRelay)>();
        while (!_stopping)
        {
            long now = Clock.NowMicroseconds() << 8;
            lock (_gate)
            {
                while (_queue.TryPeek(out (byte[] Data, bool ToRelay) item, out long at) && at <= now)
                {
                    _queue.Dequeue();
                    due.Add(item);
                }
            }

            foreach ((byte[] data, bool toRelay) in due)
            {
                Send(data, toRelay);
            }

            due.Clear();
            Thread.Sleep(1);
        }
    }

    private void Send(byte[] data, bool toRelay)
    {
        try
        {
            if (toRelay)
            {
                _back.Send(data);
            }
            else if (_client is not null)
            {
                _front.SendTo(data, _client);
                Interlocked.Add(ref _downBytes, data.Length);
                Interlocked.Increment(ref _downPackets);
            }
        }
        catch (SocketException)
        {
            // The client is gone (simulated crash); the datagram is lost like on a real network.
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
