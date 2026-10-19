# 0169. The legacy TIME column is 1.25 s per unit; batch A's facts and the construction rate from the health bar

* **Status:** Accepted
* **Date:** 2026-10-19
* **Deciders:** maintainer (Claude, continuing the project after the implementation agent), product owner
* **Related:** ADR 0030 (capture harness), ADR 0110 (harness fixes), ADR 0111, ADR 0112 (fidelity suite), TDD §22.3,
  UNK-18, UNK-20, UNK-21; [fidelity-summary.md](../mechanics/fidelity-summary.md),
  [m2-placeholders.md](../mechanics/m2-placeholders.md)

## Context

The operator returned batch A of the second session, `capture-20261018-155421` (Windows 11 25H2, clean GOG v1.10 EN,
harness `8060f00` plus `hmnopop` before every spawn and a local DirectDraw device count of 2, both now in the harness:
PR #60). 12/12 trials, 82/82 usable images.

The importer read the TIME column of the unit and building tables as seconds (TIME 8 → 8,000 ms), an assumption no
fact had checked. Two independent measurements disagree with it by the same factor:

* `train-time`: three peasants queued at the castle; the HUD's population box steps every **9.99 s** (two intervals,
  sampled every 250 ms). Peasant TIME = 8.
* `build-time`: one peasant builds a lumber mill; the selection panel shows the site's hit points rising from 1 to 800.
  Over the first 24 s the bar grows linearly; a least-squares fit of that run gives **36.85 s** for the whole bar
  (a manual read of the same frames: 37.45 s). Lumber mill TIME = 30. After 22 s the rate doubled: a second peasant
  joined, which is why only the initial straight run is used.

Both are 1.25 s per TIME unit (9.99 / 8 = 1.249; 36.85…37.45 / 30 = 1.23…1.25). An offset (TIME + 2 s) fits the peasant
but not the mill (32 s).

The analysis `region-timing` had reported the construction as inconclusive: it looked for the moment the panel
"turns into the building's card", which the original never does (it shows the building with rising hit points all
along), and its change detector needs 2 % of the region's pixels to change, which the growing bar does not.

## Decision

1. **The TIME column of the unit and building tables is 1.25 s per unit** at the default game speed
   (`Tzar2.Content.Rules.LegacyTime.MsPerTimeUnit` = 1,250, used by the importer and by the fidelity suite, which no
   longer keeps its own copy of the assumption). Training and construction take 25 % longer than before.
2. **Research times are not scaled.** `TECHNOLOGIES.INI` / `UPGRADES.INI` write `time` in milliseconds (Feudalism
   30000), not in TIME units. If the factor is the original's game clock rather than the column's unit, research needs
   it too; `research-feudalism` (batch E) decides (UNK-21).
3. **Construction is measured from the health bar.** `region-timing` now also fits the selection panel's health bar for
   `measure: build`: the bar's row is the row with the most bar-green pixels in the last frame, each frame's fill is read
   on that row, and the longest initial run that stays within 2.5 px of its own least-squares line gives the one-builder
   rate. The fact records `one_builder_full_s`; `m2.build-time` uses it when present (no walk to remove, so it no
   longer waits for the peasant's measured speed) and falls back to the old "last change minus the walk" otherwise.
4. Batch A's facts are committed (`attack-timing`, `player-colours`, `smoke` updated; `build-time`, `shadow-overlap`,
   `train-time` new). The suite passes **5 of 30 (16.7 %)**, 5 of 5 measured.

## Consequences

* The gameplay and AI golden hashes change (rules compiled with the new unit); re-pinned in the same commit with this
  ADR as the reason. Tests that count ticks of training or construction were moved by the same factor.
* Everything built or trained in a match takes longer, as in the original. Balance values that were tuned against
  1 s per unit (none known: the classic rules take the tables as they are) need a look if they appear.
* The day length (batch D) and research timing (batch E) will tell whether the original's clock runs at 0.8 of real
  time. If it does, other "game time" constants (research, regeneration, timers in triggers) may need the same factor;
  until then they stay as written.
