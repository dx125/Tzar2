# 0103. Trade routes between two docks with distance profit; tavern conversion, gambling and loans

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 stream F)
* **Related:** TDD §8.5 step 5, §10 GAME-14, UNK-28, ADRs 0034, 0100, 0102

## Context

Manual ch. 5 §7: traders (caravans) and ships cycle between two markets or harbors, carrying chosen goods both ways
("brings wood from Market A and unloads it at Market B; from B it loads stone and unloads it at A"); profit grows with
the distance and the quantity; trade with one's own markets or other kingdoms'; Trade Routes is needed. Tavern:
conversion at 1/10; gambling with a chosen proportion after Usury → Gambling; loans start at 1,000, a new one only after
repaying, then 1,200 "after some time". The profit formula, odds and loan growth are UNK-28.

## Decision

* **Traits.** `Trader { capacity, access: land|water, technology }` on caravans (`^class.trader`) and galleons, hulks
  and xebecs; `TradeDock { access }` on markets (land) and harbors (water); `Tavern` on taverns.
* **Route command** (`Trade`): traders the player commands, dock A = target, dock B = the dock at (X, Y), goods per side,
  load per trip (≤ capacity). Both docks: completed, of the trader's access, A ≠ B, own or a mutual ally's; the trader's
  owner needs the technology (or `hmnotech`). Any other order ends the route and refunds the carried load.
* **Cycle** (step 5): walk to the dock (a path request at most every 20 ticks while standing); at the dock wait
  `tradeDockMs`, sell the load if it was loaded at the other dock — credited: load + profit, profit = load × min(tiles ×
  `tradeProfitPerTileBasisPoints`, `tradeMaxProfitPercent`) / 10,000, tiles = straight distance between the loading dock
  and this one, 0 below `tradeMinDistanceTiles`, then the `income` modifier of this dock's type (Trade Taxes) — then load
  this dock's goods (paid from the stockpile, shared resources apply; unaffordable: leave empty) and head for the other
  dock. A route ends when a dock is gone, unfinished or no longer a partner.
* **Tavern** (a listed own completed tavern): `Convert` gives whole multiples of `convertRatio` for one unit each;
  `Gamble` (needs Gambling) stakes ≤ `maxGambleStake` at one of `gambleOdds`, wins stake × odds with chance
  (1 − house edge) / odds from the match's `misc` RNG stream; `Loan` (needs Usury) credits the limit and records it due
  (+ interest), refuses a second loan while one is outstanding, and after repayment raises the limit by `loanStepAmount`
  once `loanGrowDelayMs` have passed. Incomes saturate at `stockpileCap`.

## Alternatives considered

* **Path length instead of straight distance:** closer to "distance between the markets" as walked, but path lengths
  depend on traffic and replans; the dock positions are stable. UNK-28 decides.
* **Profit in a chosen resource:** the manual also says "you can choose the type of the profit"; with goods chosen per
  side the carried type is the profit type. Revisit with captures.

## Consequences

* No trade loop gains anything without distance: a load is paid before it is carried, sold only at the other dock, and
  close docks give no profit; cancelling refunds exactly the load.
* Hiring mercenaries and heroes is ADR 0108 (it needed stream E's heroes; it attaches to the tavern trait).
