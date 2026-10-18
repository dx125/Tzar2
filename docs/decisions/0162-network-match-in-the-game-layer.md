# 0162. Run multiplayer matches through a NetworkMatch in Tzar2.Game that feeds GameSession from the relay's closed ticks

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (M6)
* **Related:** REQ-NET-02…08, REQ-ARCH-03, TDD §7.2-7.4, §19.2-19.3, ADR 0070 (GameSession), ADR 0073 (fingerprint), ADR 0160

## Context

ADR 0160 put the protocol in `Tzar2.Net`, with opaque command payloads. The client may only use Game (§7.2), and
`GameSession` (ADR 0070) is what the game screen drives. Multiplayer needs the same session with the relay as the clock:
the lobby's settings must turn into the same `MatchSetup` on every peer, commands must be stamped with the kingdom
of the relay slot that sent them, and the sim thread must answer snapshot requests between ticks.

## Decision

* **`NetworkMatch`** (public, `Tzar2.Game.Multiplayer`) is a player's whole multiplayer session: `Create`/`Join`/
  `Rejoin(ReconnectTicket)`, lobby views (`LobbyView`, `LobbySlotView`, `PlayerView`, enums mirroring the Net ones),
  host operations, chat, saves, pauses, wait votes. No Net type appears in its public API used by the client.
* **Lobby blobs**: the host's settings blob is `MatchSettings` (ruleset, test or random map source with its options,
  seed and every lobby option that reaches the sim, speed, resume hash); each slot's choice blob is `SlotChoice`
  (civilization, team, difficulty). Both are versioned, bounded binary; a bad choice falls back to the default.
* **Lobby → match**: the occupied slots (human or computer), in slot order, are kingdoms 0..n-1
  (`LobbyDescription.Build`). The result is a `MatchDescriptor` (settings + kingdoms with names), from which every peer
  builds the same `MatchSetup` (`Multiplayer = true`, `LocalSlot`). The driver maps relay slots to kingdoms and back.
* **Loading barrier**: each peer builds its `GameSession` on `Load` and reports the fingerprint parts
  `[ruleset hash, map hash, settings hash, AI programs hash, resumed save hash]` with the initial state hash. A peer
  that cannot build the match reports a random part, so the barrier always stops the match with a clear message.
* **`GameSession` in lockstep mode** (internal `ILockstepDriver`): without a driver nothing changes. With one, the
  sim thread loops "pump the network, run every closed tick that arrived (up to 256 per pass, pumping every 16 while
  catching up)"; `Submit` still queues local commands, and the driver sends them as one payload per batch; `Paused`
  asks the relay; game speed is the lobby's (the relay's tick length). `PumpLockstep` runs the same loop from the
  caller's thread for headless peers (soak, tests).
* **Closure → command set** (`LockstepDriver.TryTakeTick`): the relay's control events first (ADR 0163), then each
  entry's payload decoded with `CommandCodec.ReadBatch` and stamped with its kingdom. A payload that does not decode,
  or would overflow the tick's command list, is skipped — every peer sees the same bytes, so every peer skips it. Peers'
  `SetController` commands are dropped.
* **Hashes**: after closure tick T, if `(T + 1) % 10 == 0` the peer reports the full-state hash when the sim computed
  one in that tick (every 20 ticks), else the per-tick hash (REQ-SIM-12, REQ-NET-04).
* **Threading**: before the match `NetworkMatch.Poll` (owner thread) handles the relay's events; once the session
  exists, only the thread that runs its ticks does (the sim thread, or the owner through `PumpLockstep`), so snapshot
  captures for rejoin, desync and save always happen between two ticks on that thread. UI messages go through
  `GameSession.DrainChat` (match) or `NetworkMatch.DrainChat` (lobby).
* **Rejoin** (REQ-NET-06): `NetworkMatch.Rejoin` with the ticket (code, slot, token); the relay sends the lobby, the
  match parameters and a peer's snapshot plus backlog; the peer restores the session, checks the snapshot's state hash,
  and catches up through the backlog. Restored sessions are not recorded (ADR 0164).
* **Chat** (REQ-NET-03): `GameSession.SubmitChat(text, target)`: `hm*` codes become cheat commands as in single player
  (the sim refuses them unless the lobby allowed cheats; `MatchSettings` never allows them in a ranked match); other
  text goes to all, allies (own kingdom, team mates, mutual allies), enemies or a custom set, converted to relay slots on
  the tick thread.
* **LAN** (REQ-NET-01): `LanHost` runs the same `RelayServer` on a background thread with the LAN beacon and writes
  desync reports locally; the host joins it on loopback. `LanBrowserService` and `LobbyDirectory` list LAN and internet
  lobbies.

## Alternatives considered

* **A second session class for multiplayer:** the game screen, frames, events, scenario presenter and saves would have
  to be duplicated or abstracted; the driver hook keeps `GameSession` one class with one tick function.
* **Lobby settings as the full `MatchSetup`:** the per-slot choices belong to the players, not to the host (the
  original lets each player pick civilization and team), so the host's blob holds only what the host owns.

## Consequences

* Only test and random maps can be played in multiplayer for now: a scenario map would need map transfer through the
  relay (REQ-NET-08, ≤ 100 MB, hash-verified), which is not implemented.
* The local player's first command reaches the sim after `D + 1` ticks plus the half RTT (ADR 0009); immediate local
  feedback (G-01) is the client's job.
