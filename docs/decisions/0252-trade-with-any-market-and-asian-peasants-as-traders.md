# 0252. Trade with any kingdom's market, and let Asian peasants trade

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), under ADR 0190
* **Related:** ADR 0103 (trade), ADR 0190 (ReTzar as a source of facts); ledger TRADE-003, TRADE-004, HOLD-008; UNK-28

## Context

The ledger had three open rules on trade and caravans:

* **TRADE-003** (unclear): the manual says trade "can also be done with other kingdoms' markets", but not with whom.
  Tzar2 allowed only one's own and mutual allies' markets.
* **TRADE-004** (unclear): the help names "a caravan or an Asian peasant". Tzar2 traded only with caravans and ships.
* **HOLD-008** (partial): the help says caravans load units. The note said they did not, but the import already gives
  caravans `TRADER.INI`'s holder (8 infantry). Only the synthetic tables of CI lacked it.

The data and ReTzar settle them:

* The trade dialog (`0x4a2f36`) names two markets of any owner, and the profit goes to the trader's kingdom.
* The load per trip (`0x4e8060`) is 500 for European caravans and 300 for Arabian ones. Asian peasants carry 100, and
  no other peasant trades.
* `MENU.INI [a_peon]` has `trade`, and `TECHTREE ASIA.INI` gates it with Trade Routes. The Asian market makes no
  caravans: its card has the item orders.

## Decision

* A trader may load and sell at any kingdom's market or harbor, enemies' included. The route no longer ends when
  diplomacy changes (`KingdomCommands.TradesWith`).
* `^euro.trader` carries 500 a trip and `^arab.trader` 300. `^asia.peon` gets the `Trader` trait, with 100 a trip, land
  access and Trade Routes. Ships keep their placeholder loads (UNK-28).
* The synthetic `TRADER` object gets `max_obj_in_holder = 8` and `[accept] infantryunits`, as the original's has.

## Consequences

* The profit formula stays a placeholder (UNK-28). ReTzar reads it as `load × Chebyshev tiles × (40 + bonus) /
  (map side × 100)`, with nothing under 12 tiles. That is the next change for TRADE-001.
* The ruleset hash changes (the synthetic caravan and its load). The goldens and benches are re-pinned.
* The card still loads at the trader's nearest own market (TRADE-002, a UI rule).
