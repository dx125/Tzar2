# 0026. Define B3/B4 as runnable scenarios with functional and tick-time gates; default the path budget to 750,000

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 part B)
* **Related:** TDD §21 (B3, B4, sim budget), §25 M2 exit criteria, REQ-PATH-03/12, ORIG-01, ADR 0017, ADR 0022

## Context

§21 names B3 "500 units mass move on 512² maze" and B4 "mass death: 2,000 kills in 60 s then traffic through the area"
as nightly CI gates; the M2 exit criterion is "B3/B4 benchmarks within budget". The budgets that apply: path planning
"500 move orders planned within 3 ticks" (desktop) and the sim tick p99 ≤ 12 ms at 4,000 units on 512².

## Decision

* **B3** = `MazeMoveScenario`: a perfect maze on 512² (3-tile corridors, 1-tile cliff walls, recursive backtracker
  from a fixed seed), 500 swordsmen on random corridor tiles, move orders to random corridor tiles at tick 0 —
  `b3`: 500 individual orders; `b3-groups`: 10 orders of 50. Gate: every unit has its first plan after tick ≤ 2, and
  the tick p99 ≤ 12 ms.
* **B4** = `MassDeathScenario`: 256² map, an 8-tile river with one 4-tile bridge; 2,000 units packed into the 40×40 tiles
  around the bridge die over 1,200 ticks (60 s, `DebugKill`, corpses on), then 400 units patrol across the bridge from
  both banks; 1,600 idle bystanders make 4,000 units. Gate: no `Blocked` unit, obstacle version unchanged (corpses never
  block), tick p99 ≤ 12 ms over 3,600 ticks.
* Both run in `t2 bench --scenario b3|b3-groups|b4` (functional gate failures exit with code 1) and as BenchmarkDotNet
  suites (`MazeMoveBenchmarks`: the 3-tick planning window; `MassDeathBenchmarks`: one traffic tick; plus
  `PathfindingBenchmarks`). The nightly `benchmarks` job runs all three t2 scenarios and gates p99.
* **Path budget** default 750,000 node expansions per tick (B3 needs ≈ 2 M for its 500 orders).

Measured (Apple M5, 10 cores, macOS arm64, Release, 4 sim workers, warm JIT; a shared machine, so ±30% noise):

| Scenario | Result | Budget |
|---|---|---|
| B3 individual | planned after tick 2; ticks 0-2: 8.3 / 6.4 / 3.7 ms; 600 ticks p99 0.96 ms, max 7.6 ms. BDN 3-tick window 17.9 ms (4 workers), 57 ms (1 worker) | ≤ 3 ticks, p99 ≤ 12 ms |
| B3 groups | planned after tick 0 (5.6 ms); p99 1.0 ms. BDN window 7.8 ms (4 workers) | same |
| B4 | 2,000 killed, peak 2,000 corpses, obstacle version 0, 0 blocked, 424 crossings; p50 0.25 ms, p99 0.59 ms, max 11.0 ms. BDN traffic tick 0.63 ms | p99 ≤ 12 ms, no stuck units |
| Synthetic 4,000 units / 512², 128 group orders + churn per tick (existing gate) | p50 3.7-4.1 ms, p99 6.4-9 ms (runs vary on the shared machine) | p99 ≤ 12 ms |
| Micro | route query on the 512² maze 103 µs; flow-field build 146 µs; navigation build 20.9 ms | — |

Allocations: the sim allocates 0 B per tick in steady state on the allocation gate (`AllocationTests`, synthetic
64² scenario) and in B4's traffic phase. `t2 bench`'s `alloc` column includes the scenario's command generation and
buffer growth during warm-up (B3 ≈ 19 KB/tick averaged over 600 ticks from tick 0, synthetic ≈ 1.3-1.8 KB/tick).

After the review fixes (route re-validation, enemy blocking): B3 planned after tick 2, p99 0.87 ms, max 11.4 ms;
B3 groups p99 0.82 ms; B4 p99 0.49 ms, 0 blocked; synthetic p99 6.5 ms.

## Alternatives considered

* **B3 as random orders on the synthetic map:** not a maze; long winding routes are what ORIG-03 is about.
* **Measuring B3 by p99 only:** the three planning ticks are < 1% of a run, so p99 hides them; the functional gate
  checks the planning latency, and the trace (`--trace`) shows the planning ticks.
* **A smaller budget (400,000):** B3 then needs 5 ticks; a larger one (1 M+) moves more work into the first tick.

## Consequences

* CI runners are slower than the desktop reference; the gates may need runner-specific baselines (TDD §21: relative
  thresholds after a stable baseline exists).
* The budget is a match setting stored in snapshots; changing the default changes the golden hashes.
