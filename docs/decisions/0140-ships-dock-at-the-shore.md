# 0140. Ships dock at the shore nearest to both parties to board, unload and be repaired

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (naval stream)
* **Related:** TDD §9.2 REQ-PATH-09, §10 GAME-03, GAME-09, UNK-11, UNK-43, ADRs 0021, 0042, 0043, 0093

## Context

REQ-PATH-09: "the `ship` locomotor; boarding/unloading picks the nearest valid shore cells for both parties;
harbors/coasts are docking targets". Ships already sail on their own navigation layer (ADR 0021) and carry units
(ADR 0093), but a ship never came to anyone: boarders walked to the nearest land to a ship at sea and gave up, an
unload at a point across the sea failed unless the ship was already there, and peasants could repair a ship only when
it happened to lie at the shore. A tile between land and water carries neither locomotor (ADR 0042), so land and
water meet across one shore tile.

## Decision

* **Dock** (`Shore.FindDock`): a pair of a land tile the land units' layer can stand on, in their component, and a
  water tile of the ship's component within 2 tiles of it (across the shore tile). The pair with the least
  "land tile to the land reference point + water tile to the ship" wins; ties go by ring order, then row-major. The
  search covers 48 tiles around the reference point and stops when no further ring can win.
* **Docking** (`game.dock`, hot): a ship on its way to a dock's water tile, for one purpose: Board (wait for
  boarders; ends when no boarding errand targets it), Repair (wait for repairers), Unload or UnloadTo (let the units
  out there, toward a point, and with UnloadTo send them on to it). Path requests are asynchronous, so a ship that
  stopped 10 ticks after its request and is within 2 tiles has arrived; otherwise it asks again, at most 8 times.
  Any new order to the ship (ClearTasks) ends the docking.
* **Board**: the ship sails to the dock nearest the boarders' centroid (land component of the first of them).
* **Repair** (GAME-03): the ship sails to the dock nearest the first repairer; repairers do not count failed
  approaches while the ship is on its way.
* **Unload**: where land of the target point's component (the nearest walkable tile within 6 tiles of the point, so a
  building or shore point works) is next to the ship, the units come out now as before; otherwise the ship sails to
  the dock nearest the point (or, without a point, nearest itself) and unloads there.
* **Exits**: the free tile around a ship or carpet must also be standable for the unit's own navigation layer
  (clearance), not only passable for its locomotor.
* **Harbors and coasts as docking targets**: a ship ordered to a harbor or a coastal building is already taken to
  the nearest reachable water by the path request's "nearest reachable tile" rule (REQ-PATH-01); trade ships dock at
  harbors through the trade route (ADR 0103). Ships entering harbors (`[enter] harbor`) remains not implemented.

## Consequences

* Transport works from anywhere: board with the ship at sea, unload across the sea in one order. The AI's landings
  (ADR 0144) use exactly these orders.
* The dock search is bounded (48 rings, 25 water tiles per land tile) and runs on commands only.
* UNVERIFIED (UNK-43): where the original's ships stop to take units, its reach across the shore, whether a ship
  sails to its repairers, and the search radius.
