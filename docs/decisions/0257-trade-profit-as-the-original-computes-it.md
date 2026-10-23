# 0257. Compute the trade profit as the original does

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), under ADR 0190
* **Related:** ADR 0103 (trade), ADR 0190 (ReTzar's to-do list: "the trade profit's distance rule"), ADR 0252;
  ledger TRADE-001, TECH-023, TECH-024; UNK-28

## Context

Tzar2's trade profit was a placeholder (UNK-28):

* 1 % of the load for every tile of straight line between the docks;
* none below 8 tiles;
* at most 300 %.

ReTzar reads the original's routine (`0x4e7c98`, previewed by the trade dialog at `0x4a2844`):

* **The profit** is `load × distance × (40 + bonus) / (map side × 100)`.
* **The distance** (`0x454e40`) is the Chebyshev distance between the two markets' centres, in cells. It counts as 0
  under 12 cells or when both markets are the same.
* **The bonus** is 2 for Trade Taxes and 3 for Banking.

A caravan crossing a whole 128-cell map therefore earns 40 % of its load, and half the map 20 %.

## Decision

* `world.tradeProfitPercent` (40) replaces `tradeProfitPerTileBasisPoints` and `tradeMaxProfitPercent`.
* `world.tradeMinDistanceTiles` is now 12.
* `TradeSystem.ProfitOf` counts the tiles on the larger axis and divides by the map's larger side.
* Trade Taxes and Banking keep their income percent (+5 % and +7 %, ADR 0190), the nearest whole percents to 42/40
  and 43/40.
* The fidelity check `m3.trade-profit` takes the map side from the capture (`map_tiles`).

## Consequences

* Trade earns far less on small maps over short routes than the placeholder did. The gameplay golden moves.
* Not modelled: the original loads a caravan bit by bit, at most `1000 / markets of the kingdom` a step, retrying
  every second until the load is complete.
