# 0093. Holders take units off the map; board on arrival, unload to free tiles, heal inside, share damage

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (M3 stream E)
* **Related:** TDD §8.5 steps 6 and 13, §10 GAME-09, ORIG-11, UNK-24, ADRs 0042 (ships on water), 0090, 0091

## Context

Castles and outposts garrison units ("Units garrisoned in castles and outposts will heal themselves quickly … take a
small amount of damage every time the building takes damage from siege weapons and warships", strategy guide); ships
carry soldiers (manual: right-click the ship; Landing Tactics adds room); carpets and caravans hold units. The data:
`max_obj_in_holder` (castle, outpost 50; galleon 24, hulk 16, xebec 8; carpet, caravan 8), `[accept]`, `[damage]`.
Ships already move on water (M2-B ship layer, ADR 0042). ORIG-11 asks for atomic holder transactions.

## Decision

* **Inside a holder** a unit has `game.held` (holder, its movement request serial) and no `Motion` or occupancy: it is
  not on the map — no movement, avoidance, targeting, acquisition, rendering (frames and render snapshots skip it) or
  orders (the command system filters held units out of every order but Delete). Its position follows the holder each
  tick (step 13: ships and carpets carry their units). The holder keeps `game.holder` (count, healing timer).
* **Board** (command 20): units walk to an own, completed holder that accepts their type (an errand, step 6) and enter
  when they reach it (ships across one shore tile); room and acceptance are checked again on arrival, in errand order,
  so a full holder never takes one more (the late ones give up). Capacity = `max_obj_in_holder` + `holderCapacity`
  modifiers (Landing Tactics +4 on ships, UNVERIFIED).
* **Unload / emerge** (command 21, optionally to a point and up to N units): each unit takes the free tile nearest to
  the point (buildings: the rings around the footprint; ships and carpets: passable tiles within `exitRadiusTiles`);
  without room it stays inside. A destroyed holder lets its units out the same way; where there is no room (a ship at
  sea) they die with it (strategy guide: "send them to the bottom of the ocean"); a captured holder lets the previous
  owner's units out.
* **Healing**: `Holder.healPerMinute` per unit inside (castle, outpost 120, UNVERIFIED UNK-24), with carry-over.
* **Damage shares**: a hit on a holder with units inside keeps `take%` and spreads `distribute%` evenly over the units
  inside (the first ones by entity index take the remainder); a `DamageShared` event reports it.

## Alternatives considered

* **Keeping held units on the map, hidden:** every system would have to skip them; removing movement and occupancy
  makes the existing systems ignore them by construction.
* **Holders nesting (a carpet in a ship):** no source; refused (validation).

## Consequences

* Caravans' goods and markets holding caravans belong to trade (GAME-14); ships entering harbors (`[enter] harbor`)
  is not implemented (open question).
