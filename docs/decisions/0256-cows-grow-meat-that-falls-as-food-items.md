# 0256. Let cows grow meat that falls as food items

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), under ADR 0190
* **Related:** ADR 0042 (butchering), ADR 0094 (items), ADR 0190; ledger NAT-004, ITEM-006

## Context

The help says a slaughtered cow's meat is collected by "peasants or other units" (NAT-004, partial). In Tzar2 a dead
cow left one meat node of 100 food, which only peasants could harvest.

ReTzar reads the original:

* **The cow's meat** (`+0x120`) starts at 300 and grows by 2 each second the cow can act, up to 600.
* **When the cow dies**, its meat falls as `item_meat` resource items of at most 100 food each.
* **Who takes them.** A peasant gathers a resource item (`0x464f60`, program `0x1e`) as its load and carries it home.
  Any other unit picks it up.

## Decision

* `SpawnOnDeath` takes `start`, `perSecond`, `max` and `piece` (`MeatDefinition`). The cow has 300, 2, 600 and 100.
* A new store, `MeatStock` (`game.meat`), holds each living cow's meat. This takes the hashed layout to 15 and the
  snapshot format to 17.
* `EconomySystem` grows the meat once a second while the cow can act. It stops while the cow is frozen or stone.
* `comm.meat` becomes a resource item of food. It still rots (UNVERIFIED).
* **A peasant that picks up a resource item** of a kind it gathers makes it its load and goes to a drop-off. This goes
  for gold treasures too, as in the original.
* **Any other unit**, or a peasant carrying another resource, brings the item to the stockpile at once. The original's
  carry-in-the-bag-and-drop is not modelled.

## Consequences

* NAT-004 is tested. ITEM-006's test now checks both ways of taking a resource item.
* Every golden that butchers cows moves and is re-pinned.
