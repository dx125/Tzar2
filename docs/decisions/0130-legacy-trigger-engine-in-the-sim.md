# 0130. Run the legacy trigger engine as a budgeted sim system with its state in a snapshot extension

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent M4-B
* **Related:** TDD §7.2, §8.5 step 2, §8.7 (REQ-SIM-12/13), §17.1 (REQ-SCR-01/02/03), §21, Appendix C, REQ-ARCH-01/03,
  UNK-38, UNK-41, UNK-50…UNK-66 ([m4-placeholders.md](../mechanics/m4-placeholders.md)); ADR 0019, ADR 0051, ADR 0106,
  ADR 0120

## Context

REQ-SCR-01 asks for the original rule model with exact semantics: rules are condition + ordered actions, checked about
every second, and fire once unless they contain Keep Rule; there are 8 events, 27 actions, an integer expression
language, timers, areas and object specifications. The scenario arrives as the IR of ADR 0120. The engine must be
deterministic on every peer, survive save/load (REQ-SIM-13: "script variables and trigger states"), and stay inside
the tick budget. Tzar2.Sim cannot reference Tzar2.Scripting (§7.2). The AI solved the same problem (ADR 0051).

## Decision

* **`LegacyTriggerSystem`** (Tzar2.Scripting, `Tzar2.Scripting.Legacy`) is an `ISimSystem` of `SimPhase.Scripts`
  (pipeline step 2), an `IWorldStateExtension` with key `triggers.legacy`, and the world's `IScenarioController`.
  `LegacyTriggerSystem.Attach(simulation, scenario, options)` adds it before the first tick, for a new match or right
  after `Simulation.Restore`. A second system of the same instance, `triggers.collect`, runs in `SimPhase.Cleanup`.
* **Match start.** `World.MapObjectEntities` (filled by `World.LoadMap`) maps `MapData.Objects` indices to entities.
  Attach copies it into the state, gives every object its `ObjectInfo` (name, health/max health/level/mana overrides;
  attack and defence as a permanent bonus) and removes Not Appear objects from the world until Appear Object spawns
  them where the map placed them.
* **Order, every tick:** advance the running timers by the tick length (up, or down to 0); handle a conversation reply
  (ADR 0131); then the rule pass. A pass starts every `IntervalMs` (default 1,000; the first one in tick 0): all events
  are evaluated in index order into their variables, then the rules in scenario order. A rule whose condition is
  non-zero runs its actions top to bottom and is disabled afterwards unless one of them is Keep Rule. Assignments are
  visible to later rules of the same pass. An empty or unparsable condition is false.
* **CPU budget:** a deterministic count of steps (expression nodes, object tests, spawn tiles), `StepBudgetPerTick`
  (default 200,000). When a pass has spent it, it stops after the current rule and continues in the next tick from
  that rule with the same event values (the cursor is state). Every tick finishes at least one rule.
