# 0258. Count experience on the original's scale: a point per kill

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), under ADR 0190; coordinator ("a ReTzar fact beats the placeholder", 2026-10-22)
* **Related:** ADR 0092 (experience), ADR 0191 (technology effects), ADR 0253 (Tournaments, Mastery); ledger EXP-001,
  EXP-002, EXP-004, TECH-017; UNK-23

## Context

Tzar2's experience was a placeholder (UNK-23):

* one point per point of damage dealt;
* nothing for a kill;
* level n at 100 × n × (n + 1) / 2 points;
* training dummies teaching up to level 3.

ReTzar reads the original's experience routine (`0x5022e0`, the unit's handler `0x4de900`) and what feeds it:

* **Kills.** A kill of a non-allied unit is one point (`0x504f20`). Damage gives none.
* **Levels.** Each addition raises the counter. When the counter reaches `trunc(speed × level / 100)`, the unit gains a
  level and the counter empties.
  * At speed 100 the first point gives level 1, and level k needs k more.
  * Warfare, Tactics and Sun Tzu set the speed to 70, 50 and 35. Each needs the one before (TECHTREE), so the kingdom
    has the lowest.
* **Training dummies.** A dummy gives one point for every 75 hit points it loses (`0x4481e0`). Training ends at level 8
  or at the cap (`0x4df884`).
* **The cap.** It is 0, plus 4 for each Learning, 30,000 with Heroism, and 10 for each finished academy of the kingdom
  (`0x401850`).
* **Mastery** teaches one point, at range 1 for 10 mana.

## Decision

* `combat.experiencePerDamagePercent` is 0 and `experiencePerKill` is 1.
* **The curve.** `CombatStats.XpForLevel(level, speed)` is `Σ max(1, base × speed × k / 100)` for k from 0 to level − 1,
  with `levelBaseXp` 1. At speed 100 that is `1 + n(n − 1)/2`.
* **The speed.** `combat.experienceSpeeds` (te_warfare 70, te_tactics 50, te_suntzubook 35) replaces the three
  technologies' `experienceGain` effects. The speed scales the cost of a level, not the gain, so a 1-point kill keeps
  its whole value.
* **Dummies.** A dummy teaches a point for every `combat.dummyDamagePerPoint` (75) of the damage the blows would deal.
  The remainder is kept in `Experience.DummyCarry`, the former padding byte. `dummyMaxLevel` is 8.
* **Academies.** `combat.levelCapBuildings` (academies) and `levelCapPerBuilding` (10) raise the cap.
* **Mastery** teaches 1 point, with range 1 and mana 10.
* Tournaments (one point per 75) now count on the same scale as the original.

## Consequences

* Veterans come from kills: a unit reaches level 4 after 7 kills, level 12 (hero) after 67. Hitting a castle teaches
  nothing.
* **AI balance.** The 10-minute AI skirmish and its golden are unchanged: no computer kingdom researches Learning or
  builds an academy in it, so no unit could level before either. In longer games, levels now follow kills rather than
  damage dealt. Units that only shoot buildings (towers, siege engines) no longer level, and fighters who finish
  enemies off level first.
* The sim, AI and trigger hashes are unchanged. The gameplay golden (its bot trains on dummies) and its bench are
  re-pinned.
* The original's one-level-per-addition is not modelled: Tzar2 keeps a running total, so a large gain (a potion) can
  give several levels at once, as before.
