# 0261. Give spells the original's strength, duration and area

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), under ADR 0190; coordinator (the spell magnitudes, 2026-10-22)
* **Related:** ADR 0095 (spells), ADR 0250 (curse), ADR 0260 (ranges and mana); ledger MAG-024; UNK-26

## Context

After ADR 0260, a spell's amount, duration and area were still placeholders. Several also hit one unit where the
original hits an area. ReTzar reads each spell's handler:

| Spell | Original | Address |
|---|---|---|
| Heal | +6 hit points, cast again while the unit is hurt | 0x472ff1 |
| Bless | +5 attack (points, not percent) to the units not hostile to the caster within ±4 cells, 30 s | 0x4e5809 |
| Lightning | 150 damage | 0x472e83 |
| Freeze | every unit within ±3 cells of the bolt's impact: held 20 s, no defence, up to 40 damage short of death | 0x432590 |
| Invisibility | 45 s | 0x4734a1 |
| Turn to stone | 120 to 300 s, at random | 0x473913 |
| Confusion | every unit within ±2 cells but the caster, 20 s | 0x473250 |
| Flame bolt | 60 damage | 0x473b44 |
| Magic vision | 60 s | 0x4735ac |
| Raise dead | every body that meets the caster's footprint widened by 8 cells | 0x47367c |

## Decision

* **The classic ruleset** takes these amounts and durations. Bless, freeze and confusion take their radius.
* **Areas.** A bless, freeze or confusion with a radius reaches every unit whose position lies within that many tiles of
  the point on both axes (`MagicSystem.StatusAround`):
  * bless: own units and those its kingdom may not attack;
  * freeze and confusion: every unit, own ones too, but the caster.
* **Bless** adds whole points of attack.
* **Freeze** takes the defence while it lasts (`CombatStats`). Its damage stops one hit point short of death.
* **Durations.** `Spell.durationMaxMs` gives a lasting effect a random length, drawn from the misc stream (stone: 120
  to 300 s).
* **Raise dead** targets the caster itself and raises every body within 8 tiles on both axes. `DecalIndex.TakeCorpse`
  searches a square.
* **Still placeholders:** fire rain and the whirlpool keep their approximation (pulses over a radius), and cooldowns
  and cast times stay UNVERIFIED (UNK-26).

## Consequences

* MAG-024 is tested.
* Spells hit much harder or wider: lightning kills most units at once, freeze holds a group for 20 s, stone a unit for
  minutes. The goldens move where the scenarios cast them.
* Not modelled: the original's heal repeating while the unit is hurt.
