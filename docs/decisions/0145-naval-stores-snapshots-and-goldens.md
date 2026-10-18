# 0145. Naval stores, snapshots, goldens and benchmarks

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (naval stream)
* **Related:** REQ-SIM-12/13, ADRs 0016, 0019, 0051, 0080, 0096, 0140-0144

## Decision

* Two hot stores appended after the stream E stores: `game.dock` (`Docking`: water point, unload point, move tick,
  retries, limit, purpose, padding) and `game.explore` (`Exploring`: goal tile, next tick). Snapshot format 9, hashed
  layout 7.
* Restore validation (snapshots are untrusted, ADR 0019): a docking belongs to a ship on the map with movement (and a
  holder state for boarding and unloading), its purpose is known, its points are on the map (the unload point exactly
  for unloading, zero otherwise), the limit is within the holder bound for unloading and zero otherwise, the move tick
  is not in the future and the retries within the bound, the padding is zero; an explorer is a unit on the map with
  movement, its goal tile is on the map or -1 and its next look at most one interval ahead. Eleven corruption tests.
* The AI's naval state is range-checked on read (phase, transport count, no transports outside a landing).
* Synthetic content gains invented hulk and xebec rows (not the original's numbers), shifting type ids; goldens and the
  CI bench hashes are re-pinned in the same commit, with the reasons in the tests.

## Consequences

* Save → load → continue matches through a landing in progress and through dockings and explorers.
* The gameplay, AI and S2 benches gain no measurable cost on land maps (the naval executor returns early; the naval
  system skips empty stores); measurements in the PR.
