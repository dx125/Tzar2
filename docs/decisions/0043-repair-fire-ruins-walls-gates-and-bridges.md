# 0043. Repair, burn and ruin buildings; drag walls; turn and open gates; lay bridge decks over water

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 stream A)
* **Related:** TDD §9.1, §10 GAME-03, REQ-PATH-01, REQ-PATH-08, ORIG-01, UNK-02, UNK-18, UNK-42, ADRs 0025, 0036

## Context

GAME-03: peasants build and repair buildings, ships and siege weapons; damaged buildings burn and peasants can repair
them (manual ch. 5); ruins; walls placed by dragging; gate orientation (Ctrl/Alt while placing); bridges need
Architecture and make water passable for land units. The data has no footprint
for gates (`anim_none`), the bridge box is the sprite's, and repair rates and fire thresholds are unknown (UNK-18,
UNK-42 new).

## Decision

* **Repair** (command 14): own, completed, damaged, `Repairable` target (`^Building`, `^Ship`, `^Siege`); builders walk
  there (worker task Repair). Each repairer in reach restores `max × tick / (buildTime × repairTimePercent)` hit points
  (UNVERIFIED 100%, sped up by `buildRate`; remainder carried), free of charge (UNVERIFIED). `Repaired` at full health.
* **Fire** (step 10, `StructureSystem`): a completed `Burnable` building below `burnThresholdPercent` (UNVERIFIED 50%)
  burns (`Burning`, `FireStarted`), losing `burnDamagePerMinute` (UNVERIFIED 60); above the threshold the fire is out; at
  0 it burns down (`Decayed`, then killed).
* **Ruins:** a destroyed completed `RuinOnDeath` building leaves a `DecalKind.Ruin` decal (never blocking, ORIG-01; the
  shared decal lifetime, so the ring buffer of ADR 0025 keeps its order).
* **Walls by drag** (command 16, `BuildWall`): Arg0 a `Wall` type, (X, Y) the start, Arg1 the end tile
  (`y << 16 | x`); a Bresenham line of sites (at most `maxWallSegments`, UNVERIFIED 64), each paid when placed; tiles
  that do not fit are skipped, the line stops at the first unaffordable segment. The builders start on the first; a
  builder whose wall site is done takes the nearest unfinished own wall site within `wallContinueRadiusTiles`
  (UNVERIFIED 6).
* **Orientation:** `Build` Arg1 = 1 turns a non-square footprint by 90° (`Footprint.Rotated`; the type's mask is read
  transposed). `World.FootprintRect` gives the rectangle on the map; placement, blocking, reach and exits use it.
* **Gates:** footprint and `Gate.passage` from YAML (UNVERIFIED 3 × 1 with the middle tile opening, UNK-02). A completed
  gate has `GateState {Open, Mode}`; closed, all its mask tiles block; open, the passage tiles do not. Automatic mode
  (the default, `gate_subai_on`) opens while one of the owner's units is within `gateOpenReachPx` (UNVERIFIED 32) and
  closes otherwise, never on a unit standing in the passage; `SetGate` (17) opens/closes manually or sets automatic.
  Every kingdom passes an open gate: per-player passability (REQ-PATH-01 "gates are portals with per-player
  passability", acceptance test §9.2.5) needs per-player navigation layers and is left to a pathfinding follow-up.
* **Bridges:** `Building.placement: bridge`: along the long axis the end rows stand on walkable land, every tile is free
  (land, water or shore; no cliff, structure or other bridge) and one is open water. A completed `Bridge` puts a deck on
  its tiles (`MapState` bridge layer, in snapshots and the full hash): land locomotors may cross, ships may not; a site
  puts nothing. Removing the bridge removes the deck (navigation updates incrementally, ADR 0021). The bridge footprint
  comes from its sprite box (UNVERIFIED as a footprint).
* **Architecture:** `buildRate` bp speeds construction progress (basis-point carry in `Construction`).

## Alternatives considered

* **Gates as two entity types (horizontal/vertical):** doubles every gate and its upgrade; rotation is per entity.
* **Bridges as walkable structures over water via terrain edits:** the map's static layers come from `MapData` and are
  shared; a dynamic layer keeps them immutable.

## Consequences

* The client draws gates turned by reading `Footprint.Rotated` (render snapshot unchanged for now) and the open state
  from `GateState`.
