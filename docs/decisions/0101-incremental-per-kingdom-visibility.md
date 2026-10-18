# 0101. Compute visibility in the sim: per-kingdom reference-counted grids, bitsets and last-seen records

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 stream F)
* **Related:** TDD §5 ORIG-10, §8.5 step 14, §8.9, §10 GAME-12, §13.6, §21, UNK-09, UNK-27, ADRs 0070, 0071, 0100

## Context

GAME-12: unexplored areas are black; explored areas show terrain and, as last seen, buildings and resources; enemy
units only while in sight (manual ch. 5 §1.1); No Fog shows units in explored areas; Explore starts with the map
explored; Magic Vision and fog revealers reveal for a while; invisible units hide unless detected. The original's fog
bugs (ORIG-10: fog not reset on restart, invisible units not revealing) came from ad-hoc bookkeeping. The client derived
fog from sight radii every four ticks (ADR 0070), which a lockstep game cannot use for gameplay. Budget: 1.5 ms of a
12 ms tick at 8 kingdoms × 4,000 units on 512².

## Decision

* **Revealers.** Every unit and building of a kingdom (sites included) reveals a disk around its tile: radius
  r = round((sight + half its larger footprint side) / tile), sight from the type (technology effects applied; types
  without sight get `world.defaultSightTiles`; at night × `world.nightSightPercent`), clamped to 64 tiles; the disk is the
  tiles with dx² + dy² ≤ r² + r. Held units (stream E) reveal nothing. Resources and Nature reveal nothing.
* **Incremental grid.** Per kingdom an `int` count per tile and a visible bitset (count > 0). Each revealer keeps the
  stamp it applied (entity, tile, radius, owner); step 14 re-stamps only revealers whose tile, radius or owner changed
  and removes the stamps of revealers that are gone. Operations are queued per kingdom in identity-store order and
  applied by one job per kingdom (`DeterministicWorkerPool`), so the result does not depend on the worker count.
* **Combined view.** A second pass per kingdom ORs the visible bitsets of its vision sources (itself plus mutual allies
  that both share vision, ADR 0102) into the combined bitset, ORs that into the explored bitset, and with No Fog (lobby
  or `hmnofog`) shows the explored bitset as visible. Explore and `hmreveal` set every explored bit and record every
  structure of others.
* **Last seen.** Per kingdom a record per entity slot (`Ghost`: entity, type, owner, footprint tile, health and build
  per mille). Every `world.memoryRefreshTicks` ticks (staggered by slot) a visible structure of another, non-allied
  kingdom or of Nature is recorded as it is; a record whose tile is in sight but whose entity is gone or replaced is
  dropped. Frames draw remembered structures out of sight (also ones destroyed unseen) and never draw units out of sight.
* **Queries.** `CanSee(world, player, entity)`: own and shared-vision entities always; structures when any footprint tile
  is visible; units when their tile is visible and they are not concealed (cloaked and no detector of the player within
  its `Detector` radius). Combat acquisition skips concealed units.
* **Temporary reveals** (`Visibility.Reveal`: Magic Vision, fog revealers, scripted "explore area") are stamped at once
  and removed by the update of their expiry tick.
* **State.** Stamps, explored bits, records and temporary reveals are snapshot state; counts and combined bitsets are
  rebuilt from them, so a restored world equals the uninterrupted one bit for bit.
* **Presentation.** `RenderSnapshotExchange.Viewer` makes the render snapshot carry that kingdom's fog layer (one byte
  per tile: 0 unexplored, 1 explored, 2 in sight) and only what it sees; `FrameBuilder` does the same for the session. The
  client's derived fog (`FogOfWar`) is removed.

## Alternatives considered

* **Recompute every tick from scratch with bitset rows:** simpler, but ~800 k tile writes per tick at 4,000 revealers;
  the incremental stamps touch only movers that changed tiles.
* **Last seen by tile transitions:** exact, but shared vision makes "the moment a tile left sight" expensive to find;
  the staggered refresh is bounded and deterministic (a structure glimpsed for less than four ticks may be missed).

## Consequences

* Measured (Apple M5, Release, 4 workers): synthetic 8 kingdoms × 500 units on 512², p99 6.8 ms with visibility vs 7.3 ms
  without (noise: the system costs well under the 1.5 ms share); 8-kingdom gameplay bench p99 3.4 ms.
* A property test checks the incremental grid against a from-scratch computation after random walks, deaths and spawns,
  with 1 and 4 workers.
* UNK-27 (the fog's look) and UNK-09 (night effects) stay open; only constants change when they are measured.
