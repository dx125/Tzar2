using System;

namespace Tzar2.Net.Protocol;

/// <summary>First byte of every message (ADR 0160). Values are part of the protocol: never renumber.</summary>
public enum MessageType : byte
{
    /// <summary>Client to relay, as the connect-request payload: create, join or rejoin a lobby.</summary>
    Hello = 1,

    /// <summary>Client to relay, unconnected datagram: list the public lobbies.</summary>
    BrowseQuery = 2,

    /// <summary>Relay to client, unconnected datagram: one page of public lobbies.</summary>
    BrowseReply = 3,

    /// <summary>Relay to client, as connect-reject data: why the hello was refused.</summary>
    Refusal = 4,

    /// <summary>Relay to client: slot, session token and lobby code.</summary>
    Welcome = 10,

    /// <summary>Relay to every member: the whole lobby (sent on every change).</summary>
    LobbyState = 11,

    /// <summary>Member to relay: the slot owner's (or, for computer slots, the host's) civilization/colour/team bytes.</summary>
    SetChoice = 12,

    /// <summary>Host to relay: the match settings bytes (map, game options; opaque to the relay).</summary>
    SetSettings = 13,

    /// <summary>Host to relay: open, close, add a computer to, or kick a slot.</summary>
    SetSlot = 14,

    /// <summary>Member to relay: ready flag (settings are locked while ready).</summary>
    SetReady = 15,

    /// <summary>Host to relay: start loading when every human is ready.</summary>
    StartRequest = 16,

    /// <summary>Member to relay: a chat line (separate reliable channel; REQ-NET-03).</summary>
    Chat = 17,

    /// <summary>Relay to recipients: a chat line.</summary>
    ChatDelivery = 18,

    /// <summary>Relay to one member: an informational notice (chat throttled, kicked, ...).</summary>
    Notice = 19,

    /// <summary>Relay to every member: load the match (loading barrier, REQ-NET-05).</summary>
    Load = 20,

    /// <summary>Member to relay: loaded; match fingerprint and initial state hash.</summary>
    Loaded = 21,

    /// <summary>Relay to every member: every fingerprint agrees; ticks start closing after the delay.</summary>
    Start = 22,

    /// <summary>Member to relay, once per tick: command batches (empty = heartbeat), ack, state hashes.</summary>
    Batches = 30,

    /// <summary>Relay to members: closed ticks with their ordered command payloads and control events.</summary>
    Closures = 31,

    /// <summary>Relay to one member: its batch missed its tick and moved to the next open one.</summary>
    Late = 32,

    /// <summary>Member to relay: RTT probe.</summary>
    Ping = 33,

    /// <summary>Relay to member: RTT probe echo.</summary>
    Pong = 34,

    /// <summary>Relay to members: connection, ping and controller of every slot (player list).</summary>
    Status = 35,

    /// <summary>Relay to members: state hashes disagree (REQ-NET-04).</summary>
    Desync = 40,

    /// <summary>Relay to a member: send a snapshot (rejoin donor, desync report).</summary>
    SnapshotRequest = 41,

    /// <summary>Member to relay: the requested snapshot(s).</summary>
    Snapshot = 42,

    /// <summary>Relay to a rejoining member: a peer's snapshot plus every closed tick after it (REQ-NET-06).</summary>
    Resume = 43,

    /// <summary>Host to relay: propose a multiplayer save (REQ-NET-10).</summary>
    SaveRequest = 44,

    /// <summary>Relay to members: a save was proposed; every connected human must consent.</summary>
    SaveProposal = 45,

    /// <summary>Member to relay: consent or refusal for a proposed save.</summary>
    SaveVote = 46,

    /// <summary>Relay to members: the proposal's outcome (accepted saves are written at a closed tick).</summary>
    SaveOutcome = 47,

    /// <summary>Member to relay: pause or resume the match.</summary>
    Pause = 48,

    /// <summary>Relay to members: the match is paused or running.</summary>
    PauseState = 49,

    /// <summary>Member to relay: wait longer for a dropped player (REQ-NET-07 vote).</summary>
    WaitVote = 50,

    /// <summary>Relay to members: the match or lobby ended.</summary>
    End = 60,
}

/// <summary>What a <see cref="Hello"/> asks for.</summary>
public enum HelloKind : byte
{
    /// <summary>Create a lobby and become its host.</summary>
    Create = 1,

    /// <summary>Join a lobby by code.</summary>
    Join = 2,

    /// <summary>Re-enter a lobby or running match in a known slot with its session token.</summary>
    Rejoin = 3,
}

/// <summary>Why a hello was refused (sent as connect-reject data).</summary>
public enum RefusalReason : byte
{
    /// <summary>Malformed hello.</summary>
    Malformed = 1,

