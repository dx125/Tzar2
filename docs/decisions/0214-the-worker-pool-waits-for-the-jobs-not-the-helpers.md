# 0214. The worker pool waits for the jobs, not for the helpers it woke

* **Status:** Accepted
* **Date:** 2026-10-23
* **Deciders:** agent (maintainer), coordinator's plan for the code review of work queue item 16 (item c: measure first,
  change only if CI runners show real stalls)
* **Related:** TDD §7.3; ADR 0017 (deterministic fork/join); ADR 0080 (tick budgets);
  [code review 2026-10](../reviews/2026-10-code-review.md) (performance 8)

## Context

`DeterministicWorkerPool.Run` released `min(helpers, jobs − 1)` helper threads through a semaphore, drained jobs on the
calling thread, then waited until **every released helper** had woken, found no job left and checked out. A helper the
OS had not scheduled yet held up the tick even when the other threads had finished every job. On an idle machine the
wake-up costs microseconds. On a shared CPU it can cost a whole time slice or more.

The plan was to measure first. `WorkerPoolStressTests` (4 and 8 workers, the CPU kept busy) measures the **tail**: the
time between a fork's last job and `Run`'s return.

| where | tail p50 / p99 / max |
|---|---|
| review Mac (10 cores, loaded) | 0.000 / 10.0 / 16.3 ms |
| CI macOS runner (3 cores), 4 workers | 10.1 / 135 / 19,355 ms (1,186 of 1,500 forks ≥ 5 ms) |
| CI macOS runner (3 cores), 8 workers | 10.1 / 135 / 17,498 ms |

The CI runner shows real stalls: the typical fork waits a full time slice for helpers that have nothing left to do.

## Decision

* `Run` waits for the **jobs**:
  * the thread that finishes the last job sets the event;
  * the caller takes jobs too, so when it finishes the last one it returns without waiting at all.
* Each fork has a **generation**. Jobs are claimed with a compare-and-swap on one 64-bit word that holds the generation
  (high half) and the next job index (low half). A helper that wakes late reads the generation it woke for. It can never
  take a job of a later fork, and it goes back to sleep without touching anything.
* Helpers sleep on a monitor keyed by the generation (`Monitor.Wait` / `PulseAll`), not on a semaphore, so wake-ups
  cannot pile up.
* Unchanged:
  * job boundaries come from the caller;
  * outputs merge in job order;
  * the lowest failing job's exception is rethrown;
  * `Run` allocates nothing and is not re-entrant.

  Results stay identical for any worker count; the golden hashes do not move.
* `WorkerPoolStressTests` stays:
  * it runs alone, so its busy threads do not starve parallel test classes;
  * it keeps half the cores busy, not all of them, because the other test processes on the CI runner must not starve
    (#179);
  * it caps itself at 500 forks or 10 s;
  * it fails if the p99 tail reaches the 50 ms tick.

## Alternatives considered

* **Keep the semaphore and count finished helpers:** a late helper's permit then wakes it during the next fork. It
  could take that fork's jobs with a stale job reference unless the claims carry the generation, which is this design.
* **Spin before sleeping:** helpers would burn a core between ticks. That costs battery and the render thread's time.

## Consequences

* On the review Mac the median tail stays at 0.007 ms. What remains at p99 (about 10 ms under full contention) is the
  calling thread's own wake-up after a helper finished the last job. That is a scheduler cost that no fork/join can
  avoid without spinning.
* CI runners report the tail on every OS. A stall there now means a job itself was slow, not a helper that was late.
