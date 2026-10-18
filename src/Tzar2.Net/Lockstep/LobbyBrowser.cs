using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LiteNetLib;
using Tzar2.Core.Serialization;
using Tzar2.Net.Protocol;

namespace Tzar2.Net.Lockstep;

/// <summary>Lists a relay's public lobbies with one unconnected datagram (the internet lobby browser; ADR 0160).</summary>
public static class LobbyBrowser
{
    /// <summary>Asks the relay at <paramref name="host"/>:<paramref name="port"/> for page <paramref name="page"/>.</summary>
    /// <returns>The reply, or null when the relay did not answer within <paramref name="timeoutMs"/>.</returns>
    public static BrowseReply? Query(string host, int port, int page = 0, int timeoutMs = 2_000)
    {
        ArgumentNullException.ThrowIfNull(host);
        uint nonce = (uint)Random.Shared.Next() | 1;
        BrowseReply? reply = null;
        var listener = new EventBasedNetListener();
        listener.NetworkReceiveUnconnectedEvent += (_, reader, _) =>
        {
            try
            {
                BrowseReply candidate = Wire.ReadBrowseReply(reader.GetRemainingBytesSpan());
                if (candidate.Nonce == nonce)
                {
                    reply = candidate;
                }
            }
            catch (InvalidDataException)
            {
                // Not an answer to us.
            }
        };
        var net = new NetManager(listener) { UnconnectedMessagesEnabled = true, UpdateTime = 1 };
        if (!net.Start())
        {
            throw new InvalidOperationException("Cannot open a UDP socket.");
        }

        try
        {
            var w = new BinaryBufferWriter(32);
            Wire.Write(w, new BrowseQuery((ushort)Math.Clamp(page, 0, ushort.MaxValue), nonce));
            IPAddress address = IPAddress.TryParse(host, out IPAddress? parsed) ? parsed
                : Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
            var target = new IPEndPoint(address, port);
            long deadline = MonotonicClock.NowMicroseconds() + (timeoutMs * 1000L);
            long resend = 0;
            while (reply is null && MonotonicClock.NowMicroseconds() < deadline)
            {
                if (MonotonicClock.NowMicroseconds() >= resend)
                {
                    net.SendUnconnectedMessage(w.WrittenSpan, target); // unconnected datagrams may be lost: repeat
                    resend = MonotonicClock.NowMicroseconds() + 250_000;
                }

                net.PollEvents();
                Thread.Sleep(2);
            }

            return reply;
        }
        finally
        {
            net.Stop();
        }
    }
}
