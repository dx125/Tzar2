# 0035. Gather unit by unit into a load and deliver it to the nearest own completed drop-off

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 part C)
* **Related:** TDD §8.5 steps 5-6, §10 GAME-02, REQ-SIM-02/10, UNK-17, docs/mechanics/m2-placeholders.md

## Context

GAME-02: peasants chop, mine and farm; drop-off at castle/storage/lumber mill/farm; nodes deplete (rocks hold RES.INI
amounts, fields are unlimited). Rates and carry capacity are unknown (UNK-17).

## Decision

* **State.** `Worker` (hot): task (harvest/build), phase (to node, gathering, to drop-off, to site, building), target
  node/site and drop-off handles (re-validated each tick, may be stale), load (resource, amount), gather timer, last node
  position, retries. `ResourceStock` holds what a node has left (-1 = unlimited).
* **Step 5 (`EconomySystem`), ascending workers.** Gathering: if the node is gone, empty, unfinished or out of reach →
  back to "to node"; a load of another resource is delivered first; otherwise the timer accumulates the tick
  (carry-over) and every `gatherMs` moves one unit from the node into the load until the load is full or the node empty.
  A node that reaches 0 is destroyed after the pass (`ResourceDepleted`; its tiles become buildable). Delivery: a
  worker in reach of its drop-off adds the load to the owner's stockpile (`ResourceDelivered` with the amount) and
  returns to its node.
* **Step 6 (`WorkerSystem`).** To node: if the node is gone, take the nearest node of the same resource within
  `nodeSearchRadiusTiles` of the old one (edge distance, ties to the lower index), else deliver what is carried or go
  idle. To drop-off: the nearest own **completed** structure that accepts the resource (edge distance, lower index);
  none → search again every 20 ticks. Movement uses ordinary path requests to the **approach point**: the walkable tile
  next to the footprint nearest to the worker (rings 1-3, row-major ties). After 5 attempts without reaching the target
  the worker gives up.
* **Reach** = unit radius + `interactionReachPx` from the footprint rectangle.
* Fields (`ResourceNode` unlimited food) are buildings: harvestable only when completed, and only by their owner;
  Nature's nodes (trees, rocks) are anyone's (PR #14 review S3). Only player-owned units are workers.

## Alternatives considered

* **Gathering continuous amounts (rate per second):** the same long-run rate, but loads and stockpiles would need
  fixed-point remainders; unit-by-unit with a carry-over timer is exact in integers.
* **Reservation of node slots:** the original lets several peasants work one tree; avoidance keeps them apart.

## Consequences

* Gather/carry captures (UNK-17) replace `capacity` and `gatherMs` in YAML; field decay, cows, fishing and
  auto-gathering after building a drop-off (1.05) are M3.
