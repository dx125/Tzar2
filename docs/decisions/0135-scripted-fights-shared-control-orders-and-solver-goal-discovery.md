# 0135. Scripted fights, shared-control orders, and the solver's goal discovery

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent M4-B
* **Related:** ADR 0130 (trigger engine), ADR 0134 (harness and solver), ADR 0140 (ships dock at the shore), GAME-09,
  GAME-13, UNK-57, UNK-69, UNK-81

## Context

After ADR 0134, six of the 19 maps of the original main campaign were still not completable headless. They needed
things the engine or the solver could not do yet:

* **Staged fights between allies.** A scenario orders a hero to "attack" an allied hero (an Object Order whose area
  is the other object), and a later rule waits until one of them is weak. The engine turned every scripted "attack"
  into an attack-move. Attack-moves never engage allies, so the fight never happened.
* **Shared unit control stopped at fighting.** Campaign heroes belong to an AI kingdom that lets the player command
  them. They must board the player's ships and carry items. The sim accepted boarding, unloading and item orders only
  from a unit's owner.
* **Goals hidden behind variables and appearances.** The final rule needs `$talked`, which a conversation's phrase
  sets, and the conversation is started by another rule. The solver pursued the final rule's own events first: it
  walked the heroes away before the conversation that needed them. The same happened with objects that appear only
  when an earlier rule fires.
* **Goals the solver could not reach on foot.** Some goal areas are walled in by enemy buildings. Others lie across
  water. "Bring the book to X" needs an item carried. Other goals were "$x <= 5" (leave by the west edge) and
  "$cargo >= 10" (load the ships).

## Decision

* **Scripted fights (sim + engine).** `ScenarioOperations.ScriptedAttack` sets a new combat mode,
  `CombatMode.Scripted = 5`. The unit fights its target whatever the stances, until the target dies or can no longer
  be fought, or until the unit gets another order. An Object Order "attack" whose area is an object now gives that
  order to each of its units. Attack orders on rectangles stay attack-moves. The snapshot layout is unchanged, and the
  combat-state validation accepts the new value. (UNVERIFIED, UNK-57.)
* **Shared unit control covers boarding, unloading and items (sim).** A player may order units it commands through
  GAME-13 unit control to board a holder. The holder must be its own, the unit's kingdom's, or one of a kingdom that
  may command the unit. The player may also unload holders it commands, and pick up or drop items with units it
  commands. Deleting and economic orders stay with the owner, as before. (UNVERIFIED, UNK-69.)
* **Production fix (sim).** Training a unit whose type is itself a producer adds a queue to the sorted production
  store, which can move the trainer's entry. `ProductionSystem` now looks the trainer up again before it pops the
  finished item. Before this fix, a converted map crashed with an index of −1.
* **Solver goal discovery** (still generic: it reads only the IR and the trigger state):
  * **Gates first.** For a rule whose condition needs a user variable that is still 0, the solver first pursues the
    rules that set it, walking the IR. A rule sets a variable when one of its Expression actions assigns it, or when it
    starts a conversation one of whose phrase results does. For an event about objects that have not appeared yet, the
    solver pursues the rules whose Appear Object actions name them. This repeats to depth 6. While a gate is open, the
    rule's own events are not pursued.
  * **Reachability.** Each unit walks to the nearest point of the goal area that its navigation layer connects to. If
    there is none:
    * when the area is a closed pocket within 12 tiles, it **breaches**: the units and the army attack the nearest
      enemy building next to the area;
    * otherwise it **ferries** them, using ADR 0140's orders: `Board` the nearest ship the kingdom commands, then
      `Unload` at the goal. The ship unloads only once everyone is aboard, or after a minute. Ships that a pending rule
      will erase or destroy are never used as ferries.
  * **Items.** A unit the kingdom commands picks the item up, walks to the area and drops it there.
  * **Coordinate goals and cargo goals.** "$x/$y op n" walks the units over the line, or ferries them there.
    "$cargo >= n" boards the nearest free units into each named holder. Loading comes before moving the holders.
  * **Crowds.** A unit that has not moved a tile in 30 seconds is stuck. Typed goals pick other units instead, and the
    army walks the same way to make room for stuck named units.
  * **Allies as targets.** A Destroyed or "$health" goal never targets units the local kingdom commands. If a scripted
    fight against an ally is staged or in progress, the solver waits for it. Otherwise it declares war on that ally with
    a player's diplomacy order, fights with its own units, and makes peace again afterwards.
  * **Clearing.** Clearing (the cheat assistance of ADR 0134) and the army's attacks leave alone the other kingdoms'
    objects that pending rules move by script, such as a fleet whose arrival triggers the next step.
  * **Protection on retries.** `hmgod` covers the heroes' kingdoms when the heroes appear, not only at start. Each
    kingdom gets the code once, because the code toggles.
  * **Log.** Notable decisions are logged once each in the mission's trace: a breach, a ferry, a declared war, a stuck
    unit, waiting for a scripted fight. Rejected commands are counted by kingdom and reason.
* **Allowed assistance (ADR 0134).** The new orders are player orders and are on the allowed list: `PickItem`,
  `DropItem` and `SetDiplomacy`. The list still has nothing that writes script state or skips a step.
* **Survey.** Maps that are not on the played path are retried with escalating assistance, like the maps on the path.

## Consequences

* The staged duels of the original campaign play out as scripted. A player cannot start a fight with an ally, because
  the Attack command still refuses one.
* Shared control is wider than before. A reviewer of GAME-13 should confirm it against the original (UNK-69).
* Some maps remain blocked by things outside the solver. One is terrain sealed by cliffs (UNK-81, which needs an
  in-game check). Another is ship and unit crowding at a single landing point.
