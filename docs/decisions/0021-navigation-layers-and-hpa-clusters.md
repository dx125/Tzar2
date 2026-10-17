# 0021. Keep navigation as derived per-movement-class layers with HPA* clusters, portal trees and components

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 part B)
* **Related:** TDD §9.1, §9.2 REQ-PATH-00/01/02/08, ORIG-03, §8.9, §22 item 1; ADR 0018 (map model), ADR 0017

## Context

REQ-PATH-01/02 ask for per-locomotor connectivity with O(1) reachability, HPA*-style 16×16 clusters with an abstract
graph, and incremental updates when obstacles change. Large units need clearance (§9.1: "large units occupy several
cells"). Every peer must compute exactly the same paths, and snapshots must restore to the same future. Building the
layers of a 512² map takes ~20 ms (measured, `PathfindingBenchmarks.NavigationBuild`).

## Decision

* **Movement classes.** `NavigationGrid` keeps one `NavLayer` per (locomotor passability, clearance) used by a mobile
  ground or water type. Locomotors whose base passability is identical on the map share a layer (the synthetic and
  classic land locomotors usually collapse into one); air flies straight (REQ-PATH-10) and has none. Clearance is a new
  `EntityTypeDefinition.ClearanceTiles` (1-3): a tile is walkable for clearance c if the c×c square anchored at it is
  passable and free of blockers; unit centers map to anchor tiles via `PointOffset = (c − 1) × 1,024`.
* **Moves and costs.** 8-connected without corner cutting, integer octile costs 1,024 / 1,448.
* **Clusters.** 16×16 tiles, split into regions (connected within the cluster). Each maximal run of walkable pairs
  across a border is an entrance with one transition (runs < 6) or two (the run ends); each transition is a pair of
  portal nodes. Node id = `cluster × 32 + index`; portals are listed N, E, S, W in ascending coordinate.
* **Portal trees.** For every portal, a Dijkstra over its cluster stores the direction to the parent per tile. It
  yields the intra-cluster portal costs, refines abstract edges without searching, and lets units follow a route by
  descending the trees (ADR 0022).
* **Components.** Union-find over (cluster, region) keys joined by portal pairs, labelled canonically (by smallest
  key), so `AreConnected(a, b)` is two array reads.
* **Compact graph.** After each update the layer also builds a CSR adjacency over compact node ids (ascending node-id
  order, so tie-breaking is unchanged), which the searches use (1.8× faster route queries than the per-cluster arrays).
* **Incremental updates.** `MapState` logs tiles whose effective passability changed (blocker count 0 ↔ 1); a layer
  recomputes walkability around them, rebuilds the regions of dirty clusters, the portals, partner links and trees of
  dirty clusters and their 4-neighbours, then the components and the compact graph. The result is byte-identical to a
  rebuild (property test `Incremental_updates_equal_a_rebuild`).
* **Derived state.** Navigation is a pure function of the map's passability: it is built at match load (the
  `Simulation` constructor, also after a restore), never hashed or snapshotted. Snapshots that change blockers wholesale
  trigger a rebuild.

## Alternatives considered

* **Plain grid A\* with a node limit:** reproduces ORIG-03 (depth-limited search). Plain A* on the 512² maze expands
  126,226 nodes for one long query vs. 5,316 for HPA* (`A_long_path_across_a_512_maze...`).
* **Global connected-component labelling on the tile grid:** O(map) per obstacle change; the region graph is O(clusters).
* **Clearance as a per-tile distance transform used by one shared layer:** fewer layers, but clusters, portals and
  components would then depend on the query's clearance; separate layers keep every query on a fixed graph.
* **Snapshotting navigation:** 6+ MB per layer at 512² for data that is a pure function of the map.

## Consequences

* Memory per layer at 512²: walkability + regions 0.5 MB, region tables 2 MB, portal trees ≈ 8 × 256 B per cluster.
  At 1,024² roughly 4×.
* A blocker change costs O(dirty clusters × portals × 256) plus a pass over all regions; trees chopped every second are
  cheap, but a full rebuild happens on snapshot restore.
* Gates (per-player passability) and bridges need per-player/overlay layers later; they extend `NavLayer`.
