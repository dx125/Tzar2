# 0094. Items are entities that move into inventories atomically; their effects apply while carried or when used

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (M3 stream E)
* **Related:** TDD §10 GAME-10, ORIG-11 ("Clonar objetos"), §5.1 (Curse spellbook), UNK-25, ADRs 0090, 0093

## Context

The manual and the editor manual list weapons (+attack), armor (+defence), spell books (spell, "needs manna",
"time restriction", "max charges"), scrolls (story text), potions (level, attack, defence, health, mana), resource items
("the player's corresponding resource will increase with the amount"), flags, souls (`item_kill`, `item_berserk`) and
"plusplus"; inventories with pages and a default item; the Arabian Magic Workshop crafts items ("better items cost
more and take more time"). The object INIs give the item classes and who may carry them (`[enter]`). Item cloning was a
known exploit.

## Decision

* An **item** is an entity of an `Item` type (`game.item`: power, charges, ready tick) owned by Nature. On the ground it
  has a position; carried, it is `game.held` by its unit and listed in the unit's `game.inventory` (slots of
  `max_obj_in_holder`, 8; a default slot).
* **Pick up** (command 22): the unit walks there; on arrival the item must still lie on the ground and the unit must
  have room and be allowed to carry it — the first unit to arrive (errand order) gets it, so it can never be in two
  places. Resource items go to the stockpile at once and disappear; scrolls show their text (`ItemUsed`).
* **Drop / give / default** (commands 23, 25, 26): giving walks to an own unit and transfers on arrival (re-checked).
  A unit that dies, is deleted or converts drops (or passes on) what it carries.
* **Effects**: weapons add their power to the carrier's attack and armor to its defence while carried (`CombatStats`);
  potions and "plusplus" (⚠ HYPOTHESIS: +1 level) are used up (`UseItem`, command 24); spell books cast their spell
  through the normal cast cycle (ADR 0095) — needs-mana books cost the carrier the spell's mana, charges and the book's
  cooldown are paid when the spell takes effect, a book without charges left is gone; souls cast their spell
  (`murder`) once. `item_berserk` is not implemented (open question).
* **Defaults** per kind are UNVERIFIED YAML values (UNK-25); a map can give single items other values later (M4
  converter: the item state holds the power and charges).
* **Crafting** (command 33): a Magic Workshop crafts one listed item type at a time for gold; power = gold /
  `craftGoldPerPower` (≤ `craftMaxPower`), time = power × `craftMsPerPower` (all UNVERIFIED); the item appears next to
  the workshop. Asian market jewelry (buying artifacts) is the trade stream's (open question).

## Alternatives considered

* **Items as cold data inside the inventory:** an item on the ground would then need another representation; one
  entity per item keeps a single identity from the map to the inventory and back.

## Consequences

* Snapshot validation checks both directions (inventory slots ↔ held items), unique slots, and items only in
  inventories (ADR 0096).
