# 0293. Tzar2's own trigger actions: patrol routes, reinforcement waves, camera paths

* **Status:** Accepted
* **Date:** 2026-11-13
* **Deciders:** maintainer (Claude), at the owner's request (the rest of the editor review's section 3, 2026-11-13)
* **Related:** TDD §18.3; ADR 0130, ADR 0132, ADR 0285, ADR 0290, ADR 0291, ADR 0292; STATUS item 26.9

## Context

The editor review's section 3 proposed actions beyond the original's, marked as not the original's. They are:

* patrols along waypoints;
* waves of reinforcements;
* a camera along points for cut scenes.

A fourth proposal, a difficulty variable that works, was done by ADR 0287 (`$difficulty`). The owner allows modern
improvements in the editor and the tools; the rules of the original's own actions stay as they are.

The original's actions can do these only by hand:

* Object Order sends units to one area once;
* Spawn Object makes one group once;
* Scroll View and Lock View jump the camera.

A patrol, a stream of reinforcements or a cut scene took many rules, timers and a Delay each.

## Decision

* **Three actions of Tzar2's own**, in the scenario format next to the original's. The editor's titles end with
  "(Tzar2's own)", and the validator notes each with "action.own": the original game cannot play the map.
  * **Patrol Route** (objects, points, again and again). Each unit walks to the next point as an attack move, fighting
    what it meets, and then takes the following point. At the end it starts over, or stops. Running the action again
    gives a unit the new route.
  * **Reinforcement Waves** (type, owner, per wave, appear in, waves, between waves, march to). A wave appears on the
    next tick and then every interval, as Spawn Object places it. With a target, each wave marches there as an attack
    move.
  * **Camera Path** (points, time per leg). The view glides from point to point and the player cannot scroll
    meanwhile. It is presentation only: the sim sends one `CameraPoint` event per point and changes nothing.
* **A point** is an area's centre, or an object where it stands then. The editor adds one by a click on the map, from the
  selected object or from a named area.
* **The patrols and waves still running live in the trigger state** (version 5, written only when there are any, so
  older states keep their bytes). They are looked after every tick: waves by their timers, patrols every
  `PatrolEveryTicks` (10 ticks). A unit that is moving, fighting or inside a holder is left alone. One within two tiles
  of its point takes the next point.

## Alternatives considered

* **Macros that write ordinary rules** (as the goal wizard does): a route of N points needs N events, timers and rules,
  and a unit cannot take its next point when it gets there.
* **Lua for these** (M8): the Lua host is not built, and these three are what map makers ask for first.
* **A camera path in the sim's state:** the camera is the player's alone; events leave the match unchanged.

## Consequences

* A map with these actions plays only in Tzar2; the validator says so, and the converter never makes them.
* Snapshots, saves, replays and rewinds carry the patrols and waves; a test rewinds a match with both and checks every
  hash.
* Writing these tests found a bug in `main`: the restore check of a kingdom's flags did not know the rescue flags or the
  AI halves. It is fixed separately (#297).
