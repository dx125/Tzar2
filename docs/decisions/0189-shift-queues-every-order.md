# 0189. Queue every order given with Shift, and take a whole type with Alt

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), on the owner's order to finish the controls (2026-10-22)
* **Related:** ledger CTRL-024 to CTRL-027, ORD-005 to ORD-007, ORD-014, BLD-018, BLD-019; ADR 0043 (walls, gates,
  bridges); manual ch. 5 §2.3 (Commanding Units)

## Context

The manual gives Shift and Alt the same meaning for several orders:

* Move: Shift adds waypoints, which show while Shift is held.
* Attack: Shift adds enemies to a set attacked one after another; Alt takes all objects of the clicked one's type.
* Guard: the same for the objects to guard.
* Patrol: Shift makes a path of several points.

The original has cursors for the attack set and the attack type (`cs_attackset`, `cs_attacktype`) and for the patrol
path (`cs_patrol_ex`). Its waypoint flag is `wp_flag`. Tzar2 had only the waypoints of moves (ORD-005).

The placement ghost judged every building by the land under it. A bridge over water was never legal in the client, so
peasants could not build bridges. A harbor away from water looked legal.

## Decision

* **One queue for every order.** The waypoint store (`game.waypoints`) keeps orders of four kinds:
  * a point to walk to;
  * an enemy to attack;
  * an object to guard;
  * the points of a patrol path.

  It holds up to 16 orders, a placeholder.
  * An attack, guard or move given with Shift waits when the unit is busy: walking, attacking on an order, or already
    queueing. A guard order also waits for the object being guarded.
  * The next order starts when the current one ends: on arrival, when the attacked enemy is gone, or when the guarded
    object is gone. A unit that arrives from a chase in the middle of a fight moves on only after the fight.
* **A patrol path is walked round.** A point added with Shift to a patrolling unit makes a path from the patrol's two
  ends plus that point. The unit walks it in the order given, back to the start.
* **Alt takes the type on the screen.** With Alt, an attack or a guard takes the clicked object and every other one of
  its type and owner on the screen, as the double click picks units of a type. Each next object is the nearest to the
  last one. The set is cut at what one order and a full queue hold.
* **What the player sees.**
  * While Shift is held, the selection's way shows: the original's flag on each point, and marks on the objects it
    attacks or guards in turn.
  * The cursors change with Shift and Alt.
  * A right click gives the order of a pending Attack, Guard or Patrol (manual).
* **Turned placements.** Alt or Ctrl turns a gate's ghost, and Alt a bridge's (the Build command's argument 1, as the
  editor already places them). A turned bridge draws `bridge_vert`.
* **The ghost asks what the sim asks.** The frame carries a second tile layer, with water, land, free tiles and the
  kingdom's own wall. With it, `Placement` judges coast, bridge and gate placements the way the sim does.
* **A gate replaces its own wall.** A gate may stand on pieces of its kingdom's finished wall, which it replaces.
* **Explore takes an area.** Explore waits for a click on an area. The unit walks there and explores within 12 tiles
  of it (a placeholder).
* **Unload shows where units can land.** Over a place without land the pointer is the forbidden one.

## Alternatives considered

* **The type set in the sim** (a hunt for the type after each kill). It would reach beyond the screen. The manual says
  the objects of the type are *selected*, as with the double click, which picks those on the screen.
* **Guarding a set at once** (one guard watching several objects). How the original shares a guard between its
  objects is not known; taking them in turn keeps one rule for attack and guard.

## Consequences

* `Waypoint` gains a kind and a target, and `Exploring` an area. The hashed layout goes to 12 and the snapshot format
  to 14. Every golden and the three CI benches are re-pinned.
* The original's gate has four directions and a 7-tile picture (`gw0`–`gw3`). Tzar2's gate is 3 tiles and turns
  between two directions, and it is not drawn yet. That is a separate change.
* Not done: the confirmation animation of an order on an object (CTRL-017), which is not identified in the data.
