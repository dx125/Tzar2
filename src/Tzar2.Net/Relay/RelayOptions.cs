using System;
using System.Threading;

namespace Tzar2.Net.Relay;

/// <summary>Relay and lobby configuration (ADR 0160). The defaults suit an internet relay; the LAN host uses them too.</summary>
public sealed class RelayOptions
{
    /// <summary>UDP port (0 = any free port).</summary>
    public int Port { get; set; }

    /// <summary>Most lobbies at a time.</summary>
    public int MaxLobbies { get; set; } = 256;

    /// <summary>Most connected peers at a time.</summary>
    public int MaxPeers { get; set; } = 1024;

    /// <summary>Most lobbies created from one address that are still open.</summary>
    public int MaxLobbiesPerAddress { get; set; } = 4;

    /// <summary>Connection attempts and browse queries per second and address (token bucket refill).</summary>
    public int ConnectsPerSecond { get; set; } = 5;

    /// <summary>Burst of connection attempts and browse queries per address.</summary>
    public int ConnectBurst { get; set; } = 20;

    /// <summary>Messages per second a member may send (a match needs ~25: batch, ping, a few control messages).</summary>
    public int MessagesPerSecond { get; set; } = 200;

    /// <summary>Burst of messages.</summary>
    public int MessageBurst { get; set; } = 400;

    /// <summary>Bytes per second a member may send outside requested snapshots (REQ-NET-03 peak: 16 KB/s).</summary>
    public int BytesPerSecond { get; set; } = 48 * 1024;

    /// <summary>Burst of bytes.</summary>
    public int ByteBurst { get; set; } = 192 * 1024;

    /// <summary>Chat lines per <see cref="ChatWindowMs"/> (REQ-NET-03: 5 per 10 s).</summary>
    public int ChatLines { get; set; } = 5;

    /// <summary>Chat rate window.</summary>
    public int ChatWindowMs { get; set; } = 10_000;

    /// <summary>LiteNetLib disconnect timeout: a silent peer counts as dropped after this long.</summary>
    public int DisconnectTimeoutMs { get; set; } = 5_000;

    /// <summary>Countdown between the loading barrier and the first tick (clients settle their RTT estimate meanwhile).</summary>
    public int StartDelayMs { get; set; } = 1_500;

    /// <summary>Longest loading phase before the match is abandoned.</summary>
    public int LoadTimeoutMs { get; set; } = 120_000;

    /// <summary>Most closures repeated in one redundant datagram (ADR 0009).</summary>
    public int RedundancyWindow { get; set; } = 32;

    /// <summary>Closed ticks the relay keeps at least (desync reports, rejoin backlogs).</summary>
    public int LogTicks { get; set; } = 1_200;

    /// <summary>Ticks after which an incompletely reported hash tick is compared with what arrived (ADR 0009: 2 s).</summary>
    public int HashCompareTimeoutTicks { get; set; } = 40;

    /// <summary>Time to collect the peers' snapshots for a desync report.</summary>
    public int DesyncCollectMs { get; set; } = 10_000;

    /// <summary>A match without a connected human for this long is closed.</summary>
    public int EmptyMatchTimeoutMs { get; set; } = 60_000;

    /// <summary>Time every connected human has to answer a save proposal.</summary>
    public int SaveVoteTimeoutMs { get; set; } = 20_000;

    /// <summary>Extra wait per vote for a dropped player (REQ-NET-07: players can vote to wait longer).</summary>
    public int WaitVoteMs { get; set; } = 30_000;

    /// <summary>Most wait votes per dropped player.</summary>
    public int MaxWaitVotes { get; set; } = 4;

    /// <summary>A pause ends by itself after this long (nobody can hold a match hostage).</summary>
    public int MaxPauseMs { get; set; } = 120_000;

    /// <summary>Where desync reports go (REQ-NET-04), or null to only log them.</summary>
    public IDesyncSink? DesyncSink { get; set; }

    /// <summary>Structured log (thread of <see cref="RelayServer.Update"/>).</summary>
    public Action<RelayLogEntry>? Log { get; set; }

    /// <summary>Lobby codes for tests; null = random.</summary>
    public Func<string>? CodeGenerator { get; set; }

