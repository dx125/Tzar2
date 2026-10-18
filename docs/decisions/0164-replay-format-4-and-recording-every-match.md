# 0164. Record every match into replay format 4 with the full settings, a setup description and chat

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (M6)
* **Related:** REQ-NET-09, §19.4, ORIG-14, G-05, ADR 0016, ADR 0019 (replay parsing), ADR 0073 (AI program hashes)

## Context

Format 3 (ADR 0073) stored the seed, the player count, cheats, tick length, path budget, starting resources and the
population limit, but not victory, game type, limits, fog, explore, day/night, weather, teams or ranked. A replay of a
skirmish with the default victory condition (Standard) therefore failed `ReplayVerifier`'s config check, and nothing in
the file said how to rebuild the map and the kingdoms for headless verification (`t2 replay verify`). Nothing recorded
matches yet: `GameSession` only offered a header.

## Decision

* **Format 4** adds to the header: the rest of `SimulationConfig` (as the snapshot stores it), a **setup description**
  (opaque to `Tzar2.Replay`, at most 64 KiB; the game writes a `MatchDescriptor`: map source, settings, kingdoms with
  civilization, controller, difficulty, team and name) and the player names; and a new record, tag 3 = chat line (tick,
  sender, recipients, ≤ 256 bytes). `ReplayFile.Parse` still reads format 3 (with the default config fields).
* **`GameSession` records every match that starts at tick 0** (single and multiplayer, REQ-NET-09): a keyframe snapshot
  every 1,200 ticks before the tick runs, every tick's command set and its checkpoint hashes, the players' chat lines.
  `FinishReplay()` returns the bytes. A restored match (quick load, rejoin, resumed save) is not recorded: the replay
  format starts at tick 0.
* **Re-simulation**: `MatchDescriptor.Parse(header.Setup).ToSetup(...)` and `GameSession.ForReplay(setup, ruleset)` give
  a tick-0 simulation with the same AIs (standby ones included); `ReplayVerifier.Verify` compares every stored hash.
  Matches whose map cannot be described (scenarios, campaign missions) record an empty description; verifying them
  needs the map from elsewhere.
* The last two keyframes are also kept in memory for desync reports (ADR 0165); multiplayer sessions keep them even
  when they do not record.
* Replays carry no personal data beyond the chosen player names (G-05).

## Alternatives considered

* **Store the compiled map in the replay:** large (a 512² map is megabytes) and needs a map serializer; the
  description rebuilds the same map deterministically and its content hash is checked anyway.
* **Record only multiplayer matches:** REQ-NET-09 asks for every match.

## Consequences

* Memory: a two-hour match with a large map holds 120 keyframes in memory (about a megabyte each at 4,000 units);
  streaming to disk is a follow-up when long matches need it.
* Replays of matches restored from a save are a follow-up (the format would need a starting keyframe at tick N).
