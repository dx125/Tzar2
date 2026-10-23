# 0263. Fire rain and the whirlpool as the original casts them

* **Status:** Accepted
* **Date:** 2026-10-23
* **Deciders:** agent (maintainer), under ADR 0190; coordinator (the remaining spells, 2026-10-23)
* **Related:** ADR 0095 (spells), ADR 0260 (ranges and mana), ADR 0261 (spell magnitudes); ledger MAG-003, MAG-004,
  MAG-005, MAG-024; UNK-26

## Context

After ADR 0261 two area spells were still placeholders. Fire rain dealt 10 every second for 5 s in a circle of 2 tiles.
The whirlpool dealt 30 every second for 6 s to enemy ships in a circle of 2 tiles and never moved.

ReTzar reads their handlers in the original:

| Spell | Original | Address |
|---|---|---|
| Fire rain | twenty fireballs, one every 170 ms, each falling about 375 ms on a random cell within ±64 pixels (2 cells) of the point; each deals 40 magic damage, with no attacker, to every unit and building that meets the 5 × 5 cells around its impact | 0x472ddb, 0x450820 |
| Whirlpool | forms only where its 3 × 3 cells are water; every 250 ms 20 magic damage, credited to the caster, to each ship on them (whoever owns it); every 2 s a random step of one cell where the square stays on water; gone after 15 steps (30 s) | 0x4733c4, 0x4fd0e0 |

## Decision

* **Spell fields.** Four new, optional fields join `Spell`. Each is part of the ruleset hash.
  * `square`: the pulse reaches what meets the square of `radius` tiles around the pulse's tile, not a circle.
  * `scatter`: each pulse lands at a random point up to that many tiles from the cast point on each axis. The draw
    uses the sim's misc stream.
  * `delayMs`: the first pulse comes that much after `pulseMs`. `AreaEffect.PulseAccumMs` starts at `-delayMs`, and
    the snapshot check allows that.
  * `moveMs`: every that long the area takes a random step of one tile (−1, 0 or +1 on each axis), if its whole square
    stays on water. The steps follow from the time since the cast, so `AreaEffect` keeps no new state and the snapshot
    layout does not change.
* **Classic values.**
  * Fire rain: amount 40, radius 2 (square), scatter 2, pulse 170 ms, delay 375 ms, 3,775 ms, so twenty pulses. It
    still hurts everyone, the caster's side included.
  * Whirlpool: amount 20, radius 1 (square), pulse 250 ms, move 2 s, 30 s. It hurts every ship, its own side's
    included, as the original does.
* **Casting.** A cast of a wandering area is refused (`InvalidTarget`) where its square is not water.

## Consequences

* The gameplay golden scenario casts neither spell, so only its ruleset hash moves (re-pinned). The other goldens and
  the bench hashes stay.
* Still UNVERIFIED (UNK-26): the cooldowns and cast times, and whether heal casts again while the unit is hurt.
* The client still draws only the cast. It does not show the falling fireballs or the wandering whirlpool; that is a
  presentation task.
