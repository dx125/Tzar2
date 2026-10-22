using System;

namespace Tzar2.Net;

/// <summary>
/// LiteNetLib's keep-alive for a disconnect timeout (ADR 0231, issue #62). LiteNetLib pings once a second by default,
/// on its own thread; with a timeout near that, a connection survived only while the game thread pumped and sent its own
/// traffic, so a stalled thread (a loaded machine, a client loading a map) dropped live peers. Five pings per timeout
/// keep a peer whose game thread is busy connected, whatever the timeout.
/// </summary>
internal static class KeepAlive
{
    /// <summary>The LiteNetLib ping interval for <paramref name="disconnectTimeoutMs"/>: a fifth of it, 50 ms to 1 s.</summary>
    public static int PingIntervalMs(int disconnectTimeoutMs) => Math.Clamp(disconnectTimeoutMs / 5, 50, 1_000);
}
