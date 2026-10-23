# 0212. The client sees the session, its frames and the UI models, never the running engines

* **Status:** Accepted
* **Date:** 2026-10-23
* **Deciders:** agent (maintainer), code review of work queue item 16 (coordinator's plan, item a)
* **Related:** TDD §7.2, REQ-ARCH-03; ADRs 0073 (client presentation contract), 0182 (engine-free UI models);
  [code review 2026-10](../reviews/2026-10-code-review.md) (module boundaries 2)

## Context

The owner will fork the remake, perhaps onto another engine. The client must then be replaceable behind a contract that
holds no handle to the running match. ADR 0073 lets the client use only Game, Content and Import, plus
`Core.Ids`, `Core.Diagnostics`, `Sim.Commands` and `Sim.Events`. `ClientTypeReferenceTests` checks the client's own code.
Nothing checked what the Game assembly hands out, and some of it gave the client the engines themselves:

* `GameSession.Simulation` (the world and its mutators, on the session's own thread), `GameSession.Ai` and
  `GameSession.ReplayHeader()`;
* `FrameBuilder` (public, filling a frame from the `World`), `KingdomStatistics.From(KingdomState)`;
* `IMatchPreparation.Prepare(Simulation, LegacyTriggerSystem)` and `MatchSetup.Preparation`.

The client used none of them. Only the CLI (`t2`) and tests did.

## Decision

* The client's contract is the public surface of `Tzar2.Game.Session` and `Tzar2.Game.Ui`. It names only types the
  client may use (ADR 0073), plus a short list of **plain values**:
  * the diplomacy enums `Stance` and `Sharing`;
  * `GameType` and `VictoryCondition`;
  * `TickResult`, which holds a tick's hashes;
  * the replay's `ReplayAiProgram`, `ReplayChat` and `ReplayHeader`;
  * `TriggerOptions`.

  These are enums and immutable records that describe settings or results. None of them is a handle to running state.
* These members are now `internal`: `GameSession.Simulation`, `GameSession.Ai`, `GameSession.ReplayHeader()`,
  `FrameBuilder`, `KingdomStatistics.From`, `IMatchPreparation` and `MatchSetup.Preparation`. The hosts that need them
  (`t2`, `Tzar2.Tools.Cli.Tests`, `Tzar2.Editor.Tests`, `Tzar2.Game.Tests`) get them through `InternalsVisibleTo`.
* `ClientContractTests` (in `Tzar2.Architecture.Tests`) checks the contract by reflection. It covers every public
  field, property, method, constructor and event of those namespaces, including the element types of arrays and the
  arguments of generic types. A new member that hands out an engine fails the test. A new plain value is added to its
  list on purpose.

## Alternatives considered

* **Mirror enums in Game (`DiplomaticStance` and so on):** this removes the last Sim names from the contract, at the
  cost of a mapping layer for values that are already plain data. It can be done if a fork replaces the sim's enums.
* **A separate host API assembly for tools:** cleaner than `InternalsVisibleTo`, but a new project for four members.
  It can be revisited if more hosts appear.

## Consequences

* A client on another engine implements against frames, UI models and commands only. The match's state reaches it
  only as frames, and its changes go back only as commands.
* Tools keep their access. A tool outside the repository would need its own way in (the internals are not public).
* Not changed here: `World`'s writable spans for the AI (plan item b).
