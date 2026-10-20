# 0175. Peasants gather 900 ms per unit, measured once on wood and applied to every resource

* **Status:** Accepted
* **Date:** 2026-10-19
* **Deciders:** maintainer (Claude), product owner
* **Related:** ADR 0042 (economy), ADR 0170 (carry loads), ADR 0174 (measure once), UNK-17;
  [m2-placeholders.md](../mechanics/m2-placeholders.md)

## Context

The engine gathered 1,000 ms per unit (UNVERIFIED). Batches B and E timed the stockpile's steps: wood every 6.9 s and
4.55 s per load of 10, which looked like a gathering two or three times faster. The selection box of those scenarios,
however, took up to three peasants standing together, so the steps were deliveries of a group, not trips of one
peasant.

`gather-rate` (`capture-20261019-232318`, MP2's clamped bottom-left view, ADR 0171) was to build a lumber mill under a
tree and read one peasant's trips in memory. The mill was not built (its site overlapped the trees; the operator built
one by hand later), so the memory scan found nothing. Its burst of the tree (200 frames, 200 ms) shows the one peasant
chopping the tree at tile (19, 50) from the start of the burst until frame 38 and carrying the log away from frame 39
(50.3 s into the trial). The gather order was given at 39.8 s and the peasant stood about 57 px from its chopping place
(1.4 s at 40 px/s), so it chopped from about 41.3 s: **one load of 10 wood in about 9.0 s (± 0.5 s), 900 ms per
unit**.

## Decision

1. `^class.peon.Harvester.gatherMs` is **900 ms for every resource** (food from fields, wood, stone, gold, iron): one
   measurement applied to all (ADR 0174). Cows keep their own milking time (ADR 0170).
2. The `gather-rate` scenario is kept for M8 with its known fault (the mill's site); no facts from this run are
   committed (the analysis needs the memory series).

## Alternatives considered

* **Keep 1,000 ms:** the one clean load says 10 % less; the measurement replaces the placeholder.
* **Re-run with the mill moved:** the result would sharpen a value that is already within about 5 %; the owner's
  decision puts more measurement after the first play-test.

## Consequences

* Gathering is 10 % faster; golden hashes and the CI bench hashes are re-pinned.
* Open for M8: other resources may differ, and one load is one sample.
