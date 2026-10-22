# 0192. Gates and bridges take the cells of their pictures, either way

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), under ADR 0190
* **Related:** ADRs 0043 (walls, gates, bridges), 0189 (turned placements), 0190 (ReTzar as a source of facts);
  ledger CTRL-024, BLD-018, BLD-019; UNK-02

## Context

Our gates were 3 × 1 tiles with the middle one opening, a placeholder (UNK-02): the gate's object draws `anim_none`, so
the data seemed to give no footprint. They were also never drawn. A turned bridge or gate was the same footprint
transposed.

ReTzar reads the original's code:

* **A gate's cells are those of its drawbridge picture for its facing** (`0x498373`). `gw0_anim` (wooden, across) is
  224 × 32 px, 7 × 1 tiles. `gw2_anim` (down) is 32 × 192, 1 × 6. Stone gates use `gs…`.
* **The door is the three tiles in the middle.** Across, tiles 2 to 4; down, tiles 1 to 3 (`0x456210`, `0x4576bc`).
  Only the door opens.
* **A gate has four facings**, 0 and 1 across and 2 and 3 down. The map's gate shape gives them: bit 1 turns the gate,
  bit 0 mirrors it. The facing chooses its picture.
* **The drawbridge rests raised (closed) on row 0 and lowered (open) on row 6** (`0x456080`).
* **A bridge turned has the cells of `bridge_vert`**: 6 × 11, not its 11 × 7 transposed.

## Decision

* **A type may have a turned footprint of its own.**
  * `Building.turnedWidth`, `turnedHeight` and `turnedMask` give it; `Gate.turnedPassage` gives the door when turned.
  * Without them, the turned footprint is the footprint transposed.
  * `EntityTypeDefinition.Turned`, `FootprintSize(turned)` and `FootprintMask(turned)` are what the sim, the editor and
    the client read. `Footprint.Mask` and `Footprint.Passage` give the placed shape.
* **The importer reads the cells from the pictures.**
  * Gates: `g{w|s}0_anim` and `g{w|s}2_anim`.
  * Bridges: `bridge_horz` and `bridge_vert`.
  * Rules: the gate templates give the doors, `0011100` across and `011100` down.
* **The footprint keeps a mirror bit**, in what was padding. Build's argument 1 carries it (bit 1, beside the turn in
  bit 0), and maps carry it as `MapObjectFlags.Mirrored`.
* **The client draws gates** with the drawbridge of their facing: row 0 closed, row 6 open, and the `…_build` picture
  for a site. Alt or Ctrl turns a gate's ghost through its four facings.

## Alternatives considered

* **Keep 3 × 1 gates.** A 7-tile picture would not fit them, and the original's walls take 7-tile gates.
* **Model the passage in front of the gate** (`0x456b50`, `0x457980`: three rows that must be clear to build, and that
  units wait in while the gate is open). Left for later: the door is what changes play.

## Consequences

* The gameplay golden and bench change (the bot builds a gate and a bridge), and so does the reference data hash.
* Gate changes may touch three tiles each. The bound on passability changes per tick is three times the gate change
  budget.
