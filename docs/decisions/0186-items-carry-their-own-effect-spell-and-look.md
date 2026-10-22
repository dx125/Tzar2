# 0186. Items carry their own effect, spell and look; the magic workshop makes each kind's variants

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), from the original's data and maps; product owner (answers 2026-10-21: the workshop
  panel's three kinds have several items each)
* **Related:** ledger ITEM-016, ITEM-002, ITEM-003, ITEM-005, ITEM-008; UNK-25, UNK-67; ADR 0094 (items)

## Context

Until now every item of a type was the same. A spell book always held its type's spell (Fire Rain). A potion added its
type's fixed values. `item_plusplus` was taken to be a level-up potion, a guess (⚠ HYPOTHESIS). The workshop offered
five item types.

The original says otherwise:

* **The workshop panel.** It has three toggles, `book_itm`, `potion_itm` and `power_itm` (STRINGS3 "Book Items",
  "Potion Items", "Power Items"). The owner: each kind holds several items, "different spells in books, different
  effects of potions, weapons for attack or defence (swords, bows or shields)".
* **The editor's strings.** STRINGS3 `[tzaredit]` lists an item's properties: Health, Experience, Manna, Defense,
  Attack, then Sword, Shield and Bow.
* **The 33 maps' item records.** The generic fields of each object record carry the item's numbers; the class
  extension carries its name, which is an IMAGES.INI look.
  * **`item_plusplus` (power items).** Swords (`item_sword`, `item_sword1`–`3`) and bows (`item_bow1`–`3`) have an
    attack of 7–24 and the value word 1. Shields (`item_shield1`–`3`) have a defence of 5–50 and the value word 2.
    Every one of them is a power item that a unit carries, and none is a level-up.
  * **`item_health` (potions).** Each sets one of health (46), level (3, 4 or 10) or mana (35, 100). Looks:
    `item_potion2`, `item_potion3` and `item_potion4`.
  * **`item_magic` (books).** The magic type runs 0–19, with charges 1–20, in all three looks `item_book1`–`3`.
    Read as an index into STRINGS.INI `[spell_names]`, the types give Rain of Fire, Lightning, Freeze, Invisibility,
    Stone Golem, Magic Vision, Raise the Dead, Turn to Stone, Whirlpool, Flame Bolt, Teach and Curse.
* **RES.INI.** `[PotionItem]` has `attack`, `defence`, `health`, `exp` and `manna`: the random potion's effects.

## Decision

* **An item's own state.** `ItemState` gains three fields (hashed layout 11, snapshot format 13):
  * `Effect` (`ItemEffect`: default, attack, defence, health, levels, mana);
  * `Spell` (1 + a spell id, 0 for the type's spell);
  * `Look` (1 + the index in the type's `Item.looks`, 0 for the type's own image).
* **Power items.** A carried weapon or armor adds its power to its own effect, else to its kind's stat. A potion with
  an effect adds its power to that stat only. A book casts its own spell when it has one (`World.ItemSpell`).
* **Rules.** `Item.looks` lists the images a single item may wear. `Item.crafts` lists what a crafter makes of the
  type, each entry `effect [look]` or `spell [look]`; none means the type as it is. In `classic.yaml`:
  * `item_plusplus` is a weapon (legacy items map) with `looks` sword, sword1–3, bow1–3, shield1–3. It crafts a sword
    (attack), a bow (attack) and a shield (defence).
  * `item_health` crafts health, levels, mana, attack and defence potions.
  * `item_magic` crafts a book of each of 20 spells.
  * The workshop's `ItemCrafter.items` are these three types: the panel's three kinds.
* **Crafting.** The order `CraftItem` takes the item type in Arg0, the gold in Arg1 and the variant in X; the job keeps
  the variant. The gold buys power as before. Power becomes points of attack or defence, 10 hit points or 5 mana per
  point, one level per 5 points, or a book's uses (`craftHealthPerPower`, `craftManaPerPower`, `craftPowerPerLevel`;
  UNVERIFIED, UNK-25).
* **Maps.** `ScenarioSetup` gives a map's items their numbers:
  * a power item's attack or defence (the value word 2 means defence);
  * a potion's first set stat;
  * a book's spell, its magic type taken as the `[spell_names]` index (⚠ HYPOTHESIS);
  * the look from the item's name.
* **The client.** It draws an item in its look (`FrameEntity.ItemLook`). The workshop dialog lists each kind's items
  by name and picture.
* **Campaign heroes.** They keep their items' spell, effect and look (persistent hero format 2; format 1 is still read).

## Alternatives considered

* **One entity type per variant** (a sword type, a shield type, 20 book types). Maps set numbers per item, not per
  type, and the look is independent of the effect (red potions held health or levels). Types would multiply without
  covering the maps.
* **Packing the variant into Arg0.** It works but hides a field in the item type's bits. X is free for this order.

## Consequences

* Map items now behave as the maps say. Before, a campaign sword gave +1 attack and a shield +2, the value word read
  as the power, and every power item was a level-up. Now they give their 7–24 attack or 5–50 defence.
* Still unknown:
  * whether the magic type indexes `[spell_names]`;
  * which spells the workshop offers;
  * the minimum gold of a book (STRINGS3 `goldreq`: "This Magic Book requires at least %d amount of gold");
  * which potion colour the workshop gives attack and defence;
  * the craft scales.

  An owner session in the original's workshop and editor settles them.
* A potion record that sets several stats keeps only the first (none of the 33 maps has one).
