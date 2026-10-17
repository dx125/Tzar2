# 0015. Use an in-house sparse-set ECS with index-sorted dense arrays

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 part A); product owner (32-bit `EntityId`)
* **Related:** TDD §8.4, §8.7, §8.9, §21, REQ-SIM-07, REQ-SIM-08, REQ-SIM-12, REQ-SIM-13, ADR 0005, ADR 0016

## Context

REQ-SIM-08 asks for unmanaged components in dense arrays with a sparse set per component type, hot/cold split,
block-copy snapshots and AOT safety, and recommends an in-house ECS; Arch and Friflo "MAY be evaluated" against
three criteria: **deterministic iteration**, **snapshotting**, **AOT**. REQ-SIM-07 adds: creation order is
deterministic and all system iteration is in ascending index order. ADR 0005 asked the owner whether 32-bit handles
(1,048,575 slots) suffice for 1,024² maps with mods.

Paper evaluation (documentation and source structure, no benchmark):

| Criterion | Arch 2.x (Apache-2.0) | Friflo.Engine.ECS 3.x (MIT) | In-house |
|---|---|---|---|
| Deterministic iteration in ascending entity index | No: archetype/chunk order; an entity changes position when its component set changes (structural change moves it to another archetype) | No: archetype order, same structural-move effect | Yes, by construction |
| Block-copy snapshot of the whole state | No built-in; Arch.Persistence serializes through MessagePack/JSON resolvers | JSON serializer; raw archetype memory not exposed as a stable format | Yes: dense arrays are raw `unmanaged` memory in a fixed store order |
| AOT/trimming | Mostly; source generators optional, some reflection paths | Advertised AOT-friendly | Yes: no reflection on the hot path, generic constraints only |
| Stable hash independent of history | No (order depends on structural history) | No | Yes (see below) |

Both libraries are faster than a sparse set for large archetype queries, but neither meets the first two criteria
without wrapping every query in a sort, which would remove that advantage.

## Decision

* **In-house ECS in `Tzar2.Sim.Ecs`.**
* **`EntityId` stays 32 bits (owner decision):** 20-bit index, 12-bit generation (ADR 0005). `EntityRegistry`
  allocates fresh slots in ascending order and recycles destroyed slots **first in, first out**, but only in the
  cleanup step (pipeline step 17, `CommitDestroyed`), so a handle destroyed in tick T is never reused in tick T and a
  slot's 12-bit generation wraps as late as possible.
* **Component stores are sparse sets whose dense arrays stay sorted by entity index.** Lookup is O(1) through the
  sparse array; appending a higher index is O(1); inserting below the highest index or removing shifts the tail (one
  `memmove` plus sparse fix-ups). Consequence: a store's byte content depends only on *which* entities have the
  component, not on insertion history, so two worlds in the same logical state have identical stores, hashes and
  snapshots, and iteration is in ascending index order without sorting.
* **Hot/cold split.** Hot components implement `IComponent`, are `unmanaged`, sequential and **padding-free**
  (`Tzar2.Sim.Tests` checks every implementing type by reflection), hashed every tick and snapshotted as raw memory.
  Cold components implement `IColdComponent<T>` (managed data such as names), are serialized per value and enter
  only the full-state hash.
* Stores are registered in a fixed order in the `World` constructor; the registry is sealed at the first tick, so
  the hash/snapshot layout cannot change mid-match. Type lookup uses a `Dictionary<Type, …>` that is never iterated.
* Structural changes go through `World` (`Set`, `SetCold`, `Remove`, `Spawn`, `Destroy`), which checks liveness;
  systems mutate component data in place through `Span<T>`/`ref` access.

## Alternatives considered

* **Arch / Friflo:** fail deterministic ascending iteration and raw snapshots (table above).
* **Swap-remove sparse sets (classic EnTT layout):** O(1) removal and still deterministic (the history is
  deterministic), but iteration order then depends on the add/remove history, so a restored world built in a
  different order would hash differently, and REQ-SIM-07's ascending order would need a sort per query.
* **Bitset scan over the sparse array:** ascending for free, but iteration cost grows with the highest index, not
  with the component count, and dense data is no longer contiguous.
* **LIFO or lowest-free-index slot reuse:** both deterministic; FIFO maximizes the time until a generation repeats.

## Consequences

* Measured (Apple M5, .NET 10, Release, `t2 bench`): 4,000 moving units on 512², 8 players, 128 move orders per
  tick: tick p50 ≈ 0.11 ms, p99 ≈ 0.7-0.9 ms; with 32 delete+respawn per tick p99 ≈ 1.0-1.3 ms (budget: 12 ms).
  Steady-state ticks allocate 0 B in Release (`AllocationTests`).
* Mass insertion into the middle of a large store is O(n) per insert. If profiling shows this (e.g. 10,000+ spawns
  into reused low slots in one tick), batch structural changes per tick and merge once (a sorted merge keeps the
  layout and hash unchanged).
* A store cannot hold two generations of one slot; `Set` throws if the sparse entry is stale, which only happens if
  a caller bypasses `World.Destroy`.
* **Amended by [ADR 0019](0019-sim-hardening.md):** slots whose generation reaches 4,095 are retired instead of
  wrapping; `World` refuses structural changes between ticks.
* Revisit the 32-bit handle before snapshots/replays are frozen for 1.0 if mods need more than 1,048,575
  simultaneous entities.