    /// <summary>Checks the values.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Port, 65535);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxLobbies);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPeers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxLobbiesPerAddress);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ConnectsPerSecond);
        ArgumentOutOfRangeException.ThrowIfLessThan(ConnectBurst, ConnectsPerSecond / 1000 + 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MessagesPerSecond);
        ArgumentOutOfRangeException.ThrowIfLessThan(MessageBurst, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BytesPerSecond);
        ArgumentOutOfRangeException.ThrowIfLessThan(ByteBurst, 64 * 1024);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ChatLines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ChatWindowMs);
        ArgumentOutOfRangeException.ThrowIfLessThan(DisconnectTimeoutMs, 500);
        ArgumentOutOfRangeException.ThrowIfNegative(StartDelayMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(LoadTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfLessThan(RedundancyWindow, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RedundancyWindow, Protocol.Limits.MaxClosuresPerMessage);
        ArgumentOutOfRangeException.ThrowIfLessThan(LogTicks, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(HashCompareTimeoutTicks, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(DesyncCollectMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(EmptyMatchTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SaveVoteTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfNegative(WaitVoteMs);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxWaitVotes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPauseMs);
    }
}

/// <summary>One structured log record of the relay (the server writes them as JSON lines).</summary>
/// <param name="Event">Event name (<c>lobby.create</c>, <c>match.start</c>, <c>desync</c>, ...).</param>
/// <param name="Lobby">Lobby code, or empty.</param>
/// <param name="Slot">Slot, or -1.</param>
/// <param name="Detail">Free text.</param>
public sealed record RelayLogEntry(string Event, string Lobby, int Slot, string Detail);

/// <summary>Receives desync reports (REQ-NET-04).</summary>
public interface IDesyncSink
{
    /// <summary>Stores a report (called on the relay thread; keep it quick or hand it off).</summary>
    void Store(DesyncBundle bundle);
}

/// <summary>Relay counters (Prometheus, §19.7). Updated by the relay thread, read from any thread.</summary>
public sealed class RelayMetrics
{
    private long _lobbiesCreated;
    private long _matchesStarted;
    private long _ticksClosed;
    private long _desyncs;
    private long _rejoins;
    private long _takeovers;
    private long _refusedHellos;
    private long _rateLimited;
    private long _malformed;
    private long _lateBatches;
    private long _chatLines;
    private long _saves;

    /// <summary>Open lobbies (any phase).</summary>
    public int Lobbies { get; internal set; }

    /// <summary>Running matches.</summary>
    public int Matches { get; internal set; }

    /// <summary>Connected peers.</summary>
    public int Peers { get; internal set; }

    /// <summary>UDP bytes received (LiteNetLib statistics).</summary>
    public long BytesReceived { get; internal set; }

    /// <summary>UDP bytes sent.</summary>
    public long BytesSent { get; internal set; }

    /// <summary>Lobbies created.</summary>
    public long LobbiesCreated => Interlocked.Read(ref _lobbiesCreated);

    /// <summary>Matches started.</summary>
    public long MatchesStarted => Interlocked.Read(ref _matchesStarted);

    /// <summary>Ticks closed over all matches.</summary>
    public long TicksClosed => Interlocked.Read(ref _ticksClosed);

    /// <summary>Desyncs detected.</summary>
    public long Desyncs => Interlocked.Read(ref _desyncs);

    /// <summary>Rejoins served.</summary>
    public long Rejoins => Interlocked.Read(ref _rejoins);

    /// <summary>Dropped kingdoms taken over (AI, neutral or defeated).</summary>
    public long Takeovers => Interlocked.Read(ref _takeovers);

    /// <summary>Hellos refused.</summary>
    public long RefusedHellos => Interlocked.Read(ref _refusedHellos);

    /// <summary>Peers disconnected or requests dropped by a rate limit.</summary>
    public long RateLimited => Interlocked.Read(ref _rateLimited);

    /// <summary>Malformed messages (the sender is disconnected).</summary>
    public long Malformed => Interlocked.Read(ref _malformed);

    /// <summary>Batches that missed their tick.</summary>
    public long LateBatches => Interlocked.Read(ref _lateBatches);

    /// <summary>Chat lines delivered.</summary>
    public long ChatLines => Interlocked.Read(ref _chatLines);

    /// <summary>Multiplayer saves agreed.</summary>
    public long Saves => Interlocked.Read(ref _saves);

    internal void LobbyCreated() => Interlocked.Increment(ref _lobbiesCreated);

    internal void MatchStarted() => Interlocked.Increment(ref _matchesStarted);

    internal void TickClosed() => Interlocked.Increment(ref _ticksClosed);

    internal void Desync() => Interlocked.Increment(ref _desyncs);

    internal void Rejoin() => Interlocked.Increment(ref _rejoins);

    internal void Takeover() => Interlocked.Increment(ref _takeovers);

    internal void Refused() => Interlocked.Increment(ref _refusedHellos);

    internal void Limited() => Interlocked.Increment(ref _rateLimited);

    internal void MalformedMessage() => Interlocked.Increment(ref _malformed);

    internal void Late() => Interlocked.Increment(ref _lateBatches);

    internal void Chat() => Interlocked.Increment(ref _chatLines);

    internal void Save() => Interlocked.Increment(ref _saves);
}
