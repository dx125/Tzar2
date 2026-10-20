# 0174. The fidelity gate moves from M3 to M8; a missing rate is measured once and applied to all

* **Status:** Accepted
* **Date:** 2026-10-19
* **Deciders:** product owner, maintainer (Claude)
* **Related:** ADR 0112 (fidelity suite), ADR 0113 (batches E–I), ADRs 0169–0173 (measurements), TDD §22.3, §25

## Context

TDD §25 makes "fidelity suite ≥ 80 % passing" an exit criterion of M3. After batches A–E the suite passes 9 of 30
checks. The batches showed what is cheap to learn from the original and what is not: one measurement of a conversion
(TIME 1.25 s per unit, carry loads, walking speed by the walk sheet's stride, the 37.5-minute day, groups at their
slowest member's speed) settles every entity at once, while unit-by-unit duels, research, capture and similar
scenarios fail on the original's own behaviour (its AI takes over units handed to it, Ctrl+C picks one of three
castles, the Hard AI ends passive games) and cost an operator run each. Nobody has played the rebuild yet.

## Decision

1. **The fidelity gate (≥ 80 % of the §22.3 checks) is an M8 exit criterion** ("remaining fidelity items"), no longer
   M3's. M3 exits on skirmish against the Hard AI on all map types and the hotkey parity checklist. The suite, the
   facts and `Tzar2.Fidelity.Tests` stay as they are and keep failing on a measured mismatch.
2. **No more unit-by-unit measurements.** Where the original's tables hold a value and a conversion has been measured,
   the conversion applies to every entity. Where a value is missing (a rate), it is measured once on one object and
   applied to all entities it plausibly covers, marked as such in the placeholder registers.
3. Capture scenarios that do not serve such a single measurement are not run again until M8.

## Alternatives considered

* **Keep the gate in M3:** M3 would stay open for months of operator runs whose results mostly confirm the tables,
  while play-testing, the campaigns and the Windows beta wait.
* **Drop the suite:** it is the oracle for the final fidelity work (RISK-06); it costs nothing to keep.

## Consequences

* The next work is the gathering rate (one measurement, `gather-rate`), tracing for diagnosis, and a playable build for
  the owner's first play-test; then the campaigns (M4) and the Windows beta (M7).
* STATUS lists M3's gate under M8; TDD §25 is read with this ADR.
