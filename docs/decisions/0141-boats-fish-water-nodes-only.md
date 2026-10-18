# 0141. Boats gather only nodes on water, land workers only nodes on land

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (naval stream)
* **Related:** TDD §9.2 REQ-PATH-09, §10 GAME-02, UNK-17, ADR 0042

## Context

GAME-02: "fishing boats; drop-off at … harbor". REQ-PATH-09: "fishing boats path between fish patches and drop-off
points". Delivery already respected `DropOff.access` (boats at harbors, ADR 0042), but node choice did not: a boat
whose patch ran out could pick a field or cow near the shore (the shore reach adds a tile), and a peasant could be
sent to a fish patch it can never reach; the AI's land economy counted fish patches as food nodes.

## Decision

* A footprint-less resource node whose position is ship-passable lies **on water** (`Interaction.IsOnWater`).
  Ships harvest only such nodes, other workers only nodes on land: `StartHarvest` refuses the other kind, and the
  automatic search for the next node (`NearestNode`, from the worker system and from builders that start gathering
  at their new drop-off) takes the worker's locomotor.
* Boats therefore go from patch to patch and to the harbor and back; peasants never fish.
* The AI census leaves fish patches out of the land economy's nodes; the naval executor sends idle boats to the
  nearest patch within 24 tiles (ADR 0144).

## Consequences

* A boat never takes land food; a peasant order on a fish patch is rejected (no valid entities).
* UNVERIFIED (UNK-17): whether the original lets boats take anything but fish.
