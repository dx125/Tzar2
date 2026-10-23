# 0264. Give map objects item properties; the random map generator makes its items as the original

* **Status:** Accepted
* **Date:** 2026-10-23
* **Deciders:** agent (maintainer), under ADR 0190; coordinator (work queue item 17, 2026-10-23)
* **Related:** ADR 0152 (map object extras, map.bin version 2), ADR 0060 (the random map generator), ADR 0121
  (`.t2map`); ledger ITEM-015, ITEM-016; work queue item 17

## Context

The random map generator places one generic `comm.item` with the type's own strength. ReTzar reads the original's
generator (0x4b5b14, 0x4b5c50–0x4b6150). It places three item classes, `item_plusplus`, `item_magic` and
`item_health`, R(1, 10) + side / 32 of each. Each item gets its own properties:

| Class | Picture | Property |
|---|---|---|
| Books (`item_magic`) | `item_book1`–`3` | one of seven spells (cloak, flamebolt, turn to stone, lightning, raise dead, bless, heal: table 0x58e788 into the spell table 0x58a2f0), 1–5 charges, and a value of 1–3 at +102 whose use is not known |
| Weapons and shields (`item_plusplus`), R(2, 4) | a bow (`item_bow1`–`3`), a shield (`item_shield1`–`3`) or a sword (`item_sword`, `item_sword1`–`3`) | the bow's ranged attack, the shield's defence or the sword's attack: 5–15 |
| Potions (`item_health`), five kinds from two descriptors | `item_potion2`, `4`, `3`, `7`, `6` | health or mana 10–50; experience levels, attack or defence 2–8 |

RES.INI's TreasuryItem, WeaponItem, ArmourItem and PotionItem are not what the generator reads (ITEM-015, unclear).

The sim can already hold these properties per item, since the magic workshop sets them on the items it makes
(`ItemState`: effect, power, charges, spell, look; ITEM-016). A map, however, cannot carry them. `MapObject` has a
type, an owner, a position and ADR 0152's flags, facing and amount, and nothing else.

A scenario's `ObjectInfo.Item` (Appendix C.2) is not the place for them:

* random maps have no scenario;
* the sim does not read it;
* it keeps the legacy item record's fields (value, magic type, cooldown), not the sim's.

## Decision

* **`MapObject.Item`:** an init-only `MapItem?`, null by default. A `MapItem` holds:
  * `Effect`: an `ItemEffect`; `Default` means the type's own effect;
  * `Power`: 0 means the type's;
  * `Charges`: 0 means the type's;
  * `Spell`: a spell key, empty for the type's spell;
  * `Look`: one of the type's `Looks` image names, empty for the type's own.

  Keys rather than ids keep a map valid across rulesets that number spells differently. An unknown key falls back
  to the type's own spell or look.
* **map.bin version 3.** It is written only when some object has an item. It holds version 2's extras and, per object,
  a u8 that is 1 when an item follows. The item is u8 effect, i32 power, i32 charges, string spell, string look.

  Maps without items still write version 1 or 2 with the same bytes. Versions 1 and 2 still load.
  `MapData.ContentHash` adds the items only when there are some. So every existing map, package, golden and replay
  keeps its hash.
* **`World.LoadMap`** applies a map object's item to the entity's `ItemState`, the same way the workshop applies a
  crafted item.
* **The generator** (a separate PR) places the original's three classes, with the counts and properties above. Its
  golden hashes are re-pinned in that PR, and the generated maps then write map.bin version 3.

The meanings carried over from ReTzar are listed below. Where our names differ from the original's, the original's
pictures and ranges are kept:

* A potion's kind sets its effect: `item_potion2` health, `item_potion4` mana, `item_potion3` levels, `item_potion7`
  attack, `item_potion6` defence. Our workshop's list (ITEM-016) names `item_potion6` for attack and `item_potion5`
  for defence; that list is not changed here.
* The bow's value is ranged attack (+0x17). Our item effects have one attack for both kinds.
* The books' value of 1–3 at +102 is kept as the book's power. ⚠ UNVERIFIED what the original does with it.

## Consequences

* A map can now carry each item's strength. The editor could set these properties and the legacy converter could
  read them from the original's maps (+0x60 to +0x66 of the item record), but neither is done here.
* map.bin has three versions. A reader must accept all three, and a writer must write the lowest version that holds
  the map.

## Alternatives considered

* **Use the scenario's `ObjectInfo.Item`:** rejected. Random maps have none, and its fields are the legacy record's.
* **Always write version 3:** rejected. It would change the bytes and hashes of every map, and every golden would need
  re-pinning.
* **Read RES.INI's item sections:** rejected. The original's generator does not read them (ITEM-015).
