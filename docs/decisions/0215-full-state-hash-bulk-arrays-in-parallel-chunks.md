# 0215. Hash the full state's large arrays in fixed chunks, in parallel

* **Status:** Accepted
* **Date:** 2026-10-23
* **Deciders:** agent (maintainer), code review of work queue item 16 (coordinator's plan: the full state hash)
* **Related:** REQ-SIM-12; ADRs 0005 (length-prefixed hashing), 0016 (state hashes), 0017 (fork/join);
  [code review 2026-10](../reviews/2026-10-code-review.md)

## Context

Every 20 ticks the sim computes the full-state hash (desync detection, replays, snapshots) on the sim thread, after the
tick's systems. It ran 5.4 ms on the slowest ticks on CI runners. On the review Mac, with an s2 world (4,000 units on
512², 8 kingdoms), one full hash took 0.56 ms:

| part | time |
|---|---|
| the map's dynamic layers (blockers, bridges, occupancy: 1.8 MB) | 0.28 ms |
| the paths (thousands of tiny appends) | 0.13 ms |
| the visibility bitsets, the stores and the rest | the remainder |

The hash was one xxHash64 stream over everything. That stream cannot be split across threads.

## Decision

* `BulkHash` (Sim) takes the full hash's large flat arrays: the map's blockers, bridges and occupancy, and each
  kingdom's explored and visible bitsets.
  * It cuts each array into fixed **64 KiB chunks** and hashes every chunk with **xxHash3** on the sim's workers
    (`DeterministicWorkerPool`), or inline without a pool.
  * Each array goes into the stream as its byte length followed by its chunks' hashes, in order.
  * The value depends on the data and the chunk size only, never on the worker count or on which thread hashed which
    chunk.
  * It allocates only when an array needs more chunks than ever before, so `AllocationTests` still hold.
* `PathStore.AppendTo` encodes its paths into a 16 KiB buffer and appends it in batches
  (`StateHasher.AddEncoded`). These are the same bytes as before.
* The tick hash, the hashed layout version and the snapshot format are unchanged. Only the full hashes change. They are
  re-pinned: the golden tests of the sim, gameplay and AI scenarios, and the three CI bench hashes. The AI's command
  stream is unchanged, so the match plays the same.

## Alternatives considered

* **Leave the occupancy out of the full hash:** it is derived from positions, so leaving it out would save a third.
  But it is exactly the kind of state that drifts in a desync bug, and the full hash exists to catch that.
* **Incremental (dirty-chunk) hashing of the map:** occupancy changes every tick almost everywhere units walk, so the
  chunks stay dirty, and keeping the bookkeeping right costs more than hashing.
* **xxHash3 for the whole stream:** that would change every tick hash and every pin for a smaller gain than doing the
  bulk in parallel.

## Consequences

* With an s2 world on the review Mac (best of five runs of 20), one full hash takes:
  * 0.31 ms inline;
  * 0.18 ms on 4 workers;
  * 0.56 ms before.

  The CI benches' `full hash` section shows the effect on the runners.
* `FullHashTests` pins the definition. A bulk array hashes as its length plus its chunks' xxHash3 values, for any
  worker count, including a partial last chunk and an empty array. The path store's batched encoding equals the old
  one-by-one appends.
* Old replays and desync reports carry full hashes of the old definition and no longer verify; replays were already
  tied to the engine version.
