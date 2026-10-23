# 0254. Make healing, freezing and raise dead skip machines and the bodiless

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), under ADR 0190
* **Related:** ADR 0250 (the curse), ADR 0190; ledger MAG-022

## Context

While reading the curse for ADR 0250, ReTzar showed two queries of the original:

* **Query `0x2719`** answers yes for golems, siege engines, magic carpets, ships, ghosts, skeletons and fishing boats.
  Healing, curses, freezing and raising skip those objects.
* **Query `0x271c`** names the creatures that leave no body to raise: bats, golems, carpets, mages, dwarves, orcs,
  jinns, ghosts, skeletons and dragons.

Tzar2 healed, froze and raised all of them.

## Decision

* `spell_heal` and `spell_freeze` take the same `targets.except` list as the curse (the machines).
* For `raisedead`, `targets` now names the corpses that may rise. `DecalIndex.TakeCorpse` takes a type filter.
  Machines and the bodiless are left out.

## Consequences

* MAG-022 is tested.
* The ruleset hash changes. The goldens follow where the scenarios heal or raise.
