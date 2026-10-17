# 0023. Share hierarchical flow fields among groups of six or more, at most 48 at a time

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 part B)
* **Related:** TDD §9.2 REQ-PATH-04, ORIG-02, §8.9 (flow-field caches with obstacle versioning), ADR 0021, ADR 0022

## Context

REQ-PATH-04: when ≥ 6 units with the same locomotor go to the same goal, compute a shared flow field, cached by
(goal, locomotor, obstacle version) with LRU eviction. A tile-level integration field over a 512² map is ~1 MB and
several milliseconds per goal. The synthetic benchmark issues 128 group orders per tick; without a bound 710 fields were
alive at once (180 MB).

## Decision

* **Hierarchical field.** `FlowField` = an abstract Dijkstra from the goal over the compact portal graph (cost and next
  hop per portal; 8 bytes per portal) plus **local fields** per cluster (cost and direction per tile), built on demand
  when a unit is in the cluster: a multi-source Dijkstra seeded at the cluster's exit portals (portals whose next hop
  is their partner) and, in the goal cluster, at the goal tile. A unit steers to the farthest visible tile within 10
  steps of the descent, refreshed every 8 ticks or when reached.
* **Purity and caching.** A field is a pure function of (layer, goal tile, navigation version): `FlowFieldCache` may
  build, keep or recycle fields freely without affecting results. A field used by a unit this or last tick is never
  recycled (no thrash for live groups); beyond 64 cached fields the least recently used idle one is recycled. Local
  fields come from one shared pool. New fields of a batch are built in the path jobs.
* **Budget.** The first flow member of a group is charged the field's settled-node count (deterministic: Dijkstra's
  settle order does not depend on history).
* **Bound.** A group uses a flow field only if fewer than `MaxFlowGroups` = 48 distinct fields are referenced by units
  (counted from the units' state at the start of the tick's preparation, so it is deterministic and survives restore);
  further groups plan individual paths.
* **Open ground.** A flow unit with a clear corridor to its slot walks straight to it (`MotionMode.Direct`), so compact
  formations translate instead of funnelling into the goal tile; the field matters where walls are in the way.
* **Progress.** Flow progress is the field's cost at the unit's tile (strictly decreasing along the descent).

## Alternatives considered

* **Full tile integration fields per goal:** 1 MB and milliseconds per field on 512²; the hierarchical field costs
  ~146 µs to build on the 512² maze (`FlowFieldBuild`) and local fields only where units are.
* **Corridor-limited fields:** cheaper, but units pushed out of the corridor need a fallback and the corridor must be
  stored in the state to stay deterministic.
* **Early-terminating abstract Dijkstra:** the charged cost and coverage would depend on which units asked first.

## Consequences

* Memory: ≤ ~112 fields × 8 bytes × portals (≈ 64 KB each on a 512² maze) plus local fields.
* A group that cannot get a field (49th concurrent goal) still gets slots and individual paths: behaviour degrades in
  cost, not in correctness.
* Local fields are built in the movement pre-pass (one 256-tile Dijkstra per newly entered cluster) and are not
  charged to the path budget; after an obstacle change every live field is rebuilt there. Bounded by 48 fields ×
  clusters entered, but a candidate for moving into the budgeted path phase.
* Revisit `MaxFlowGroups` with real AI/human command rates (M3).
