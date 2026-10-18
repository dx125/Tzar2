# 0104. Claim gateways and wild animals by touch; Nature's units wander and guard their home

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 stream F)
* **Related:** TDD §10 GAME-15, ADRs 0042, 0060, 0100

## Context

Manual ch. 5 §4.4.2: "Magic Gateway – becomes yours once you have found and touched it with a military unit. Allows the
summoning of dwarves, orcs, ghosts and skeletons." COMM UNITS: Ork and Dwarf are built at "Gate" for gold, Ghost and
Skeleton at "Spawn"; MENU.INI `[magic_gateway]` creates all four. Random maps place gateways, bears, wild cows, fish and
guarded towns as Nature's (ADR 0060). Birds and eagles exist only as objects (`teren = 0`).

## Decision

* **Claims** (step 6, every `claimIntervalMs`): a Nature-owned entity with `ClaimOnTouch` changes owner to the kingdom
  of the nearest unit within `claimReachPx` edge to edge (gateways: `by: military`, wild cows: `by: any`); ties to the
  lower distance, then the earlier unit. Its queue appears with the new owner (`ChangeOwner`). Unclaimed gateways and
  wild cows are never combat targets; a Nature-owned claimable may be a producer on the map.
* **Summons.** Classic maps the producer column "Gate" to `magicgateway`; ghosts and skeletons are made producible there.
* **Roaming** (`Animal { wanders, guards }`): Nature's units with it get a home at match start. Wanderers move to a
  point within `wanderRadiusTiles` of home every `wanderIntervalMs` on average, hashed from the match seed, entity and
  tick (no RNG stream consumed); guards (bears) that get further than `guardRadiusTiles` drop their fight and walk home.
  Bears fight anyone near (Nature is everyone's enemy). Birds are `ambient`: never targets, never counted for victory.

## Consequences

* Guarded towns' towers already fire at kingdoms (Nature's armed buildings).
* The Evil Castle and the Messiah are campaign content (M4).
