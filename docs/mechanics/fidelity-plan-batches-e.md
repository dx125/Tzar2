# Plan: capture batches E–I and the editor session (towards the M3 gate)

Status: **plan** (implementation follows once PR #37 has merged; ADR 0113 will record it). The M3 gate is passing / all
30 TDD §22.3 checks (ADR 0112); 80 % = 24 checks. Batches A–D can settle at most 9. This plan covers the other 21.

## Ground rules (lessons of session 1, ADR 0110)

* Prefer **no cheats**: MP2's starting castle and peasants, command-card **letters** from `MENU.INI` (castle: `P`
  peasant, `D` Feudalism, `A` Architecture, `L` Learning; tavern: `L` loan, `G` gamble; mage: `L` lightning, …; build
  sub-menu `B`, common buildings via the card's "common" button then `H` house, `N` tavern, `F` farm). Where a
  building is needed, `hmbuildozer` (instant construction) plus the letters.
* **HUD geometry from `DATA/LAYOUT.INI [LAYOUT1024L]`** (resource boxes, population box, clock), not from pixels of one
  capture; card buttons are a 2 × 4 grid at x 45/105, y 355/415/475/530 (session-1 frames).
* **Values from memory** follow the attack-timing pattern: scan for a known value, narrow to stable, narrow to the
  expected change, sample. **Timings from the screen** use `region-timing`.
* Units are spawned with a click to place, owners set with both readings of `hmkingdom`, selection by box.
* **Game speed:** where only outcomes matter (combat outcomes, XP), the game runs at its top speed (`+` pressed until
  the maximum), which shortens trials without changing results; timings are always measured at the default speed.
* Every batch starts with smoke's two cheat probes, so a batch that fails tells us why.

## Ranking (value per minute of capture)

| Rank | Check (§22.3) | Method | Capture min | Checks | Batch |
|---|---|---|---|---|---|
| 1 | m2.group-move-speed | unit-speed trial: knight + pikeman selected together, track both | 1 | 1 | E |
| 2 | m2.research-time | Ctrl+C, `D` (Feudalism, 30 s): selection-panel `region-timing` | 1.5 | 1 | E |
| 3 | m3.technology-effects | after Feudalism: train a peasant, food counter step (50 → 40 if −20 %); Architecture: build-time ratio | 2.5 | 1 | E |
| 4 | m2.damage-bonuses | attack-timing pairs with a BONUS.INI match (pikeman vs knight) and against a building (barracks HP counter) | 4 | 1 | E |
| 5 | m2.gather-time | gather trips from two trees at different distances: intercept of trip time vs distance = gathering time | 3.5 | 1 | E |
| 6 | m3.capture-time | an enemy house (spawned for kingdom 2) captured by 1 and 4 peasants (`C`); owner change by `region-timing` on the house's colour | 4 | 1 | E |
| 7 | m3.fire + m2.repair-rate | one building, damaged by spawned attackers in steps (HP counter): fire appears (region change) → threshold; attackers removed → burn rate; peasants `R` → repair rate with 1 and 3 repairers | 8 | 2 | F |
| 8 | m2.healing | a wounded unit (attacked, then saved) entering the castle; HP counter while held | 4 | 1 | F |
| 9 | m3.field-decay | a field (`B`, common, `F` / `hmbuildozer`), left alone 4 min; field HP counter | 5 | 1 | F |
| 10 | m2.xp-gain | a knight (`hmgod`) kills 5 spawned peasants one by one; repeated "increased" narrowing finds the XP counter | 5 | 1 | F |
| 11 | m3.mana-regeneration + m3.spell-effects | a mage: mana counter after a lightning (`L`) on a spawned target (target HP counter) and while it refills | 8 | 2 | G |
| 12 | m2.level-effects | continue the XP trial to the next level, then attack-timing of the levelled knight | 8 | 1 | G |
| 13 | m3.loan-growth | tavern (`hmbuildozer`), `L`, `G`: gold counter step, then the repayment and the next limit | 5 | 1 | G |
| 14 | m3.score | scripted short game then `hmnext`; the end screen's numbers read with digit templates learned from the HUD font | 4 | 1 | G |
| 15 | m2.combat-outcomes | 16 pairs per game in a 4 × 4 grid at top speed; winner per spot from ramp colours, remaining HP from the health bars (`` ` ``). First a determinism probe (3 pairs × 16): if every trial of a pair ends alike, 16 trials stand for 100 (S4); else 7 games per pair | 25 (deterministic) | 1 | H |
| 16 | m3.trade-profit | two markets (`hmbuildozer`), a caravan (`C`), trade at two distances; gold counter | 12 | 1 | I |
| 17 | m3.gambling | tavern `G`, 40 bets at fixed odds; gold counter | 8 | 1 | I |

Not by the capture harness:

* **mapgen.statistics (RE-10)** — **editor session** (Sasha, Windows, already planned): generate 2 random maps per type
  (6 types) at two sizes in the original's random-map dialog or the editor's generator and save them as `.wmp`; the
  M4-A map reader (PR #33) parses them locally and a `t2 fidelity maps <dir>` analysis writes land share, resources,
  castle spacing (numbers only). About 15 minutes of manual work. +1 check.
* **ai.script-semantics (UNK-35, RE-07)** — **out of reach for the capture batches**: it is 26 separate behaviours
  (ai-placeholders.md S1–S26), each needing its own test script run by the original's AI and observed through `say`
  messages. A test script is our own text (no original data), so it is legitimate, but it needs the AI script slot of
  a map set in the editor and a way to read the chat log. Proposal: keep the check **pending** (it counts as not
  passing) and plan a dedicated editor-assisted session after M3; or, by an ADR, re-scope it out of §22.3 into RE-07's
  own test plan. Not dropped silently: this needs the owner's decision.

## Expected gate (if every trial succeeds; batch A must first confirm the spawn and kingdom cheats)

| After | Checks settled | Gate (of 30) | Capture time |
|---|---|---|---|
| A–D (ADR 0112) | 9 | 30 % | 75 min |
| E (group speed, research, tech effect, bonuses, gather time, capture) | 15 | 50 % | ~19 min (incl. 2 min smoke probes) |
| F (fire, repair, healing, field decay, XP) | 20 | 67 % | ~24 min |
| G (mana, spells, level effects, loans, score) | 25 | 83 % | ~27 min |
| H (combat outcomes) | 26 | 87 % | ~27 min |
| I (trade, gambling) | 28 | 93 % | ~22 min |
| editor session (random maps) | 29 | 97 % | ~15 min manual |

The gate passes 80 % after batch G if E–G all succeed. With realistic losses (one or two failed trials per batch,
re-run in the next batch), H and I are the safety margin. Only ai.script-semantics stays pending.

**Total for Sasha:** A–D 75 min, E–I about 119 min, editor 15 min: **about 3 h 30 min**, in nine capture runs of
at most 30 minutes plus one manual session. The batches can run on different days; E–I need the answers of batch A
(spawn placement, `hmkingdom`) before their final scenario files are written.
