# 0033. Extend the compiled contract with immutable gameplay traits, resources, settings, bonuses and map objects

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 part C)
* **Related:** TDD §7.2, §8.4, §9.1, §11.2, REQ-ARCH-01, REQ-SIM-09, REQ-DATA-05/06, ADR 0012, ADR 0018

## Context

ADR 0018 kept `Ruleset`/`MapData` minimal and said the trait-based types of REQ-SIM-09 would replace
`EntityTypeDefinition`'s fixed fields without changing ids or keys. Gathering, building, production and combat need
costs, times, armament, harvesting, producers, drop-offs, population and requirements; maps need placed objects.

## Decision

* `EntityTypeDefinition` keeps its movement/footprint fields and gains `Traits` (`EntityTraits`, an immutable record
  with `ImmutableArray`s): category (unit/building/resource/other), legacy class and civilization (stable keys,
  REQ-DATA-06), cost per resource type, build time, sight, defence, `Armament`, `Harvester`, `Builds`, `Producer`,
  `ResourceNode`, drop-off mask, population cost/provided, required building types and technology. `EntityTraits.None`
  keeps the movement test types unchanged.
* `Ruleset` gains resource types (≤ 8), `GameplaySettings` (group speed, population limit, starting-resource levels,
  minimum damage, reaches, acquisition interval, search radius, refunds) and a sorted damage-bonus table with
  per-attacker index (binary search). The constructor validates every cross-reference (ascending known ids, builders
  build buildings, producers are buildings producing mobile types, nodes and drop-offs have footprints...).
  `HashVersion` 3 covers all of it.
* World units everywhere: sight and ranges in world units, speeds in units/s, times in ms (REQ-SIM-02).
* `MapData` gains `Objects` (`MapObject`: type key, owner or `Nature` = 0xFF, world X/Y; structures by their
  footprint's top-left tile). `HashVersion` 2. `Validate(ruleset)` checks the keys. The `Simulation` constructor places
  them at match start (never on restore).
* The trait-to-component mapping lives in Sim (`World.Spawn`/`PlaceStructure`): `Armament` → `Combat`,
  `Harvester`/`Builder` → `Worker`, `ResourceNode` → `ResourceStock`, `Producer` → `Production` (completed buildings),
  construction sites → `Construction`.

## Alternatives considered

* **A trait list of polymorphic objects:** closer to the YAML, but every system would search lists; typed nullable
  traits are as data-driven for M2 and cheap to read in hot loops.
* **A dense n × n bonus matrix:** O(1) lookup but n² memory for large modded rulesets; the sorted table is small and
  lookups happen only on hits.
* **Map objects as a separate file:** the map hash must cover them anyway (lobby check), so they belong to `MapData`.

## Consequences

* New traits are added as nullable members plus hash and validation lines; the M2 movement tests and the synthetic
  benchmark rulesets compile unchanged.
* The legacy map converter (§18.5) fills `MapData.Objects` from `.wmp` object tables.
