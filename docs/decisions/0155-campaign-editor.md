# 0155. Edit campaigns as a list of map documents; derive the graph from the maps; save `.t2camp` byte-identically

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent M5 (editor)
* **Related:** TDD §18.2 (Campaign editor), §18.3 (visual campaign graph), §18.4, REQ-EDT-08/09, Appendix C.9; ADR 0120,
  ADR 0121, ADR 0133, ADR 0151, ADR 0154

## Context

A legacy campaign is a set of maps plus a description; map order lives inside the maps, as their Load Map, Player Win and
Player Lose actions (Appendix C.9).

The original campaign editor imported, exported and deleted maps, edited descriptions and details, marked entry maps,
and saved and opened campaigns. §18.3 adds a visual graph with entry missions, a persistence mapping and carried
variables. `.t2camp` (ADR 0121) stores `campaign.json`, `maps/<id>.t2map`, and resources shared by every map that has
them. The campaign flow (ADR 0133) plays an `ICampaignSource`.

## Decision

* **`CampaignDocument`** (`Tzar2.Content.Editing`) is an immutable `CampaignState` with its own undo:
  * id, title and description;
  * per map: id, map name (the name actions use), campaign-screen description, entry flag, and the map's own
    `MapDocument` with its own history;
  * graph node positions.

  The map's own description is its "details" in the original's sense.
* **The graph is derived, never stored separately.**
  * `Build()` rebuilds the `CampaignData` edges from each map's actions, in action order, with names matched ignoring
    case. This is exactly the converter's order, so a converted campaign builds back to the same `campaign.json`.
  * `Graph()` adds reachability, dangling names and the persistent units by name (the `CampaignGraph` of ADR 0154,
    moved to Content).
  * `CarriedVariables()` lists the `#` variables the maps use (ADR 0133).
* **Validation:**
  * blocking when there is no map or no entry map (the original rejects such a campaign);
  * error for two maps with one name;
  * warnings for dangling transitions and unreachable maps;
  * a note for a Persistent unit that appears in one map only;
  * plus every map's own findings, with the campaign's shared resources counted as present.
  * Each finding carries its map id (`ValidationIssue.MapId`).
* **`CampaignPackage`** writes `.t2camp` through `PackageWriter`, `CampaignJson` and `MapPackage`.
  * A resource with the same path and bytes in every map that has it (and the campaign's own shared resources) is
    stored once in `assets/`, the converter's rule (REQ-EDT-09).
  * Graph positions go to `editor.json` only when there are any.
  * Entries the editor does not own (provenance, conversion report) are kept verbatim.
  * `Write(Read(p)) == p` for editor and converter campaigns; tests check both.
  * `WriteMap` exports one map as a stand-alone `.t2map` with the shared resources it lacks.
* **Play:**
  * `Tzar2.Game.Editing.CampaignPlaytest` is an `ICampaignSource` over the document; each map is built when its mission
    starts.
  * The campaign flow, carry-over and saves of ADR 0133 run unchanged. A test plays an edited three-map campaign end
    to end with `CampaignAutoplay`, including the persistent hero and a carried variable.
* **Godot `CampaignWindow`** (Campaign menu, or "Open in editor" on a converted campaign):
  * map list with new/import (`.t2map`)/add from legacy file/export/delete/reorder;
  * the `CampaignGraphView`, where nodes can be dragged and their positions are saved;
  * campaign and map properties;
  * the persistence mapping and carried variables;
  * validation;
  * "Edit this map in the editor" (Ctrl+S then saves the campaign);
  * play from the start or from a chosen map.

## Alternatives considered

* **Store the graph edges as editable data:** they would drift from the maps' actions, which are what the game runs.
  Transitions are edited where they live: in the maps' Load Map, Player Win and Player Lose actions.
* **One history for the campaign and all its maps:** an undo in one map would undo edits in another the user is not
  looking at. Each map keeps its own history; campaign-level edits have theirs.

## Consequences

* An editor campaign plays in the game exactly like a converted one, and both survive load/save unchanged.
* §18.6's authored campaign is made with these documents and tools, and played to the end (ADR 0156).
