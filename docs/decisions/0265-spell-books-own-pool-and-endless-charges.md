# 0265. Spell books keep their own recharge and may be endless

* **Status:** Accepted
* **Date:** 2026-10-23
* **Deciders:** agent (maintainer), under ADR 0190; coordinator (UNK-67, 2026-10-23)
* **Related:** ADR 0186 (map items), ADR 0264 (item properties on map objects); ledger ITEM-016, ITEM-018; UNK-67

## Context

A map's spell book record (`item_magic`, docs/formats/wmp.md §4.4) holds five fields after the spell. ReTzar reads
the original's book (0x4640f0, the object setup at 0x494f66, `book_charges`). The raw records of both original
campaigns confirm that reading:

| Offset | Meaning | Original campaigns |
|---|---|---|
| +0x60 | the spell, in the original's numbering | 0–19 |
| +0x62 | "needs mana": the book has a mana pool of its own and casts only when the pool is full; a cast empties it | 0 or 1 |
| +0x63 | "limited charges": without it the book never runs out | SAA11's book has 0 |
| +0x64 | the pool's size: the editor's "time restriction" | 1–30 |
| +0x66 | the charges | 1–20 |

A cast from a book is free for its holder.

Tzar2 got two of these wrong:

* The legacy reader took +0x63 as "needs mana" and ignored +0x62.
* `ScenarioSetup` applied only the charges. So an endless book became a book of N charges, and every book took its
  type's cooldown (10 s, UNVERIFIED) instead of its own.

`ItemState` cannot express either property: a book has no cooldown of its own, and "no charges" means "spent" when
the type has charges.

## Decision

* **`ItemState` gains `CooldownMs` and `Flags`.** `Flags` has two bits:
  * `OwnCooldown`: `CooldownMs` replaces the type's cooldown, and may be 0, which means no restriction;
  * `Endless`: the book never runs out and is never used up.

  Two reserved bytes keep the layout free of padding. The snapshot's item layout changes, so the full-state golden
  hashes are re-pinned. The persistent heroes format (version 3) and the player profile carry both fields with the
  hero's items.
* **A book's pool becomes a cooldown.** The original refills a pool of N points at the mana regeneration rate. Our
  `manaRegenPerMinute` is 60, one point a second (UNK-26, UNVERIFIED), so a pool of N is a cooldown of N seconds. A
  book without +0x62 has no restriction.

  The original starts a book with a full pool, so a placed book is ready at once. That is our `ReadyTick` of 0.
* **Reading the record:**
  * `LegacyItem` reads `NeedsMana` from +0x62 and the new `LimitedCharges` from +0x63.
  * `ItemInfo` carries `LimitedCharges`, and scenario JSON writes it as `limitedCharges` (absent means unknown).
  * The editor shows it as a check box.
* **`ScenarioSetup`:**
  * a book without limited charges is `Endless`;
  * a book with them gets its charges;
  * a book that needs mana gets `OwnCooldown` with its time restriction, and a book that does not gets `OwnCooldown`
    with 0.
  * A record without these fields (an older conversion, a hand-made scenario) keeps the type's values.
* **The rest:** the cast stays free for the holder, as before (`NeedsMana` of the item type, false for the classic
  books). The imported reference data changes with the scenario JSON, so `legacy.referenceDataHash` is re-pinned,
  measured after merging main with `TZAR2_ORIGIN`.

## Consequences

* The campaigns' books behave as in the original. SAA11's book is endless, and each book waits its own 1–30 s
  between casts.
* Packages imported before this change keep the old reading until they are imported again. That is the import's
  usual rule.
* ADR 0264's `MapItem` adds the same two properties to map.bin version 3, so the random map generator's books can
  have their pool of 1–5 and 1–3 charges.

## Alternatives considered

* **Give books a mana pool component:** rejected. It is closer to the original, but it needs a second regeneration
  path and the HUD's mana bar for items. A cooldown gives the same wait at the 1-per-second rate we have.
* **Use `Charges = 0` for endless books:** rejected. The type's charges would still make a book with no charges
  spent.
