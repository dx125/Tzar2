# 0073. Run the skirmish's computer kingdoms with the legacy AI inside the sim pipeline

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (M3 stream D, after PR #20)
* **Related:** TDD §7.4, §16.1 (REQ-AI-01…05), §25 M3 ("skirmish vs. Hard AI"); ADR 0050, 0051, 0052, 0070
  (decision 3 superseded)

## Context

The M3 client shipped with `PlaceholderAi`, a stand-in `IPlayerController` that thought on the sim thread before the
tick and fed its batch through the local scheduler. The legacy AI (PR #20, ADR 0051/0052) is a sim system: on every
lockstep peer it runs the scripts and executors in the AI phase of the tick and issues commands through the command
system's validation in the same tick, with its state in the snapshot extension `ai.legacy`.

## Decision

1. **`GameSession` attaches `LegacyAiSystem`** before the first tick for every lobby slot with
   `ControllerKind.Computer`, with the slot's civilization and the lobby difficulty (`Difficulty` → `AiDifficulty`).
   This is the same path the AI tests and benchmarks use (`AiSkirmishScenario`): the AI's commands never pass the
   local scheduler, so single player, replays and lockstep peers see one and the same command stream. The session also
   sets each kingdom's civilization from the lobby (tech tree, script bindings).
2. **Scripts** (`SkirmishAi.Resolve`, behind the Game layer — the client never names an AI type, §7.2): the original
   `random.ais` (AI.INI's default, Appendix D.1) compiled from the user's base package (`text/data/ai/*`, includes
   resolved there); when the package has no scripts or the compile reports an error, the engine default
   `content/engine/ai/skirmish.ais`, with a `Notice` that the main menu and the skirmish setup show. The resolved
   script is part of the setup (`MatchSetup.Ai`). Every computer kingdom runs the same program, as in the original's
   random-map lobby.
3. **The AI script is part of the match identity.** `MatchFingerprint` (ruleset and map content hashes, lobby settings,
   and the program hash of every computer kingdom) is computed when a session starts; a peer whose fingerprint differs
   — another package, a fallback to the default — is refused before the first tick (`MatchMismatchException` names
   the kingdom), instead of desyncing at the first full-state hash. The replay header carries the same per-slot
   program hashes (`ReplayHeader.AiPrograms`, replay format v3), and a snapshot restored with another script is refused
   (the AI state stores the program hash). `GameSession.Restore` continues a match from `CaptureSnapshot` with the AI
   re-attached.
4. **`IPlayerController` and `PlaceholderAi` are deleted.** Tests that need a passive opponent pass
   `MatchSetup.ComputerAi = false`; tests that need another slot's input use the internal `SubmitAs` (what a remote peer
   would send; pending commands are kept per slot and stamped like the relay does).
5. **End-to-end gate:** `HumanVsAiTests` plays a human (one harvest order) against a Hard AI for 12 minutes on the
   synthetic tables twice and requires identical per-tick hash streams and final full hashes, plus completed AI
   buildings, trained AI units, AI hits on the human kingdom and human deliveries. With `TZAR2_PACKAGE` the same runs
   15 minutes with the original `random.ais` on the imported classic ruleset. Further: a mid-match snapshot restored
   with the AI re-attached continues the same hash stream; another script changes the fingerprint, the replay header
   and the full hash at tick 1, and its peer is refused; another seed of equal length gives another match; the AI runs
   on the sim thread at top speed without faults.
6. **`ClientTypeReferenceTests`** keep the client at type level to Game, Content, Import and the presentation
   contract from Core and Sim (ids, commands, events), on the sources and on the compiled client assembly.

## Alternatives considered

* **Keep `IPlayerController` as an adapter around the legacy AI:** it would issue the AI's orders a tick later than on
  lockstep peers and keep two AI paths alive; the AI is designed as a sim system.
* **Let the player pick a strategy script per kingdom now:** AI.INI's list is available (`AiIni.Parse`), but the lobby
  UI for it belongs with the multiplayer setup screens; `random.ais` is the original's default.

## Consequences

* Difficulty in the setup dialog changes what the scripts see (ADR 0052: no resource bonus).
* A computer kingdom's AI stops when it is defeated (the AI skips defeated players), checked by a session test.
* Measured on the synthetic tables (80² test map, Hard, 12 min): the AI completes ~40 buildings, trains ~120 units and
  lands ~1,200 hits on a passive human; the original `random.ais` on the imported package (96², Hard, 15 min): ~28
  buildings, ~84 units, ~280 hits.
