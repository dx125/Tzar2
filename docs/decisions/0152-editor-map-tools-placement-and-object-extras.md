# 0152. Edit maps with headless tools and placement rules; give map objects flags, facing and amount

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent M5 (editor)
* **Related:** TDD §9.1, §18.2 (terrain, trees, decorations, buildings, units, items, object properties, tools, map,
  kingdoms, user resources, sounds), §18.3 (copy/paste, prefabs, transforms, layers, mod-aware palettes), REQ-EDT-01/03,
  Appendix C.1/C.2; ADR 0042, ADR 0043, ADR 0060, ADR 0121, ADR 0150, ADR 0151

## Context

§18.2 lists what the original editor could do with a map. Several of those features have nowhere to live in the M4
`MapObject` (type, owner, position):

* gates placed horizontally or vertically, on either side, or open;
* bridges along either axis;
* a unit's facing (TAB);
* a rock's own amount ("amount per object; defaults restorable").

The converter also dropped the amounts that legacy maps store (REQ-EDT-03, lossless). The sim already supports rotated
footprints, open gates, facings and resource stocks (ADR 0043); it just did not receive them from map data. The
editor's rules (what may stand where, what a brush stroke does) must be testable without Godot (ADR 0150).

## Decision

* **`MapObject` gains three init-only fields:**
  * `Flags`: `Rotated`, `Open`, `Mirrored`;
  * `Facing`: 0–7;
  * `Amount`: 0 means the type's default.

  `MapBinary` writes version 2 (per object: u8 flags, u8 facing, i32 amount) only when an object has one of them.
  Otherwise it writes the version-1 bytes as before, and `MapData.ContentHash` adds the extras only in that case.
  Every existing map, package, golden and replay keeps its bytes and hashes.
* **`World.LoadMap` places the extras:**
  * `Rotated` is passed to `PlaceStructure` (gates, bridges);
  * `Open` opens the gate through `SetGateOpen` and sets the gate to manual mode, so it stays open until ordered;
  * the facing goes on `Identity.Facing`;
  * a positive amount replaces the resource stock.

  `Mirrored` is presentation only (⚠ UNVERIFIED what the manual's gate "side" changes). The converter now keeps the
  amounts of gold and stone rocks and fish patches. It also records, per map, which legacy object index became which
  map object (`MapConversion.LegacyObjectIndices`, used for click-to-locate in the report panel).
* **The tools live in `Tzar2.Content.Editing`:**
  * `TerrainTools`: six brush sizes; painting removes cliffs on the touched tiles; cliffs only on land; tiles the set
    cannot draw are listed and drawn red.
  * `TreeTools`: specific tree / random of a type / random of all, five brushes, density, accumulation, delete. Seeded
    with the Core RNG, so the same stroke gives the same forest.
  * `PlaceTools`: the original's modifiers. On gates, Ctrl turns, Alt flips the side and Ctrl+Alt places open; on
    bridges, Alt turns. Also TAB facing, random within a group, and amounts.
  * `WallTool`: a Bresenham line like the sim's `BuildWall`.
  * `ObjectLocator`, `LayerSettings`, `Transforms` (multi-select turn and mirror), `ObjectClipboard` (copy/paste across
    maps; prefabs are the same JSON).
  * `KingdomEdits`: starting locations, Low/Medium/High fill from the ruleset's levels, equalize column/row/all,
    technology lists, diplomacy.
  * `AssetEdits`, `DecorationEdits`.
  * `NewMap`: empty with scattered patches, rocks and animals, or random through the generator of ADR 0060, with
    per-player kingdoms.
* **`Occupancy` holds the placement rules.**
  * Structures: they fit on the map, stand on land and free tiles, never on cliffs. Harbors touch the shore. Bridges
    cross water, with land at both ends of the long axis.
  * Units: one per tile per layer, on their own layer (ground, water, air). Air units may stand over anything.
    (⚠ UNVERIFIED: the manual only says "stacking rules".)
  * Items: on land. Fish patches: on water.

  The validator reuses the same rules.
* **`EditorPalette`** is built from the compiled ruleset, so units, buildings and items of enabled mods appear without
  editor changes. Creatures are the common units no building produces; Nature owns them, trees, rocks and items by
  default.
* **`EditorController`** is the interaction model behind the map view: pointer and key events become document edits.
  * A stroke is one undo step; Escape rolls it back.
  * Click and rubber band select; Shift adds.
  * Alt-drag moves; Ctrl-drag copies.
  * Walls are placed by drag; Area draws rectangles.
  * Delete, TAB/Shift+TAB, and keys 1–8 set starting locations.
* **The Godot editor mode** (`src/Tzar2.Client/src/Editor/`) is opened from EDITOR in the main menu or with
  `--editor [file]`.
  * The map is drawn by the game's `WorldRenderer` in a sub-viewport. `TerrainLayer.Recompose` redraws only the chunks
    a stroke touched, and `Tzar2.Game.Editing.EditorFrames` turns the document's objects into render frames.
  * An overlay shows red tiles, cliffs, selection, brush, starting locations and areas.
  * Docks: tools, properties (multi-edit), locator, validation (click to locate), layers; plus the mini-map, auto-scroll
    while painting, and dialogs for map properties, kingdoms/diplomacy, user resources and sounds/music.
  * `--editor-smoke` drives the real screen headless through the controller. The Godot smoke test runs it.

## Alternatives considered

* **Gate orientation as two entity types:** ADR 0043 rejected this for the sim (doubles gates and upgrades).
* **Extras in `ObjectInfo` (scenario):** they would only apply when a scenario is attached, while they are map facts
  that skirmish maps need too.
* **A new map.bin version for every map:** this would change every package and hash for nothing.

## Consequences

* A map's gates, bridges, facings and rock amounts survive the editor, the converter and the sim.
* Tree image variants within one entity type cannot be chosen: the ruleset has one `comm.tree` type and the renderer
  picks the image by entity id. This is blocked in the parity checklist until TREES.INI variants are decoded.
* The editor uses Godot's standard controls, not the original's dialog bitmaps. The original's editor was a separate
  Windows program with standard controls too.
