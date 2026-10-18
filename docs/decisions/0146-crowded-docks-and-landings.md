# 0146. Crowded docks and landings: boarders wait, docks spread, taken goals settle

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (naval stream)
* **Related:** TDD §9.2 REQ-PATH-07/09, ORIG-02, UNK-11, UNK-43, ADRs 0093, 0140

## Context

Original campaign maps 11 (alex1), 13 (bigisland) and 15 (vsaa), run headless by the campaign solver, stalled where
many ships and units met at one shore:
* a boarder that reached the shore before its ship gave up its errand after its 8 retries; with nobody waiting, the
  ship's boarding docking ended and the ship stopped at sea (bigisland: the second hero "never arrives");
* boarders walked to the land nearest to the ship's position, which on a ragged coast can be a point they cannot get
  close from, and stayed Moving for tens of thousands of ticks (vsaa, among twenty of the player's ships);
* a unit sent again and again to a landing point where another kingdom's peasant stood never arrived: it stalled
  just outside its settle radius, and the "jammed against settled units" arrival needed 40 stalled ticks, more than
  the 38 between two orders (bigisland, the pikeman at the landing point);
* every ship of a group docked at the same water tile, and a ship that arrived without room for its units ended its
  docking with the units still aboard (alex1, four loaded hulks at the harbor).

## Decision

* **Boarders wait for their ship**: while the ship has a docking in progress, a boarding errand does not count its
  movement attempts (like repairers, ADR 0140). It walks to the land tile of its own component within the shore reach of
  the ship, nearest to the ship (`Shore.LandingNear`); a ship that is not at such a shore is asked once, halfway through
  the retries, to come to the unit (the dock nearest it).
* **Docks spread along the shore**: water within 2 tiles of another ship, or of another ship's docking point, is taken.
  The 64 such ships and points nearest to the land reference point are avoided while any free dock exists within the
  search radius; otherwise the nearest dock is used as before. A dock's land and water tiles are at most 2 tiles apart
  in straight-line distance, so boarding and repair reach them from either corner.
* **Unloading finds room**: a ship that cannot let all its units out at its dock looks for another dock near the same
  point, avoiding the one it is at; the docking's retries carry over, so this ends. An unload point without land within
  6 tiles (inside a town, on water) is given the land within 24 tiles; with none, the units come out at once only when
  the exit is within 6 tiles of the point, never back on the shore they left.
* **A taken goal settles**: a unit on its final leg that has made no progress for 20 ticks (`SettleTicks`), with
  standing units in contact and within one tile of its settle radius, has arrived (the goal is occupied: ORIG-02, never
  orbit). The older 40-tick rule for units jammed against a crowd stays.

## Consequences

* Synthetic repros in `NavalCrowdingTests`: four full ships unloading at one point; a second boarder for a docked ship;
  boarders through and behind a wall of own ships; a ship crossing a bay past idle ships; units unloaded at their own
  landing spot; a unit re-ordered every 38 ticks to a taken spot.
* On the originals, the boarding, ferrying and landing stalls are gone. What still stops those maps lies in the
  solver (ferry orders and targets); the campaign stream handles it.
* Goldens move: the settle rule changes the gameplay and AI matches; the docks change where ships stop.
* UNVERIFIED (UNK-43): how the original spaces ships at a shore, and whether it lets units settle at an occupied goal
  this soon.
