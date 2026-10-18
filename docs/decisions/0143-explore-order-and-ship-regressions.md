# 0143. The Explore order, and the patch-note regressions about ships

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (naval stream)
* **Related:** TDD §5.1, §8.4 REQ-SIM-10, §8.6 REQ-SIM-11 (Explore), §9.3, GAME-09, GAME-11, ADRs 0093, 0095, 0140

## Context

TDD §5.1 lists four 1.05–1.10 patch-note fixes about ships that must not come back: "ship repair behaviour;
stand-ground/explore for ships; … ship storage double upgrade; crusade anchor updates when crusaders die inside
ships/buildings". The patch notes give no more detail than these phrases. REQ-SIM-10/11 list an Explore activity and
order, which the sim did not have.

## Decision

* **Explore** (command 34, store `game.explore`): own mobile units on the map head, every 20 ticks when idle, for the
  nearest tile (ring order, then row-major, within 96 tiles) that their kingdom has not explored and that lies in
  their navigation component; ships explore water, land units land. The order ends when nothing reachable is left;
  any other order ends it.
* Regression tests (`NavalTests`), with the reading we take of each phrase (UNVERIFIED):
  * *ship repair behaviour*: a damaged ship at sea comes to its repairers' shore and waits until it is whole
    (ADR 0140).
  * *stand-ground/explore for ships*: a ship on stand ground does not sail after an enemy in sight but out of range,
    while an aggressive one does; an exploring ship stays on water and an exploring soldier on land.
  * *ship storage double upgrade*: Landing Tactics adds its room once, for ships built before and after it, with two
    harbors, and after a snapshot.
  * *crusaders dying inside ships*: crusaders aboard keep their crusade going; when the ship sinks with them, their
    links go and the anchor runs its cooldown.

## Consequences

* The four phrases have tests; if captures show another meaning, the tests change with the behaviour.
* Explore costs a bounded ring scan per idle explorer every 20 ticks.
