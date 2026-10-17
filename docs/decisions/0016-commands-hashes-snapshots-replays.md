# 0016. Commands, state hashes, snapshots and replays as hand-written binary formats

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 part A)
* **Related:** TDD §7.3, §7.4, §8.5-§8.7, §19.2, §19.4, §19.5, REQ-SIM-11/12/13, REQ-ARCH-02/03, REQ-NET-04/09/11,
  ADR 0005, ADR 0009, ADR 0014, ADR 0015

## Context

The sim needs a command model for lockstep and replays, per-tick and periodic full-state hashes, versioned
snapshots for saves/reconnects/keyframes/desync dumps, and an immutable read path for the presentation. Everything
must be AOT-safe (REQ-ARCH-02) and parsers bounded (REQ-NET-11).

## Decision

**Commands (`Tzar2.Sim.Commands`).**
* `Command` is a 32-byte unmanaged header: kind (`ushort`, wire-stable, append-only; `0xF000+` debug/cheats),
  player slot, flags (Shift/Ctrl/Alt, §9.3), target entity, X, Y, two arguments, and an (offset, count) into its
  `CommandList`'s entity buffer. Entity sets are canonical: sorted, de-duplicated by slot, `None` dropped.
* `CommandList` serves both as a client's per-tick batch and as a tick's closed set. `AppendStamped(slot, batch)`
  overwrites the player of every command with the sender's slot (ADR 0009: the relay stamps, the client is never
  trusted). Execution order is the order in the closed set (the relay's slot order).
* `CommandCodec`: LEB128/zigzag varints; batches omit the player byte; entity sets are delta-encoded (index delta +
  generation). A 12-unit move order is ≤ 48 bytes. Decoding is bounded (4,096 commands per list, 4,096 entities per
  command, canonical deltas) and throws only `InvalidDataException` (fuzzed by an FsCheck property).
* Commands are re-validated when executed (pipeline step 1): player slot active, kind known, entities alive and
  owned, coordinates on the map, cheats allowed. Invalid entities are skipped; a command with no valid entity, or
  invalid arguments, is ignored and reported as a `CommandRejected` event with a `CommandRejection` reason.

**Hashes (`StateHashing`, xxHash64 via `StateHasher`, ADR 0005 length-prefix contract).**
* Per-tick hash after every tick: layout version, tick, entity registry (generations, alive flags, recycle queue),
  every hot store (store index, entity span, component span), player states, RNG streams.
* Full-state hash every `FullHashIntervalTicks` (default 20): the same plus cold stores and the map's dynamic layers
  (obstacle version, blockers, occupancy). Both are returned in `TickResult`.

**Snapshots (`WorldSnapshot`, `T2SN` v1).** Magic, version, ruleset and map content hashes, `SimulationConfig`,
tick, registry, RNG, raw player states, map dynamic layers, every store (key, tier, contents: hot stores are raw
block copies of the dense arrays), and a trailing full-state hash that `Restore` recomputes. Restoring checks the
ruleset/map hashes, store keys/sizes/order, entity liveness, and the hash. Re-capturing a restored world is
byte-identical; save → load → continue gives the same hash stream (tested at several split ticks, with a different
worker count after the restore). Little-endian only, like `StateHasher`.

**Replays (`Tzar2.Replay`, `T2RP` v1).** Header (engine version, ruleset/map hashes, config, overflow policy, hash
and keyframe intervals) + records: tick (closed command set, state hash every 10 ticks as in REQ-NET-04, full hash
when produced), keyframe (the snapshot before tick T, default every 1,200 ticks = 60 s), end. `ReplayVerifier`
re-simulates and reports the first mismatching tick; `SeekTo` restores the nearest keyframe and fast-forwards.
Replay depends on Core and Sim only (TDD §7.2): it compares the simulation's `RulesetHash`/`MapHash`, and the caller
supplies the ruleset and map through factory delegates.

**Presentation boundary (REQ-ARCH-03).** `RenderSnapshot` holds the entities with identity and position (ascending)
and the tick's events; `RenderSnapshotExchange` is a lock-free triple buffer (the sim publishes without waiting,
the reader gets the newest complete snapshot). `SimEvent`s (spawned, destroyed, arrived, blocked, command rejected)
are output only: not state, not hashed, not snapshotted.

**Amended by [ADR 0019](0019-sim-hardening.md):** snapshot format v2 (no static or derived map layers, full
semantic validation on restore), streaming replay parse, stricter command lists and keyframe/seek checks.

## Alternatives considered

* **MessagePack / System.Text.Json source-gen for snapshots:** AOT-safe, but per-field encoding defeats the
  block-copy design and is slower; JSON is for tooling (`t2 desync diff` may render snapshots as JSON later).
* **Fixed-size command structs with inline entity arrays:** simpler, but a 200-unit selection would need a 1 KB+
  struct or splitting; the shared entity buffer keeps commands compact.
* **Hash the snapshot bytes as the full hash:** simple, but forces a full serialization every 20 ticks; the
  `StateHasher` walk over the same spans costs no allocation.

## Consequences

* Changing a hot component, the store order, the hashed layout or the codec changes the golden hashes in
  `Tzar2.Determinism.Tests` and must bump `StateHashing.LayoutVersion` / the snapshot or replay format version.
* `SimulationConfig` is part of snapshots and replays; adding a field requires a format version bump.
* Open: `t2 replay verify` and `t2 desync diff` CLI front-ends (M6) on top of `ReplayFile`/`WorldSnapshot`;
  replay compression; Lua `state` tables and trigger state in snapshots once Scripting exists (REQ-SIM-14).
