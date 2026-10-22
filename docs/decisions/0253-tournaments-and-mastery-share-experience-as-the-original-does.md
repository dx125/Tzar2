# 0253. Share experience through Tournaments and Mastery as the original does

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), under ADR 0190
* **Related:** ADR 0092 (experience), ADR 0190 (ReTzar as a source of facts); ledger TECH-028, TECH-029; UNK-23

## Context

The manual gives two Asian technologies one line each. Mastery "lets priests teach kung-fu units" (TECH-028, partial).
Tournaments "lets kung-fu units share their experience" (TECH-029, missing). Tournaments did nothing in Tzar2.
Mastery gated a spell that turned mana into experience for any unit.

ReTzar reads both from the original's code:

* **Tournaments** (`0x4de900`, the unit's experience handler).
  * With the technology, every point a kingdom's unit learns goes into a pool of the kingdom (`+0x112`). Points shared
    by Leadership or handed out by the pool itself do not count.
  * When the pool reaches 75, it empties, and whatever went over 75 is lost.
  * Then each of the kingdom's Asian peasants, monks, ninjas and priests learns one point.
* **Mastery's spell** (`0x473bf0`): the Asian priest teaches one point to an Asian peasant, monk, ninja or priest whose
  level is below its own. On any other unit the spell fails before it is paid.

## Decision

* **Tournaments.** `combat.tournamentTechnology` (te_tournaments), `tournamentEvery` (75), `tournamentGrant` (1) and
  `tournamentPupils` (a type filter: Asian peasants, monks, ninjas and priests).
  * The pool lives in the kingdom's state (`PlayerState.KungFuPool`, the byte of padding: it always stays below 75).
  * `ExperienceSystem` fills it from what units learn, after Warfare's speed and before Leadership's shares.
  * The pupils' lessons are added after it, so they do not feed the pool.
* **Mastery.** `spell_raiseexp` targets the same pupils. A cast whose target is not below the caster's level stops
  before it is paid.
* **The scale.** Tzar2's experience is counted on another scale than the original's (damage dealt, 100 × n(n + 1) / 2
  a level; UNK-23).
  * Tournaments keeps the original's ratio: one point per pupil for every 75 learned.
  * Mastery teaches 100, about a level's worth, where the original teaches one point. That value stays UNVERIFIED.

## Consequences

* TECH-028 and TECH-029 are tested. No technology of the data does nothing any more (`OriginTechnologyTests`).
* The hashed layout keeps its size; the ruleset hash changes and the gameplay golden follows.
* Found while reading: the original gives one experience point per kill and none per damage, and a level costs about
  as many points as the level (`speed × level / 100`). That would replace UNK-23's placeholder scale, and with it
  Mastery's 100. It needs its own change; every golden would move.
