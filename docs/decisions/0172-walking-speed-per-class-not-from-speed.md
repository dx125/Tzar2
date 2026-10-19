# 0172. Walking speed is per class and does not follow SPEED (foot 40 px/s, mounted 72)

* **Status:** Accepted. Supersedes ADR 0171's speed model (SPEED × 10 px/s) and its two-SPEED-value check.
* **Date:** 2026-10-19
* **Deciders:** maintainer (Claude), product owner
* **Related:** ADR 0045 (repair), ADR 0112 (fidelity suite), ADR 0171, TDD §22.3, REQ-PATH-11, UNK-12;
  [m2-placeholders.md](../mechanics/m2-placeholders.md), [m3-placeholders.md](../mechanics/m3-placeholders.md),
  [fidelity-summary.md](../mechanics/fidelity-summary.md)

## Context

Two captures at the place ADR 0171 chose (MP2's bottom-left corner, rows 51–53), `capture-20261019-163231`
(`unit-speed`) and `capture-20261019-190321` (batch C, `unit-speed-more`), 30/30 trials run, every walk tracked over
340–470 px with r² 0.991–0.999 in both directions:

| Class | SPEED (table) | Walk sheet stride (IMAGES.INI `move_len`) | Measured px/s |
|---|---|---|---|
| pikeman | 2 | 50 | 39.9–40.0 |
| maceman, longbowman | 3 | 50 | 40.0 |
| peasant, footman, archer, priest, spy, mage | 4 | 50 | 39.9–40.0 |
| dwarf, ork, skeleton | 4 | 50 | 40.0 |
| troll | 2 | 56 | 47.0 |
| ghost | 4 | 80 | 66.6 |
| crusader | 5 | 85 | 71.6–71.9 |
| knight | 6 | 85 | 71.7–72.0 |
| cow | 1 | 32 | 25.4–25.8 |
| bear | 4 | 32 | 25.8 |

**The original does not scale walking speed by the SPEED column.** Pikemen (SPEED 2) walk exactly as fast as peasants
(SPEED 4), trolls (SPEED 2) faster than both. The speed follows the walk sheet's stride per animation cycle: every
class with stride 50 walks 40 px/s, both with 85 walk 72. It does not follow the cycle's duration (`anim_time`: priest
and mage 1,000 ms, footman 750 ms, the same speed; cow 1,500 ms and bear 850 ms, the same speed) or the number of
frames (knight 12, crusader 14). The ratio speed / stride is not constant (0.80 at strides 32 and 50, 0.83–0.85 at 56,
80 and 85), so the exact rule is not known.

Eight trials spawned nothing: the European kingdom of MP2 cannot spawn Arabian or Asian classes (horse archer, horse
samurai, ninja, monk, heavy cavalry, jihad warrior, janissary, armored chariot), and catapults, heavy catapults,
ballistas and the caravan did not appear either (gold unchanged, nothing placed); the reason is not known.

## Decision

1. **Measured classes carry their own speed** in the classic ruleset, `Mobile.pixelsPerSecond` on the class template
   (VERIFIED): 40 for the 12 foot classes measured, 72 knight and crusader, 47 troll, 67 ghost (66.6), 26 cow and bear
   (25.7). A class template applies to every civilisation's unit of that class; the captures measured the European unit
   (the common one for monsters and animals).
2. **The other classes keep SPEED × `speedPixelsPerSecondPerPoint` (10)**, marked UNVERIFIED and known not to be the
   original's rule. Not replaced by an estimate from the stride: the rule between stride and speed is not known, and a
   placeholder is replaced by a measurement (m2 register). The stride hypothesis says what to expect (85 → ~72 for horse
   archers, horse samurai, ninjas, janissaries, heavy cavalry and chariots; 40 → ~32 for catapults and ballistas).
3. **The suite's speed check** (`m2.speed-per-point`, id kept) compares every measured class with the engine's speed
   for that class (`EngineModel.PixelsPerSecondOf`: the European type's compiled speed, else the class template's
   `pixelsPerSecond`), 10 % tolerance, at least 3 classes. It passes: 18 classes. Suite **7 of 30 (23.3 %)**.
4. **The gameplay benchmark bot** keeps covering repair in 1,600 ticks: each kingdom's first house starts at three
   quarters of its hit points (above the burn threshold), and the bot repairs the least damaged building of its base
   (within 20 tiles of its castle, not on fire) with the three nearest busy peasants.
5. Both captures' facts are committed (`unit-speed.yaml`, `unit-speed-more.yaml`).

## Alternatives considered

* **A stride-based model (px/s = f(move_len))** for every class: it would explain all 18 classes, but f is not known
  (two near-linear pieces fit, neither is a rule), and the ruleset compiler would have to read the walk sheets of the
  imported art. Revisit when the Arabian and Asian units, siege weapons and ships are measured.
* **Keep SPEED × k and fit k:** refuted; no k fits pikemen, peasants and knights at once.
* **Weaken the golden test's repair assertion:** the hash stream is meant to cover repair; a damaged house at the start
  makes that independent of the battle.

## Consequences

* Pikemen, macemen and longbowmen walk twice or a third faster than before; knights and crusaders 72 px/s; cows and
  bears slower. Golden hashes re-pinned: gameplay ruleset, stream and final; AI commands, stream and final; the trigger
  mission; CI bench gameplay and AI (the sim bench is unchanged).
* The Hard AI's first sea landing on the tiny Archipelagos map comes between minutes 12 and 14 (was a little after 9);
  that test plays 14 minutes.
* Open (UNK-12): Arabian and Asian classes need a scenario whose own kingdom has that civilisation (MP4 has Asian and
  Arabian kingdoms, intro.wmp an Asian kingdom 1; spawning only works for the own civilisation); siege weapons and
  caravans need to find out why spawning fails; ships and flyers are not measured.
