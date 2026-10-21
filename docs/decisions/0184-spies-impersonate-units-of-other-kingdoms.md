# 0184. Let a spy take the look of another kingdom's unit until it reveals itself or attacks

* **Status:** Accepted
* **Date:** 2026-10-21
* **Deciders:** agent (maintainer); product owner (asked for the rest of the command card, 2026-10-21)
* **Related:** ledger MAG-012, MAG-013, HUD-022; GAME-11, GAME-12; ADRs 0095, 0101, 0182

## Context

What the sources say about spies:

* Manual and help: spies "can move, attack, pick up items, patrol, guard, explore, impersonate, and reveal". "Select
  a spy and click on the Impersonate Order Button, then select the unit that needs to be impersonated." "Select an
  impersonated unit and click on the Reveal Order Button."
* MENU.INI `[spy_add]`: `impersonate = I`, `reveal_self = V`. ORDERS.INI `impersonate` has a place
  (`command_impersonate`: a click on a unit). IMAGES.INI has an `impersonate` animation.
* Patch notes:
  * "Spy's kingdom is shown properly" (1.05).
  * "Obsessed by spies peasants cannot build" (1.05): a spy that looks like a peasant does not get its abilities.
  * "When a spy impersonates a crusader and then reveals himself, the crusader anchor is properly updated".
* `uncloak` is not a spy's order: it is in `[ninja_cloaked]`, the cloaked ninja's card (MAG-013).

Not known: how close the spy must be, which units can be copied, whether the copied kingdom's towers and detectors
see through it, and whether attacking ends the disguise.

## Decision

* **Trait and component:** a unit type with the `Impersonator` trait (the classic spy) can disguise itself. The
  disguise is a component, `Disguise`, holding the type it looks like and the kingdom it seems to belong to.
* **Impersonate** (target: another kingdom's unit on foot or mounted, on the map): the spy walks to it, like a bribe,
  and takes its look on arrival. Event `Impersonated`.
* **What others see:**
  * Every other kingdom's frame shows the unit as the copied type and kingdom.
  * Its own kingdom's frame keeps its true kingdom (colour) and draws it in the copied look (`FrameEntity.LooksLike`;
    HUD-022, 1.05).
  * Its stats, abilities and card stay the spy's (1.05: impersonating peasants cannot build).
* **Who leaves it alone:** units and towers of the copied kingdom, and of that kingdom's allies, do not pick it as a
  target. Everyone else may.
* **Reveal and attack:** `RevealSelf` ends the disguise, and so does attacking. Death ends it too.
* **Card:** the spy's card gets the source's ability buttons (`ICardSource.AbilityButtons("impersonate")`: MENU.INI
  `spy_add`). Impersonate shows while not disguised, reveal while disguised. Impersonate waits for a click on a unit.
* **Hashes:** the disguise store raises the hashed layout to 9 and the snapshot format to 11.

## Alternatives considered

* **Change the spy's type and owner:** the crusader-anchor fix hints that the original did something like this. But
  the spy would gain the copied unit's abilities, which 1.05 says it must not have, and a kingdom would count an
  enemy's spy among its own.
* **Impersonate from a distance:** the help gives no range. Walking to the target is how the original's other
  touch orders work (bribe, capture), and it keeps the spy at risk while approaching.

## Consequences

* The AI reads true owners, so it is not fooled by disguises. A capture of the original would tell whether its AI
  is.
* ⚠ UNVERIFIED, for the owner or a capture: the reach, which units can be copied, whether towers and detectors see
  through a disguise, and whether attacking ends it.
* Old saves and snapshots are refused by version; every golden and bench hash is re-pinned.
