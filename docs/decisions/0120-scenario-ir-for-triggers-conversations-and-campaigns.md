# 0120. Share one scenario IR between the legacy converter and the trigger engine

* **Status:** Accepted (agreed by M4 stream A, the converter, and stream B, the trigger engine)
* **Date:** 2026-10-18
* **Deciders:** implementation agents M4-A and M4-B
* **Related:** TDD §7.2, §17.1, §18.4, §18.5, REQ-ARCH-01, REQ-SCR-01/02, REQ-EDT-03/05/08, Appendix C, UNK-04,
  UNK-37, UNK-38, UNK-41, ADR 0018, ADR 0050

## Context

M4 runs two streams in parallel. Stream A turns legacy `.wmp`/`.wcm` files into our map and campaign model. Stream B
runs the legacy trigger engine, conversations, quests and campaign progression. Both need the same description of a
scenario: rules, events, actions, expressions, conversations, quests, custom sounds and music, kingdoms, and the
campaign graph. The editor (M5) will need it as well. REQ-ARCH-01 lets Scripting consume only compiled contract types.
It reaches Tzar2.Content through Sim, just as the AI does (ADR 0050), and it may not see Formats or Import. The
converter must report problems in expressions (REQ-EDT-06), so the parser can't live in Scripting.

## Decision

* The IR lives in **`src/Tzar2.Content/Compiled/Scenario/`**, namespace `Tzar2.Content.Compiled.Scenario`
  (expressions in `.Expressions`). It is immutable and has no I/O. Types are records with value equality; arrays use
  `EquatableArray<T>`.
* **`ScenarioData`** sits next to `MapData`:
  * `MapProperties`
  * `ObjectInfo` per placed object (same index as `MapData.Objects`): name, title, Not Appear / Persistent / No AI,
    and skill overrides where null means "use default"
  * `KingdomDef`: legacy number 1–8, names, civilization, player type HumanOrAi/AiOnly/Rescue, active, resources, AI,
    shared victory, start tile, forbidden lists and initial researches
  * `DiplomacyDef` pairs
  * the trigger program: user `Variables`, `Events` (8 kinds plus `UnknownEvent`), `Actions` (27 kinds plus
    `UnknownAction`), `Rules`, `Conversations` (phrase graphs)
  * `Sounds` (custom sounds) and `Music`

  Its constructor checks every cross-reference: object, action, event, variable, conversation and phrase indices,
  that `TimerRef`/`StopTimer` point at Set Timer and that `RemoveQuest` points at Add Quest.
* **References are indices**, with the names kept for display. Objects are `MapData.Objects` indices. Named object
  sets use `NamedObjects` (indices + All/Any); type counts use `TypedObjects` (type key, player mask, ≥/≤, count,
  All/Any). Areas are `RectArea` (inclusive tile rectangle) or `ObjectArea` (object index; optional radius, ⚠
  UNVERIFIED). Quests are not a separate table: an `AddQuestAction` names its quest, the legacy way.
* **Players stay legacy.** Masks are `ushort` with bit 0 = player 1. Single kingdoms are `int` 1–8. Mapping to sim
  slots is stream B's job.
* **Expressions:**
  * `ExpressionDef(Source, Root?, Error?)` keeps the text and the resolved tree.
  * Tree nodes: `IntLiteral`, `UserVar(index)`, `ReservedVar(enum)`, `EventRef(index)`, `TimerRef(action index)`,
    `UnknownRef(name)` (evaluates to 0, REQ-SCR-02), `UnaryExpr`, `BinaryExpr`, `AssignExpr`, `CallExpr(CallKind)`.
  * `ExpressionParser` (Content) is the only parser. Names resolve case-insensitively through an `IExpressionScope`;
    the `@` before a function is optional.
  * Precedence: assignment (right-associative), `||`, `&&`, equality, relational, additive, multiplicative, unary.
  * Evaluation semantics belong to the Scripting VM: integer width, division by zero, short-circuiting, `rnd` bounds
    (UNK-41).
* **Encoding:** `ScenarioJson` is the canonical JSON (`scenario.json` in a `.t2map`). Fields come in a fixed order,
  unions carry a `kind`, text is unescaped UTF-8, and newlines are LF. Expressions are stored as source text and
  re-parsed against the scenario's own tables on reading, which gives the same tree. `ScenarioData.ContentHash` is
  xxHash64 of the compact form.
* **`CampaignData`** holds:
  * an id, name and description, plus a ContentHash for profile keys
  * `CampaignMap`s: stable id, the map name actions refer to, package file, description, entry flag
  * `CampaignEdge`s rebuilt from Player Win / Player Lose / Load Map. A dangling target keeps `ToMapId = null`, so
    the editor can flag it (REQ-EDT-08).
* **Ownership:** stream A owns the types, the parser, the JSON and the synthetic fixtures. Stream B owns runtime
  semantics. Either stream may extend the types additively; a rename needs the other stream's agreement.
  Scripting's entry in `DependencyRulesTests.CompiledContract` is added by stream B with its first use.
* Unknown-register ranges for M4: stream A uses UNK-70…UNK-89, stream B UNK-50…UNK-69.

## Alternatives considered

* **Store expressions as text only, parse in Scripting:** the converter and the editor couldn't validate them, and
  Import may not reference Scripting.
* **Reference objects by string id:** names aren't unique in legacy maps and many objects have none. Indices are
  unambiguous and match `MapData.Objects`; names stay available for persistence matching.
* **YAML (`triggers.yaml`, §18.4) instead of JSON:** the YAML static generator needs annotated DTOs and offers no
  canonical byte order. JSON written by hand with `Utf8JsonWriter` is AOT-safe, diff-friendly enough and canonical.
  §18.4's file names are therefore `scenario.json` and `campaign.json`.

## Consequences

* The editor (M5) edits the same records and validates with the same parser and reference checks.
* `UnknownEvent`/`UnknownAction` keep undecoded records, so a map never silently loses triggers (REQ-EDT-10). The
  conversion report counts them.
* Revisit if the trigger VM wants a lowered form, such as bytecode. That would be a Scripting-side compile step from
  this IR, not a change to it.
