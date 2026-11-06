# 0279. Give the Arabian and Asian casters' spells the original's falls, sight and healing round

* **Status:** Accepted
* **Date:** 2026-11-06
* **Deciders:** maintainer (Claude), at the owner's request before the eleventh play-test
* **Related:** MAG-024, MAG-033, MAG-034, MAG-035, MAG-036, MAG-037, FOG-007; ADR 0261, ADR 0263, ADR 0278; ReTzar
  `src/gameplay.rs`

## Context

After the tenth play-test, the owner asked for the Arabian and Asian mages and priests to be checked in the same way as
the European mage (ADR 0278). Their spells come from the original's menus (`MENU.INI`):

| Caster | Spells |
| --- | --- |
| Arabian mage | magic carpet, jinn, rain of fire, magic vision, raise dead |
| Asian mage | confusion, turn to stone, whirlpool, flame bolt, dragon |
| Arabian priest | heal, bless (the European priest's menu) |
| Asian priest | heal, Mastery (teaching) |

Most of them already matched ReTzar's reading of the original:

* **Matched:** the summons (ADR 0278); turn to stone; the whirlpool; confusion ("hostile to all"); the flame bolt
  (MAG-029); raise dead (8 tiles round the caster); the blessing (+5 attack); teaching.

These differed:

| | The original (ReTzar) | Tzar2 before |
| --- | --- | --- |
| Rain of fire | Twenty meteors, one made every 170 ms, each falling 150 pixels onto its tile in 375 ms, then striking the 5 × 5 tiles round it (0x472ddb, 0x450620, 0x450820). | Twenty strikes on the ground, timed the same but with nothing in the air: no picture could be drawn. |
| The freeze ball | It falls straight down onto its point from 500 pixels above at 700 px/s, wherever the caster stands (0x432350). | Flew from the caster at 320 px/s. |
| A spell cast far away | Beyond the caster's sight less r, its kingdom sees r tiles round the point for 4000 ms (0x472ab0): r is 8 for magic vision, 3 for the others listed. | Nothing. |
| Magic vision | Cast on a unit or building of any kingdom, the caster's kingdom sees what it sees, following it, for 60 s. At a point, only the far cast's look (0x4735ac). The help: "If you select a unit it will reveal the fog for you for a certain period of time"; bears "are used to explore the map". | A fixed revealer of 8 tiles at the point for 60 s. |
| The heal | The Heal program goes on to the nearest wounded friend within the healer's sight once its unit is well, or when it was not wounded (op 0x51, 0x4e2dcb). Stone takes no healing (0x504d00). | The heal stopped with its unit and healed stone. |

## Decision

* **Falling balls (MAG-034).** A projectile may have a height to fall from (`dropPx`). A ball of a point spell starts
  that far above its point, not at the caster:
  * `freeze_bolt`: 700 px/s from 500 px;
  * the new `meteor`: 400 px/s from 150 px.
* **Rain of fire.** It has `projectile: meteor` and no delay. Each of its twenty pulses (every 170 ms) makes a meteor
  at its random tile. The meteor's landing strikes the area once (`MagicSystem.Land`).
* **The far cast's look (MAG-035).** A spell field `reveal` (tiles):
  * 8 for magic vision;
  * 3 for the lightning, fire rain, bless, curse, confusion, freeze, whirlpool, invisibility, hypnosis, stone and flame
    bolt.

  Beyond the caster's sight less that, the cast adds a temporary reveal of that radius at the point for
  `MagicSystem.RevealMs` (4000 ms).
* **Magic vision (MAG-036).**
  * **On a unit or building.** The cast keeps the object under the cursor. An `AreaEffect` whose `Caster` is that
    object looks every 250 ms for 60 s: a temporary reveal of the object's sight round it, for the caster's kingdom. It
    ends early if the object dies or goes into a holder.
  * **At a point.** Only MAG-035's look.
  * The revealer object is no longer placed.
* **The heal (MAG-037).** A heal whose unit is no longer wounded (stone counts as not wounded) passes to the nearest
  wounded unit of the healer's kingdom or an ally within the healer's sight, on both axes. With none, the order ends.
  This holds both when the order starts and after each cast. The heal adds nothing to stone.

## Alternatives considered

* **Keep the fire rain's strikes on the ground and let the client invent the meteors.** The client cannot know where a
  meteor falls before it lands, as the tile is drawn at random. A meteor in the simulation is the original's own model.
* **Magic vision as a status on the seen unit.** That needs a new component and save format. A reveal renewed every
  250 ms by an area effect uses what exists.

## Consequences

* The ruleset hash changes: the new fields, the meteor projectile, and the freeze ball's speed. The goldens that pin it
  are re-pinned. No scripted scenario casts these spells.
* The meteors and the falling freeze ball are projectiles in the frame. The client can draw them (batch E: pictures and
  sounds).
* A healer ordered once keeps the wounded round it well, as in the original.