    /// <summary>Another protocol version.</summary>
    ProtocolVersion = 2,

    /// <summary>No lobby with that code.</summary>
    UnknownLobby = 3,

    /// <summary>No open slot.</summary>
    LobbyFull = 4,

    /// <summary>The match already started (only rejoin is possible).</summary>
    InMatch = 5,

    /// <summary>Wrong slot or session token.</summary>
    BadToken = 6,

    /// <summary>Too many connection attempts or lobbies from this address, or the relay is full.</summary>
    RateLimited = 7,

    /// <summary>Another game version than the lobby's host.</summary>
    GameVersion = 8,
}

/// <summary>A lobby slot.</summary>
public enum SlotState : byte
{
    /// <summary>Free for a human to join.</summary>
    Open = 0,

    /// <summary>Closed by the host.</summary>
    Closed = 1,

    /// <summary>A human member.</summary>
    Human = 2,

    /// <summary>A computer kingdom (the AI runs in every peer's sim).</summary>
    Computer = 3,
}

/// <summary>Lobby life cycle.</summary>
public enum LobbyPhase : byte
{
    /// <summary>Gathering players.</summary>
    Lobby = 0,

    /// <summary>Loading barrier.</summary>
    Loading = 1,

    /// <summary>Ticks are closing.</summary>
    Running = 2,

    /// <summary>Ended.</summary>
    Ended = 3,
}

/// <summary>What happens to a dropped player's kingdom after the timeout (REQ-NET-07).</summary>
public enum DropPolicy : byte
{
    /// <summary>The computer takes the kingdom over (the AI runs in every peer's sim).</summary>
    AiTakeover = 0,

    /// <summary>Nobody commands the kingdom any more.</summary>
    Neutral = 1,

    /// <summary>The kingdom is defeated.</summary>
    Defeat = 2,
}

/// <summary>Who controls a slot during the match (player list).</summary>
public enum SlotControl : byte
{
    /// <summary>Nobody (closed or open slot).</summary>
    Absent = 0,

    /// <summary>Its human.</summary>
    Human = 1,

    /// <summary>A computer kingdom from the lobby, or a dropped human taken over by the AI.</summary>
    Computer = 2,

    /// <summary>A dropped human set neutral.</summary>
    Neutral = 3,

    /// <summary>A dropped human whose kingdom was defeated.</summary>
    Defeated = 4,

    /// <summary>A human who dropped and is within the drop timeout.</summary>
    Dropped = 5,
}

/// <summary>A sim-affecting event the relay orders into a closed tick: every peer applies it in that tick.</summary>
public enum ControlKind : byte
{
    /// <summary>A dropped player's timeout elapsed; <see cref="ControlEvent.Arg"/> is the <see cref="DropPolicy"/>.</summary>
    Takeover = 1,

    /// <summary>A taken-over player rejoined and controls the kingdom again.</summary>
    Returned = 2,

    /// <summary>Every human consented to a save; write it after this tick (<see cref="ControlEvent.Arg"/> = save id).</summary>
    Save = 3,
}

/// <summary>What a snapshot is requested for.</summary>
public enum SnapshotPurpose : byte
{
    /// <summary>A rejoining peer resumes from it.</summary>
    Rejoin = 1,

    /// <summary>The desync report (the last agreed keyframe and the current state).</summary>
    Desync = 2,
}

/// <summary>Why a lobby or match ended.</summary>
public enum EndReason : byte
{
    /// <summary>The host closed the lobby or every human left.</summary>
    Closed = 1,

    /// <summary>A peer's match fingerprint or initial state differs (loading barrier).</summary>
    LoadMismatch = 2,

    /// <summary>State hashes disagreed (REQ-NET-04).</summary>
    Desync = 3,

    /// <summary>Loading took too long.</summary>
    LoadTimeout = 4,

    /// <summary>The relay shuts down.</summary>
    Shutdown = 5,
}

/// <summary>Notices to one member.</summary>
public enum NoticeKind : byte
{
    /// <summary>A chat line was dropped: more than 5 lines in 10 s (REQ-NET-03).</summary>
    ChatThrottled = 1,

    /// <summary>A lobby request was refused (not the host, locked while ready, ...).</summary>
    Refused = 2,

    /// <summary>No pauses left.</summary>
    NoPausesLeft = 3,
}

/// <summary>Chat recipients (REQ-NET-03, as in the original): a bit mask of slots, with helpers for all/allies/enemies.</summary>
public static class ChatRecipients
{
    /// <summary>Every slot.</summary>
    public const byte All = 0xFF;
}

/// <summary>Lobby creation parameters.</summary>
/// <param name="LobbyName">Display name in the browser.</param>
/// <param name="MaxPlayers">Slots (1-8).</param>
/// <param name="Public">Listed by the browse query.</param>
public sealed record LobbyCreate(string LobbyName, byte MaxPlayers, bool Public);

