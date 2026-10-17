# 0022. Plan paths as pure, budgeted, parallel requests; follow long paths as abstract routes

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 part B)
* **Related:** TDD §8.5 step 7, §9.2 REQ-PATH-01/02/03/12, ORIG-03, §21 (B3: 500 orders within 3 ticks), ADR 0017, ADR 0021

## Context

REQ-PATH-03 wants a FIFO request queue served under a **count-based** budget of node expansions per tick, identical on
all peers. §21 wants 500 move orders planned within 3 ticks on 512² (B3, a maze) while the tick stays within 12 ms.
The worker pool (ADR 0017) must never change results. Refining a 12,000-tile maze path into tiles and smoothing it costs
milliseconds per unit.

## Decision

* **Queue.** `PathRequestQueue` (state: snapshotted, full hash) holds requests in issue order: commands of a tick in
  command order, units of one order ascending, then re-plans in motion-store (entity index) order. This is
  deterministic but not literally REQ-PATH-03's "(tick, entity ID)" order. Restored entries are validated (flags,
  points on the map, group ids, and a flow goal for live flow requests). A newer order bumps the unit's `RequestSerial`, making older requests
  stale; stale entries cost nothing and the queue is compacted when it grows past 2 × units + 1,024.
* **Prepare → compute → accept** each tick (`PathRequestSystem`):
  1. *Prepare* the first 512 live requests in queue order (sequential): fix the start (nearest walkable tile if the
     unit stands on a blocked one), resolve the goal (unreachable → nearest tile of the unit's component, found by a
     cluster-ring scan, with a `GoalUnreachable` event in the same tick), and for groups plan slots (ADR 0024). From
     then on a request's result is a pure function of its fields and the layer.
  2. *Compute* about a budget's worth (an adaptive estimate; anything else is computed on demand during acceptance)
     as fork/join jobs with per-job scratch; flow fields of the batch are built in the same jobs (ADR 0023).
  3. *Accept* in FIFO order while the spent expansions are below `SimulationConfig.PathBudgetPerTick` (at least one
     per tick). A request costs what its fresh computation expands (plus its preparation), so neither caches, carried
     results nor the worker count can change which requests are accepted. Computed but unaccepted results are carried
     to the next tick if request and navigation version are unchanged.
* **Search.** Start and goal at most 48 tiles apart per axis: plain A* in their bounding box + 16 tiles (optimal for
  most short queries). Same cluster and region: the start's cluster Dijkstra. Otherwise HPA*: cluster Dijkstras from
  start and goal connect to their portals, A* runs on the compact portal graph with an octile heuristic; open lists are
  total-order heaps (key, then node id).
* **Routes.** For HPA* results the planner stores only the abstract **route** (portal node ids) in `PathStore`
  (`MotionMode.Route`). The unit descends the tree of the exit portal of its current cluster, crosses to the partner,
  continues with the next portal's tree, and finally the goal's cluster tree, looking up to 10 tiles ahead for the
  farthest visible tile (corridor line-of-sight with the unit's radius as margin). Short paths are smoothed tile paths
  (`MotionMode.Path`). A route plan stores the portal node ids **and their tiles**; when passability changes anywhere,
  each route is re-checked (`PathSearch.RouteValid`: the remaining portals still exist at the same tiles and are still
  joined) and only invalid ones are re-planned. The check is a pure function of the plan and the current layer, so a
  restored match decides identically, and a tree felled across the map no longer stops every routed unit (review
  finding B1). A unit pushed back across a portal resumes from the route two portals back; a unit off its route
  re-plans.
* **Preparation is not budgeted.** Goal resolution and the arrival-slot flood (≤ 65,536 cells per group) run for up to
  512 requests per tick and are charged to the budget only on acceptance (`PrepCost`), so the per-tick CPU bound is
  the batch limit plus the budget, not the budget alone.
* **Budget.** Default 750,000 expansions per tick (a match setting, in the snapshot header). B3 needs ≈ 2 M expansions
  for its 500 orders: planned after tick 2, with 8.3 / 6.4 / 3.7 ms for ticks 0-2 on 4 workers (Apple M-series, warm
  JIT; ADR 0026). The synthetic 4,000-unit scenario (128 group orders per tick) stays at p99 6.4 ms with it.
* **Quality.** Tile-path cost vs. plain A*: mean 1.015, worst 1.11 over a fixed 690-query sample on random 96² maps,
  1.054 on the long 512² maze query; the per-query property bound is 1.35 (medium-range queries through single-
  transition entrances). Paths are always valid and connectivity always agrees with plain A* (property tests).

## Alternatives considered

* **Time-sliced searches with persistent search state:** needs the open lists in snapshots; budgets in whole requests
  plus deterministic costs give the same bound with stateless jobs.
* **Fully refined tile paths for every request:** correct, but refinement + smoothing dominate long maze paths
  (`TilePathQueries` vs. `RouteQueries`) and store thousands of waypoints per unit.
* **JPS within clusters:** unnecessary: clusters are refined by precomputed trees, not searched.
* **ALT landmarks for a stronger heuristic in mazes:** must be recomputed on every obstacle change (results must be a
  pure function of the map), i.e. on every chopped tree; rejected for now.

## Consequences

* "Planned within 3 ticks" holds for B3 at the default budget; slower CPUs need more ticks, never different results.
* Units keep standing (mode `Waiting`) until their request is accepted; queue starvation is impossible (FIFO, ≥ 1/tick).
* REQ-PATH-07's congestion-aware costs are not part of the search (costs must not depend on unit positions that change
  every tick, or results would depend on when a request is computed); congestion is handled by avoidance, queueing,
  re-plans after 2 s without progress, and settling (ADR 0025).
