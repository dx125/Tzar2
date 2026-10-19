# 0171. SPEED 4 walks 40 px/s (10 px/s per point); speed scenarios move away from Nature's cows

* **Status:** Accepted; decisions 1 and 3 (speed = SPEED × 10 px/s, the two-SPEED-value check) superseded by ADR 0172,
  which measured that the original ignores SPEED. Amended 2026-10-19: the minimap click of decision 4.
* **Date:** 2026-10-19
* **Deciders:** maintainer (Claude), product owner
* **Related:** ADR 0104 (Nature), ADR 0112 (fidelity suite), ADR 0170, TDD §22.3, REQ-PATH-11, UNK-12;
  [m2-placeholders.md](../mechanics/m2-placeholders.md), [fidelity-summary.md](../mechanics/fidelity-summary.md)

## Context

The re-capture of `unit-speed`, `capture-20261019-020223` (Windows 11 25H2, clean GOG v1.10 EN, harness `ae9af2f`,
15/15 trials, 30/30 usable images; the first run on a Russian keyboard layout, which the harness switched to US itself),
spawned each class in the middle of MP2, selected it with a click and ordered it 300 px west, then east.

Four classes walked the whole path in both directions, all **SPEED 4**:

| Unit | West | East |
|---|---|---|
| peasant | 39.98 px/s | 40.00 px/s |
| archer | 39.90 | 39.97 |
| priest | 40.09 | 40.01 |
| mage | 39.93 | 40.03 |

(r² 0.96–0.99, 7.7 s and 13.9 s of walking.) So SPEED 4 is **40.0 px/s = 1.25 tiles/s** at the default game speed;
the engine had 12 px/s per point, 48 px/s.

The other eleven classes, all military, gave no usable track. MP2's middle has a row of **Nature's wild cows** (map
rows 33–34, owner 10) two tiles below the path. The frames show the spawned soldier walking off to them about two
seconds after being placed, before the select click; the move orders then went to nobody, and the tracker followed
the fight (a knight hacking at a cow below the band, carcasses appearing in the band). The engine already treats
Nature as everyone's enemy (ADR 0104), so this is the original behaving as the engine does, not a new rule. The
analysis also took some of those tracks at face value: a crusader at 69 px/s *westward* in the burst ordered east, a
pikeman (SPEED 2) at 40 px/s for 1.7 s, which polluted the SPEED → px/s relation.

## Decision

1. **`speedPixelsPerSecondPerPoint: 10`** in the classic ruleset (SPEED 4 = 40 px/s, VERIFIED). That speed is
   proportional to SPEED stays UNVERIFIED until a second SPEED value is measured; the register says so.
2. **The unit-speed analysis rejects** a fit shorter than 3 s or 100 px, and a fit whose horizontal direction is
   against the order of a burst named `west` or `east`. The entry stays in the facts with a `rejected` reason; the
   relation and the suite use only clean fits.
3. **The suite's speed check** (`m2.speed-per-point`) needs three units **and two SPEED values**: one value fixes the
   scale but says nothing about proportionality, which is what the check claims. It is pending now.
4. **Speed scenarios move** (`50-unit-speed`, `51-unit-speed-more`, `80-group-speed`): the minimap click (0.1, 0.95)
   clamps the view to MP2's bottom-left corner, where screen (x, y) shows tile ((x − 150) / 32, (y + 1280) / 32); the
   unit is spawned on rows 51–53 next to our own base (only our cows and peasants nearby, the nearest Nature cow 18
   rows away), placed, selected after 0.5 s (a double click would select every peasant in view) and walked 400 px west
   and 480 px east; the band (x 440–990) leaves out our own cows. A `view` snapshot before the spawn and an `end`
   snapshot after the walk show where things were. The scenario test also checks minimap clicks against the
   edge-scroll zone.
5. **The gameplay benchmark bot** (`GameplayScenario`) adapts so that the golden hash stream still covers repair and
   item pickup in 1,600 ticks at the new speed: it repairs the own building that is not on fire with the fewest hit
   points missing, using the busy peasant nearest to it, and soldiers pick items up within 24 tiles instead of 16.

**Amendment (2026-10-19, run `capture-20261019-150630`).** The minimap click (0.1, 0.95) of decision 4 did nothing:
MP2's 64 × 64 tiles are drawn 1 px per tile in the middle of the 128-px minimap box (x 36–99, y 63–126), and the game
ignores a click outside the map. The view stayed on the start view (tiles x 13.5–41, y 40–63, centred on our middle
castle), the vertical clamp was as computed (our cows of row 52 at y ≈ 400), but the path ran through our own cows and
every fit was biased (SPEED 4 units at 22–30 px/s); the run's facts are not committed. The scenarios now click map tile
(6, 60), minimap (0.297, 0.719), which clamps the view to the bottom-left corner as intended; the scenario test rejects a
minimap click outside MP2's map for scenarios that launch MP2.

## Alternatives considered

* **Keep 12 px/s until proportionality is measured:** 12 is known to be wrong for SPEED 4, which is the speed of most
  of the units on a map (peasant, footman, archer, priest, mage); 10 is right for those and no worse for the others.
* **Fit px/s = a × SPEED + b from this run:** the only points at other SPEED values were fights, not walks.
* **Stay in the middle and select faster:** a selected soldier keeps its move order, but idles at the end of the walk
  and goes for the cows again, and the measurement depends on a race with the game's target search.
* **Remove the cows with cheats** (`hmkingdom` on each): many steps per trial, and cows wander.

## Consequences

* Every unit walks a sixth slower than before. Golden hashes: the gameplay ruleset, hash stream and final hash, the
  AI command and hash streams, the trigger mission hash and the CI bench hashes (gameplay, AI) are re-pinned; the sim
  scenario does not use the classic ruleset and keeps its hash.
* The peasant's measured speed (40 px/s) is now available to the suite's build-time and gather-time checks to remove
  the walk from a timing.
* `unit-speed` is captured again at the new place (about 16 min); batch C (`unit-speed-more`) and E (`group-speed`)
  use it too. If the clamped view is off by a tile, the `view` snapshot shows it.
* Batches E–I have other scenarios that leave military units idle in MP2's middle (combat outcomes, experience, damage
  bonuses, capture); they need the same review before they are run.
* Suite: still **6 of 30 (20 %)**.
