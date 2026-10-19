# 0173. A game day lasts 37.5 minutes, read from the clock globe; a match starts at 10:00

* **Status:** Accepted. The night's share is still open (UNK-32).
* **Date:** 2026-10-19
* **Deciders:** maintainer (Claude), product owner
* **Related:** ADR 0105 (day/night), ADR 0112 (fidelity suite), TDD §22.3, UNK-32;
  [m3-placeholders.md](../mechanics/m3-placeholders.md), [fidelity-summary.md](../mechanics/fidelity-summary.md)

## Context

Batch D, `capture-20261019-195053` (`day-clock`: MP2 with day/night on, no input, a region holding the clock globe and
a piece of the world view captured every 2 s), ran 12.2 of its 14.5 minutes: the game then exited by itself (exit code
0), most likely because the Hard AI had beaten the passive kingdom. Its units walked through the captured view from
minute 4 and it built a house there.

The analysis read the view's brightness, and those units and the house made it cross its mid level many times within
seconds; it reported a 15-second day and a night share of 0.34, both meaningless. The **clock globe** did not care:

* `IMAGES/SCREENS/S_VIEW/DAYNIGHT.BMP` is 36 × 864, **24 frames** of the 36 × 36 globe (`LAYOUT.INI` clock). Frames 0–3
  and 21–23 show the moon, 4–8 and 18–20 stars, 9–11 the sun rising, 12–14 the sun at its height, 15–17 the sun
  setting: one frame per hour, frame 12 at noon.
* In the capture the globe showed frame 10 at the start and stepped to the next frame **8 times, every 93.7 s**
  (74, 168, 262, 356, 450, 544, 638, 730 s: 656 s for 7 intervals), reaching frame 18 when the game ended.

So a game hour lasts 93.75 s and, if the night's hours are as long as the day's, a day 24 × 93.75 s = **2,250 s =
37.5 min** at the default game speed; a match starts at **10:00**. The engine had 12 minutes (`dayLengthMs: 720000`,
UNVERIFIED) and 10:00 (UNVERIFIED).

## Decision

1. **`world.dayLengthMs: 2250000`** (VERIFIED, with the assumption above) and **`dayStartMinute: 600`** (VERIFIED by the
   globe's frame at the start) in the classic ruleset. The night's start and end stay UNVERIFIED.
2. **The `day-clock` analysis reads the globe**: steps are changes of the globe's disc (centre (22, 17), radius 13 in
   the burst region, inside the frame, where nothing from the view can reach); changes closer than 30 s are one step;
   three or more steps at regular intervals give `hour_s`, and `day_length_s = 24 × hour_s` (`day_length_from: globe`).
   The brightness is read right of the globe only, and its crossings count only when no two are closer than 60 s
   (`brightness: clean | noisy`); a noisy series gives no day length and no night share.
3. Batch D's facts are committed. Suite: `m3.day-length` passes, **8 of 30 (26.7 %)**.

## Alternatives considered

* **Match each captured globe against the 24 frames of `DAYNIGHT.BMP`** to read the hour of every step: more precise
  and it would also give the start hour automatically, but it needs the original's bitmap at analysis time; the steps'
  timing is what the check needs, and the start hour was read once by eye (recorded here).
* **Keep the brightness method and filter the units out:** the house the AI built stays in the view; the globe is the
  game's own clock.

## Consequences

* A day is three times longer than the engine assumed; the ruleset hash changes, nothing in the 1,600-tick golden run
  depends on the time of day (stream and bench hashes unchanged).
* **Night share (UNK-32) still needs a capture of a night:** dusk to dawn spans about 14 game hours (22 minutes), so the
  run must last 35 minutes or more, in a view the AI does not walk through, in a match it does not end. Today's
  `day-clock` is ended by the Hard AI after about 12 minutes; the scenario needs a setup without an attacking AI.
