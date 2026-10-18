# 0051. Run the AI as a sim system that issues regular commands and keeps its state in a snapshot extension

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 part B)
* **Related:** TDD §7.3, §8.3 (REQ-SIM-06), §8.5 step 3, §8.6, §8.7 (REQ-SIM-12, REQ-SIM-13), §16.1 (REQ-AI-03),
  §21, §22.2, REQ-ARCH-03; ADR 0016, ADR 0017, ADR 0019, ADR 0039, ADR 0050

## Context

REQ-AI-03: "the AI runs inside the sim on every peer, uses per-player RNG streams, and has a count-based decision budget
per tick, staggered across players. AI commands go through the normal command validation." The AI keeps state across
ticks (flags, forgotten rules, a pass in progress, goals, attack waves), but systems may not keep state outside the
world (ADR 0019), and snapshots must contain everything (REQ-SIM-13). Tzar2.Sim cannot reference Tzar2.AI (§7.2), and
the gameplay systems are owned by other work streams, so the hooks must be small and generic.

## Decision

* **`LegacyAiSystem`** (Tzar2.AI) is an `ISimSystem` in phase `SimPhase.AI` (pipeline step 3).
  `LegacyAiSystem.Attach(simulation, players, options)` adds it before the first tick, for a new match or right after
  `Simulation.Restore`. Every tick, for each AI player in ascending slot order: continue or start a script pass
  (ADR 0050; budget `InstructionBudgetPerTick`, a pass every `ThinkIntervalTicks`, staggered by player), and every
  `ExecutorIntervalTicks` (staggered) run the executors (ADR 0052), which write commands stamped with the AI's slot.
* **Commands.** After all AI players have acted, their commands execute through
  `CommandSystem.ExecuteInTick(world, commands)`, the same validation as step 1 (ownership, existence, costs,
  requirements, placement; rejections become `CommandRejected` events). The AI has no other way to change the world:
  it is just another player and has no cheat access. The commands take effect in the tick that issued them, after the
  AI phase, which is equivalent to "commands for T+1" applied before every other system of the tick and needs no
  outbox in the snapshot. `LastCommands` exposes them for tests and replays-to-be.
* **Randomness** comes only from `World.Rng.Ai(slot)` (REQ-SIM-06); nothing else in the AI draws.
* **Iteration order** is fixed: AI players by slot; a per-tick **census** walks the world's dense stores in ascending
  entity order (REQ-SIM-07); ties are broken by entity index, resource id or player slot; no hash-ordered collection is
  iterated (T2D004).
* **State.** `World.Extensions` (Tzar2.Sim, `WorldStateExtensions`) holds `IWorldStateExtension`s: key, canonical
  `WriteTo`, validating `ReadFrom`. Snapshots (format 7: the gameplay format 6 of main plus this section) store the blocks in key order after the component stores; the
  full-state hash adds them only when there are any, so worlds without extensions hash exactly as before (the M2
  golden hashes are unchanged). A restored world keeps the blocks as bytes until their extension registers, so restore
  and attach can happen in either order, and an unclaimed block is carried through unchanged. The AI's block
  `ai.legacy` holds a fingerprint of every `AiOptions` value that changes the commands (cadence, budgets, executor
  tuning, the technology view and extra executors by name) and, per player: slot, program hash, difficulty,
  civilization and AI flags (a restore with other options, scripts or settings is rejected), flag/forgotten/latch bit sets, the pass position and its partial goals, the last
  goals, resign request and the wave. Untrusted input: every count and enum is range-checked; malformed bytes give
  `InvalidDataException`.
* **Attach is atomic:** the extension registers first (reading a restored state into fresh objects, committed only when
  all of it is valid); only then is the system added to the pipeline, so a rejected restore leaves the simulation as
  it was. `Tzar2.Architecture.Tests` also checks that the AI's IL calls none of the world's structural mutators.
* **Read-only queries.** `PlacementQueries.CanPlace` and `RequirementsMet` (ADR 0070) and `GameplayQueries.MayHarvest` expose the checks the commands
  use, so executors choose orders that will be accepted (the commands re-check anyway).
* **Tests.** 1 vs 4 workers and an uninterrupted run vs. a snapshot restored mid-pass with another worker count give
  identical command and hash streams; `AiGoldenHashTests` pins the command stream, the hash stream and the final hash of
  a 4-AI match on every OS, both runtimes and both configurations; CI also runs `t2 bench --scenario ai --map 128
  --players 4 --seed 1592598566 --ticks 2400` (final `3C235D50C0E27C1B`) and compares its per-tick stream across the
  three OSes.

## Consequences

* The AI costs nothing on peers' networks: every peer computes the same AI commands. Replays need only the human
  commands plus the AI settings.
* Triggers and Lua (REQ-SIM-13 "script variables and trigger states") can use the same extension mechanism.
* Measured (Apple M5, macOS arm64, Release, .NET 10, 4 workers), `t2 bench --scenario ai`, 8 AI kingdoms on 256²,
  24,000 ticks (20 minutes of game time): 1,357 entities, ~36,000 resource units delivered and 5 attack waves per
  kingdom; tick p50 0.18 ms, p99 1.29 ms (budget 12 ms), mean 0.23 ms.

## Alternatives considered

* **Run the AI outside the sim, between ticks, and feed its commands into the next tick's command list:** keeps Sim
  untouched, but the AI would draw from the world's RNG while the world is locked, and its state would need a parallel
  snapshot channel that every save, reconnect and replay path must remember.
* **Run the AI on one host and send its commands over the network:** costs bandwidth and makes the AI depend on one
  peer's presence; TDD §16.1 asks for it to run on every peer.
* **AI state in ECS components:** the state is per player, not per entity, and of variable size (goal lists, bit sets).
