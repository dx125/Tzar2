# 0025. Steer with integer keep-right avoidance, resolve overlaps by a Jacobi push, and keep corpses out of passability

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 part B)
* **Related:** TDD §8.5 step 8, §9.2 REQ-PATH-06/07/08, ORIG-01/02, §9.2 acceptance 1-3, ADR 0017, ADR 0015

## Context

REQ-PATH-06: integer local avoidance; friendly units may be shoved, enemies and buildings may not. REQ-PATH-07: queue at
choke points and re-plan after 2 s without progress. REQ-PATH-08 (ORIG-01): corpses, debris and ground items never
alter passability. The movement step must give identical results for any worker count (ADR 0017) and allocate nothing.

## Decision

* **Frame.** Each tick the movement system copies every mobile unit (position, radius, owner, plane: ground/water/air,
  last velocity) into flat arrays and buckets them in a 2-tile spatial grid (stable counting sort). Jobs read only this
  copy and write only their own units' outputs; the merge applies positions, facing, occupancy, events and re-plans in
  ascending unit order.
* **Steer.** Toward the steering point at the unit's (or group's) speed. Units ahead in the way (within the combined
  radii laterally, up to 4 ticks ahead) add a lateral push to the side away from them; dead ahead means "go right", so
  opposing columns pass each other on narrow bridges. Units of the same owner moving the same way are followed: the
  speed is capped to the gap instead. Idle friends cause half the push (they will be shoved). At most 48 neighbours
  count per unit (bounds pathological stacks).
* **Terrain.** A ground unit's center and its four axis points at its radius (≤ 960) must be on walkable anchor tiles;
  blocked moves slide along an axis, then sideways at half speed. A unit standing on unwalkable ground (a building
  appeared) may move freely and is pushed toward the nearest walkable tile.
* **Resolve.** Overlaps are pushed apart from the steered positions (a Jacobi step, so order-independent): equal
  shares between two movers or two idle units; an idle friend takes ¾ against a mover (shoving), an idle enemy takes
  nothing and the mover all; pushes are capped at half the radius per tick; coincident units separate along a direction
  fixed by the pair's indices.
* **Progress and stalls.** Progress is measured toward the steering point (paths, direct), along the portal tree
  (routes) or on the field cost (flow). No progress for 2 s → re-plan; after 3 re-plans a unit that has nobody around
  gives up with `Blocked`; a unit held up by a crowd keeps re-planning (it is not stuck, it waits its turn).
* **Corpses (ORIG-01).** `World.Kill` records a corpse decal in `DecalIndex` and destroys the unit. Decals have a TTL
  and a global cap from the ruleset (default 120 s, 4,000, oldest dropped first), live in snapshots and the full hash,
  and never touch blockers, occupancy or navigation. Structures (`World.PlaceStructure`, types with a footprint mask)
  are the only dynamic blockers.

## Alternatives considered

* **RVO/ORCA:** needs fractional linear programming per unit; fixed-point versions are complex and costly. The keep-right
  rule plus pushes meets acceptance 3 (157% of single-file capacity across a one-tile bridge, `ORIG_02_opposing...`).
* **Cell reservation with sidestep:** grid artefacts at 2×2 cells per tile, and reservations are extra state.
* **Gauss-Seidel collision resolution:** faster convergence, but the result depends on iteration order, which would
  tie it to the job split.
* **Corpses as blockers with a cap:** still walls off choke points (ORIG-01).

## Consequences

* ORIG-01: 500 corpses on a bridge leave every movement tick bit-identical to the run without them.
* The "jammed near the goal" rule may stop a unit short of its slot in very dense crowds; it reports `Arrived`.
* Ships and air units avoid only their own plane; air units ignore terrain (REQ-PATH-10).
