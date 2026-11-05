# 0274. Let shells and bolts land on a tile, and flame balls burn their way, as the original's

* **Status:** Accepted
* **Date:** 2026-11-04
* **Deciders:** maintainer (Claude), at the owner's request after the ninth play-test
* **Related:** WAR-013, WAR-018, MAG-024, MAG-029; ADR 0091 (projectiles), ADR 0261, ADR 0263, ADR 0271; ReTzar
  `src/gameplay.rs`

## Context

The owner saw that ships firing at buildings hurt nothing around them. In the original, peasants repairing a tower
under a catapult's or a cannon ship's fire were hurt. He asked for a check of every attack that hurts an area.

ReTzar's reading of the original's code (`land_projectiles`, 0x433770 and on) gives these rules:

* **Shells.** Catapults, heavy catapults, galleons, hulks, cannon towers and rocket towers shoot shells. A shell lands
  on the tile its target stood on at launch; for a building that is its first tile. Everything whose tiles hold that
  tile takes the attack. Then everything else meeting the 5 × 5 tiles round it takes half the attack before defence
  (0x4ed74a, 0x44de50). The square has no owner filter. When the tile is empty as the shell lands, nobody is hurt.
* **Ballista bolts** hurt everything on their tile (0x434120).
* **The fire rain** hurts everything meeting 5 × 5 tiles; Tzar2 already did this (ADR 0263).
* **Freeze** takes the units within 3 tiles each way; Tzar2 already did this.
* **The dragon** has no missile: its fire hurts its target only (0x4e9b60).
* **The flame bolt** is a flame ball at 700 px/s (0x431f40). Each tile it enters burns:
  * on its first three tiles, the first unit or building there that is no friend of the caster takes twice the
    spell's 60;
  * from the fourth tile on, everything but the caster meeting the 3 × 3 tiles round it takes 60 on its own tile and
    30 beside it.

Tzar2's projectiles had a hit radius and a splash radius in pixels:

* The stones' splash was 32 px with friendly fire; the cannon balls' was 24 px, sparing their own side.
* The rockets and the dragon's fire balls followed their target with a 16 px splash; the bolts followed their target.
* The flame bolt followed its target and hurt it alone.

A splash of 24 px round a building's centre never reaches the peasants at its walls.

## Decision

* **New fields of a projectile** (`combat.projectiles`):
  * `onTile` lands on the target's tile at launch (a building's first tile; the shot's tile for Attack ground) and hits
    everything whose tiles hold it, of any kingdom; an empty tile hurts nobody;
  * `splashTiles` gives the square round that tile whose other occupants take `splashPercent` of the attack before
    defence;
  * `burns` makes a flame ball.
* **Classic:**
  * `stone`, `cannon` and `rocket_arrow` are `onTile` with `splashTiles: 2` and `splashPercent: 50`;
  * `bolt` is `onTile` alone;
  * `flame_ball` (the dragon) follows its target with no splash;
  * `flame_bolt` burns at 700 px/s.
* **The tiles an entity holds** are its footprint for a building and the tile under it for a unit
  (`CombatSystem.WithinTiles`, on the combat grid).
* **A flame ball's way** is a new store, `game.trail` (`Trail`: the tile it is on and the tiles it has entered), on the
  projectile's entity. It is set when the spell is cast and burns as the ball flies. Snapshot format 27; older saves
  have no flame ball in flight with a trail (ADR 0271).
* **A spell hit with an attack** is spell damage of that amount: the burn's 120, 60 or 30.

The speeds stay Tzar2's, apart from the flame bolt. The original's shells fly at 100 (catapults), 250 (cannon tower),
300 (ships) and 400 px/s (rocket tower); one Tzar2 projectile serves several shooters.

## Alternatives considered

* **Larger splash radii in pixels.** A circle round the landing point is not the original's square round the target's
  first tile. It would hurt peasants on every side of a building, where the original hurts only those near its first
  tile.
* **Keep the towers' and ships' shells sparing their own side.** That was a reading of the help, which names siege
  weapons only. The code has no owner test.

## Consequences

* **Defenders get hurt.** Shooting at a building now hurts its repairers near its first tile, and a kingdom's own
  units close to the target. The computer's attacks and towers do more damage round their targets.
* **The hashes change** (ruleset, state and the new store).
