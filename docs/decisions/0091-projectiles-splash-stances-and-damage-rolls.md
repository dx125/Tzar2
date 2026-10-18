# 0091. Fly projectiles, splash at the impact, keep simultaneous hits; minimum range, damage rolls and stances

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (M3 stream E)
* **Related:** TDD §8.5 step 9, §10 GAME-07, UNK-07, UNK-08, UNK-22, ADR 0038, ADR 0090

## Context

GAME-07: damage classes sword/arrow/cannon, the BONUS.INI matrix, projectiles (arrow, bolt, cannon, rocket arrow,
stone) with flight time and hit rules, splash (catapults), siege against buildings, minimum ranges, line of sight or
height, formations, retaliation — keeping the simultaneous resolution of ADR 0038. What is known: the strategy guide
says peasants repairing towers "move them away just as the Catapult projectiles are about to land" (stones land where
the target stood and can be dodged); the object INIs give `min_attack_range` (catapults 4, cannon tower 2, rocket tower
3) and `max_damage` (towers); the full BONUS.INI matrix is already compiled (ADR 0038). Nothing is known about flight
speeds, splash sizes, misses, height or formations.

## Decision

* **Damage classes**: arrow for ranged shooters by default, sword for melee; `Armament.damage: cannon` for catapults,
  heavy catapults, cannon towers, rocket towers, galleons and hulks (classic.yaml). The class picks the holder's
  `[damage]` share (ADR 0093).
* **Projectiles** are entities with only a `game.projectile` component (no identity: never a target, never drawn by
  the frame builders; `ProjectileLaunched` events carry the aim point and flight time for the presentation). They are
  fired at the hit frame, fly at their kind's speed and, at pass 0 of the next ticks, arrive: a **homing** projectile
  (arrows, bolts, magic) follows its target and hits it unless it died or entered a holder (then it is lost); a
  **ground** projectile (stones, cannon balls) lands where the target stood at launch and hits it only if it is still
  within the hit radius; **splash** hurts everything else within the splash radius with the splash share (friendly
  fire per kind, off for siege: UNVERIFIED). The shooter's attack is taken at launch; the target's defence at impact.
  Arrivals join the tick's hit list, so the simultaneous resolution of ADR 0038 still holds (a shooter killed this tick
  still lands what it fired).
* **Attack ground** (order `attack_ground`, command 29): `CombatMode.Ground` with the point in `Combat.ChaseX/Y`;
  in range the unit fires at the point; what stands within the hit radius takes the full hit, the rest splash.
* **Minimum range**: no swing inside `min_attack_range`; auto targets inside it are dropped, an explicit target waits
  (UNVERIFIED: no backing off).
* **Damage rolls** (⚠ HYPOTHESIS): a type with `max_damage` above its attack rolls its attack in [attack, max_damage]
  from the `combat` RNG stream, in hit order (deterministic).
* **Stances** (orders `stand_ground`, `hold_steady`, `holdfire`/`fireatwill`; command 28, store `game.stance` for the
  non-default ones): stand ground attacks only what is in reach and never chases; hold fire never starts a fight and
  does not retaliate; explicit attack orders still apply.
* **Retaliation** as ADR 0038, minus hold fire, never against an attacker inside a holder.
* **Targets**: units inside holders, stone units and (for other kingdoms) cloaked units are never targets; confused
  units take everyone as enemies and everyone takes them as one (`CombatSystem.IsEnemyTarget` stays the single choke
  point; the diplomacy and visibility stream adds its checks there).
* **Line of sight, height, formations**: none (no source describes them; the editor's height map only shapes terrain).
* **Honor**: a hero hits a hero `combat.honorAttackPercent` harder once its kingdom has Honor (ADR 0092).
* **Killed events** carry the killer's owner in `Y` (backlog: the client inferred it from the killer's index).

## Alternatives considered

* **Instant hits plus a delay (no entities):** cannot be dodged and cannot splash at a point.
* **Misses by chance:** no source; the dodge rule follows from ground aiming.

## Consequences

* The client can draw projectiles from `ProjectileLaunched` (aim point, flight ms) without new frame data.
* Fidelity captures replace speeds, radii and the roll hypothesis (UNK-22, UNK-08).
