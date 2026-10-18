# 0160. Put the relay, lobby and lockstep protocol in Tzar2.Net and share them between the server and the LAN host

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (M6)
* **Related:** DEC-08, REQ-NET-01…08, REQ-NET-11, TDD §7.2, §7.3, §19.1-19.3, §19.6, ADR 0009 (spike S3), ADR 0011, G-04, G-05

## Context

Spike S3 (ADR 0009) fixed the lockstep shape: the relay closes tick T at its deadline, late batches move to the next
open tick, closures and batches travel as unreliable datagrams that repeat everything unacknowledged, hashes every 10
ticks, rejoin from a peer snapshot plus the relay's log. Its code was throwaway and simulated a toy sim; M6 needs the
production version.

Two constraints decide where it lives:

* REQ-NET-01: "LAN mode: one client hosts an embedded relay (**same code**)". The Godot client may only use Game
  (and Content/Import); Game may reference Net, not Server (§7.2, `DependencyRulesTests`). `Tzar2.Server` is a net10.0
  executable; everything Godot loads is net8.0 (ADR 0002).
* Net may reference only Core (§7.2). It cannot see Sim commands, so the relay cannot parse or stamp them.

## Decision

* **`Tzar2.Net` holds the whole protocol**: `Protocol` (message types, records, `Wire` codec, `Limits`, `FuzzTargets`),
  `Relay` (`RelayServer`, options, metrics, rate limits, `DesyncBundle`), `Lockstep` (`LockstepClient`, `LobbyBrowser`,
  the existing `RttEstimator`/`InputDelayController`), `Lan` (`LanBeacon`, `LanBrowser`). LiteNetLib stays internal to
  Net; no public API exposes its types, so Server's compiled assembly references only Core and Net (ADR 0011 map:
  Server has no packages). `Tzar2.Server` is the headless host around `RelayServer` (ADR 0161) and the LAN host in Game
  runs the same `RelayServer` on a thread.
* **Command payloads are opaque bytes** (the game's `CommandCodec` batch). A closure is a list of `(slot, payload)`
  entries in arrival order plus **control events**. The slot of an entry is set by the relay from the sender's
  connection, never read from the payload: the game decodes each payload and stamps that slot on every command, which
  is the "relay overwrites the player id" rule of ADR 0009 without the relay parsing commands.
* **Control events** are the only things the relay itself orders into ticks, so every peer applies them in the same
  tick: `Takeover(slot, policy)` after the drop timeout, `Returned(slot)` when a taken-over player rejoins,
  `Save(id)` when every connected human consented to a save. The game turns them into sim commands.
* **Lobby**: create (host = slot 0), join by 6-character code (case and separators ignored), browse public lobbies with
  one unconnected datagram (`BrowseQuery`/`BrowseReply`), slots open/closed/human/computer, a per-slot choice blob
  (civilization, colour, team; set by the slot's human, or by the host for computer slots), a host settings blob (map
  and game options), ready flags (a ready player's choice is locked; new settings clear everyone's ready flag, as in the
  original), host hand-over when the host leaves the lobby. The relay does not interpret either blob.
* **Loading barrier** (REQ-NET-05): the host's `StartRequest` carries the match parameters (tick length = game speed,
  hash interval, drop timeout and policy, pauses per player); the relay assigns a random match id and sends `Load`;
  every connected human reports `Loaded(fingerprint parts, initial state hash)`; the first differing part ends the
  match with `LoadMismatch` naming the slot and the part. Then a 1.5 s countdown.
* **Ticks** as ADR 0009, with three bounds: a batch's target is clamped to the next 64 ticks; a tick holds at most 128
  entries and 64 KiB of payload, further batches spill into the following ticks (delayed, not lost); a member with more
  than 128 ticks of pending commands is disconnected. The relay keeps the closure log for the last 1,200 ticks (60 s)
  and everything a connected peer has not acknowledged (at most one hour). Batches acknowledge the highest contiguously
  *received* tick, so a slow peer is not re-sent closures it already has (its "catch-up burst" is the redundant window).
* **Hashes and desyncs** (REQ-NET-04): a peer reports its checkpoint hash every `HashInterval` ticks; the relay compares
  once every live peer reported (or after 40 ticks), takes the majority, and on a mismatch notifies everyone, stops
  closing ticks and asks every peer for its last agreed keyframe and its current snapshot; with the log it stores a
  `.t2desync` bundle through `IDesyncSink` (ADR 0165), then ends the match. Casual-lobby resync is not implemented.
* **Rejoin** (REQ-NET-06): `Hello(Rejoin, code, slot, token)`; the relay sends the lobby and the match parameters, asks
  the connected peer with the lowest RTT for a snapshot at its last executed tick, and sends `Resume(snapshot, every
  closure after it)`. A taken-over kingdom is handed back with `Returned` in the next tick.
* **Drop policy** (REQ-NET-07): a dropped human's ticks keep closing without it (nobody freezes); after the timeout
  (default 30 s, plus 30 s per wait vote, at most 4 votes) the relay orders `Takeover` with the lobby's policy: AI,
  neutral or defeated. Pauses: per-player budget from the lobby, any player resumes, a pause ends by itself after 2
  minutes; the tick deadlines move by the paused time.
* **Security** (§19.6, REQ-NET-11, G-05): session tokens and lobby codes from `RandomNumberGenerator`, tokens compared
  in constant time; per address 5 connection attempts or browse queries per second (burst 20) and 4 open lobbies; per
  member 200 messages and 48 KiB per second (bursts 400 / 192 KiB; a snapshot the relay asked for is exempt); chat 5
  lines per 10 s and 256 UTF-8 bytes with control characters removed; names and chat are sanitised before they reach
  other players. Every decoder checks counts against both a limit and the remaining input before allocating; any
  malformed message disconnects its sender. The relay never forwards a peer's address to another peer.
* **LAN discovery**: hosts listen on UDP 47624 and answer a broadcast query (also sent to loopback) with their relay
  port (default 47625) and lobbies; the browser shows them, nothing in an answer is trusted beyond display.

## Alternatives considered

* **Relay in `Tzar2.Server`, a second LAN relay in Game:** two implementations of the tick authority, against "same
  code"; and Server is net10.0.
* **Net references Sim to stamp commands itself:** §7.2 forbids it, and the relay would then decode every command it
  forwards (more attack surface) for no gain: the game decodes each entry anyway.
* **Lobby state in the relay as typed fields (civ, team, map):** every gameplay setting would need a protocol change;
  the blobs keep the relay independent of rulesets and mods.
* **Browsing over HTTP only:** the internet server offers HTTP too (ADR 0161), but the game would need a second
  address; one UDP datagram works for the relay and LAN hosts alike.

## Consequences

* The spike stays in `spikes/S3-net` as the measurement harness; the production protocol is not wire-compatible with it
  (magic `T2NP`, protocol version 1).
* A lobby's game version must match the host's exactly (G-04); the protocol version is checked in the hello.
* LiteNetLib assembles fragmented reliable messages before the relay sees them: a peer can make the relay buffer one
  large message before the byte budget disconnects it. Bounded by LiteNetLib's fragment limits; accepted for 1.0.
* Spectator slots (G-08), casual resync and in-match speed votes are not implemented.
