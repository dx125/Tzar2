# 0024. Give every group member its own arrival slot, keep compact formations, and move groups at their slowest speed

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 part B)
* **Related:** TDD §9.2 REQ-PATH-05/11, ORIG-02, §9.2 acceptance 2, UNK-11, UNK-12

## Context

ORIG-02: every unit targets the same goal cell, so large armies circle the destination. REQ-PATH-05 asks for free goal
cells found by BFS rings, assignment by ascending distance with deterministic tie-breaks, optionally keeping the
formation, and never orbiting. Acceptance 2 (200 **mixed** units to one point: everyone stopped within 6 s of the
first arrival, nobody moving after 10 s) cannot hold if knights (80 px/s) and catapults (24 px/s) travel at their own
speeds from a blob 10 tiles deep.

## Decision

* Units of one Move order form a group, partitioned by (navigation layer, start component). Each partition resolves
  one goal (nearest reachable tile if needed) and plans **slots** (`ArrivalPlanner`): a breadth-first flood from the
  goal cell over occupancy cells reachable in the partition's component, collecting free cells (occupancy minus the
  members themselves) on a lattice spaced for the group's largest radius (2.25 r).
* **Formation.** If the group's spread is at most max(2 × compact radius, 8 tiles), a member's desired point is the goal
  plus its offset from the centroid; otherwise the group gathers around the goal.
* **Assignment.** Members in order of ascending distance of their desired point to the goal (ties: entity index) each
  take the free slot nearest to their desired point (ties: flood order). A member without a slot heads for the goal.
* **Group speed.** Members move at the slowest member's speed (`Motion.GroupSpeed`); single orders keep classic speeds.
* **Settling** (never orbit): a unit within its settle radius (slot area + 1.5 tiles) that makes no progress for 1 s
  stops and reports `Arrived`; one jammed against already settled units within twice that radius settles after 2 s.

## Alternatives considered

* **Optimal assignment (Hungarian):** O(n³) for 500 units; greedy nearest-slot is O(n × slots) and good enough.
* **Units sorted by distance taking the slots nearest the goal:** front units take the centre and the rest walk around
  them; formations break and arrival times spread.
* **Own speeds for every member:** classic-faithful for mixed groups, but violates acceptance 2 by design.

## Consequences

* A 200-unit mixed blob stops within 105 ticks (5.3 s) of the first arrival (`ORIG_02_two_hundred_mixed_units...`).
* Slots are planned with the positions at preparation time; units that move in between still get valid slots.

## Questions for the product owner

* Classic Tzar may move every unit at its own speed (UNK-12/RE-06 will tell). Keep "group moves at its slowest speed"
  for all rulesets (needed for acceptance 2), or make it a ruleset option with `classic` = own speeds?
  **Answered (M2 part C):** a ruleset option `gameplay.groupMoveSpeed: slowest | own`, `slowest` in both rulesets until
  UNK-12 is captured (ADR 0032).
