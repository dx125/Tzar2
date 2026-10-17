# 0018. Keep the compiled Ruleset/MapData contract small in Content/Compiled; derive map layers in Sim

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 part A)
* **Related:** TDD §7.2, §8.2, §9.1, §9.2, REQ-ARCH-01, REQ-PATH-00/01/02/08, UNK-10, UNK-11, ADR 0012, ADR 0015

## Context

REQ-ARCH-01: Sim consumes only compiled `Ruleset` and `MapData` objects and may reference Content only for those
types (§7.2). The rules compiler (§11.2) and the map converter (§18.5) do not exist yet, and another branch adds the
VFS/package code to Tzar2.Content. Pathfinding (§9.2, next task) needs passability per locomotor, an obstacle version
for cache invalidation, and an occupancy grid; maps go up to 1,024² tiles (REQ-PATH-00).

## Decision

* **Contract types in `src/Tzar2.Content/Compiled/` (namespace `Tzar2.Content.Compiled`)**, separate from VFS and
  packages: `Ruleset` (name, terrain types with `LocomotorMask` passability and water flag, minimal entity types with
  locomotor/speed/health, occupancy cells per tile axis, max players), `MapData` (W×H tiles, 1-1,024 per side;
  corner grids of terrain ids and heights, (W+1)×(H+1), as §9.1; per-tile cliff flags), `Locomotor`/`LocomotorMask`
  (foot, mounted, large, ship, air). Both are immutable, validated on construction and carry an xxHash64
  `ContentHash` for lobby checks and replay/snapshot headers. The rules compiler will produce them; the trait-based
  entity types of REQ-SIM-09 replace `EntityTypeDefinition`'s fixed fields without changing ids or keys.
* **`Tzar2.Sim.Map.MapState`** derives and owns the sim layers (row-major):
  * base passability per tile: AND of the four corner terrain masks; a cliff leaves only air;
  * blockers per tile (`ushort` count) for footprint obstacles; a blocked tile is air-only; every change increments
    `ObstacleVersion`, the key for connectivity/HPA*/flow-field caches (REQ-PATH-01/02/04). Corpses and debris never
    become blockers (REQ-PATH-08);
  * occupancy per sub-tile cell (`ushort` count; default 2×2 cells per tile, UNK-11), maintained by spawn, destroy and
    movement through the `OccupancyCell` component;
  * world units per §8.2: 2,048 per tile, so 1,024² spans 2,097,152 units (int32-safe). At 1,024² the layers take
    1 MiB (passability) + 2 MiB (blockers) + 8 MiB (occupancy).
* Dynamic layers are in the full-state hash. Snapshots store only the obstacle version and the non-zero blockers;
  base passability is recomputed from map + ruleset and occupancy (`int` counts) is rebuilt from the entities
  (amended by [ADR 0019](0019-sim-hardening.md); occupancy cells per tile axis are limited to 1, 2 or 4).
* Movement until §9.2 lands is a placeholder: straight line at the type's speed, facing quantized to 8 directions,
  stop with a `Blocked` event at a tile the locomotor cannot enter. (Replaced in M2 part B by the pathfinding and
  movement stack of ADRs 0021-0026; the map layers above are its input, extended by the changed-tile log and
  footprint structures.)

## Alternatives considered

* **Contract types in Sim:** Content's ruleset compiler would then reference Sim, inverting §7.2.
* **A separate `Tzar2.Content.Compiled` assembly:** cleaner isolation, but one more project for a handful of types;
  the folder keeps merges with the VFS work trivial.
* **Per-tile terrain ids instead of corners:** loses the 4-corner transition model of §9.1 (UNK-10).
* **Bit-packed occupancy (1 bit per cell):** smaller, but cells must count several small units (UNK-11).

## Consequences

* Pathfinding builds its connectivity labels and cluster graphs from `PassabilityAt`/`IsPassable` and invalidates on
  `ObstacleVersion`.
* Gates (per-player passability), bridges (water made passable) and coast/placement classes need additional layers
  when those objects arrive; they extend `MapState`, not the contract.
* Heights are carried but unused by the sim until terrain effects are known.
