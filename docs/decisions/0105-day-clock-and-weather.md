# 0105. Derive the day clock and the weather from the tick and the seed

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 stream F)
* **Related:** TDD §8.5 step 15, §10 GAME-16, §13.7, §13.8, UNK-09, UNK-32, ADR 0071

## Context

The client drew night and rain from placeholder clocks of its own (ADR 0071). GAME-16 asks for the cycle and weather per
terrain set in the sim; whether night or weather change gameplay is UNK-09. `<SET>.TERRAIN.INI` says which sets have
rain (grass, summer) and snow (snow); the desert has neither.

## Decision

* **Clock:** minute = (`dayStartMinute` + tick time × 1,440 / `dayLengthMs`) mod 1,440; night from `nightStartMinute` to
  `nightEndMinute`; with DAY-NIGHT off (lobby) or a zero length: always noon. Night multiplies sight by
  `nightSightPercent` (classic 100: no effect until UNK-09 is measured).
* **Weather:** the terrain set's kind (`world.weather`, restating the INI flags) in weather periods of `weatherPeriodMs`
  that roll under `weatherChancePercent` (SplitMix64 of the match seed and the period index); WEATHER off: clear.
* **No state:** both are functions of the tick; `ClockSystem` only emits `DayPhaseChanged` and `WeatherChanged`. Frames
  and the render snapshot carry the minute, night and weather; the client's globe, darkness and rain read them.

## Consequences

* Replays, snapshots and peers agree without storing anything; changing a constant changes the ruleset hash.
* Light sources (UNK-32) stay presentation.
