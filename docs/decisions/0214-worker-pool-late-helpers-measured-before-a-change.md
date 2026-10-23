# 0214. Measure the worker pool's wait for late helpers before changing the pool

* **Status:** Accepted
* **Date:** 2026-10-23
* **Deciders:** agent (maintainer), coordinator's plan for the code review of work queue item 16 (item c)
* **Related:** TDD §7.3; ADR 0017 (deterministic fork/join); ADR 0080 (tick budgets);
  [code review 2026-10](../reviews/2026-10-code-review.md) (performance 8)

## Context

`DeterministicWorkerPool.Run` releases `min(helpers, jobs − 1)` helper threads, drains jobs on the calling thread, and
then waits until **every released helper** has woken, found no job left and checked out. A helper that the OS has not
scheduled yet holds up the tick even when the other threads finished every job long ago. On an idle machine the wake-up
costs microseconds. On a shared CPU it can cost a whole time slice.

* The review machine (Apple M5, 10 cores, a load average of 30–97 from other agents) showed sim ticks of exactly 10 and
  20 ms in movement and visibility, the sections that fork.
* The new stress test reproduces this. With every core busy, 6–7 % of 1,500 forks wait 10 ms after their last job:

  | workers | tail after the last job (p50 / p99 / max) | whole fork (p50 / p99 / max) |
  |---|---|---|
  | 4 | 0.000 / 10.0 / 16.3 ms | 0.28 / 10.8 / 17.1 ms |
  | 8 | 0.001 / 10.0 / 10.0 ms | 0.57 / 11.4 / 35.6 ms |

The nightly benchmarks on CI runners show no such quanta (gameplay p99 ≈ 9 ms with real work, ADR 0080), but nothing
measured the tail there. The fix is not free:

* A late helper must not take a job of the *next* fork.
* Its semaphore permit must not pile up past `MaxWorkers`.
* A deadlock would freeze every match.

## Decision

* The pool stays as it is for now.
* `WorkerPoolStressTests` (Sim.Tests, 4 and 8 workers) measures the tail between a fork's last job and `Run`'s return
  under full CPU contention, and writes its percentiles to the test output on every CI run and OS. It fails only if p99
  of the tail reaches the 50 ms tick.
* The pool changes only if CI runners show real stalls, meaning a p99 tail of a time slice or more. The planned fix:
  * `Run` waits for the **jobs** (a completed-job count), not for the helpers;
  * a generation number on each fork lets a helper that wakes late see that its fork is over and go back to sleep
    without taking work;
  * permits are reclaimed so the semaphore never overflows.

## Alternatives considered

* **Change it now:** the only evidence is a machine shared by many agents. A player's PC runs Godot, the sim and audio on
  more cores than the sim uses, and the tail is within the 50 ms tick even there.
* **Spin before sleeping:** helpers would burn a core between ticks. That hurts battery and the render thread.

## Consequences

* CI keeps a measurement of the tail on Windows, Linux and macOS runners. The decision to change the pool rests on it.
* On a heavily loaded machine a tick can still take 10–20 ms longer than its work, within the 50 ms budget.
