# 0080. Gate the nightly on an S2 proxy with per-CPU baselines; keep the order-flood scenario as informational stress

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (sim performance)
* **Related:** TDD §4.3 (S2), §8.7 REQ-SIM-12, §8.9, §9.2 REQ-PATH-02/03, §21, §22.8, ADR 0017, ADR 0022, ADR 0025,
  ADR 0026, ADR 0039

## Context

The nightly `Benchmarks` job on main failed after M2 part C: `t2 bench --ticks 2000 --warmup 200 --churn 2
--workers 4` (the synthetic scenario: 4,000 units on 512², 8 players) measured tick p50 10.8 ms, p99 15.1 ms on the
4-vCPU `ubuntu-24.04` runner against the §21 budget of p99 ≤ 12 ms.

**Where the time went.** `t2 bench` now prints a per-section table (systems, their phases, the hashes; p50, p99, max,
mean and the mean over the slowest 1% of ticks). Before this change, on the CI runner: path requests 6.2 ms p50 of a
9.8 ms tick (almost all of it computing paths), movement 2.8 ms (of which the sequential pre-pass 1.7 ms), commands
0.7 ms, steer/resolve 0.4/0.3 ms, the per-tick hash 0.07 ms; economy, combat and production are ≈ 0 in this scenario
(it has no gameplay traits). The full-state hash costs 1.2–1.3 ms on the CI runner every 20th tick, i.e. on 5% of the
ticks, so it is part of almost every p99 tick (it hashes about 4.5 MB: blockers and the occupancy grid).

Path requests dominate because of the scenario, not the gameplay systems: every player orders 16 units to a new
random point **every tick** (128 units re-ordered per tick, 2,560 per second) and deletes/respawns 2 units per tick.
The flow-field cap (48 live groups) is always reached, so ~130 long individual paths are planned per tick, each
~900 node expansions (two 256-tile cluster searches plus ~425 abstract A* expansions). That is far beyond what 8 AI
players issue; TDD §4.3 S2 is "Real World 512² map, 8 AI players, 4,000 units".

**Runners differ.** Ten probe jobs landed on four CPU models (AMD EPYC 7763, 9V74, 9V45, Intel Xeon 8573C). The same
build's tick p50 differs by up to 1.7× between models, and by 3–10% between runs on one model (up to 30% on a 9V45).
A fixed absolute or relative threshold on raw times is therefore either flaky or blind to a 10% regression.

## Decision

**Optimizations (results unchanged; every golden hash and hash stream is identical).**
* *Route queries read the start and goal costs off the portal trees.* `PathSearch.FindRoute` no longer runs the two
  cluster Dijkstras for long queries: the cost between a tile and each portal of its region is the length of the
  portal's precomputed shortest-path tree path from that tile (steps are symmetric), and the expansions it charges
  (REQ-PATH-03 budget) are the tile's region size, which is exactly what the Dijkstra settles. `FindTilePath` and the
  same-region case still search (they trace the trees). Tested: route and tile queries report identical cost and
  expansions on random maps. 60 → 39 µs per long query; the abstract graph's edges carry their target's tile (one
  sequential read instead of two random ones in the A* inner loop).
* *Route following runs in parallel.* The movement pre-pass updates route-mode units as fork/join jobs over chunks of
  256 units (per-job goal-tree scratch, cached while the cluster and root repeat); the jobs touch only the unit's own
  motion, and their side effects (plan clears, re-plan requests) are applied afterwards in unit order, so the
  path-request queue order is that of the sequential pass. Units with a follow check due this tick stay sequential.
* `MinHeap` is a 4-ary heap over one entry array (neutral here; the pop order is fixed by the total order anyway).

**Profiling.** `ITickProfiler` (Sim, diagnostics only: nothing is read back) receives named, nested sections from the
scheduler (each system), from movement and path requests (their phases) and from `Simulation.Tick` (hash, full hash,
render snapshot). `t2 bench` always prints the table (`section ms …`); the CLI owns the clock.

**Scenarios.**
* `t2 bench --scenario s2` is the **gating S2 proxy**: the synthetic map and 4,000 units, 8 players, each player
  orders 24 units to a new random point and replaces 1 unit every 10 ticks (staggered; 2 group orders per second per
  player, 384 units re-ordered per second, so nearly all units are moving all the time). `SyntheticScenarioOptions.
  OrderIntervalTicks` (default 1, i.e. unchanged) provides the rate. Day/night, weather, visibility and AI think do
  not exist yet; when they land they belong in this scenario's budget.
* The old default (`--churn 2`, 16 orders per player every tick) stays as the **stress** scenario: informational, with
  its own budget of p99 ≤ 12 ms on the runner and the same 10% relative check, reported as warnings only.
* B3, B4 and gameplay stay gating as before (ADR 0026, ADR 0039).

**Gates (`tools/ci/check-bench.sh`).** Each gated scenario runs twice and the run with the lower p50 is checked (noise
only adds time):
1. absolute: tick p99 ≤ 12 ms on the runner (4 vCPUs, weaker than the §4.3 6-core reference, so this is
   conservative);
