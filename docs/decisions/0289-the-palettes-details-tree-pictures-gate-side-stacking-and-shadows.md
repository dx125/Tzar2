# 0289. The palette's details: tree pictures, the gate's side, units per tile and the imported shadow

* **Status:** Accepted
* **Date:** 2026-11-11
* **Deciders:** maintainer (Claude), the editor review (batch E)
* **Related:** PRES-009, PRES-022, PRES-023, PRES-024, WORLD-024, CTRL-024; ADR 0120, ADR 0152, ADR 0281; STATUS
  item 26.7

## Context

The editor review left four details of the palette marked UNVERIFIED or not done.

* **Tree pictures.** A terrain set's tree strip has 31 pictures in kinds (`[treename]`), and the terrains each kind
  grows on are listed (`[treeterrain]`, PRES-009). In the original map the value of a tree's cell is its picture, 1 to
  31 (ReTzar `add_trees`: picture value - 1). Tzar2's converter kept the tree but not its picture, so every forest of
  the original maps was drawn with pictures picked by terrain. The editor's tree brush could not choose a picture.
* **The gate's side.** The editor's Alt flips a gate along its axis (CTRL-024). Tzar2 drew the other pictures and
  changed nothing else, marked UNVERIFIED. ReTzar uses the record's byte 0x38 & 3 only to pick the gate's pictures,
  its site's and its flags'.
* **Units per tile.** The editor places one unit per tile and layer, marked UNVERIFIED. The 33 maps of the owner's
  install, converted, put no two units on one tile.
* **The imported shadow.** The editor's authentic import makes a shadow of a picture: its silhouette moved 4 px right
  and down, a placeholder. The original's building shadows were measured on the owner's install (44 buildings and
  their `_s` pictures). Each is its silhouette moved right by a median 14 px, about a tenth of its height, and down 0
  to 16 px. Only the part the body does not cover is stored, and it is drawn where the body is.

## Decision

* **Tree pictures.**
  * `MapObject.Look` is a tree's picture, 1 to 31, or 0 for one by terrain. It is presentation only: not in the map's
    content hash, so maps hash, play and restore saves as before.
  * map.bin version 4 keeps the pictures after the objects, as (object index, picture). It is written only when a
    tree has one, so other maps keep their bytes.
  * The converter keeps the cell's value. The clipboard keeps a tree's picture.
  * The renderer draws a tree's own picture where the map gives one, in the game and in the editor.
  * The editor's tree brush offers the kinds of the document's terrain set: it plants one of the chosen kind's
    pictures, or by terrain as before.
* **The gate's side** changes only the pictures, as Tzar2 did; PRES-024 records ReTzar's reading and a test that the
  side does not change the tiles.
* **Units per tile:** one per tile, as Tzar2's editor did (WORLD-024, from the maps). Whether a flier may stand above a
  unit on the ground stays open; the maps hold no case.
* **The imported shadow** moves the silhouette right by a tenth of its height and down by a twentieth (PRES-023), and
  keeps only the part the body does not cover, as the original's.

## Alternatives considered

* **Putting the tree's picture in the map's hash:** a converted map's hash would change, and the restore of every
  save of a converted map would be refused ("taken on a different map"). The picture changes nothing in the
  simulation.
* **Keeping the pictures as decorations:** a picture would not follow its tree when the editor moves or copies it.
* **A slanted projection for the imported shadow:** closer to the original's drawn shadows, but it needs the light's
  direction per height, which the plain move approximates within the measure's spread.

## Consequences

* The original maps' forests show the pictures their makers chose.
* The editor can plant a kind of tree (pines, palms, as the terrain set names them).
* The editor's parity list has no UNVERIFIED gate side or stacking rule left; the imported shadow follows a measure.
