# 0019. Treat snapshots and replays as untrusted input; ticks never fail half-way

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 hardening), after the review of PR #8
* **Related:** TDD §7.3, §8.4-§8.7, §19.2, §19.4, §19.6, REQ-ARCH-03, REQ-NET-06, REQ-NET-09, REQ-NET-11, ADR 0005,
  ADR 0014-0018

## Context

The M2 part A review (should-fix S1-S9, nits N1-N11) found the following problems:

* a restore checked only the structure. A crafted snapshot with a recomputed trailing hash (the hash is a checksum,
  not a MAC) could duplicate a free slot or carry an off-grid occupancy cell, and then fail mid-tick;
* replays accepted a keyframe whose snapshot held another tick;
* 3 occupancy cells per tile mapped points into the neighbouring tile;
* 65,536 units in one cell overflowed the `ushort` occupancy count mid-tick and left the world half-applied;
* snapshots and keyframes carried 10 MiB of map layers on a 1,024² map;
* parsing a 2-hour replay allocated 130 MiB;
* names longer than 256 bytes made saves that could not be loaded;
* nothing stopped overlapping `Tick`/hash/snapshot calls;
* the 12-bit generation wrapped after 4,095 reuses (ABA).

Reconnects (REQ-NET-06) restore snapshots sent by another peer, so a restore must survive hostile input.

## Decision

* **Snapshots are untrusted input. Restoring one validates every invariant**, then compares the trailing full-state
  hash:
  * registry: generations in range; the free and pending lists hold distinct dead slots and are disjoint; every
    recyclable dead slot is in exactly one of them; retired slots are never in the free list;
  * stores: sorted by entity, only live entities, element sizes match;
  * world: player flags; positions and motion targets on the map; identity type, owner and facing in range; health
    current ≤ max; motion state known; every occupancy cell equals the cell of its entity's position.

  Occupancy is **rebuilt** from the entities. A snapshot proves only that it is well-formed. Whether it is the
  *right* state is settled by the lockstep hash agreement: a rejoining peer's restored hash must match the other
  peers' hashes for that tick (REQ-NET-04/06). The checksum is not authentication and needs none.
* **Snapshot format v2 stores no static or derived map layers.** The map is referenced by its content hash; only
  the obstacle version and the non-zero blockers are stored (delta-indexed). A 1,024² map with one unit snapshots in
  under 1 KiB (was 10 MiB).
* **A tick never fails half-way on valid input:**
  * occupancy counts are `int` (they cannot overflow with ≤ 2^20 entities);
  * commands that create entities are rejected up front with `CommandRejection.EntityLimit` when
    `EntityRegistry.RemainingCapacity` is too small.

  If a tick still throws (a bug), the `Simulation` becomes `IsFaulted` and refuses further ticks and snapshots, so a
  half-applied world is never continued. Hashes remain available for the repro bundle.
* **Generation wrap:** a slot whose generation reaches 4,095 is **retired** at `CommitDestroyed` and never reused.
  Cost: at most one slot per 4,095 reuses of that slot. A stale handle can never resolve to a later entity.
* **Thread affinity and mutation guard:**
  * `Tick`, `ComputeStateHash`, `ComputeFullHash` and `CaptureSnapshot` refuse to overlap
    (`InvalidOperationException`);
  * between ticks the `World` is locked: `Spawn`, `Destroy`, `Set`, `SetCold` and `Remove` throw. Only match setup
    before the first tick, and the systems inside a tick, may change it (REQ-ARCH-03: the presentation reads render
    snapshots and sends commands);
  * `WorldSnapshot.Restore` is internal (it leaves a world partially overwritten on failure); `Simulation.Restore`
    discards that world;
  * the worker pool rejects nested `Run` calls instead of deadlocking, and captures every exception (OOM included)
    on helper threads.
* **System state lives in `World`.** Systems keep nothing across ticks, so `Simulation.Restore(…, configureSystems)`
  rebuilds the same pipeline.
* **Replays:**
  * `ReplayFile.Parse` validates all records in one streaming pass. It keeps a 24-byte index entry per tick and
    keyframe slices of the input (no copies). Commands are decoded on demand (`ReadCommands`): a 2-hour replay
    parses in < 16 MiB (was 130 MiB);
  * `SeekTo` requires the restored keyframe's tick to equal the keyframe record's tick and rejects targets past the
    end;
  * `Verify` rejects a start or end outside `[start, tick count]`;
  * `ReplayVerification.OverflowModeMatches` reports a Debug/Release overflow-policy mismatch.
* **Content and commands:**
  * `Ruleset` accepts 1, 2 or 4 occupancy cells per tile axis (divisors of 2,048);
  * `EntityName` rejects names over 256 UTF-8 bytes and invalid UTF-16;
  * `CommandList.Add` keeps the **newer** handle when two handles share a slot, and rejects `None` kinds and unknown
    flags, so every locally built list round-trips;
  * lists hold at most 32,768 entities in total, checked when building and when decoding; the decoder reuses a
    per-list scratch buffer;
  * `Fixed % -Epsilon` returns 0 instead of throwing (`long.MinValue % -1`).
* The allocation gate also measures all threads with 4 workers (in a non-parallel test collection, with a 1 KiB
  allowance per 200 ticks for runtime background threads).

## Consequences

* The golden full-state hashes changed, because the occupancy counts are now 4 bytes. The per-tick hashes did not.
  The new values are hash stream `297C6D90FC748B9E` and final hash `38232E7ED6D36D86` (ADR 0017).
* Snapshot format v1 files are rejected (none were published).
* Host code that changed the world between ticks must issue commands or run as a system. Setup before tick 0 is
  unaffected.
* Pathfinding (M2 part B) reads `MapState.Occupancy` as `ReadOnlySpan<int>`.
