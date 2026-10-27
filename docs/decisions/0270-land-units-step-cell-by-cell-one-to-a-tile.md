# 0270. Land units step cell by cell, one to a tile, as the original

* **Status:** Accepted
* **Date:** 2026-10-26
* **Deciders:** Claude (maintainer), from the product owner's sixth play-test and his order of work (item 18, order 2),
  under ADR 0190 (ReTzar as a source)
* **Related:** ADRs 0021–0026 (navigation, paths, flow fields, local avoidance), 0188 (bodies), 0146 (landings);
  ledger WORLD-020, WORLD-015, WORLD-019; ORIG-02

## Context

The sixth play-test found the crowds wrong:
* big fights are a crowd jittering left and right; nobody goes round the side;
* the rear of a large group jitters in place while its front is stuck or fighting;
* moving troops shove standing friends several tiles aside;
* peasants walk in place on the way to the drop-off.

Our land units moved continuously (ADR 0025). They steered, avoided each other by a lateral push and resolved
overlaps by a soft push. Paths knew only the terrain and the buildings. In a crowd the pushes make the jitter, the
shoving and the walking in place, and no unit plans its way round another.

The original works differently (ReTzar's reading of its code, src/gameplay/movement.rs: 0x4def20, 0x4debb0):
* every unit stands on a cell, and a cell holds one unit of a layer;
* a step takes the next cell of the path at once, and the picture slides there; a step in any of the eight
  directions takes the same time;
* if a unit is entering the next cell, the walker waits (250 ms, up to ten times);
* if a unit stands on the next cell, or meets the walker head on, the walker searches again round the units.

ADR 0188 named this model as the faithful one, and kept the continuous one only because it existed.

## Decision

* **`gameplay.movement: cells`** (the classic ruleset; `continuous` stays the default for rulesets that do not say,
  such as the M2 synthetic scenario and its benches).
  * Land units (not ships, not flyers) step from tile to tile.
  * The routes, flow fields, arrival slots and re-plans stay Tzar2's own; only the step is the original's.
* **One to a tile.**
  * Each tick the movement system builds who stands on each anchor tile of the land layers: a unit owns the tile it
    enters, else the one it stands on.
  * A large unit owns its clearance's square.
  * A unit finding its tile taken (spawned or landed on it) steps to a free neighbour.
  * Nobody is pushed.
* **The step.**
  * A new component `CellStep` (hot store `core.cellstep`, appended) holds the tile entered, where the step began and
    how far it has come.
  * The unit walks at its (or its group's) speed, carrying what is left of a tick into the next step.
  * A unit that has begun a step ends it, stopped or not. `Interaction.IsMoving` is true until then, so work and fights
    wait for the step's end, as the original's programs wait for it.
* **The next tile.**
  * It is the neighbour that brings the unit nearest the point it heads for (its path's next point, flow field, route
    or goal), on walkable ground and cutting no corner, and through gates only for their friends (ADR 0102).
  * If the neighbour is free, the unit steps.
  * If a friend there is on its way through this unit's tile (head on), the two pass each other (ORIG-02).
  * If a unit is entering the tile, the walker waits up to 2.5 s.
  * If a unit stands there, the walker searches its way round the other units: an A* over at most 1,200 tiles
    within 12 of it, toward the goal or the next point, and takes the first step.
  * That search treats an enemy, or a creature of Nature, as a wall. It may go through a unit that makes way, at a
    higher cost: 2 tiles' worth for one walking, 6 for one standing. If its first step is onto such a unit, the
    walker asks that unit to make room or to pass it.
* **Friends make room** (the owner: allies should step aside and come back, not be shoved).
  * Units that make way are friends and the units of any kingdom that is not an enemy of the walker's, either way.
    The campaigns' neutral towns would otherwise wall a road for good.
  * With no way round, an idle friend in the way walks to the nearest free tile off the walker's way, within three
    steps.
  * It goes back two seconds later when its tile is free.
  * In a corridor, or a crowd with no room off the way, the two change places. So does a unit already making room
    but hemmed in.
  * An idle unit that makes way steps off a walker's goal tile: the order named that place (a trigger's area).
    One at work, or an enemy, keeps it, and the walker settles next to it.
  * A walker that has not got on for a second (a crowd of walkers locked together) changes places with the walker
    that needs its tile, unless it is at work or striking.
  * Each unit's own layer says where it may stand: a ninja climbs where a footman cannot (WORLD-018).
  * The campaigns found these: the main campaign's rajba locked a ninja in its own army for an hour of game time.
* **Stuck.** Progress means coming nearer the goal, or moving on along a path.
  * A unit that steps for 6 s without progress plans again. Without this, units pushed off a straight line (a bridge
    crossed head on) kept stepping along the bank.
  * A blocked unit settles near its goal when the goal is held, re-plans after 2 s, and gives up after the old
    counts.
* **Hand-to-hand reach** (`meleeReachPx`) becomes 18 px. With 14 px bodies, a diagonal neighbour is then in reach, as
  the original's target cells grown by one are; the next tile but one is not.
* **Snapshots and hashes.** The snapshot format is 22 and the hashed layout 20. The occupancy is rebuilt every tick,
  as the original rebuilds its cells.

## Alternatives considered

* **Keep the continuous model and only make paths know standing units.** Movers would still push each other in motion,
  and the jitter came from that.
* **The original's group path (one path from the group's centre, each unit shifted by its place).** Tzar2's arrival
  slots already keep a compact group's formation (ADR 0024). It may come later if play shows the need.
* **Cells for ships and flyers too.** They do not crowd like land units, and their landings were tuned on the
  continuous model (ADR 0146). Not now.

## Consequences

* Crowds no longer overlap or push. Two units sent to one point walk one behind the other. Attackers of one enemy
  take the tiles round it.
* The computer's economy is about 8 % slower: peasants wait for each other at nodes and drop-offs, as in the
  original.
  * In the AI skirmish tests the race between two computers is no longer even. The tests ask that one of them
    attacks within ten or twelve minutes.
* Every golden hash and all three CI bench hashes change; the sim bench only by the new store in the hashed layout
  (the M2 synthetic scenario keeps the continuous movement).
* The gameplay bench's p50 is within 2 % of the continuous model's: a unit held up for a while searches its way round
  every 8 ticks, not every tick (every tick cost 18 %).
* The M2 bodies (ADR 0188) still give the reach and the spacing of ships and flyers.
* Not done: units stepping aside for a passing enemy (they fight), and a unit of two tiles' clearance changing
  places.
