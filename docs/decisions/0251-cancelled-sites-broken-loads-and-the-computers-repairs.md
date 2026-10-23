# 0251. Refund cancelled sites, drop broken-off loads and leave repairs to idle peasants, as the original does

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), under ADR 0190
* **Related:** ADR 0180 (loads appear at once), ADR 0190 (ReTzar as a source of facts); ledger BLD-020, ECO-002,
  AI-003

## Context

The ledger left three rules unclear because the documents say too little:

* **BLD-020.** The help says that deleting a building "at the beginning" of its construction gives its cost back.
  Tzar2 refunded a cancelled site in full at any time (a placeholder).
* **ECO-002.** The owner saw that gathering broken off before the load is whole gives nothing. Tzar2 does this (ADR
  0180), but no source said how the original fills a load.
* **AI-003.** The guide says the computer does not repair its towers, but in a single campaign mission.

ReTzar reads all three from the original's code:

* **A site leaving the map** (`0x41d6a8`) gives back all its cost before any work. After that it gives
  `cost × f / 1000` of each resource, with `f = (1000·T − 1000·p) / (2·T)` truncated: half of what is left to build.
* **Loads.** A peasant's load is set whole at the end of its work cycles (`0x4ad1cc`: six chop cycles for a tree; a
  mine's take at once). Nothing is carried before that.
* **The computer's idle peasant** (`0x4153a0`) takes up a site within 21 tiles, carries its load home, picks up a
  resource item within 16, and after two think periods idle repairs the damaged object nearest it within 15. No other
  plan of the computer repairs.

## Decision

* **A cancelled site** gives back `gameplay.constructionRefundPercent` of what was paid before any work, and that share
  times `f` after (`GameplayCommands.SiteRefundBasisPoints`). The setting stays at 100 %.
* **ECO-002** is tested as it is.
* **AI-003** is tested through Tzar2's idle work. Every kingdom's peasant that stands idle for `idleWorkAfterMs`
  repairs the nearest own damaged building within `idleWorkRadiusTiles`, and Tzar2.AI gives no repair order. The delay
  and radius stay UNVERIFIED: the original's are two think periods and 15 tiles, for the computer only.

## Consequences

* Cancelling a half-built site now costs a quarter of its price. The AI does not cancel sites, so the goldens follow
  only from the ruleset text (the setting's comment does not enter the hash; the code path does not run in them).
* Follow-up: the computer's idle peasant (sites within 21, items within 16, repairs within 15 after two periods) is a
  rule of its own for the AI work.
