# 0281. Draw the map's decorations and let rocks and ruins close their cells

* **Status:** Accepted
* **Date:** 2026-11-08
* **Deciders:** maintainer (Claude), at the owner's request (the editor review)
* **Related:** WORLD-003, WORLD-004, PRES-021, CAP-007; TDD §18.2 "Decorations", §18.4; ADR 0121, ADR 0152; UNK-70;
  ReTzar `src/world.rs`, `src/gameplay/movement.rs`, `src/frontend/scene.rs`

## Context

The owner asked for a review of the scenario and campaign editor. The editor's parity checklist marked decorations
"done", but nothing outside the editor read them:

* The converter kept a map's ground patches, ground objects, ruins and tree stumps in `decorations.json` (ADR 0121, "drawn,
  not simulated"). The game never read that file, and the editor drew each decoration as a dot.
* The original campaigns hold about 10,800 ground patches, 3,400 ground objects, 18 ruins and 8,200 stumps. Their maps
  showed bare ground.
* The editor's palette guessed decoration pictures from words in sprite names. It placed a `water_object` class the
  original does not have.

ReTzar's reading of the original shows that ground objects and ruins are not just pictures:

* Ground objects (rocks, mountains, holes, lava pits) and ruins (statues, altars) mark the cells of their picture
  static and occupied (0x434d10, 0x41ddc0). Units walk round them; ships stay off those on water.
* A cell stays free when the picture's `.dat` grid sets bit 2 for it.

ReTzar loads the `.dat` only for pictures under `building\` or `scenery\`. The original's loader (0x497c90, read in the
GOG `Tzar.exe`) swaps the picture's extension for `.dat`, whatever the folder. So the common pictures (the cemetery,
`go_ruins`) have their grids too.

The facts:

* The cells are the `object` box in whole tiles, rounded down, at least one.
* The grid is read row by row at that width (0x41de41). In the data, 37 grids are of another size and 8 shorter than the
  box. Tzar2 treats cells past the grid as closed.
* 114 ground-object pictures have no `.dat`; they close every cell.
* A ground patch the original stores at cell (0, 0) lies at its own pixel (`MapObject::location`). Ground objects lie at
  their cell.

Several terrain sets name their own pictures alike: 80 names, for example `go_small_mont_1` of grass and of summer.

## Decision

* `MapDecoration` moves to `Tzar2.Content.Compiled`. `MapData.Decorations` carries a map's decorations:
  * the converter and the editor's `BuildMap` fill it;
  * `map.bin` does not hold them (`decorations.json` does), so packages stay byte-identical;
  * the map's hash takes them in only when there are some, so maps without decorations hash as before.
* `Ruleset.Scenery` (a `SceneryTable`) gives each decoration picture of the terrain sets (`go_`, `gr_`; no companions) the
  cells it closes. It is compiled from the imported sprite index and DAT footprints and is part of the ruleset hash.
  A lookup takes the map's set first, then `common`, then any set.
* `MapState` closes the cells of every ground object and ruin in the base passability: only fliers pass. Ground patches
  and stumps close nothing. Base passability is a function of map and ruleset, so snapshots do not change.
* This waits behind a ruleset switch, `world.decorationsObstruct`, off in `classic.yaml` (the owner's decision,
  2026-11-08). Tzar2's step cuts no corner (ADR 0021); the original's path search steps diagonally between two closed
  cells that touch at a corner (ReTzar `pathing.rs`: the cost tests only the cell entered). With the rocks closed, the
  heroes of `v_00` stand at a rock touching a cliff at its corner and never reach the mission's end; the other 25
  missions of both campaigns complete. The next batch gives the step the original's corners, then turns the switch on: done in ADR 0282.
* The client draws the decorations with the game's own `WorldRenderer`, both in matches and in the editor:
  * ground patches flat under everything;
  * ground objects and ruins sorted by their foot, with their `_s` shadows;
  * stumps as the first pictures of the tree strip;
  * pictures animate at their `fps`, all in step from `startrow`;
  * nothing shows on land not yet explored.
  
  Pictures are found by the map's terrain set (`SpriteIndex.Find(name, set)`, `SpriteBank.AddScenery`). The editor
  rebuilds its sprite bank when the set changes.
* The editor's decoration palette is `DecorationCatalog`, read from the set's and the common `IMAGES.INI`:
  * `gp_` pictures are ground patches;
  * `go_` pictures are ground objects, on water when their `terrains = water`;
  * `gr_` pictures are the special locations (ruins);
  * groups are the `displayname`;
  * pictures can be placed at random within a group.
  
  Erasing finds a converted patch by its pixel.

## Alternatives considered

* **Ground objects as entities:** one entity per rock would put about 3,400 static entities in a campaign map's
  snapshots and hashes. A closed cell needs none of that.
* **A precomputed obstacle layer in `map.bin`:** the editor and the converter would have to keep it in step with the
  decorations, and the format would change. The ruleset already knows the pictures' cells.
* **Following ReTzar's folder filter:** the original's code loads every picture's grid; the original is the final source.

## Consequences

* Original maps look as they did in the original. Once the switch is on, units go round rocks and statues as they did
  there; the campaign acceptance run checks the missions still complete.
* The ruleset hash changes for every ruleset built from the original's data. Old replays of those rules do not play.
  Saves continue (the tables hash is unchanged); a unit saved on a now-closed cell stays where it is.
* Not done, recorded in the ledger:
  * ruins as capture points scoring their `ruinpoints` (CAP-007, missing; the owner asked for it in this series);
  * the original's per-object animation pace (payload 0x58);
  * a stump left when a tree is felled during play.
