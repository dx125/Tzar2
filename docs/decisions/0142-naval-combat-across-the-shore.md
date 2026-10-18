# 0142. Naval combat: ships and land units fight across the shore only within range

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (naval stream)
* **Related:** TDD §10 GAME-07, REQ-PATH-09, UNK-22, UNK-43, ADRs 0091, 0092

## Context

Ships are armed (arrow: xebec, fishing boat; cannon: galleon, hulk; ADR 0091) and the damage matrix already rates
coast towers against ships. But target acquisition used the sight radius for everything: a swordsman at the shore
acquired a ship it could never reach and walked up and down the beach; a galleon acquired a castle far inland and
sailed along the coast after it; neither ever gave up because the target stayed in sight.

## Decision

* Movement planes: land (land locomotors and buildings), water (ships), air. Two entities on land and water planes are
  **across the shore** from each other; air is never across.
* **Acquisition** across the shore takes only targets within the attacker's range now (melee: never). Within its own
  plane nothing changes.
* **Automatic fights** (auto-acquired, move-advance) across the shore drop the target as soon as it is out of range
  instead of chasing it; an explicit attack order still chases to the nearest reachable point (the player asked).
* Stances apply to ships unchanged: stand ground fights only what is in range, hold fire never starts a fight.
* Damage shares into holders (ADR 0093), projectiles and the BONUS.INI matrix (ADR 0091) are unchanged.

## Consequences

* Archers on the shore shoot ships in range; ships bombard what they reach; nobody wanders after the unreachable.
* UNVERIFIED (UNK-22, UNK-43): the original's ship ranges, whether land melee units can hit a ship moored at the
  shore, and how it treats unreachable targets.