/// <summary>Connect request.</summary>
/// <param name="Kind">Create, join or rejoin.</param>
/// <param name="GameVersion">Engine version (lobbies only admit the host's version; G-04).</param>
/// <param name="Name">Player name.</param>
/// <param name="Code">Lobby code (join, rejoin).</param>
/// <param name="Slot">Slot (rejoin).</param>
/// <param name="Token">Session token (rejoin).</param>
/// <param name="Create">Creation parameters (create).</param>
public sealed record Hello(HelloKind Kind, string GameVersion, string Name, string Code, byte Slot, ulong Token, LobbyCreate? Create)
{
    /// <summary>The sender's protocol version (only this field is read from a hello of another version).</summary>
    public ushort Protocol { get; init; } = Wire.ProtocolVersion;
}

/// <summary>Why a hello was refused.</summary>
public sealed record Refusal(RefusalReason Reason, string Detail);

/// <summary>Browse request.</summary>
/// <param name="Page">Page index (16 lobbies per page).</param>
/// <param name="Nonce">Echoed in the reply (LAN/loopback clients match replies to queries).</param>
public sealed record BrowseQuery(ushort Page, uint Nonce);

/// <summary>One public lobby.</summary>
public sealed record LobbySummary(string Code, string Name, byte Players, byte MaxPlayers, LobbyPhase Phase, string GameVersion);

/// <summary>Browse reply.</summary>
public sealed record BrowseReply(uint Nonce, int Total, LobbySummary[] Lobbies);

/// <summary>Accepted into a lobby.</summary>
public sealed record Welcome(string Code, byte Slot, ulong Token, byte MaxPlayers);

/// <summary>One slot of a lobby.</summary>
/// <param name="State">Open, closed, human or computer.</param>
/// <param name="Name">The human's name (empty otherwise).</param>
/// <param name="Ready">Ready flag (humans).</param>
/// <param name="Connected">Whether the human is connected.</param>
/// <param name="PingMs">The human's RTT to the relay.</param>
/// <param name="Choice">Civilization/colour/team bytes (opaque to the relay).</param>
public sealed record SlotInfo(SlotState State, string Name, bool Ready, bool Connected, ushort PingMs, byte[] Choice);

/// <summary>The whole lobby.</summary>
public sealed record LobbyState(string Code, string Name, byte HostSlot, LobbyPhase Phase, int SettingsRevision, byte[] Settings, SlotInfo[] Slots);

/// <summary>Match parameters fixed when loading starts.</summary>
/// <param name="MatchId">Random id of the match (desync reports, saves).</param>
/// <param name="TickMicroseconds">Wall-clock tick length (game speed).</param>
/// <param name="HashInterval">Peers report a state hash every this many ticks (REQ-NET-04: 10).</param>
/// <param name="DropTimeoutMs">Drop timeout (REQ-NET-07: 30 s).</param>
/// <param name="Policy">What happens to a dropped kingdom.</param>
/// <param name="MaxPauses">Pauses per player.</param>
public sealed record MatchParameters(ulong MatchId, int TickMicroseconds, int HashInterval, int DropTimeoutMs, DropPolicy Policy, byte MaxPauses);

/// <summary>A state hash of one executed tick.</summary>
public readonly record struct TickHash(int Tick, ulong Hash);

/// <summary>One upstream batch: commands for <see cref="TargetTick"/> (opaque payload; empty = heartbeat).</summary>
/// <param name="Seq">Sequence number (redundant delivery: the relay skips seen ones).</param>
/// <param name="TargetTick">The tick the commands are meant for (estimated open tick + input delay).</param>
/// <param name="AckTick">The highest tick the member has received every closure up to.</param>
/// <param name="Payload">Encoded commands (the game's command codec).</param>
/// <param name="Hashes">State hashes of executed checkpoint ticks.</param>
public sealed record Batch(uint Seq, int TargetTick, int AckTick, byte[] Payload, TickHash[] Hashes);

/// <summary>One member's commands in a closed tick.</summary>
public sealed record ClosureEntry(byte Slot, byte[] Payload);

/// <summary>A control event in a closed tick.</summary>
public readonly record struct ControlEvent(ControlKind Kind, byte Slot, int Arg);

/// <summary>A closed tick: what every peer executes in it, in this order.</summary>
public sealed record TickClosure(int Tick, ClosureEntry[] Entries, ControlEvent[] Controls)
{
    /// <summary>An empty closure.</summary>
    public static TickClosure Empty(int tick) => new(tick, [], []);
}

