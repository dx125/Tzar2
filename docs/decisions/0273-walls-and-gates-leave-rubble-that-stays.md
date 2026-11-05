# 0273. Keep the rubble of walls and gates with the decals, and lay gates as the original does

* **Status:** Accepted
* **Date:** 2026-11-04
* **Deciders:** maintainer (Claude), at the owner's request after the ninth play-test
* **Related:** BLD-018, BLD-028, CTRL-024, TECH-036; ADR 0043, ADR 0192, ADR 0271; ReTzar `src/gameplay.rs`

## Context

The owner asked for three checks against the original:

1. Can a gate be built over walls that run its way, or that only meet its ends?
2. Does a gate destroyed while closed let units through? In the original its picture turns to a gate with its door fallen.
3. Can a ship be upgraded?

ReTzar's reading of the original's code answers the first two:

* **The gate's site test (0x456b50)** looks at the gate's tiles and its passage (0x457980). The passage is the door's
  tiles and the tiles before it where the drawbridge comes down: two, or one for a wooden gate facing up. Each tile must
  be on the map, with no standing tree, unit, building or item, but walls never count. Laying the site (0x455e20)
  removes every wall on the gate's tiles, of any kingdom, built or not. The test asks no terrain kind.
* **A destroyed building leaves rubble (0x41db50).** This is a debris object of nobody's that never blocks:
  * a gate leaves `g{w|s}<facing>_demol`, its drawbridge fallen;
  * a wall leaves `walls_{wood|stone}_debris`;
  * a bridge leaves `bridge_{horz|vert}_demol`, the harbor `debris harbor`, and other buildings `debris<W>x<H>` of their
    size up to 6.

  The rubble shows its next frame every 2000 ms (0x444bd0). A gate's and a wall's rubble stays on its last frame, the
  rest goes after its last.

Tzar2 had three differences:

* **The gate's site.** Tzar2 replaced only the kingdom's own finished walls and did not look at the way before the door.
* **The Build command** took its argument 1 only as 0 or 1. The client sends bit 1 for a gate facing the other way
  (CTRL-024), so the sim refused two facings of four.
* **Ruins and rubble.** Tzar2 kept a building's ruin as a decal that lived as long as a corpse, but never drew it. Walls
  left no ruin. The decals live in a ring that drops them in creation order, so all of them share one lifetime.

The galleon can be upgraded, on the second page of its card as in the original. Its card stayed `e_galleon`, which
offers level 2 only. The original shows `e_galleon<level>` (ReTzar `object_menu`).

## Decision

* **A gate's site is the original's** (`GameplayCommands.GateSite`).
  * Its tiles are free land or walls of any kingdom, built or not. The walls on its tiles are destroyed when the site is
    laid; those before its door stay.
  * The tiles before its door must be on the map with no building, tree, bridge or field but walls. Their number per
    facing is the new `Gate.approachTiles`: `[2, 1, 2, 2]` for wooden gates and `[2, 2, 2, 2]` for stone ones.
  * Tzar2 still asks land for the gate's own tiles.
  * The placement ghost asks the same through two new terrain bits of the frame: `Placement.Wall` and `Placement.Clear`.
    It marks the tiles before the door too.
* **Build takes argument 1 from 0 to 3.** Bit 1 counts for gates only.
* **Walls and gates leave rubble that stays** (`RuinOnDeath: { lasting: true }`).
  * `DecalIndex` keeps it in a second ring with no expiry, capped like the first. A piece at the point of an older one
    replaces it.
  * Every decal also carries its look: a gate's or bridge's facing.
  * Snapshot format 26 adds both. Older saves load with no rubble (ADR 0271).
  * Bridges now leave a ruin too.
* **The client draws ruins and rubble** on the ground with the original's pictures (`SpriteRules.RuinImage`,
  `RuinFrame`).
  * Ruins come from the decal ring with their age and go past their last frame.
  * Rubble rests on its last frame.
  * Neither has a kingdom's colours.
* **The galleon's card follows its cannon level** (`ICardSource.LevelMenu`). The level counts the finished upgrades
  that change no type.

## Alternatives considered

* **Rubble as entities, as the original's debris objects.** This needs a new entity type with no gameplay and an
  owner. In Tzar2, Nature is everyone's enemy, so it would also need exceptions in targeting and selection. The decals
  are already the place for what lies on the ground and never blocks.
* **Rubble kept only by the client.** It would be lost on loading a save and differ between players who joined late.
* **Gates over the kingdom's own walls only.** This is safer against a gate laid to remove an enemy's wall. But the
  original's site test and site laying name no owner, and the rules stay the original's.

## Consequences

* **A gate can now be laid over another kingdom's wall,** which removes that wall. This is the original's behaviour.
  If play shows it is abused, it is a rule to revisit with the owner.
* **The rubble's cap is the decal cap (4,000).** Past it the oldest piece goes.
* **The hashes changed:** the ruleset's (the gate's approach and the lasting ruins) and the state's (the decals' looks
  and the rubble).
