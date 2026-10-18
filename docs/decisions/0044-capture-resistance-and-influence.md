# 0044. Capture by digging against resistance plus EMIT influence; poach animals at once; protect the castle

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 stream A)
* **Related:** TDD §8.5 step 12, §10 GAME-04, UNK-19, ADRs 0038, 0040

## Context

GAME-04: peasants capture enemy buildings by digging at them; the manual (ch. 5) says each building's resistance sets
how hard that is and that buildings far from their owner's towns resist less; Fanaticism and Buddhism raise
resistance; castle and guilds are deletable only with Shift + Delete. The tables give RESISTANCE and an
`EMIT (own/other)` pair (a positive and a negative number for castles, guilds, outposts...); `OBJECTS type = building,
capture` marks the capturable classes. How EMIT works is unknown (UNK-19).

## Decision

* **Capturable** from the object INI's `capture` flag; peasants are `Capturer`s. `Capture` (command 15): another
  kingdom's completed capturable building; capturers walk there (worker task Capture) and dig in reach.
* **Progress** (step 12, `CaptureSystem`): each digger adds `capturePointsPerSecond` (UNVERIFIED 10) per second to the
  building's `CaptureState` (milli-points). Progress belongs to one player; another player's diggers take over only after
  the first stopped for more than a tick. Without diggers for `captureResetMs` (UNVERIFIED 5 s) it is lost. At the
  resistance the building changes owner (`World.ChangeOwner`: the queue is lost, the fight ends, the hit points follow
  the new owner's technologies; `Captured`).
* **Resistance** = RESISTANCE after the owner's `resistance` modifiers (Fanaticism, Buddhism) + Σ over every other
  completed building with an EMIT of a player within `influenceRadiusTiles` (UNVERIFIED 16, center to center):
  `(own if same owner else other) × (R − d) / R`, at least `minimumCaptureResistance`. ⚠ HYPOTHESIS (UNK-19): own
  buildings' EMIT raises it, other kingdoms' lowers it, fading linearly — it makes a building near its town harder and
  one near the enemy easier to take, as the manual says.
* **Instant capture** (`Capturable.instant`): cows under Cow Poaching (`Capturable.technology: te_cowpoaching`, which
  only the Arabian tree researches) change owner when a capturer reaches them.
* **Protected delete:** `ProtectedDelete` types (castle, guilds) are deleted only by a Delete with the Shift flag
  (`CommandFlags.Queue`); otherwise `ShiftDeleteRequired`.

## Alternatives considered

* **Influence as a per-tile field updated incrementally:** cheaper per query, but captures are rare; summing the EMIT
  buildings for the few buildings being dug is a few thousand operations per tick at most.
* **Resistance as a timer (seconds):** the data's numbers (50 for a house, 1,000 for a castle) read like points.

## Consequences

* Captures of capture times near and far from towns (UNK-19) replace the rate, the radius and the falloff.
* Nature's buildings (magic gateways, captured by touching, GAME-15) and garrison defence (GAME-09) are not part of this.
