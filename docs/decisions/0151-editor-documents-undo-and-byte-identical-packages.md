# 0151. Undo with immutable states plus terrain patches; save `.t2map` byte-identically through the package writers

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent M5 (editor)
* **Related:** TDD §18.1 (REQ-EDT-02), §18.3, §18.4, REQ-EDT-03/05/11, REQ-IMP-02; ADR 0012, ADR 0120, ADR 0121, ADR 0150

## Context

REQ-EDT-02 asks for undo of every operation, with unlimited history that survives a save within a session. Maps are
up to 512 × 512 tiles, or 1,024 extended. One brush stroke touches a few hundred corners, while placing an object
touches one record. Saving must be deterministic, and a save/load round trip must give the same bytes. Converted maps
carry parts the editor does not edit: provenance, the conversion report, placeholders and gettext catalogues
(ADR 0121).

## Decision

* **State:**
  * A document is mutable terrain layers plus one immutable `DocumentState`. The terrain layers are the painted
    corners, ruleset terrain ids, heights and cliff tiles. The state holds the objects (`ImmutableList`), the scenario
    model (IR records), decorations, user resources (`ImmutableSortedDictionary`) and editor extras.
  * Every change runs inside an `EditTransaction`. Terrain writes take effect at once, so a stroke is visible while the
    mouse moves, and the first "before" value of each cell is kept. The state is replaced as a whole.
  * `Commit` pushes one `EditStep`: label, state before, state after, and a `TerrainPatch` of the changed cells sorted
    by layer and index. An edit that changes nothing records no step. Disposing an edit without committing it rolls it
    back.
* **Undo** swaps the state back and writes the patch's "before" values; redo writes the "after" values. The history is
  unlimited. `MarkSaved` only records the saved version (`IsDirty`), so undo works across saves. States share
  structure, so a step costs the parts it changed. A stroke costs its cells, not the layer.
* **Package (`MapPackage`)** uses the converter's writers. `PackageWriter` gives sorted, stored entries with a fixed
  timestamp, `MapBinary` writes `map.bin` and `ScenarioJson` writes `scenario.json`. `decorations.json` uses the
  converter's formatting.
  * `editor.json` holds editor extras: object ids that are not index + 1, named areas, object groups, and the source
    language when it differs from the provenance's. It is written only when it holds something.
  * Every entry the editor does not own is kept byte for byte.
  * The manifest is kept, and its name follows the map title.
* **Round-trip guarantee:** `Write(Read(p)) == p` for every package the editor or the converter wrote, and the same
  document always gives the same bytes. Tests check both.
* **Canonical build:** `BuildScenario` always stores one `ObjectInfo` per object (the converter's form).
  * It keeps the declared variable order.
  * It appends variables declared only by use in a fixed order: events, actions, rules, phrases.

## Alternatives considered

* **Command objects with hand-written inverses for every operation:** many inverse implementations to keep correct,
  for no gain over swapping immutable states.
* **Snapshots of the whole document per step:** 512² corners × 4 layers per stroke, which runs to gigabytes over a
  long session.
* **Regenerate `strings/*.po` and the provenance on save:** this loses the converter's contexts and translations, and
  breaks the byte-identical round trip of converted maps.

## Consequences

* Opening a converted map and saving it without edits changes nothing, so diffs show only real edits (REQ-EDT-11).
* A `.t2map` gains `editor.json` only when editor extras exist. The game ignores it.
* Localisation export/import (§18.3) writes `strings/<lang>.po` explicitly; it is not a side effect of saving.
