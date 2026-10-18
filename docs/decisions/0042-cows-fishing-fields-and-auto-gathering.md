# 0042. Gather from footprint-less nodes and animals; boats deliver to harbors; neglected fields decay

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 stream A)
* **Related:** TDD §10 GAME-02, §8.5 step 5, REQ-PATH-08, ORIG-01, ORIG-05, UNK-17, ADR 0035

## Context

GAME-02: peasants chop, mine, farm and tend cows (milk and meat); fishing boats; drop-off at castle, storage, lumber
mill, farm and harbor; auto-gathering after building a storage or lumber mill (1.05); fields need constant care and
disappear if neglected; cows after Domestication; cow poaching (Arabian). `RES.INI` gives a fish patch's amount; the
fish patch object has no box; the cow's object INI has `max_manna = 50`. Rates, capacities and decay are UNK-17.

## Decision

* **Nodes without a footprint.** `ResourceNode` is allowed on any type: fish patches and meat are immobile and
  footprint-less (never blocking, never in the occupancy grid: ORIG-01/05), cows are mobile units. Reach is the body
  distance (center minus radius) instead of the footprint rectangle; a worker walks to the node's position.
* **Cows.** ⚠ HYPOTHESIS: a cow's `max_manna` is its milk (`legacy.milkClasses: [cow]`): the food it holds, regrowing
  one unit per `ResourceNode.regenMs` (UNVERIFIED 4 s) and kept when empty (`keepWhenEmpty`). Peasants milk own cows
  (`ResourceNode.gatherMs`, UNVERIFIED 1.5 s per unit). An explicit Attack on an own cow butchers it; any dead cow
  (`SpawnOnDeath`) leaves `comm.meat`: a Nature node of 100 food (UNVERIFIED) that rots after 120 s (`Lifetime`,
  UNVERIFIED) and anyone may harvest. Cow poaching is an instant capture (ADR 0044).
* **Fishing.** Fishing boats are ship-locomotor harvesters (capacity 20, UNVERIFIED). `DropOff.access` says who may
  deliver: `land` (default) or `water`; the harbor takes food from boats only (UNVERIFIED) and is placed on the coast
  (`Building.placement: coast`: walkable land with water for ships within two tiles). Across the shore — a boat at a
  harbor, a peasant at a ship — the work reach is one tile longer, because the shore tiles between land and water carry
  neither on the current test maps.
* **Fields.** Trait `Decay {afterMs, healthPerMinute}` (UNVERIFIED 60 s, 30 HP/min): a completed field untended for
  longer loses hit points and disappears at 0 (`Decayed`); gathering from it resets the neglect.
* **Technologies.** Gathering time = node or harvester time × 10,000 / (10,000 + `gatherRate` bp of the *node* type)
  for the worker's owner (Rotating Fields, Irrigation, Saw, Stone/Gold Mining, Ore Flotation); `carryCapacity` too.
* **Auto-gather (1.05).** `DropOff.autoGather`: when such a building completes, each builder that can gather starts on
  the nearest node of a resource the building accepts (within `nodeSearchRadiusTiles`); a completed field's builder
  farms it (UNVERIFIED).
* **Drop-offs per civilization** (manual ch. 5): templates `^<civ>.<class>` (new: the importer inherits them) give
  the European storage gold and iron, the Asian gold and stone, the Arabian iron, gold and stone.

## Alternatives considered

* **Meat and fish as decals:** decals have one shared lifetime and are not entities that can hold stock or be
  targeted; nodes reuse the whole gathering path.
* **Milk as an unlimited node:** the data gives each cow a capacity; a regrowing stock keeps that number meaningful.

## Consequences

* Captures of the rates (UNK-17) replace the YAML placeholders; the shore reach goes away with the terrain-set
  conversion if shores become passable.
