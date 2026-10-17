# 0037. Train from a fixed-size queue per building, pay when queued, leave toward the rally point

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 part C)
* **Related:** TDD §8.5 step 4, §9.3 (rally points), §10 GAME-05, UNK-20

## Context

GAME-05: training queues and rally points; queue length is unknown (UNK-20). The producer column of the unit tables
names the building (ADR 0032).

## Decision

* `Production` (hot, on completed producers): progress of the first item, rally point, count, stall reason and an
  `[InlineArray(16)]` of type ids (queue capacity per type from YAML, ≤ 16; classic 10, UNVERIFIED).
* `Train` (type, count 1-16) queues into each listed own producer that trains the type, while there is room and the
  player can pay (paid per item when queued; requirements as for buildings). `Cancel` with a slot (-1 = last) removes an
  item and refunds `productionRefundPercent` (UNVERIFIED, 100%); cancelling the first item resets its progress.
* Step 4: the first item accumulates the tick until its build time. Then the unit leaves if there is population room
  (else it waits: `ProductionStalled` Population), an entity slot, and a walkable tile next to the building (rings 1-3,
  nearest to the rally point, or to the south side without one). It spawns there (`UnitTrained`) and, with a rally
  point, gets a plain Move order to it.
* `SetRally` (X, Y) sets the point; Arg0 = 1 clears it. Rally variants (Alt: default action, Ctrl+Alt: move-advance,
  §9.3) are M3.

## Alternatives considered

* **Paying when training starts:** some RTS do; the original's HUD shows the cost taken on click. Unverified either way;
  paying at queue time also makes cancel refunds unambiguous.
* **A queue in a side table:** keeps the component small, but the inline array is a block copy in snapshots and hashes.

## Consequences

* Research and upgrades (GAME-06) will reuse the queue model with their own item kinds.
