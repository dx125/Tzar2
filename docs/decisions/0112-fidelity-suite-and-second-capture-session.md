# 0112. The fidelity suite: a catalogue of measurable facts, `Tzar2.Fidelity.Tests`, `t2 fidelity report`, and the second capture session in four batches

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (fidelity follow-up)
* **Related:** TDD §22.3 (fidelity tests), §25 M3 exit criterion "fidelity suite ≥ 80 % passing"; ADRs 0030, 0110, 0111;
  [fidelity-summary.md](../mechanics/fidelity-summary.md), [tools/capture/README.md](../../tools/capture/README.md)

## Context

M3's exit criterion asks for a fidelity suite that passes at 80 % or more, but no such suite existed. There were only
the committed fact files (`tests/fidelity/facts/*.yaml`) and one test that checked three of them. The criterion
needs a fixed denominator. The placeholder registers (201 rows) cannot serve: most rows are not measurable by a
black-box capture of the running game.

## Decision

1. **The suite is a catalogue of checks** (`Tzar2.Tools.Cli/Fidelity/Suite/FidelitySuite.cs`). Each check pairs a fact
   that a capture scenario measures with the engine value it must match, and has a tolerance. The catalogue is the
   denominator. A check belongs to one area: rendering, m2-gameplay, m3-gameplay, mapgen or ai.
   - **Pass:** a conclusive fact agrees with the engine.
   - **Fail:** a conclusive fact disagrees.
   - **Pending:** no conclusive fact yet.

   Pending checks are not in the denominator:
   - **pass rate** = passing / measured. This is the M3 figure.
   - **coverage** = measured / catalogue. It is reported next to the pass rate so that a high pass rate over few
     facts does not pass for a finished suite.

2. **The fourteen checks:**

   | Check | Unknown | Tolerance |
   |---|---|---|
   | shadow factor = client mask strength | UNK-03 | 1/32 |
   | overlapping shadows darken once | UNK-03 | double-darkened pairs ≤ 10 % (10–30 % stays pending) |
   | kingdom colour squares use ramp n | UNK-01a | exact |
   | kingdom sprites use ramp n (≥ 7 kingdoms) | UNK-01a | exact |
   | RGB555 surface, replication | UNK-39 | exact |
   | speed = SPEED × `speedPixelsPerSecondPerPoint` (≥ 3 units, r² ≥ 0.95) | UNK-12 | 10 % |
   | time between hits = `attackCycleMs` / RATE | UNK-07 | 10 % or 2 sample intervals |
   | damage = max(minimum, ATTACK − DEFENCE) | UNK-08 | exact (most frequent step) |
   | load per trip = peasant `Harvester.capacity` | UNK-17 | exact |
   | default population limit | UNK-20 | exact |
   | production time = TIME s | UNK-20 / data | 10 % |
   | construction with one builder = TIME s | UNK-18 | 20 % (a ~2 s walk subtracted) |
   | day length = `world.dayLengthMs` | UNK-32 | 10 % |
   | night share = night hours / 24 | UNK-32 | 0.05 |

   - **Engine side.** Values come from the engine's own rules file (conversion factors), the classic ruleset
     compiled from the synthetic tables (only values defined in YAML, such as capacity, minimum damage, population
     and day length), the client's shadow shader, and `Rgb555.Expand5`.
   - **Stat-dependent checks.** These use the reference build's stats, which the scenario records with each trial.
   - **Mapgen and ai.** Both areas have no checks yet. Random maps need the original's random-map dialog and the
     `.wmp` object layout (RE-03, UNK-10). AI experiments need a working `hmkingdom` and script access. Their
     placeholders are outside the denominator until a scenario can measure them.

3. **`Tzar2.Fidelity.Tests`.** One theory row per measured check fails when that check fails, plus a report test.
   The project is gated the way the package-gated skirmish tests are: it skips without facts
   (`TZAR2_FIDELITY_FACTS`, else `tests/fidelity/facts`). The facts are committed, so CI runs it.
   - **`manual.yaml`.** Values read by hand from capture images go into `tests/fidelity/facts/manual.yaml`, with
     provenance. For now this is only the default population limit.
4. **`t2 fidelity report [--facts <dir>]`** prints checks, measured, pass, fail, pass % and coverage per area and in
   total, then one line per check. It exits non-zero when a check fails.
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

7. **Not attempted, with the reason:**
   - **XP gain:** no on-screen number, and the memory value is unknown.
   - **Trade profit vs distance:** needs two markets and a caravan route built by hand, more than fragile UI
     automation can do in a capture.
   - **Technology magnitudes:** needs the research sub-menus' coordinates and a stat readout.
   - **Random maps per type:** the random-map dialog's coordinates are unknown, and the map binary's objects are not
     decoded. A useful manual step for the operator is to save a few editor-generated random maps per type for RE-03.

## Consequences

* Today the report shows 4 of 14 checks measured, 4 passing (100 %), 29 % coverage. After batches A–D every check can
  be measured. Mapgen and ai stay outside until their scenarios exist.
* A future fact that disagrees with the engine makes `Tzar2.Fidelity.Tests` red. The fix is to move the engine (and
  the YAML placeholder) to the measurement, or to explain the deviation in an ADR.
* The gather, train and build scenarios depend on the MP2 castle view of session 1. If the camera does not land there,
  the analyses report "no deliveries" or "region never changed", and the checks stay pending; nothing passes wrongly.
