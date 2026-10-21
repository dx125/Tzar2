# 0180. Queue any number of any items, give loads at once, and let peasants work without being told

* **Status:** Accepted
* **Date:** 2026-10-20
* **Deciders:** agent (maintainer); product owner (third play-test, 2026-10-20)
* **Related:** GAME-02, GAME-03, GAME-05; UNK-17, UNK-20; ADRs 0035, 0037, 0040, 0175, 0179

## Context

The owner's third play-test reported, from playing the original:

1. Arabian and Asian peasants could not help build a European castle. In the original any peasant helps on a site.
2. A barracks queued at most 10 units, of one kind at a time; clicking the archer added a swordsman. Two causes:
   * the sim's queue held 10 items (UNK-20 placeholder);
   * the client showed only the first item's icon with the total count.

   The original queues any number of any units the resources pay for: "10 archers, 10 swordsmen and 100 knights". Its
   card shows each count on the order button.
3. Peasants' loads grew chop by chop. In the original the whole load (10 wood, 10 gold...) appears after a number of
   chops; only milk fills the pail gradually.
4. Peasants walked to far trees instead of those near the lumber mill. The next node was the one nearest to the last
   node, reachable or not; a tree inside a forest cannot be reached.
5. Units made in a building appeared on top of each other, on the one free tile nearest to the rally point. In the
   original they spread around the building. The original's bug, worth fixing: a unit could appear in a pocket shut in
   by walls or buildings.
6. Idle peasants in the original find the nearest work by themselves after a while (building, repair, gathering).

## Decision

1. **Help on sites.** Any builder of the player helps on a site or continues a dragged wall, whatever its
   civilization. Placing a site still needs a builder that can build that type (its civilization's tech tree).
2. **Queue entries.**
   * A producer's queue is a list of entries `(item, count, paid modifier)`: one entry per item and payment, in the
     order the items were first ordered, any number of each (up to 65,535).
   * `queueCapacity` now counts different items: 24, the most the sim stores.
   * Training is paid per unit when queued, as before.
   * `Cancel` takes one item off an entry: `Arg0` = the entry, or −1 for the last one; `Arg1` > 0 = the queue item + 1,
     what a right click on an order button sends. The finished item waiting for room goes last.
   * The frame carries every producer's entries (`GameFrame.QueueEntries`).
   * The AI keeps its old depth of 10 items per building (ai-placeholders E21).
3. **Loads at once.**
   * A gatherer works one gather time per unit of its load (900 ms, ADR 0175) and gets the whole load when the work is
     done.
   * A node with less left gives what it has.
   * Work broken off is lost.
   * Milk (nodes that regrow) still fills unit by unit.
4. **The next node.** Among the nodes of the resource within `nodeSearchRadiusTiles` of the last one, the worker takes:
   * only nodes it can get next to (a walkable tile of its own area touches the node);
   * of those, the one with the shortest way from the last node plus the way from it to the nearest own drop-off.

   Lumber mills and storages started by their builders choose the same way.
5. **Spawn points.** A unit appears on a tile around the building:
   * in the connected area with the most room around it (walkable tiles of the nav component), so a pocket is left
     out when there is a way out elsewhere;
   * on the ring nearest to the walls (up to 4 rings), the free tile (no unit's occupancy cell) nearest to the rally
     point, or the south side without one;
   * with no free tile, on the nearest tile of that area anyway.
6. **Idle work.**
   * When: a land worker without a task that has stood still for `idleWorkAfterMs` (10 s). It must have no order,
     nothing to fight, not be inside a holder, and not be busy with an errand, a trade, exploring or a spell.
   * What: the nearest work within `idleWorkRadiusTiles` (12): an own site, an own damaged repairable building, or the
     next node (rule 4) of a resource it gathers that an own drop-off takes.
   * Order: a node is preferred when one exists.

   Delay and radius are UNVERIFIED placeholders.

The hashed layout version is 8 and the snapshot format version 10 (Production and Worker changed).

## Alternatives considered

* **Keep one queue slot per item and raise the capacity.** Storing 100 knights one by one would take 100 slots per
  building. Entries also give the card its per-button counts directly.
* **Merge only consecutive orders of an item.** That is closer to a strict first-in-first-out queue, but alternating
  clicks would use up the entries. The original's card shows one count per button, so one entry per item.
* **Keep the partial load on an interrupted harvest.** The owner describes loads that appear at once. Partial work
  dropping is the simplest reading; captures can refine it (UNK-17).
* **Score the next node by distance to the worker only.** That is the old rule, which led peasants away from the mill.
* **Idle work only for the AI's kingdoms.** The owner describes the original's own peasants doing it, so it is a game
  rule for every kingdom.

## Consequences

* The hashes change: sim, gameplay and AI goldens, the scripted mission, and the bench hashes are re-pinned. Old saves
  and snapshots are refused by version.
* Tests that need an idle peasant to stay idle compile the rules with `idleWorkAfterMs: 0`.
* The AI delivers about 8 % less in the 2,400-tick bench, because broken-off work is lost. The economy otherwise keeps
  its rate.
* Follow-up: captures of the original's idle delay, its next-tree choice and its exit tiles (m2-placeholders).