* **Cost bound of one rule** (scenarios are untrusted; review of #31): at attach a rule keeps at most 256 actions
  (`MaxActionsPerRule`), and an expression may have at most 4,096 nodes and 256 levels (`MaxExpressionNodes`,
  `MaxExpressionDepth`, checked iteratively). Larger rules are truncated and larger expressions treated as empty, each
  with an entry in `LegacyTriggerSystem.Diagnostics`. Loop-like costs (typed specifications over every entity, Spawn
  Object tiles, Set Object Properties per object) count steps too, and `HardStepCapPerTick` (default 1,000,000) cuts
  the rule that reaches it where it stands (`ScenarioFault.RuleCut`); the pass continues next tick. A tick therefore
  spends at most the hard cap plus the unwinding of the cut rule, whatever the scenario. Conversation chains are bounded
  by `MaxConversationSteps`, delayed action lists by 1,024, queued conversations by 64.
* **Events** follow Appendix C.5 as state tests: in area, exist, destroyed (named: dead and not hidden), in holder,
  player defeated/won (any kingdom of the mask), object expression (reserved variables are the object's properties).
  Typed specifications count per kingdom of the mask, and All/Any combine the kingdoms (UNK-54). *Superseded 2026-10-23:*
  the masked kingdoms are counted together, as ReTzar reads the original (SCN-001). "Destroyed" of a type
  and "left the holder" need memory: the engine counts the tick's `Destroyed`/`Unloaded` sim events, the ones of the
  command step when step 2 starts and the rest after cleanup, each once.
* **Actions** change the world only through the public mutators (`Spawn`, `Kill`, `Destroy`, `ChangeOwner`,
  component refs) and the new `ScenarioOperations` of Tzar2.Sim: `ForceVictory`, `ForceDefeat` and `SetDiplomacy`
  without the players' rate limit. Object Order issues regular commands of the units' owners through
  `CommandSystem.ExecuteInTick`. Presentation actions only emit sim events (REQ-SCR-03; ADR 0131).
* **Sim hooks** (`IScenarioController`, set on `World.Scenario` by Attach): `IsLocked` (Lock Units: the players'
  commands skip locked units in step 1, deletion included; triggers and the AI are not filtered), `CannotWin`/
  `CannotLose` (Change Player Status: the automatic evaluation of step 16 neither defeats nor crowns such kingdoms;
  Player Win/Lose actions still do), and `Reply` (the `ScenarioReply` command). The hooks hold no state; a restored
  match sets them again on attach.
* **Arithmetic** is 32-bit signed and saturating; division and remainder by zero give 0 and a `ScenarioFault` event;
  unknown names are 0; `@rnd(a, b)` draws from the `scripts` RNG stream with inclusive bounds (UNK-41, UNK-53).
  Faults never stop the match.
* **Beyond Appendix C** (the converter found these in real maps, ADR 0120): statement sequences (value of the last),
  the further functions and reserved variables (UNK-64), the Delay action, which parks the rest of a rule's action list
  in the state and runs it when its game time is up (UNK-65), object categories (units, military units, buildings,
  major objects, any) wherever a typed specification is allowed, and the legacy owner codes 0/9/10 (UNK-66).
* **Victory by triggers:** Player Win / Player Lose set the sim's outcome flags (and a `MapRequested` event when they
  name a next map); `VictorySystem` (ADR 0106) keeps evaluating the match's own condition, which converted scenarios
  usually set to Not Specified.
* **State** (`TriggerState`): variables, event values and memories, disabled rules, timers, map-object entities and
  hidden flags, player status, quests, locked entities, the pass clock and cursor, the conversation session, queued
  conversations, the last next-map request. The block starts with the scenario's `ContentHash` and a fingerprint of
  the options; a restore with another scenario or other options is refused. Every count and index is range-checked
  against the scenario's shape; malformed bytes give `InvalidDataException` (tests: every truncation, every single-bit
  change either fails or round-trips byte-identically, out-of-range values).
* **Goldens:** `TriggerSnapshotTests` pins the hash after a trigger-heavy synthetic mission (3,600 ticks), checks save →
  load → continue at five points and 1 vs 4 workers.

## Alternatives considered

* **Triggers in Tzar2.Sim:** simpler wiring, but §7.2 puts the trigger engine in Scripting with the Lua host, and the
  sim would grow a dependency on the scenario IR.
* **Evaluate events only from sim events of the tick:** the original keeps event variables true *while* the condition
  holds; state tests do that directly and are restorable without replaying history.
* **A time budget:** not deterministic (TDD §21: count-based budgets only).

## Consequences

* Converted maps run their rules on every peer; nothing about the triggers travels over the network except the
  players' replies.
* The hash of worlds without a scenario is unchanged (extensions add to the full hash only when present), so the M2/M3
  goldens stay.
* Everything marked UNVERIFIED in [m4-placeholders.md](../mechanics/m4-placeholders.md) is a single place in the
  engine; a capture or an editor experiment replaces it without changing the state format.
