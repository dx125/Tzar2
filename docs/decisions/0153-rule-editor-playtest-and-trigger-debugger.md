# 0153. Generate the rule editor from a trigger schema; playtest scenario matches; debug triggers through a passive trace

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent M5 (editor)
* **Related:** TDD §17.1, §18.2 (Rules, Conversations), §18.3 (playtest, trigger debugger, areas and groups, search and
  replace, Lua editor, localization), Appendix C.3–C.8; ADR 0120, ADR 0130, ADR 0131, ADR 0133, ADR 0150, ADR 0151

## Context

The rule editor must cover all 8 events and 27 actions of Appendix C (plus the converter's Delay and unknown kinds), each
with its property page. Every page is built from the same handful of shared pages (objects, holder, area, expression,
players, texts, user resources). §18.3 adds:

* playtest as any player, with time controls and the map revealed;
* a trigger debugger: last result per rule, variable watch, breakpoints on rules and actions, step, event log;
* named areas and object groups reusable across triggers;
* search and replace;
* a Lua script editor;
* `.po` localization tables.

The trigger engine belongs to M4-B (ADR 0130). Its state is part of the snapshot and the golden hashes, so the
debugger must not change it.

## Decision

* **`TriggerSchema`** (`Tzar2.Content.Editing`) describes every event and action kind as data: an id, a title, the
  record type, a factory, and fields. Each field has a `FieldKind` (one of the shared pages) and get/set delegates over
  the immutable IR record. The Godot `SchemaForm` builds every property page from it.
  * A test checks that every concrete `EventDef`/`ActionDef` has a page.
  * It checks that every field writes back what it reads, and takes a changed value.
* **Expressions** stay source text in the document (ADR 0150). `ExpressionHelp` provides:
  * the vocabulary for completion: reserved variables, functions, user variables, events, timers;
  * a live check against the document's tables;
  * `Rename`, which renames an event, a timer or a variable and rewrites every expression that uses it (whole
    identifiers, ignoring case, `$`/`#` for variables).
* **Named areas and object groups** live in `editor.json` (ADR 0151) and are reused **by value**. Changing an area's
  rectangle replaces every trigger area equal to the old rectangle. Changing a group's members replaces every named
  set with exactly the old members. Search and replace of object references is
  `ObjectEdits.ReplaceReferences`.
* **Localization:** `MapStrings` collects every player-visible text under a stable context (`map/title`,
  `action/<name>/text`, `conversation/<c>/<phrase>`, `object/<id>/title`…). It exports a gettext catalogue and imports
  a translated one, as an undoable edit.
* **Lua:** scripts are part of the document (`scripts/*.lua` in the package). The editor highlights them and checks the
  syntax that is clear without the host: unterminated strings and comments, unbalanced brackets and block keywords.
  API completion needs the Lua API v1 (M8), so the parity item is **blocked** with that reason.
* **Playtest** (`Tzar2.Game.Editing.Playtest`):
  * The document is built into `MapData` + `ScenarioData` and started through `ScenarioMatch` like a campaign mission.
    So the match is exactly what the game would play.
  * "As any player" makes the chosen kingdom the only Human-or-AI one; the others play as AI.
  * "Reveal map" sets No Fog and Explore.
  * Cheats are allowed. The game screen's speed keys and pause are the time controls.
  * Leaving the game returns to the editor with the same document and undo history.
* **Trigger debugger:**
  * `ITriggerTrace` (Scripting) is an optional observer on `LegacyTriggerSystem.Trace`, called on the sim thread when a
    pass starts, a condition is evaluated and an action runs. It is not state, not hashed, and has no way to change the
    engine; a test plays the same map with and without it and compares the full hashes.
  * `TriggerDebugger` (`Tzar2.Game.Editing`) records the last value of each rule and the event log.
  * After every tick it copies a watch (variables, events, timers, rules) through the new `GameSession.AfterTick`.
  * A breakpoint on a rule (when it fires) or on an action (when it runs) pauses the session after that tick. A tick is
    atomic in lockstep, so "after the tick" is the finest deterministic point.
  * `GameSession.StepPaused` runs one tick while paused.
  * The Godot `DebuggerWindow` shows all of it during a playtest.

## Alternatives considered

* **Hand-written Godot forms per action:** 37 forms to keep in step with the IR, and no headless test of coverage.
* **Areas and groups as references inside the IR:** this changes `scenario.json` and the trigger engine for a pure
  editing convenience, and breaks the byte-identical round trip of converted maps.
* **Breaking inside a tick:** it would need the trigger engine to suspend mid-rule. That is not deterministic in
  lockstep and would change the engine owned by M4-B.
* **Reading the trigger state from the main thread:** it races with the sim thread. The watch is copied on the sim
  thread instead.

## Consequences

* New IR kinds need a `TriggerSchema` entry; the coverage test fails until they have one.
* M4-B's engine gained one optional property and three calls; the golden hashes are unchanged.
* When the Lua host lands (M8), the Lua tab gets real diagnostics and completion from the host.
