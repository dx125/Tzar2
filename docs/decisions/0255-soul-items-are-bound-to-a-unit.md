# 0255. Bind soul items to a unit, and spare the Messiah all magic damage

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), under ADR 0190
* **Related:** ADR 0094 (items), ADR 0250 (the Messiah), ADR 0190; ledger ITEM-010, WAR-010

## Context

**Soul items.** Tzar2's soul was a book of murder, used once on any target, which spared heroes. Its card orders
`item_kill` and `item_berserk` did nothing. ReTzar reads the original:

* **Soul Bind** (`0x473403`) leaves the target with its owner. A soul gem bound to the target appears in the caster's
  bag.
* **The gem's orders** (`0x465340`) act on the bound unit. `item_kill` strikes it for 10,000 magic damage, with no
  exception for heroes. `item_berserk` puts it under Discord (20 s). The gem stays.
* **When the bound unit dies, its gems vanish** (`0x4e219f`). An unbound gem does nothing.
* Soul Bind is on no command card, so it comes from map books (Tzar2 already reads their spell numbers). The workshop
  writes no such books.

**The Messiah.** ADR 0250 left damage spells hurting the Messiah. But the original's damage routine refuses damage
category 3 to the Messiah. Category 3 is magic: every spell, souls, and the attacks of mages, dragons, the Messiah and
ranged priests.

## Decision

* **Soul items.**
  * `ItemState.Bound` holds a soul's unit. This changes the hashed layout to 14 and the snapshot format to 16.
  * The new spell effect `soulBind` (`spell_soulbind`, range 8 and mana 50 as ReTzar's dispatch reads) makes the gem.
  * `UseItem` on a soul with `Arg1` 0 kills the bound unit; with 1 it confuses it for `combat.soulBerserkSpell`'s
    duration (spell_berserk, now 20 s as the original's Discord).
  * Destroying a unit destroys the souls bound to it.
  * The card: a click on a soul kills, Ctrl + click sends berserk.
* **The Messiah.** Spell damage and `Kill` spare `MagicImmune` units. So do the hits of `combat.magicAttackers`
  (mages, dragons, the Messiah, priests).

## Consequences

* ITEM-010 is tested and WAR-010 completes. Map souls without a bound unit now do nothing, as in the original.
* Not modelled: a gem lying on the ground can be commanded by anyone in the original; in Tzar2 only its carrier's
  orders act. The original's berserk is an area spell (±2 cells); Tzar2's targets one unit.
* Every golden and the CI benches are re-pinned.
