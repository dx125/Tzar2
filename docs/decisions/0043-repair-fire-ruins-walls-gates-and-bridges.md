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
  there (worker task Repair). Owner decisions after the PR #21 review (free, linear repair let four peasants keep a
  house alive against ten footmen forever), all UNVERIFIED (UNK-18): one repairer restores
  `max × tick / (buildTime × repairTimePercent)` hit points (sped up by `buildRate`); further repairers on the same
  entity add less (`repairerFalloffPercent` 25: 100%, 75%, 50%, 25%, then nothing — effectively four); a full 0 → max
  repair costs `repairCostPercent` (50%) of the effective construction cost, charged pro rata as hit points come back
  (`RepairProgress` keeps the hit points restored, so the bill is `floor(cost × share × healed / max)` cumulatively,
  independent of how it splits over ticks); if the owner cannot pay, the repair pauses. The owner is re-checked every
  tick (a building captured in step 12 is not healed by its old owner's peasants). `Repaired` at full health.
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
  gate has `GateState {Open, Mode, Desired, LastChangeTick}`; closed, all its mask tiles block; open, the passage tiles
  do not. Owner decisions after the review (one legal order per tick toggling 200 gates cost 15.6 ms per tick):
  a gate changes at most once per `gateChangeIntervalMs` (1 s); `SetGate` (17) open/close orders on a gate that changed
  sooner are rejected for it (`GateBusy` when no listed gate takes the order); step 10 applies at most
  `maxGateChangesPerTick` (16) changes per tick in ascending entity order and only touches the passage tiles. Automatic
  mode (the default, `gate_subai_on`) wants the gate open while an own unit is within `gateOpenReachPx` (32) and closed
  once none is within `gateCloseReachPx` (64): hysteresis. The units come from a coarse grid (8-tile cells) rebuilt per
  tick, so the work is bounded by the units near gates. A gate never closes on a unit standing in its passage. Every
  kingdom passes an open gate: per-player passability (REQ-PATH-01, acceptance test §9.2.5) needs per-player
  navigation layers and is left to a pathfinding follow-up.
* **Bridges:** `Building.placement: bridge`: along the long axis the end rows stand on walkable land, every tile is free
  (land, water or shore; no cliff, structure or other bridge) and one is open water. A completed `Bridge` puts a deck on
  its tiles (`MapState` bridge layer, in snapshots and the full hash): land locomotors may cross, ships may not; a site
  puts nothing. Removing the bridge removes the deck (navigation updates incrementally, ADR 0021). The bridge footprint
  comes from its sprite box (UNVERIFIED as a footprint).
* **Architecture:** `buildRate` bp speeds construction progress (basis-point carry in `Construction`).

* **Restore:** the blocker and bridge layers are derived state; a restored snapshot must hold exactly the layers its
  structures produce (footprints, rotation, sites, gate states), otherwise it is rejected (a phantom deck or a stale
  gate flag would otherwise fail mid-tick).

## Alternatives considered

* **Gates as two entity types (horizontal/vertical):** doubles every gate and its upgrade; rotation is per entity.
* **Bridges as walkable structures over water via terrain edits:** the map's static layers come from `MapData` and are
  shared; a dynamic layer keeps them immutable.

## Consequences

* The client draws gates turned by reading `Footprint.Rotated` (render snapshot unchanged for now) and the open state
  from `GateState`.
