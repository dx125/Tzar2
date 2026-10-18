# 0165. Collect desync reports with keyframes before and after, and diff snapshots component by component

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (M6)
* **Related:** REQ-NET-04, REQ-SIM-12/13, TDD §8.8 (`t2 desync diff`), ADR 0016, ADR 0160, ADR 0164

## Context

REQ-NET-04: "On mismatch, all clients upload their nearest snapshot plus recent commands; the relay stores a desync
report (diffable with `t2 desync diff`)." To find the cause one needs the state where the peers still agreed, the
commands after it, and a diff that names the component and the entity that differ, not just "the hashes differ".

## Decision

* **Detection**: the relay compares checkpoint hashes (every 10 ticks) and takes the majority (ADR 0160).
* **Collection**: on the first mismatch at tick D the relay stops closing ticks and asks every connected peer for two
  snapshots: its current state ("after") and its latest keyframe from before tick `D − 9` ("before": every checkpoint
  up to it agreed). Peers keep their two latest keyframes (every 1,200 ticks; ADR 0164). After 10 s or when everyone
  answered, the relay writes a `.t2desync` bundle: lobby code, match id, D, majority hash, odd slots, the lobby
  settings and slots (enough to rebuild the match), every peer's snapshots with their ticks and hashes, and its closure
  log from the earliest "before" tick to the last closed tick. The match then ends with the reason.
* **Where reports go**: the internet relay writes them to its desync directory (ADR 0161); a LAN host into the local
  user directory.
* **`SnapshotDiff.Compare(World, World)`** (Sim) restores nothing itself; given two restored worlds it lists:
  * tick and settings;
  * each RNG stream by name;
  * player state field by field (flags, each resource, population, cap, limit, civilization, each technology);
  * the entity registry;
  * every hot store entity by entity, with the type key and owner, the byte range that differs and both values;
  * entity-set differences per store;
  * cold stores, map, decals, paths and kingdoms as wholes;
  * extension blocks (AI, triggers) by key.
* **`DesyncReports`** (Game) rebuilds the setup from a bundle and diffs two slots' "after" (or "before") snapshots;
  `t2 desync diff` prints it for two snapshot files or a bundle. Re-running the log from the "before" keyframe on a
  fixed build is the way to bisect the first diverging tick.

## Alternatives considered

* **Hash every component per tick and exchange the vector:** finer detection but more traffic for every match; the
  report gives the same information after the fact.
* **Diff raw snapshot bytes:** says where in the file, not which entity and component.
* **Casual-lobby resync from the majority snapshot** (REQ-NET-04 MAY): not implemented; a desync ends the match.

## Consequences

* A report holds two snapshots per peer: up to tens of MB for large matches. The server keeps the newest 200.
* Components are reported as byte ranges, not field names (field-level names would need reflection over the
  component types, which the AOT/trimming rules make awkward); the type key, owner and offsets are usually enough.