/// <summary>A slot's state in the running match (player list).</summary>
public readonly record struct PeerStatus(SlotControl Control, bool Connected, ushort RttMs, byte InputDelay, ushort DroppedSeconds);

/// <summary>Desync detected at <see cref="Tick"/>; the odd slots are outside the majority.</summary>
public sealed record DesyncNotice(int Tick, ulong MajorityHash, byte[] OddSlots);

/// <summary>A snapshot: rejoin donor (current only) or desync report (before = last agreed keyframe, and current).</summary>
public sealed record SnapshotMessage(SnapshotPurpose Purpose, uint RequestId, int Tick, ulong Hash, byte[] Data, int BeforeTick, byte[] BeforeData);

/// <summary>What a rejoining member resumes from.</summary>
public sealed record ResumeMessage(int Tick, ulong Hash, byte[] Data, TickClosure[] Backlog);

/// <summary>A proposed save.</summary>
public sealed record SaveProposal(uint Id, byte By, string Label);

/// <summary>The outcome of a proposed save.</summary>
/// <param name="Id">Proposal id.</param>
/// <param name="Accepted">Whether every connected human consented.</param>
/// <param name="RefusedBy">The first slot that refused (255 = timeout or none).</param>
public sealed record SaveOutcome(uint Id, bool Accepted, byte RefusedBy);

/// <summary>End of a lobby or match.</summary>
/// <param name="LastTick">Last closed tick (-1 before the match).</param>
/// <param name="Reason">Why.</param>
/// <param name="Slot">The slot involved (mismatch, odd slot), or 255.</param>
/// <param name="Detail">Human-readable detail.</param>
public sealed record EndMessage(int LastTick, EndReason Reason, byte Slot, string Detail);

/// <summary>Protocol limits (REQ-NET-11: every count and size is bounded).</summary>
public static class Limits
{
    /// <summary>Most slots of a lobby (the original's 8 kingdoms).</summary>
    public const int MaxSlots = 8;

    /// <summary>Longest player name in UTF-8 bytes.</summary>
    public const int MaxNameBytes = 32;

    /// <summary>Longest lobby name.</summary>
    public const int MaxLobbyNameBytes = 48;

    /// <summary>Longest game version string.</summary>
    public const int MaxVersionBytes = 32;

    /// <summary>Lobby codes are 6 characters; at most this many are accepted.</summary>
    public const int MaxCodeBytes = 8;

    /// <summary>Largest match settings.</summary>
    public const int MaxSettingsBytes = 16 * 1024;

    /// <summary>Largest slot choice.</summary>
    public const int MaxChoiceBytes = 64;

    /// <summary>Longest chat line (REQ-NET-03).</summary>
    public const int MaxChatBytes = 256;

    /// <summary>Longest detail text.</summary>
    public const int MaxDetailBytes = 512;

    /// <summary>Largest command payload of one batch.</summary>
    public const int MaxPayloadBytes = 8 * 1024;

    /// <summary>Most hashes in one batch.</summary>
    public const int MaxHashesPerBatch = 16;

    /// <summary>Most batches in one upstream message (redundant repeats).</summary>
    public const int MaxBatchesPerMessage = 32;

    /// <summary>Most entries in one closed tick (late batches of several ticks can land together).</summary>
    public const int MaxEntriesPerClosure = 128;

    /// <summary>Most command bytes in one closed tick; further batches move to the next tick.</summary>
    public const int MaxClosureBytes = 64 * 1024;

    /// <summary>Most control events in one closed tick.</summary>
    public const int MaxControlsPerClosure = 16;

    /// <summary>Most closures in one downstream message.</summary>
    public const int MaxClosuresPerMessage = 64;

    /// <summary>Most closures in a rejoin backlog (one hour of play at 20 Hz).</summary>
    public const int MaxBacklogClosures = 72_000;

    /// <summary>Largest snapshot.</summary>
    public const int MaxSnapshotBytes = 32 << 20;

    /// <summary>Most fingerprint parts.</summary>
    public const int MaxFingerprintParts = 8;

    /// <summary>Lobbies per browse page.</summary>
    public const int BrowsePageSize = 16;

    /// <summary>Longest save label.</summary>
    public const int MaxLabelBytes = 64;

    /// <summary>Highest tick a message may name (about 2.5 years at 20 Hz; keeps tick arithmetic far from overflow).</summary>
    public const int MaxTick = 1 << 30;
}

/// <summary>Helpers for slot masks.</summary>
public static class SlotMask
{
    /// <summary>Whether <paramref name="mask"/> contains <paramref name="slot"/>.</summary>
    public static bool Contains(byte mask, int slot) => (uint)slot < 8 && (mask & (1 << slot)) != 0;

    /// <summary>The mask of one slot.</summary>
    public static byte Of(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, 7);
        return (byte)(1 << slot);
    }
}
