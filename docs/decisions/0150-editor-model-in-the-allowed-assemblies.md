# 0150. Keep the editor model in `Editing` namespaces of Content, Import and Game; the client's editor mode is a view

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent M5 (editor)
* **Related:** TDD §7.2, §18.1 (REQ-EDT-01/02), §18.3, §18.5 (REQ-EDT-04…12), §25 (M5), REQ-ARCH-01/03; ADR 0011,
  ADR 0073, ADR 0120, ADR 0121, ADR 0133

## Context

M5 builds the map, scenario and campaign editor as a mode of the Godot client (DEC-11, REQ-EDT-01). The client may use
only Game, Content, Import and the contract namespaces `Tzar2.Core.Ids`, `Tzar2.Sim.Commands` and `Tzar2.Sim.Events`
(§7.2, ADR 0073). REQ-EDT-02 asks for an engine-agnostic map document model in `Tzar2.Content`, so that the CLI can
validate and convert maps headlessly. The editor logic must be testable without Godot. It has three parts with
different dependencies:

* the document itself (terrain, objects, the scenario IR, undo, validation, package I/O). It needs only the compiled
  contract and the package writers;
* the legacy converter's integration (import sessions, report panel, code pages, type mapping, campaign reconstruction).
  It needs `Tzar2.Import`, which Game may not see;
* playtest and the trigger debugger. These need `Tzar2.Game` (sessions, `ScenarioMatch`, the campaign flow) and,
  through Game, the trigger engine's state.

A new `Tzar2.Editor` assembly would need a new row in §7.2. The client would also be allowed one more assembly, and
that assembly would see Sim and Scripting through Game.

## Decision

* **No new assembly.** The editor model lives in three `Editing` namespaces of assemblies the client may already use:
  * **`Tzar2.Content.Editing`** is the document model (REQ-EDT-02): `MapDocument` (terrain layers plus an immutable
    `DocumentState` of objects, scenario, decorations, user resources and editor extras), `EditTransaction` and
    `EditHistory` (unlimited undo, ADR 0151), the edit operations (objects, terrain tools, triggers, kingdoms), the
    `MapValidator` behind the validation panel, `MapPackage`/`CampaignPackage` (`.t2map`/`.t2camp` through the existing
    writers) and the campaign document. It follows ADR 0012: no floating point, no unordered iteration, no clock or
    randomness outside the Core RNG. It does not use the rules compiler or the VFS; it takes a compiled `Ruleset` and the
    terrain-set manifest.
  * **`Tzar2.Import.Editing`** is the converter integration (§18.5): import sessions over files and folders, the report
    panel model with click-to-locate, code-page overrides, the type-mapping table and campaign reconstruction. There is
    still one converter (REQ-EDT-05); this namespace only drives it.
  * **`Tzar2.Game.Editing`** is playtest (as any kingdom, time controls, reveal map), the trigger debugger (rule results,
    variable watch, breakpoints, step, event log), editor frames for the shared renderer, and the headless smoke run of
    converted maps (REQ-EDT-12).
* **The Godot client's editor mode** (`src/Tzar2.Client/src/Editor/`) is a view and controller over these models. It
  turns input into model operations and redraws when `MapDocument.Changed` fires. It holds no editing rules of its own,
  so everything that matters is covered by the headless tests (`tests/Tzar2.Editor.Tests`).
* **References inside a document are object ids.** Inside a document, the trigger program refers to objects by stable
  `EditorObject.Id`, and expressions are kept as source text. `MapDocument.BuildScenario` maps the ids to
  `MapData.Objects` indices and parses the expressions against the tables of the moment, so deletions and renames never
  leave stale indices or trees. Deleting an object removes its references; deleting an action removes it from every
  rule (Appendix C.3).
* **Architecture tests:**
  * `EditorModelTests` checks that the `Editing` sources use only what their layer may use. `Tzar2.Content.Editing` may
    not use the rules compiler, the VFS, Import or Game. `Tzar2.Import.Editing` may not use Game, Sim or Scripting.
    `Tzar2.Game.Editing` may not use Import or Formats.
  * The existing client rules (`ClientTypeReferenceTests`) cover the editor mode unchanged.

## Alternatives considered

* **A `Tzar2.Editor` assembly referencing Content, Import and Game:** one home for the editor, but it would widen what
  the client may use, and the document model would leave Content, against REQ-EDT-02.
* **Everything in the client:** the editor rules would only be testable through Godot, and the CLI could not validate.
* **Everything in Content:** the converter integration needs Import, and playtest needs Game. Content may reference
  neither.

## Consequences

* `t2` can open, validate, save and convert maps with the same code as the editor.
* Game's and Import's `Editing` namespaces depend on Content's. Each layer is tested on its own, and the acceptance suite
  (§18.6) combines them headlessly.
* If the editor grows a large non-Godot UI logic layer (form layouts and the like), revisit the decision and give it an
  assembly with its own §7.2 row.
