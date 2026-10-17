# 0036. Place construction sites that block at once, grow with builders, and refund when cancelled

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 part C)
* **Related:** TDD §8.5 step 4, §9.1, §10 GAME-03, REQ-PATH-01, UNK-02, UNK-18, ADR 0025, docs/formats/dat.md §3

## Context

GAME-03: peasants build buildings; build rate vs. builders is unknown (UNK-18). Footprints come from the compiled DAT
grids (ADR 0032). Navigation already updates incrementally when structures appear (ADRs 0021, 0025).

## Decision

* **Placement** (`Build` command): the type must be constructible (footprint + build time), at least one listed own
  worker must be able to build it, the requirements must be met (completed required buildings; any technology
  requirement fails in M2), every blocked mask tile must be on the map and walkable land without a structure
  (`IsPassable(Foot)`: no water, cliff or blocker), and the cost must be affordable. Units standing there are allowed:
  movement pushes them off unwalkable ground (ADR 0025). The cost is paid at placement.
* **The site** is a structure with `Construction {progress, total}`: it blocks its tiles at once, starts with 1 HP, does
  not produce, is not a drop-off and is not harvestable. Builders assigned to it walk to its approach point.
* **Progress** (step 4, `ProductionSystem`): every builder in phase Building and in reach adds the tick's ms (linear in
  builders, UNVERIFIED UNK-18); health grows by the increase of `1 + (max − 1) × progress / total`, so damage taken
  while building stays taken. On completion the site loses `Construction`, gains its `Production` queue (and stock for
  fields) and emits `ConstructionCompleted`; its builders go idle.
* **Cancel** on a site refunds `constructionRefundPercent` (UNVERIFIED, 100%) and destroys it (no corpse, tiles freed).
  `Delete` destroys without refund.

## Alternatives considered

* **Blocking tiles only on completion:** units would walk through sites and get trapped when they complete.
* **Diminishing returns per builder:** plausible but unmeasured; linear is the simplest placeholder.

## Consequences

* Repair, burning, ruins, walls by drag, gate orientation and bridges are M3 (they reuse the site and footprint model).
