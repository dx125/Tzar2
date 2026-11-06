# 0277. Draw the placement ghost, the selection and the gates' flags as the original does

* **Status:** Accepted
* **Date:** 2026-11-06
* **Deciders:** maintainer (Claude), at the owner's request after the tenth play-test
* **Related:** CTRL-016, CTRL-023, CTRL-024, BLD-019, HUD-007, PRES-017, PRES-018, ECO-026; ADR 0189, ADR 0192;
  ReTzar `src/frontend/placement.rs`, `src/frontend/mission.rs`, `src/world.rs`

## Context

The owner's tenth play-test (2026-11-06) found six faults in how things are drawn and placed:

1. **The gate's green ghost** has the wrong picture.
2. **Turning a gate.** It is hard to tell which way a gate turned by Alt will open, and the keys feel unlike the
   original's.
3. **The green ghosts** do not look like the original's (compare with ReTzar).
4. **Gate flags.** A blue kingdom's gate flies a red flag.
5. **Items on the ground.** Gold and other resources show pieces of selection frames, and the selection frames are
   unlike the original's.
6. **The move cross.** A move order into unexplored land shows no red cross, though the original shows it.

What the causes are:

* **The gate's ghost.** It drew row 0 of `g{w|s}{n}_anim`, the drawbridge alone. Since PRES-017 a built gate draws its
  towers too (row 8).
* **The turning keys.** The original's help says "while placing a gate you can change its direction by holding down
  the Alt or Ctrl key", and for a bridge, Alt. Tzar2 stepped through the four facings at each press.
* **The ghost's look.** ReTzar's preview is the building's own picture times (0.3, 1, 0.3) at 60 % opacity, with a
  30-pixel square on each tile, red where it cannot stand. Tzar2 drew a flat green shape, which the sixth play-test had
  asked for. The owner now asks for the original's look.
* **The flags.** A gate's flags are a separate picture, `gates c`, 8 columns of 16 rows in the kingdom's colours. Its
  column follows the facing (1, 0, 3, 2 → 0, 2, 4, 6, one more for stone) and its offsets come from a table in
  Tzar.exe (0x589770). Tzar2 did not draw it.
* **The items.** A bag has no footprint, so Tzar2 drew it as a unit with its type's picture, `item`. That is
  `item_none`, the empty slot of the inventory: four corners.
* **The selection.** The original draws one-pixel brackets six pixels long round the object's selection box, in the
  terrain's `[colors] selection` (white, orange on snow). Tzar2 drew ten-pixel corners, coloured by stance, round a
  building's footprint.
* **The cross.** Tzar2 drew it on the ground, under the fog.

## Decision

* **The ghost:**
  * The building's picture is tinted by the shader (colour × tint, 60 %): green, or red where it cannot stand.
  * Each tile gets a square, red where it is blocked.
  * A gate draws its towers with the drawbridge lowered (PRES-017's layers), and its tiles before the door as fainter
    squares.
* **Turning** (`Placement.FacingOf`): the facing is read from the keys held, each frame.
  * Alt turns a gate or a bridge by 90°.
  * Ctrl has a gate face the other way.
  * Both together do both.
  * The presses do nothing else while a turnable building is being placed.
* **Gate flags** (`SpriteRules.GateFlags`, PRES-018): the two flags of `gates c` are drawn on a finished gate in its
  kingdom's colours. The left one is on the drawbridge's row, the right one eight rows down. `gates c` is loaded as an
  overlay.
* **Items:** an item on the ground draws its own look (`ItemLook`) before its type's picture.
* **Selection** (`SelectionMarks.Brackets`, HUD-007): six-pixel brackets, one pixel thick, round the selection box of a
  unit or a building, in the terrain's selection colour.
* **The cross:** it is drawn on a sprite layer above the fog and the night.

## Alternatives considered

* **Keep stepping through the facings at each press.** It contradicts the help's "holding down". The held keys also
  make the four facings reachable without counting presses.
* **Keep the colours by stance for the selection.** That was Tzar2's own invention. UI may improve on the original, but
  the owner asked for the original's look here.

## Consequences

* With nothing held, a gate is placed across and opens downward.
* The sixth play-test's flat green shadow is gone. If the owner prefers it, it is the shader's previous line.
* The rubble and ruins draw no flags, as before.