2. relative (§21 "regressions > 10% fail"): tick p50 ≤ 1.10 × the baseline for the runner's **CPU model** in
   `tools/ci/bench-baseline.tsv` (median over the recording runs, the slowest run for models seen fewer than three
   times). For a CPU model without a row, the p50 normalized by a calibration loop must stay within 25% of the slowest
   recorded normalized p50. B3 has no relative row (its p50 is idle ticks); its planning-within-3-ticks check gates it.

**Calibration.** `t2 bench` measures a frozen workload (8-connected Dijkstra searches with a binary heap over a 256²
grid; best of 7 runs) and prints `calibration` and `normalized` (= time × 25 ms / calibration). An 8 MB pointer chase
was tried first and rejected: it tracked memory latency (3× between runner models) rather than the sim (1.7×). The
Dijkstra loop leaves about 20% between models, which is why same-model baselines are the primary relative gate and
normalization only the fallback. The workload must never change (it re-bases every normalized number).

**Baselines** are re-recorded, never edited by hand, when a change is meant to cost time: every check prints its
`baseline` row (name, CPU model, p50, normalized p50).

**Adding or re-recording a baseline row** (a runner with a new CPU model, which the log reports as "no baseline for
this CPU", or an intended slowdown):
1. Open the nightly run's `Benchmarks` job (or its `t2-bench` artifact) and copy the
   `baseline<TAB>name<TAB>cpu<TAB>p50<TAB>normalized p50` lines that `check-bench.sh` printed for s2, gameplay, b4 and
   stress.
2. Prefer three or more runs on that CPU model (`gh workflow run nightly.yml --ref <branch>` repeatedly): take the
   median p50 per scenario; with fewer runs take the slowest.
3. Drop the leading `baseline` field and add or replace the rows in `tools/ci/bench-baseline.tsv` (tab-separated, one
   row per scenario and CPU model). For an intended slowdown, replace the rows of every CPU model, and say why in the
   commit message.

### Measurements

Same CI runner (AMD EPYC 7763, 4 vCPUs), stress scenario, before → after, two alternating rounds:

| workers | tick p50 ms | tick p99 ms | path compute p50 ms | movement pre-pass p50 ms |
|---|---|---|---|---|
| 4 | 11.1 / 9.5 → 6.4 / 6.5 | 14.4 / 11.6 → 8.6 / 8.7 | 6.8 / 5.6 → 3.5 / 3.6 | 1.9 / 1.8 → 0.94 / 0.93 |
| 1 | 19.7 / 19.7 → 14.3 / 14.2 | 23.3 / 23.2 → 17.7 / 17.8 | 14.9 / 14.8 → 9.6 / 9.5 | 1.9 / 2.0 → 1.9 / 2.0 |

Full hash: p99 1.3 ms on both sides (unchanged by design). Local Apple M5, 1 worker: p50 10.6 → 7.6 ms, path compute
8.4 → 5.6 ms. After, across the four CPU models (4 workers): s2 p50 2.0–3.6 ms, p99 4.3–5.7 ms; stress p50
3.8–6.8 ms, p99 6.9–9.1 ms; gameplay p99 3.8–5.7 ms.

## Alternatives considered

* **Keep the order flood as the gate:** it measures a load no match produces (each player re-ordering 16 units 20
  times a second) and would steer optimization toward path throughput under an impossible APM, while letting a
  movement regression at realistic load hide behind path noise. It stays as stress, so path throughput is still watched.
* **Make the gameplay scenario the S2 gate at 512² and 4,000 units:** closest in content, but at 512² the armies do
  not meet or train within minutes of game time with the current bot, and stream A is changing economy and
  construction; revisit when the AI (M3) plays real matches (B1).
* **Normalize everything by the calibration loop with one baseline:** 20% residual between CPU models is too much for a
  10% gate.
* **Drop the occupancy grid from the full hash** (about 4 MB of the 4.5 MB hashed every 20 ticks; it is derived from
  positions, which are hashed): would cut ~1 ms from 5% of ticks, but changes the full hash defined by REQ-SIM-12 /
  ADR 0016 and every golden value. Decided (coordinator, 2026-10-18): the full hash keeps the occupancy grid.
* **Cache per-unit cluster searches across requests:** slots of a group are different tiles, so there is little reuse;
  the tree read removes the searches instead.

## Consequences

* The nightly measures what §4.3 asks for and still catches 10% regressions on the CPU models it has seen.
* A new runner CPU model is caught only by the loose normalized check until its row is recorded; add rows from that
  run's `baseline` output.
* The remaining hot spots, in order: the abstract A* (~38 µs per long query: heap and ~3,500 edge relaxations), the
  full-state hash on every 20th tick, the sequential part of the pre-pass (flow-field units: shared cache), command
  application with churn (sorted component stores shift on insert/remove, ADR 0015).
* Revisit when AI think, visibility and day/night land (they join the s2 budget, §21 split), and when B1 replaces the
  S2 proxy.

## Questions for the product owner

* The S2 order rate (2 group orders of 24 units per second per AI player) is accepted for now (coordinator,
  2026-10-18); retune it from AI telemetry once the M3 AI plays matches.
