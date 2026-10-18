# 0113. Capture batches E–I and the editor session: measuring the rest of TDD §22.3

* **Status:** Accepted (owner decision: the plan; AI script semantics stays pending)
* **Date:** 2026-10-18
* **Deciders:** implementation agent (fidelity follow-up), product owner
* **Related:** ADR 0110 (harness), ADR 0112 (suite, M3 gate), [fidelity-plan-batches-e.md](../mechanics/fidelity-plan-batches-e.md),
  TDD §22.3, S4, RE-07, RE-10

## Context

The M3 gate is passing ÷ all 30 §22.3 checks (ADR 0112); 80 % = 24. Batches A–D can measure 9. Twenty-one checks had
no scenario. Session 1's lessons apply (ADR 0110). The meaning of `hmspawn` / `hmkingdom` is settled by batch A, so the
E–I files are sent only after batch A has come back and they have been adjusted.

## Decision

1. **Scenarios** (all `default: false`, run by `run-capture.ps1 -Batch E…I`; each batch starts with smoke's cheat probes):

   | Batch | Scenarios | Minutes | §22.3 checks |
   |---|---|---|---|
   | E | group-speed, research-feudalism, damage-bonuses, damage-building, gather-wood-far, capture-time-1/-3, build-card (layout discovery) | ~18 | group move speed, research time, technology effect, damage bonuses, gather time, capture time |
   | F | fire-repair-10s/-20s/-30s, healing, field-decay, experience | ~25 | fire, repair, healing, field decay, XP gain, level effects |
   | G | mana-spell, loans, score | ~13 | spell effects, mana regeneration, loans, score |
   | H | combat-outcomes (12 pairs × 8 duels, top game speed) | ~22 | combat outcomes |
   | I | trade, gamble (discovery runs) | ~11 | trade profit, gambling (after one follow-up) |

   Methods: no spawn cheats where possible (MP2's castle and peasants; command-card letters from `MENU.INI`, e.g. castle
   `P`/`D`, build `B`+`L`/`I`, tavern `L`+`G`, mage `L`); `hmbuildozer` for instant buildings, `hmgod` and `hmnotech`
   where needed; values from memory (scan, narrow, sample) and times from the screen. Buttons without a letter (the
   build card's category buttons) are assumed in a 2 × 4 grid; `build-card` captures the real layout in batch E before
   F, G and I rely on it.
2. **Analyses** (synthetic tests in `LaterBatchAnalysisTests`):
   - **`counter-rate`:** one memory counter followed through a trial's phases. It reports the steps and the rate per
     minute, e.g. attack, burn and repair of one mill; mana after a cast; a stockpile.
   - **`combat-outcomes`:** the winner per duel spot from the kingdom ramp colours left after the fights.
   - **`t2 fidelity maps <dir>`:** random-map statistics from `.wmp` files saved by the operator, read with the M4-A
     map reader: castle spacing, and gold, stone and trees per kingdom.
   - **Extended existing analyses:** `attack-timing` now records the bonus and RESISTANCE of a trial, `gather-rates`
     the tree and drop-off positions, `region-timing` research and capture times.
3. **Manual reading.** Experience, level effects and the score have no known memory address and no capturable number
   other than the panel or the end screen. The scenarios capture those screens. The values are read by hand into
   `tests/fidelity/facts/manual.yaml` (`experience`: `xp_after_kills`, `attack_per_level`, `defence_per_level`; `score`:
   `gathered`, `killed_value`, `built_value`, `technologies`, `total`), with provenance.
4. **Engine side** of the 19 new checks: engine rules-file values. These are `gameplay.*` and `world.*`, plus
   `^class.castle` Holder, `^class.field` Decay, `spell_lightning`, `te_feudalism`, read by path. The compiled
   synthetic ruleset (combat settings, the peasant's Harvester) is also used. Combat outcomes use the engine's
   deterministic duel model: flat damage, hits every `attackCycleMs` / RATE, the first after the hit delay. A tie
   within one tick is a draw. Mapgen statistics are compared with three maps of our own generator per type.
5. **AI script semantics stays pending** by owner decision (it counts as not passing). Measuring it would take 26 test
   scripts run by the original's AI. It is not moved out of §22.3.

## Consequences

* If every trial works, the gate after each batch:

  | After | Gate |
  |---|---|
  | A–D | 9/30 (30 %) |
  | E | 15 (50 %) |
  | F | 21 (70 %) |
  | G | 25 (83 %), passes the gate |
  | H | 26 (87 %) |
  | editor session | 27 (90 %) |

  Trade and gambling need one more iteration after I's discovery run (29 at most). AI semantics stays pending.
* Capture time for the operator: E–I about 89 minutes, plus the editor session (about 20 minutes). Together with A–D
  (75 minutes) that is about 3 hours.
* Every new scenario is experimental. A failed trial leaves its check pending, never passing. The batch-A findings
  (spawn placement, `hmkingdom`, the knight's damage against buildings, the build card) adjust E–I before they go out.
