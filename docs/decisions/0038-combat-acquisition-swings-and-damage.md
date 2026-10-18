# 0038. Fight with staggered nearest-target acquisition, a hit-frame swing cycle and a placeholder damage formula

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 part C)
* **Related:** TDD §8.5 step 9, §8.9, §9.3, §10 GAME-07, REQ-PATH-08, ORIG-01, UNK-07, UNK-08, UNK-22

## Context

GAME-07: attack, defence, health, range, sight, rate; damage classes; the BONUS.INI matrix; the damage formula,
rounding and minimum are unknown (UNK-08), and so is attack timing vs. RATE (UNK-07). The manual says defence "reduces
the amount of damage a unit suffers"; BONUS.INI gives ±% per attacker/target group.

## Decision

* **State.** `Combat` (hot): target (re-validated every tick), cooldown, time to the pending hit (-1 = none), last chase
  point and tick, mode (explicit order, auto, move-advance).
* **Targets** are live units and buildings of another owner (not resources; diplomacy is M3). Nature's armed units
  (animals, wild creatures) are hostile to every kingdom and every kingdom may attack them (PR #14 review S4). Nature
  cannot own workers, producers or drop-offs: maps that try are rejected, and such state in a snapshot is invalid.
* **Acquisition** (§8.9): a uniform grid of 4 × 4-tile cells holding the combatants only (units and buildings),
  rebuilt every tick by a stable counting sort, with a per-cell mask of the owners present: a scan skips cells holding
  only its own kingdom and cells beyond the best distance found so far (PR #14 review S1). An idle
  unit with `autoAcquire` (peasants have it off), or a unit on a move-advance, scans when `(index + tick)` hits its slot
  every `acquireIntervalMs` (200 ms, UNVERIFIED) for the nearest enemy within its sight (edge distance; ties to the lower
  entity index). An idle armed unit that is hit retaliates. Auto and move-advance targets further than 2 × sight are
  dropped; explicit targets are chased until they die.
* **Swing cycle.** In reach (range, or melee reach; a building is reached from the tiles next to it like workers do) the
  unit stops and faces the target; a swing starts when the cooldown is over, the hit lands `HitDelayMs` later if the
  target is still in reach, and the next swing starts `CooldownMs` after the previous one (carry-over). Classic
  compiles `CooldownMs = 6,000 / RATE` and the hit at 50% (both UNVERIFIED, UNK-07). Out of reach the swing is lost and
  the unit chases (a path request when idle, or when the target moved more than a tile, at most every 10 ticks;
  buildings through their approach point).
* **Damage** (UNVERIFIED, UNK-08): `max(minimumDamage, attack × (10,000 + bonus) / 10,000 − defence)`, integer,
  truncated toward zero; `bonus` = the sum of all matching BONUS.INI entries in basis points (how they combine is
  unknown). Owner decision: keep this flat formula until the damage capture. Buildings: following the manual (ch. 4,
  4. The Selection View: RESISTANCE "reduces the amount of damage a building suffers when attacked"), the result is
  multiplied by `resistanceScale / (resistanceScale + RESISTANCE)` (scale 100, UNVERIFIED) before the minimum; building
  defence is 0. RESISTANCE will also be the capture resistance (GAME-04). Ranged
  hits land at the hit frame (no projectile flight yet), no splash (UNK-22).
* **Order and deaths — simultaneous resolution** (amended after the PR #14 review, S2): pass 1 runs every attacker in
  ascending index on the state at the start of the step (targets, chases, swings) and only *collects* the hits that land
  this tick; pass 2 applies all of them; pass 3 kills every entity at 0 HP (`Killed`, then `World.Kill`). A unit killed
  this tick therefore still lands its own hit, two units striking each other in the same tick both die, and no outcome
  depends on entity indices (the applied order only decides which hit is credited as the kill and which surplus hits on
  an already dead target are dropped). Units leave a corpse decal that never blocks (ORIG-01), buildings free their
  tiles. The cooldown never carries more than one tick of debt (one swing per tick at most).
* **Move-advance** = Move with Ctrl (§9.3): the unit fights what it meets and then resumes its destination.
* **Blocked by an enemy** (owner decision): a plain move first side-steps around the enemy (ADR 0025 amendment) and
  stops only if there is no way; the idle unit then acquires the blocker. A move-advance engages it at once.

## Alternatives considered

* **Damage = attack × (1 − defence%)**: defence values (0-10) look like flat points next to attacks of 5-60; a flat
  subtraction is the more common reading of the manual's wording. Both are placeholders until the damage grid capture.
* **Parallel combat jobs:** combat writes other entities' health and targets; a sequential pass is simplest to keep
  deterministic. With the owner masks it costs about 1-2 ms per tick at 8 kingdoms in battle (ADR 0039); before them,
  scans visited ~470 candidates each (the review measured 91% of combat time there).
* **Immediate hits in index order (the first version):** the lower index won every symmetric duel.
* **Projectiles as entities:** needed for dodging and splash later; instant hits suffice to make ranged units work.

## Consequences

* The fidelity suite (§22.3) replaces the formula, cycle and reaches with measured facts; the code paths stay.
* Experience, holders' damage split (`[damage]` per class), towers and siege are M3; the damage class is already
  compiled for the holders.

## Amendment (M3 stream A, PR #21)

Owner decision: `resistanceScale` is 1000 instead of 100 (still UNVERIFIED, UNK-08); see ADR 0045.
