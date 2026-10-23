# 0211. Civilizations are an ordered list in the ruleset

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), code review of work queue item 16
* **Related:** ADR 0040 (a kingdom's civilization); TDD §10 (triggers); `LegacyMap.Race`;
  [code review 2026-10](../reviews/2026-10-code-review.md) (readiness for a fork, finding 1)

## Context

A kingdom's civilization is an index into `Ruleset.Civilizations` (ADR 0040). That list was the set of `civ` keys the
types and order gates use, sorted ordinally: `[arab, asia, comm, euro]`. The order was an accident of the spelling,
and it leaked:

* **A bug:** the legacy trigger variable `Race` returns the index + 1, so it read 1 for an Arabian kingdom, 2 for an
  Asian one and 4 for a European one. The original's maps number 1 European, 2 Arabian, 3 Asian (`LegacyMap.Race`, read
  the same way by the map converter). A trigger testing `$race == 1` for Europeans never fired.
* A kingdom with no objects of a civilization defaults to the list's first non-common entry: `arab`.
* The common key `comm` was a literal in Sim (three places), the AI and the client.
* A fork with more races cannot choose where they go. Adding one shifts the others, and the race numbers with them.

## Decision

* The ruleset lists its civilizations in `ruleset.civilizations`, in order, and names its shared key in
  `ruleset.commonCivilization`. The classic ruleset lists `[euro, arab, asia]` and `comm`, the original's race order.
* `Ruleset.Civilizations` is the listed civilizations in their order, then the common key (when types use it), then any
  other key the types or order gates use, sorted. A ruleset without the field keeps the old sorted list.
* A key listed twice, or the common key listed, is a compile error.
* `Ruleset.CommonCivilization` and `IsPlayableCivilization` replace the `"comm"` literals in Sim, the AI and the game
  screen. The common key is part of the ruleset hash.
* `Race` stays the index + 1, which now gives the original's numbers. A kingdom with no objects of a civilization
  defaults to `euro`, the first one listed.
* A fork adds a civilization by listing it. A listed civilization with no types yet is allowed, so its index is fixed
  before its content exists.

Hashes change on purpose: every kingdom's civilization index changes, and with it the state hashes. The AI's command
stream is unchanged, which shows the match plays the same. Re-pinned: the gameplay and AI golden hashes, the synthetic
mission's trigger hash, and the CI bench hashes of the gameplay and AI scenarios.

## Alternatives considered

* **Map `Race` through a table in the trigger engine** (`euro` → 1 and so on): this fixes the bug but leaves the order
  to sorting and the default to `arab`, and it is a second list of the same civilizations.
* **A civilization definition block** with a name, a race number and HUD assets each: this is the right shape once a
  fork has assets of its own. For now the order is all the engine reads, so the list stays as small as that.

## Consequences

* Race numbers, the default civilization and the order of the client's lists all follow one list in data.
* Not done here, and listed in the review: the scenario model's `enum Civilization { European, Arabian, Asian, Random }`,
  the client's own `["euro", "arab", "asia"]` lists, the diplomacy icon row, map generation's list and `"euro"` defaults.
  They should read `Ruleset.Civilizations` next.
* Revisit when a fork needs per-civilization data beyond the order (names, icons, colours): grow the list into
  definitions.
