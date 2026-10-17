# 0017. Fork/join with data-defined jobs and fixed-order merges; golden hashes in CI on three OSes

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 part A)
* **Related:** TDD §7.3, §8.5, §8.8, §21, §22.2, DEC-14, REQ-SIM-05, ADR 0007, ADR 0014, ADR 0015, ADR 0016

## Context

TDD §7.3 wants a deterministic worker pool for fork/join jobs inside a tick, with results merged in a fixed order
so the worker count never affects results. §8.8/§22.2 want a CI determinism matrix that compares hash streams across
platforms. ADR 0007 left an open item: run the S2 harness on `windows-latest` and `ubuntu-latest`.

## Decision

**Worker pool (`Tzar2.Sim.Threading.DeterministicWorkerPool`).**
* The caller chooses the number of jobs and their boundaries **from the data only** (e.g. `MovementSystem`: chunks
  of 512 movers). Workers merely pick job indices from an atomic counter; the calling thread participates; with one
  worker everything runs inline.
* A job writes only state owned by its index (its own entities' components, its own output buffers). After the join
  the system merges outputs **in ascending job index** on the sim thread (occupancy updates, events).
* If jobs throw, all jobs still run and the exception of the lowest failing job index is rethrown: the outcome does
  not depend on scheduling either. `Run` allocates nothing; threads are background threads owned by the
  `Simulation`.
* Systems run strictly in pipeline order (`SystemScheduler`: by `SimPhase`, then registration order), sealed at the
  first tick.

**Determinism CI.**
* `Tzar2.Determinism.Tests.SimGoldenHashTests`: the synthetic scenario (128² generated map, 2,000 units, 4 players,
  bot move orders and delete/respawn churn through the command codec and slot stamping, 300 ticks) must produce the
  committed golden hash stream `297C6D90FC748B9E` and final full-state hash `38232E7ED6D36D86` (since ADR 0019; first
  `06D4D032CD216BBC` / `25418CEBC3F46249`), with 1 and 4
  workers, on net8 and net10. Measured identical locally on macOS arm64 and x64 (Rosetta), Debug (overflow policy
  Throw) and Release (Saturate).
* **PR CI (`ci.yml`, job `determinism`)** runs that suite on `windows-latest`, `ubuntu-latest` and `macos-latest`,
  plus `t2 bench … --expect-hash 38232E7ED6D36D86 --hash-stream …`; `determinism-compare` diffs the per-tick hash
  streams of the three OSes byte for byte (`\n` line ends on every OS).
* **Nightly (`nightly.yml`)**: `determinism` runs the suite in Debug and Release and a long match (4,000 units on
  512², 6,000 ticks with churn) on all three OSes, `determinism-compare` diffs those streams; `spike-s2-determinism`
  runs the S2 harness (net8/net10 × Debug/Release) on Windows x64 and Linux x64 and requires the reference final hash
  `AAE3D9913A101FAF` (what the harness gives at its own commit `8a3da0c` and on main; ADR 0007's table value was
  most likely measured before the spike picked up the StateHasher length-prefix amendment); `benchmarks` gates the absolute sim budget (p99 ≤ 12 ms at 4,000 units, §21) via `t2 bench` and runs
  `SimTickBenchmarks` (BenchmarkDotNet, 1/4 workers, with/without churn).
* Worker-count independence is also a unit test (`Tzar2.Sim.Tests.DeterminismTests`: 1, 2, 3, 4, 8 workers).

## Alternatives considered

* **`Parallel.For` with thread-local results:** partitioning depends on the scheduler and core count, and REQ-SIM-05
  bans unordered merges; a custom pool also keeps the per-tick path allocation-free.
* **Per-worker partitions (`count / workers`):** results would still be mergeable in order, but any per-job state
  (e.g. budgets, RNG draws) would then depend on the worker count. Data-defined jobs rule that out.
* **Comparing only final hashes across OSes:** a mismatch would not say when it started; per-tick streams pinpoint the
  first divergent tick.

## Consequences

* Systems that need randomness inside parallel jobs must derive it from (tick, entity) or draw sequentially before
  forking; jobs must never touch the shared `RngStreams`.
* The golden values change with any intended change of the scenario, systems, codec or hashed layout (update in the
  same commit with the reason, as for the Core golden hash).
* The replay corpus of TDD §22.2 replaces the synthetic match in the nightly matrix once gameplay produces replays.
* Revisit the S2 job when M2's Scripting integration lands (its Lua probes then move into the main suite, ADR 0007).
