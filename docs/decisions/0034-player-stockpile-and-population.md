# 0034. Keep a fixed-size stockpile per player and recount population every tick

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 part C)
* **Related:** TDD §8.7, §10 GAME-01/05, REQ-SIM-12/13, UNK-14, UNK-20, ADR 0016, ADR 0019

## Context

GAME-01: food, wood, stone, gold (+ iron in the data); §11.2 allows a fifth resource by rules. GAME-05: houses raise
the cap, 1 population per unit in classic, castle `pop_count = 20`, a population limit per lobby. `PlayerState` is
hashed every tick as raw memory and must stay padding-free.

## Decision

* `PlayerState` = flags, `Resources` (an `[InlineArray(8)]` of `int`, indexed by resource type id), `Population`,
  `PopulationCap`, `PopulationLimit`. Stockpiles saturate at `int.MaxValue` on delivery and refund.
* **Population is recomputed** at the start of pipeline step 4 from the entities: units' `PopulationCost`, completed
  buildings' `PopulationProvided`, capped at the match limit. A unit trained later in the same step adds its cost at
  once. Recounting (≈ 4,000 identities, a few µs) keeps the numbers right after kills, deletes, cancellations and
  restores without bookkeeping at every structural change.
* Match settings in `SimulationConfig`: `StartingResources` (level index, -1 = ruleset default) and
  `PopulationLimit` (-1 = ruleset default). Snapshot format 4 and replay format 2 store them (the replay header now also
  stores `PathBudgetPerTick`, which v1 omitted).
* A full population does not block queueing; the finished unit waits (`ProductionStalled`, reason Population).

## Alternatives considered

* **Named fields (Food, Wood...):** no fifth resource without a layout change.
* **Incremental population counters:** faster in theory, but every spawn, kill, capture, completion and restore must
  update them; one missed path desyncs nothing (all peers miss it) but shows wrong numbers.

## Consequences

* The padding test counts `[InlineArray]` structs correctly (element × length).
* Golden hashes changed (PlayerState layout; ADR 0039).
