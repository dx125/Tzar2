# 0185. Repair in blows of max / (4 × TIME) every 1.25 s, free, with no falloff

* **Status:** Accepted
* **Date:** 2026-10-21
* **Deciders:** agent (maintainer), from capture batch J; product owner (asked for the capture, 2026-10-21)
* **Related:** ledger BLD-022, BLD-007; UNK-18; ADRs 0043 (repair), 0169 (the time unit); `tests/fidelity/facts/repair-*.yaml`,
  `manual.yaml` (`repair`)

## Context

The engine repaired by placeholders: one peasant restored a building fully in its construction time
(`repairTimePercent` 100). Further repairers added 100, 75, 50 and 25 %, then nothing (`repairerFalloffPercent` 25).
A full repair cost half the construction cost, pro rata (`repairCostPercent` 50). The falloff and the cost were owner
decisions, made because nothing was known. The owner's play-test said a repair restores a set amount per blow, and the
owner thought the amount differed by building.

Batch J's third run (`capture-20261021-220250`) gave each building to kingdom 2 while three knights damaged it, gave it
back, and followed its hit points every 100 ms while one peasant repaired it:

| Building | HEALTH | TIME | Hit points per blow | max / (4 × TIME) |
|---|---|---|---|---|
| Lumber mill | 800 | 30 | 6 | 6.67 |
| Barracks | 1400 | 60 | 5 | 5.83 |
| Castle | 8000 | 120 | 16 | 16.67 |

What the run showed:

* **Pace.** A lone repairer's blows came 1.2–1.3 s apart, which is the original's time unit (1.25 s, ADR 0169).
* **Helpers.** Our idle peasants came to help. About seven of them restored ~31 HP/s on the mill, every blow still 6,
  so a further repairer adds as much as the first.
* **Cost.** Gold, wood, food and stone did not move during any repair.

## Decision

* **Blows.** A repairer strikes a blow every `gameplay.repairBlowMs` (1250 ms), sped up by the owner's build rate.
* **Blow size.** A blow restores max × blow / (build time × `repairTimePercent`), rounded down and at least 1
  (`GameplaySettings.RepairBlowHp`). With `repairTimePercent` 400 that is the original's max / (4 × TIME).
* **No falloff.** `repairerFalloffPercent` is 0: every repairer strikes full blows.
* **Free.** `repairCostPercent` is 0.
* **The rest stays.** The settings keep the falloff and the cost for other rulesets. The work towards the next blow is
  kept per building (`RepairProgress.Carry`, now in ms), so several repairers' blows interleave at the right total rate.
* **Fidelity suite.** `m2.repair-rate` compares the commonest rising step of each building's repair phase with the
  engine's blow. All three pass, so the suite is at 10 of 30.

## Alternatives considered

* **Keep a continuous rate at a quarter of the build rate** (`repairTimePercent` 400 alone). It gives the right average,
  but not the steps the original shows. The mill would get 5.33 HP/s instead of 4.8, because the original rounds every
  blow down.
* **A share of HEALTH per blow,** as the owner suspected. The steps would be 1 : 1.75 : 10. The measured 6 : 5 : 16
  rules it out.
* **Keep the owner's falloff and cost.** Both were placeholders for the unknown, and the capture contradicts both.

## Consequences

* A lone peasant takes about four times the construction time to repair a building from nothing. Peasants only slow
  an attack down unless they are many.
* The review regression of PR #21 ("repairers cannot keep a house alive against ten footmen") still holds with the
  measured rates: eight repairers delay the fall but do not prevent it.
* The gameplay golden's ruleset, stream and final hashes and the CI gameplay bench (`4E3562100FEF635F`) are re-pinned.
  The scenario's damaged houses are repaired more slowly.
* Not measured:
  * whether Architecture speeds repairs (it speeds construction; Tzar2 applies it to the blows' pace);
  * how ships and siege weapons are repaired (Tzar2 uses the same rule with their TIME).
