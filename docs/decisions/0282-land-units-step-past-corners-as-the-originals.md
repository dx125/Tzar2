# 0282. Land units step past corners as the original's

* **Status:** Accepted
* **Date:** 2026-11-08
* **Deciders:** maintainer (Claude), the product owner (the order of the work)
* **Related:** WORLD-023, WORLD-003, WORLD-020, BLD-011; ADR 0021, ADR 0270, ADR 0281; ReTzar `src/gameplay/pathing.rs`

## Context

ADR 0021 made navigation 8-connected without corner cutting. A diagonal step needed both orthogonal neighbours
walkable. That was Tzar2's own choice; the original does otherwise. ReTzar's reading of its path search tests only the
cells a step enters, for small and large units alike (`cost`, `large_cost`). Its own test says it plainly: two trees
touching at a corner do not stop a diagonal step.

The difference stayed hidden until the map's rocks began to close their cells (ADR 0281). In the first campaign
mission, `v_00`, the heroes must pass between a small mountain and a cliff that touch at a corner. Tzar2 never found the
way, and the mission could not be finished. The owner chose to give the step the original's corners first and then turn
the rocks on.

## Decision

* A `NavLayer` knows whether it `CutsCorners`. The layers of units that step cell by cell (`MovementModel.Cells`, not
  ships or flyers) do. A diagonal step there needs only the tile it enters to be walkable:
  * `NavLayer.CanStep` uses this for regions, components, portals and the searches;
  * the step's own test (`MovementSystem.Cells.Walkable`) uses it too.
* Ships and flyers move continuously in Tzar2 (WORLD-015). They keep the old rule, so a ship never tries to slip through
  a gap of no width.
* `world.decorationsObstruct` is on in `classic.yaml`.

## Alternatives considered

* **Keep the old rule and leave the rocks off:** the maps stay unlike the original's (WORLD-003), and the difference in
  the step stays hidden until another obstacle shows it.
* **Cut corners for ships too:** a continuous mover would steer at a corner point between two land tiles and wedge there.

## Consequences

* Units squeeze between trees, rocks, cliffs and buildings that touch at a corner, as in the original. A pocket that a
  corner opens is no longer shut, for units that appear there and for those walking.
* The original's walls run along the axes only (BLD-011), so a wall is never crossed this way.
* Every gameplay hash changes: the goldens, the trigger mission and the benches are re-pinned. Both campaigns are
  checked with the campaign acceptance.
