# 0112. The fidelity suite: the §22.3 catalogue and the M3 gate, `Tzar2.Fidelity.Tests`, `t2 fidelity report`, and the second capture session in four batches

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (fidelity follow-up)
* **Related:** TDD §22.3 (fidelity tests), §25 M3 exit criterion "fidelity suite ≥ 80 % passing"; ADRs 0030, 0110, 0111;
  [fidelity-summary.md](../mechanics/fidelity-summary.md), [tools/capture/README.md](../../tools/capture/README.md)

## Context

M3's exit criterion asks for a fidelity suite that passes at 80 % or more, but no such suite existed. There were only
the committed fact files (`tests/fidelity/facts/*.yaml`) and one test that checked three of them. The criterion
needs a fixed denominator, and it must be §22.3's whole scope: a denominator of only what is measurable today would
let 80 % of a few facts pass for the suite (review of PR #37).

## Decision

1. **The suite is the TDD §22.3 scope, item by item** (`Tzar2.Tools.Cli/Fidelity/Suite/FidelitySuite.cs`). Each
   check pairs one fact of the original with the engine value it must match, within a tolerance. Every item of §22.3's
   list (unit speeds, attack cooldowns, per-pair combat outcomes, gather/carry rates, build/research times, XP gain and
   level effects, capture times, trade profit, loan growth, spell effects) and every capturable gameplay unknown of the
   placeholder registers is in the catalogue, whether a scenario can measure it today or not. A check is:
   - **Pass:** a conclusive fact agrees with the engine.
   - **Fail:** a conclusive fact disagrees.
   - **Pending:** no conclusive fact yet. A check without a scenario says what would measure it.

   **The M3 gate is passing / all §22.3 checks; pending counts as not passing.** Passing / measured is reported only as
   a secondary figure (how good the engine is where we have measurements), never as the gate.

   **The rendering checks are §22.4 / S5** (visual fidelity, golden images): shadow factor and overlap, player-colour
   squares and sprites, the RGB555 surface. They are evaluated and reported in their own section and are not part of
   the M3 percentage.

2. **The §22.3 catalogue: 30 checks.**

   Measurable with the scenarios of this ADR (9):

   | Check | Unknown | Tolerance |
   |---|---|---|
   | speed = SPEED × `speedPixelsPerSecondPerPoint` (≥ 3 units, r² ≥ 0.95) | UNK-12 | 10 % |
   | time between hits = `attackCycleMs` / RATE | UNK-07 | 10 % or 2 sample intervals |
   | damage = max(minimum, ATTACK − DEFENCE) | UNK-08 | exact (most frequent step) |
   | load per trip = peasant `Harvester.capacity` | UNK-17 | exact |
   | default population limit | UNK-20 | exact |
   | production time = TIME s | UNK-20 / data | 10 % |
   | construction with one builder = TIME s, the builder's walk removed with the peasant's *measured* speed (pending without it) | UNK-18 | 10 % or 1 s |
   | day length = `world.dayLengthMs` | UNK-32 | 10 % |
   | night share = night hours / 24 | UNK-32 | 0.05 |

   Pending until a scenario exists (21), each with what would measure it in the report: per-pair combat outcomes over
   100 trials (S4), damage with bonuses and building resistance, group move speed, gathering time per unit, research
   time, repair rate, XP gain, level effects, healing, capture time, trade profit, loan growth, gambling, spell effects,
   mana regeneration, technology effects, field decay, fire, score, random-map statistics (mapgen), legacy AI script
   semantics (ai).

   **The §22.4 / S5 section: 5 checks.**

   | Check | Unknown | Tolerance |
   |---|---|---|
   | shadow factor = client mask strength | UNK-03 | 1/32 |
   | overlapping shadows darken once | UNK-03 | double-darkened pairs ≤ 10 % (10–30 % stays pending) |
   | kingdom colour squares use ramp n | UNK-01a | exact |
   | kingdom sprites use ramp n (≥ 7 kingdoms) | UNK-01a | exact |
   | RGB555 surface, replication | UNK-39 | exact |

   - **Engine side.** Values come from the engine's own rules file (conversion factors), the classic ruleset
     compiled from the synthetic tables (only values defined in YAML, such as capacity, minimum damage, population
     and day length), the client's shadow shader, and `Rgb555.Expand5`.
   - **Stat-dependent checks.** These use the reference build's stats, which the scenario records with each trial.

3. **`Tzar2.Fidelity.Tests`.** One theory row per measured check fails when that check fails, plus a report test.
   The project is gated the way the package-gated skirmish tests are: it skips without facts
   (`TZAR2_FIDELITY_FACTS`, else `tests/fidelity/facts`). The facts are committed, so CI runs it.
   - **`manual.yaml`.** Values read by hand from capture images go into `tests/fidelity/facts/manual.yaml`, with
     provenance. For now this is only the default population limit.
4. **`t2 fidelity report [--facts <dir>]`** prints the §22.3 table per area, then the **M3 GATE** line (passing / all
   §22.3 checks) and, labelled as secondary, passing / measured; then the §22.4 table; then one line per check. It says
   which figure is the M3 gate and exits non-zero when a check fails.
5. **Second session: new and changed scenarios.** All of them use the session-1 lessons: setup screen and PLAY,
   cheats to reveal the map, a click after every spawn, and owner changes that work under either reading of `hmkingdom`.
   Where possible they use no cheats at all (the starting peasants and castle of MP2, command-card letters from
   `MENU.INI`).

   | Scenario | Measures | Analysis |
   |---|---|---|
   | `attack-timing` | 6 pairs (three with the same stats in every civilization) | `attack-timing` (+ ATTACK/DEFENCE) |
   | `unit-speed`, `unit-speed-more` | 30 land classes, including chariot and caravan | `unit-speed` |
   | `shadow-overlap` | forests and towns on MP2, MP4 | `shadow-blend` |
   | `train-time` | three queued peasants, HUD population box | `region-timing` (new) |
   | `build-time` | a lumber mill by one peasant, selection panel | `region-timing` |
   | `gather-wood`, `gather-rock-a`, `gather-rock-b`, `gather-cow` | load and trip per resource; memory counter + HUD box | `gather-rates` (new) |
   | `day-clock` | 14 minutes, day/night on | `day-clock` (new) |

   `colour-depth`, `terrain-tiles` and `shadow-blend` are no longer run by default, because session 1 measured them.

6. **Batches of ≤ 30 minutes** (`run-capture.ps1 -Batch A|B|C|D`, checked by a test):

   | Batch | Scenarios | Time |
   |---|---|---|
   | A | smoke, player-colours, attack-timing, shadow-overlap, train-time, build-time | ~21 min |
   | B | unit-speed, gather-wood, gather-rock-a, gather-rock-b, gather-cow | ~23 min |
   | C | unit-speed-more | ~16 min |
   | D | day-clock | ~15 min |

7. **Not attempted in this session** (all in the catalogue as pending checks with their reasons): XP gain (no on-screen
   number), trade, loans and gambling (markets, a route and dialog coordinates), research and technology magnitudes
   (research sub-menu coordinates), random maps (the random-map dialog; the maps' objects can now be read by the
   M4-A map reader, so editor-generated random maps saved by the operator would be a useful manual step).

## Consequences

* Today: **M3 gate 1 of 30 = 3.3 %** (the population limit); secondary 1 of 1 measured. §22.4: 3 of 5 pass. After
  batches A–D at most 9 of the 30 §22.3 checks can be measured, so the gate can reach at most 30 % this way: M3's 80 %
  needs the scenarios behind the 21 pending checks, above all the 100-trial combat outcomes.
* A future fact that disagrees with the engine makes `Tzar2.Fidelity.Tests` red. The fix is to move the engine (and
  the YAML placeholder) to the measurement, or to explain the deviation in an ADR.
* The gather, train and build scenarios depend on the MP2 castle view of session 1. If the camera does not land there,
  the analyses report "no deliveries" or "region never changed", and the checks stay pending; nothing passes wrongly.
