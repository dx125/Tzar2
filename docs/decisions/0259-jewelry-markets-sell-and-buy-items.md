# 0259. Let the markets sell and buy items under Jewelry, as the original does

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), under ADR 0190; coordinator (take TECH-030, 2026-10-22)
* **Related:** ADR 0094 (items), ADR 0186 (item variants), ADR 0190; ledger TECH-030

## Context

Jewelry (Asians) "lets the markets sell and buy items, the same items in every market" (manual; 1.09). The Asian
market's menus `market_items` and `market_sell` are empty in `MENU.INI` and filled by the game, so Tzar2 had no item
trade (TECH-030 missing).

ReTzar reads the original:

* **Offers** (`0x479c34`, `0x479cc0`). With Jewelry, one market of the kingdom (the first that stands) gets an offer a
  second after it stands, then every 60 s. The kind is drawn from book, power item, book, potion.
  * The record is random (`0x462380`, `0x463610`, `0x464d20`):
    * a book of one of the 21 spells (soul binding gives lightning and hypnosis a flame bolt), with 1 charge in the
      first ten minutes, 1–3 until twenty and 1–10 later;
    * a sword or bow of attack 5–20, or a shield of defence 5–20;
    * a potion of attack or defence 5–20, of health or mana 25–100, or of experience 5–20.
  * The list keeps the newest 8.
* **Buying** (`0x47a254`). Any market of the kingdom sells an offer for 4 times its value in gold. The item appears at
  a corner of that market, drawn at random.
* **Selling** (`0x47a1e1`). A market buys a book, power item or potion that any unit of the kingdom carries, for its
  value.
* **Value** (vt+0x2c4).
  * A power item is worth 4 × its power.
  * A book is worth its charges × the spell's rate × 1.5. A summon is rated at its creature's gold.
  * A potion is worth health + mana + 5 × experience + 2 × (attack + defence).
* **The original's spell numbering** puts hypnosis at 13, not last. Tzar2's map books followed the order of
  `STRINGS.INI [spell_names]`, which ADR 0186 marked as a hypothesis.

## Decision

* `combat.market*`:
  * technology, first and every ms, size, buy percent;
  * `marketOffers`: kinds of "item effect look min-max" variants;
  * `marketBookSpells`: the 21 spells in the original's numbering, with the two replacements.
  * Each spell's `bookValue` holds the original's rate.
* **The offers** are item entities held by the kingdom's offer market. A new store `MarketStock` (`game.market`) holds
  their list and the next offer's tick. This takes the hashed layout to 16 and the snapshot format to 18.
* **When the market falls or changes hands**, its offers go. The next market of the kingdom starts a new list.
* **Commands.** `MarketBuy` and `MarketSell` (53, 54) act on a selected own market. The new event `ItemOffered` (54)
  marks each offer.
* **The card** fills the market's empty menus: `market_items` lists the offers with their prices, `market_sell` the
  items the kingdom's units carry with their value. `FrameHeld.Value` carries an item's value to it.
* **Map books** read their spell by the original's numbering (`ScenarioSetup`).
* **Experience potions.** The original adds 5–20 points at once, which gives one level. Tzar2's offered experience potion
  gives one level, and is valued at 5.

## Consequences

* TECH-030 is tested. A market's offers can be seen in the frame, held by that market.
* Every golden and the CI benches are re-pinned.
* This was the last ADR number of 0250–0259 given to this work.
