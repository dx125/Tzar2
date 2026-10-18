# 0092. Count experience from damage to enemies, level with a growing cost, name heroes, persist named units

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (M3 stream E)
* **Related:** TDD §8.5 step 11, §10 GAME-08/20, UNK-23, ADR 0040 (experience stats), ADR 0090

## Context

The manual: "Each experience point adds one point to the attack and half a point to the defense parameter"; the
technologies: Learning ("increases the amount of experience that can be gained"; the strategy guide: "Researching
Learning allows your units to gain experience"), Heroism (unlimited experience, heroes), Leadership ("the units around
a hero gain experience along with him"), Honor ("increases the attack damage of heroes, when fighting other heroes"),
Warfare/Tactics/Sun Tzu (faster learning), Body Building (hit points per level), Philosophy (monks start higher),
Mastery (mana into experience); dummies train; "as each experience level is earned, the next level requires more
effort"; 1.05: no experience from attacking allies. `HEROES.INI` lists names per civilization. The editor's
"Persistent" flag carries unit properties to the next campaign map by name. Numbers: none (UNK-23).

## Decision

* **Experience** (`game.experience` on types with the `Experience` trait: every unit in classic): the damage a unit
  deals to another kingdom's or Nature's unit or building, times `combat.experiencePerDamagePercent` (UNVERIFIED 100%),
  plus `experiencePerKill` (UNVERIFIED 0); own targets give none (allies are the diplomacy stream's hook). It is
  collected as `PendingXp` and counted in step 11: × the owner's `experienceGain` modifier (Warfare, Tactics, Sun Tzu
  +25% each, UNVERIFIED), then capped.
* **Levels**: level n needs `levelBaseXp × n × (n + 1) / 2` (UNVERIFIED base 100: each level costs more); the cap is
  `combat.baseLevelCap` (0: no levels without Learning) plus `experienceCap` modifiers (Learning +4, Heroism +1,000 =
  unlimited; UNVERIFIED); experience beyond the cap's threshold is lost. Per level +`attackPerLevel` (1) attack and
  +`defenceHalvesPerLevel`/2 (½, truncated) defence; `healthPerLevel` modifiers (Body Building +5, UNVERIFIED) raise the
  maximum hit points (damage taken stays taken). `startingLevel` modifiers (Philosophy) apply when a unit is created.
* **Training dummies** (`TrainingDummy`, Tactics): an own dummy may be attacked by explicit order; it takes no damage
  and teaches up to `dummyMaxLevel` (UNVERIFIED 3).
* **Heroes**: at `heroLevel` (12, TDD) a unit becomes a hero (flag) and takes the first `HEROES.INI` name of its
  civilization no live hero of its kingdom carries (then "<first> 2", "<first> 3"…, UNVERIFIED). Leadership: a hero's
  gain is shared (50%, UNVERIFIED) with its kingdom's units within 4 tiles. Honor: ADR 0091. Heroes are immune to
  murder and souls (UNVERIFIED) and cannot be bribed.
* **Persistence** (`Tzar2.Sim.Persistence.PersistentHeroes`): a named unit's record — name, type key, level,
  experience, hero flag, potion bonuses, items (type, power, charges) — captured per player, applied to a same-named
  unit at match setup (world unlocked), serialized as a versioned, bounded binary list. The campaign layer (M4)
  decides when to capture and where to store.

## Alternatives considered

* **Experience per hit or per kill only:** the manual ties experience to fighting, not to kills; damage-proportional
  experience rewards strong hits and makes dummies' "training" comparable (both UNVERIFIED).
* **A table of level thresholds:** a formula with one parameter until captures give the curve.

## Consequences

* Mercenaries' and tavern heroes' experience (Fame) is the trade/tavern stream's; Tournaments' sharing of Kung-Fu
  experience is not implemented (open question).
