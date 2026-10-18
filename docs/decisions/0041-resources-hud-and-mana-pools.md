# 0041. Keep iron out of the HUD and give casters a mana pool that regenerates per minute

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 stream A)
* **Related:** TDD §10 GAME-01, §8.5 step 10, UNK-14, UNK-26, ADRs 0034, 0040

## Context

GAME-01: food, wood, stone, gold; mana per caster; iron exists in the data (`RES.INI`, `te_ironplus`, costs could use
it) but not in the HUD. `OBJECTS/*.INI` give casters `max_manna` (mage, priest, ninja, messiah; the cow's is its milk,
ADR 0042). Unit costs carry an `m` suffix for the mana a summoning caster pays (bat `25m`). The regeneration rate is not
in any table (UNK-26).

## Decision

* `ruleset.hudResources` lists the resources the HUD shows → `Ruleset.HudResourceMask` (classic: food, wood, stone,
  gold). Iron stays a full resource: stockpiles, costs, starting levels, drop-offs (storages), gathering.
* The cost suffix `m` maps to `mana`, which is not a stockpile: `EntityTraits.ManaCost` (paid by the caster, GAME-11).
* Trait `ManaPool {max, regenPerMinute}` (max from `max_manna`; regen `gameplay.manaRegenPerMinute`, UNVERIFIED 60).
  `ManaState` (hot) stores milli-mana; casters start full. Step 10 (`StructureSystem`) adds
  `(t + 1) × regen / 60 − t × regen / 60` milli-mana per tick of `t` ms elapsed, so the long-run rate is exact without a
  stored remainder; `manaRegen`/`manaMax` modifiers apply (Concentration).

## Alternatives considered

* **Mana as a sixth stockpile:** the original keeps it per caster (the manual says summoned creatures cost the caster
  mana); a stockpile would let one mage cast with another's mana.

## Consequences

* GAME-11 spends `ManaState` and `ManaCost`; the HUD (stream D) reads `HudResourceMask`.
