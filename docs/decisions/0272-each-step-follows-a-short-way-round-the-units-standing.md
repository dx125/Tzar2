# 0272. Take each step from a short search round the units standing in the way

* **Status:** Accepted
* **Date:** 2026-11-04
* **Deciders:** Claude (maintainer), from the product owner's ninth play-test and his question whether Tzar2 should have
  a pathfinding of its own that rules out the problems met so far; under ADR 0190 (ReTzar as a source)
* **Related:** ADR 0270 (cells), ADRs 0021–0025 (navigation, routes, flow fields, slots); ledger WORLD-020, WORLD-021,
  WORLD-022

## Context

Four play-tests in a row (the sixth to the ninth) found units in each other's way. Each fix added a case to the cell step
of ADR 0270: wait, go round near by, search the whole map, go through friends that step aside, change places. The ninth
play-test still found troops in each other's way, and units stuck in half-closed pockets, not knowing where to go.

The owner asked for a comparison with the original, as ReTzar reads it.

| | The original (ReTzar: pathing.rs, movement.rs) | Tzar2 before this decision |
| --- | --- | --- |
| The way | A cell A* (0x4a9060): 8 neighbours, costs 16 and 22, at most 128 tiles for land units, guided by a graph of regions in 8 × 8 sectors; it runs out at the tile of least f, and the unit searches again at the path's end | Routes over 16 × 16 clusters (HPA*), flow fields for groups, smoothed tile paths; none of them knows any unit (ADRs 0021–0023) |
| Units in the search | Every standing unit's cell is a wall in every search; cells of units on the move too in a re-plan | None, except in the searches for a way round |
| The step | The next cell of the unit's own path | The neighbour nearest a point up to ten tiles ahead (greedy) |
| Held up | Wait behind a unit on the move (10 × 250 ms); a unit standing there or head on: search again round every unit; no way: the walk fails and is retried a second later | Wait behind a unit entering the tile; a search for a way round, near by then over the map, then through friends; only its first step was taken |

The probe of the owner's replay (twelve minutes of his game, one from each ten-minute keyframe, with his commands) found
111 times a unit stood 4 s or more with an order to walk, more than two tiles from its goal. Two causes stood out:
* **The greedy step.** A unit steps toward a point. In a pocket that is closed toward the point (friends standing in a
  bend, a peasant working in a gap), the way round leads away from it. The search for a way round gave its first step
  away, and the next greedy step took the unit back in. It went to and fro until it gave up.
* **Ways that know no unit.** The routes and fields lead into crowds; only the cell step knew the units, one tile at a
  time.

## Decision

Tzar2 keeps its routes, flow fields and slots (they know terrain and buildings, and are cheap for many units), and the
step takes the original's rule for units: a unit standing is a wall.

* **WORLD-022: each step comes from a search.** On each tile a unit with an order searches (A*, octile costs) toward the
  point it heads for: the goal when it is within 12 tiles, else its steering point (route, field or path). The search
  covers the square 12 tiles round the unit and at most 256 tiles (`LaneNodes`).
  * Units standing are walls: idle, at work, fighting, of an enemy or of Nature, and units with an order that have made
    no progress for 16 ticks (`StalledTicks`). Units walking on cost two tiles more.
  * A point beyond the square: the first tile on the square's edge at least six tiles nearer it ends the search.
  * Found nothing: the step leads to the tile nearest the point, if it is nearer than the unit's own.
  * The unit takes the first step. On the next tile it searches again, so a way that leads out of a pocket is followed
    out, and the unit never steps back toward its point.
* **Held up** (WORLD-020, WORLD-021, as before otherwise).
  * A unit waits behind a unit entering the tile, making room, or walking on (not held up itself), up to 2.5 s. The
    original waits behind every unit on the move.
  * Behind a unit standing, it searches again with every unit a wall, near by, then over the whole map.
  * With no way at all, friends that make way step aside.
* **Cost.** A unit that has stood 8 ticks searches only every 8 ticks, unless it waits behind a unit entering. The
  count is `CellStep.Stood`, which was padding.

## Alternatives considered

* **Keep the greedy step and add cases.** Four play-tests did that. Each case fixed one shape of crowd, and the pocket
  is the shape the greedy step cannot leave.
* **The original's model entire: one partial A* of 128 tiles per walk, followed to its end.** It needs a path per unit
  in the snapshot, and its region graph to guide it. Tzar2's routes and fields already guide; a short search on every
  tile gives the same choices without stored paths, so saves and the hash stay as they are.
* **Cooperative search (reserving tiles in time, WHCA\*).** It rules out head-on locks in theory. But it costs a search
  in space and time for every unit every few ticks, and the head-on cases already pass each other (ORIG-02). If play
  shows locks that these rules do not solve, this is the next step.
* **Flow fields that count units (crowd density).** Good for hundreds of units on one goal; they do not follow a single
  unit out of a pocket, and the cells' one-to-a-tile rule would still need a step rule.

## Consequences

* The pocket of the ninth play-test is left at once (`A_unit_in_a_pocket_of_standing_friends_walks_out_of_it_and_round`;
  before, the knight stood in the pocket against its closed side for the whole test).
* The owner's replay, twelve minutes from the keyframes: 111 times a unit stood 4 s or more before, 17 after; units gave
  up 6 times before, once after.
* Game 5's fight (42 knights at a barracks, 120 s): 18.6 knights fighting on average against 17.3, 0.12 frozen 3 s or
  more against 0.27, and 6 enemy units left by the base against 9.
* The gameplay bench's tick p50 is about 0.8 ms against 1.4 ms: units held up search less than before.
* Every golden and CI bench hash that has land units moving changes.
* Follow-up if play shows it: cooperative reservations for head-on locks of enemies, and slots that know the units at
  the goal.
