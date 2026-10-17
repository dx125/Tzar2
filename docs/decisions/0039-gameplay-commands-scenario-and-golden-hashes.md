# 0039. Validate gameplay commands at execution; gate economy and combat with a benchmark and golden hashes

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 part C)
* **Related:** TDD §8.5 step 1, §8.6, §8.9, §21, §22.2, §25 (M2), REQ-SIM-11, ADR 0016, ADR 0017, ADR 0019, ADR 0026

## Context

§8.6: commands are re-validated at execution time (ownership, existence, costs, prerequisites). §25 M2: "basic
gathering/building/production/combat on test maps"; the sim budget is p99 ≤ 12 ms at 4,000 units (§21); determinism
must hold on three OSes, both runtimes and any worker count (ADR 0017).

## Decision

* **Commands** (wire values appended to `CommandKind`): `Harvest` 6 (target node), `Build` 7 (type + top-left point, or
  target = an own site to help), `Train` 8 (type, count 1-16, per listed producer), `Cancel` 9 (sites: refund and
  remove; producers: queue slot Arg0, -1 = last), `SetRally` 10 (point; Arg0 = 1 clears), `Attack` 11 (target).
  Move with Ctrl is move-advance. Move/Patrol/Follow/Stop clear the unit's worker task and fight.
  New rejections: `NotAffordable` 8, `IllegalPlacement` 9, `RequirementNotMet` 10, `QueueFull` 11. Costs are paid when a
  command applies, in command order, so two commands of a tick can never spend the same resources twice.
* **Events** (output only): `ResourceDelivered`, `ResourceDepleted`, `ConstructionStarted/Completed/Cancelled`,
  `UnitTrained`, `ProductionStalled`, `Damaged`, `Killed`.
* **Snapshots** validate every gameplay component (enum ranges, bounded swing and chase timers, phases matching their
  task, queue items producible by the building, stock only on nodes, combat only with armament, worker tasks matching
  traits, core components present, workers/sites/queues owned by a player — never Nature), extending ADR 0019. State layout version 3, snapshot format 4.
* **Test maps** (`ScenarioMaps.WithObjects`, synthetic tables): headless scenario tests for exact stockpile growth,
  gather timing, depletion and moving on, nearest valid drop-off, build → train → rally, multi-builder construction,
  cancellation refunds, every rejection, population stalls, the group-speed option, damage values, the swing cycle,
  acquisition, destroying a building, and a squad fight that ends identically with 1 and 4 workers and continues
  identically after a mid-fight snapshot restored with 3 workers.
* **`GameplayScenario`** (`t2 bench --scenario gameplay`): four kingdoms (`--players` up to 8) on 192² with castle,
  barracks, lumber mill, storage, farm, 30 houses, a forest, a gold rock, 60 peasants and 300 soldiers each (match
  population limit 1,000, so the barracks keep training); a
  deterministic bot (through the codec) harvests, builds houses, trains footmen and sends idle soldiers on
  move-advance to the center. Functional gate: resources delivered, kills and trained units > 0. Nightly gate:
  8 kingdoms, p99 ≤ 12 ms.
* **Golden hashes.** `GameplayGoldenHashTests` pins the classic ruleset hash compiled from the synthetic tables
  (`CEBD5DC17826D5B2`), the hash stream (`F37E338A41A10728`) and final hash (`E4802AE7C0EB883F`) of a 1,600-tick
  128² match, 1 and 4 workers, net8 and net10. PR CI also runs the bench variant (`--map 128 --players 4 --entities 480
  --seed 1592598566 --ticks 1600`, final `377E87E86F7F2784`) and compares its per-tick stream across the three OSes.
  The synthetic movement golden changed to `2D1E33CB90510133` / `C8B62DE0DA848E64` (the new stores and `PlayerState`
  are hashed).

Measured (Apple M5, macOS arm64, Release, .NET 10, shared machine ±30%; after the PR #14 review fixes):

| Scenario | Result | Budget |
|---|---|---|
| gameplay, 4 kingdoms, 2,400 ticks, 1 / 4 workers | 2,418 entities at the end; 11,663 resource units delivered, 225 kills, 40 trained, 3 houses; p99 1.69 / 1.50 ms, max 7.5 / 7.7 ms | p99 ≤ 12 ms |
| gameplay, 8 kingdoms (nightly gate), 1 / 4 workers | 4,146 entities at the end; 25,755 delivered, 1,159 kills, 80 trained; p99 3.40 / 2.28 ms, max 6.7 / 5.2 ms | same |
| gameplay, 8 kingdoms × 800 soldiers (≈ 9,200 entities at the start), 1 / 4 workers | 2,491 kills; p99 8.37 / 4.54 ms, max 26.1 / 7.3 ms (before the owner masks the review measured p99 11.6 ms, max 25.7 ms with 1 worker on a lighter load) | same |
| synthetic 4,000 units / 512², churn 2 (existing gate) | p50 3.58 ms, p99 7.44 ms | p99 ≤ 12 ms |
| B4 | 2,000 killed, obstacle version 0, 0 blocked; p99 0.57 ms | p99 ≤ 12 ms |
| allocation gate (gameplay, 1 and 4 workers, mid-battle) | 0 B per tick on the sim thread | 0 B |

## Alternatives considered

* **Validating costs when the command is accepted by the relay:** clients could race their own resources; validation at
  execution is the only point where every peer sees the same state (§8.6).
* **Extending the synthetic golden scenario with gameplay:** would mix two concerns; the movement golden keeps
  isolating pathfinding changes.

## Consequences

* In the default gameplay bench the population cap (castle 20 + 8 houses × 10) is below the starting armies, so
  training mostly stalls until losses free room; the golden variant trains. Training and production are covered by the
  unit tests.
* Any change to the classic YAML values, the synthetic tables, the compiler or a gameplay system changes the gameplay
  goldens on purpose: update them in the same commit with the reason.
