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

## Amendment: the owner's answers (2026-10-21)

The owner settled the unknowns from the original:

* **Reach:** the spy comes right next to the unit, as decided.
* **What it copies:** any man of any civilization, but no machine, flyer or magic creature. The spy's trait lists
  what it never copies (`Impersonator: { never: [machines, ships, magic_creatures], except: [mage] }`, BONUS.INI
  groups; the mage is listed among the magic creatures but is a man). Flyers and animals kept for food are refused
  by the sim itself.
* **Fighting:** attacking does not end the disguise. This replaces "and so does attacking" above.
* **Who sees through it:**
  * towers do not;
  * another kingdom's spy (a detector) that comes close enough unmasks it and ends the disguise; allies do not.
  * The distance is not in the data. Tzar2 uses the detector's radius (5 tiles, UNVERIFIED) until a capture
    measures it.
* **1.05's "obsessed by spies peasants cannot build":** a spy in a peasant's look, which keeps the spy's
  abilities.

## Amendment 2: the disguise meter (2026-10-21)

The owner described one more part of the original (MAG-021): by a spy's portrait there is a meter up to 100. The
longer the spy keeps the look of one unit ("or trains"), the higher it goes, and at 100 the spy can hardly be
unmasked. The data holds no rate. Batch J's `spy-mastery` captures the panel while a spy waits in a look.

* **State:** `Disguise.Mastery`, 0–100, in the byte that was padding. The hashed layout and the snapshot format do not
  change: old snapshots hold 0 there.
* **Growth:** the meter gains one point every `gameplay.disguiseMasteryMs` / 100. It is full after 2 minutes
  (UNVERIFIED).
* **A new look** starts the meter from 0; impersonating the same look again keeps it. "Or trains" is not modelled.
* **Unmasking:** another kingdom's spy must come within (100 − meter) % of its detector radius. That is 5 tiles for a
  fresh disguise, 2.5 at half, and never at 100. The owner says "hardly ever"; a chance that remains is not modelled.
* **Who sees it:** only the spy's owner (`FrameEntity.DisguiseMastery`). The selection view draws the meter as a
  column at the portrait's right edge. Where the original draws it and in which colour is UNVERIFIED.
* **Hashes:** the gameplay golden's ruleset hash is re-pinned. The state hashes and the CI benches do not change.

## Amendment 3: batch J's first run (2026-10-21)

The capture (`capture-20261021-203846`, manual.yaml `spy-disguise`) settled the meter and raised two questions:

* **The meter** is the number after the spy's name in the selection view, "Spy (N)". A spy in its own look shows 100.
  A new look starts it at 0, and it gains one point every 1.25 s (the original's time unit, ADR 0169) up to 100.
  `gameplay.disguiseMasteryMs` is 125000. The selection view shows "Spy (N)" in place of amendment 2's column; the
  frame gives the owner 100 for a spy in its own look.
* **The copied unit:** no other kingdom was at hand on MP2 (`hmkingdom 3` does nothing there), so the spy copied its
  own kingdom's peasant. The peasant was gone afterwards, the population one lower. The selection view showed the
  peasant's portrait, attack and defence, and a card like the peasant's (move, build, capture, harvest, pick item,
  explore, stop, reveal) under the spy's name. That matches the patch notes' crusader anchor and "obsessed by spies
  peasants cannot build". Before Tzar2 changes how impersonation works, batch J's second run repeats it with another
  kingdom's peasant on MP6.
* The unmasking distance is still not measured: the detecting spy stayed ours.

## Amendment 4: the spy takes the unit over (2026-10-21)

The owner confirmed what batch J's first run showed. When a spy impersonates another kingdom's unit, that unit does
not stay with its kingdom: the spy becomes it. On screen the spy is gone and the unit stays, now under the spy's player,
with all the unit's stats. Its old kingdom still sees one of its own. This is the alternative the decision above set
aside ("change the spy's type and owner"), and the patch notes' crusader anchor fits it.

Tzar2 will follow it in the next change: the impersonated unit changes owner and keeps its type, stats and place; it
carries the disguise (its old kingdom's colours for everyone else, the meter) and remembers that it is a spy, so that
revealing or being unmasked turns it into a spy again; the spy entity is removed. What a revealed spy's hit points are
is not known; Tzar2 will keep the share of health. Until then, MAG-012 is partial.

Batch J's second run (`capture-20261021-212542`) damaged all three buildings by handing them to kingdom 2 for the
attack, but no 4-byte memory field held their hit points. The spy runs on MP6 failed: the map loads slower than the
10 s wait (the first cheats were lost), the camera starts in the middle, and every kingdom there has an AI. The third
run scans 2 bytes, captures the buildings' selection view during the repair, and measures the unmasking on MP2 with
kingdom 2's AI driving the detecting spy.
