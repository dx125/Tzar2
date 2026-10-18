# 0144. The naval AI: a harbor, ships and fishing, and landings overseas

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (naval stream)
* **Related:** TDD §16.1 REQ-AI-02, GAME-18, UNK-35, UNK-43, ADRs 0051, 0052, 0140, 0141

## Context

The M3 exit check found that AI kingdoms on Archipelagos never attack: each kingdom has its own island
(ADR 0060), the default script names no ships, and the executors had no naval manager (ADR 0052 deferred it). REQ-AI-02
lists "naval and trade managers". On Island maps the generator connects every kingdom by land, and the AI already
attacked there; the test only skipped the assertion.

## Decision

* **Census**: ships (any mobile unit of the ship locomotor) form their own list and leave the workers and the army,
  so boats are not sent to land nodes and warships do not march with land waves.
* **Naval executor** (built in, runs first; does nothing while every enemy town is reachable by land and the AI has no
  ships, so land maps behave as before):
  * *Harbor*: one, placed by the new coast role: rings around the town up to 40 tiles (8,000 candidates, most rejected
    by a cheap water test), placeable, with the land side of its ring walkable for the builders.
  * *Ships*, one per run, in need order: the transports (default 1) from 240 s before the first wave, the fishing
    boats (2) when fish is within 24 tiles of the harbor, then warships (1) once the transport exists. Transports are
    the cheapest ship that carries; warships the hardest-hitting armed ship. The first transport and the first boat are
    saved for across runs (`Reserve`: the other executors of the run do not spend that stock); the rest are bought from
    spare stock only.
  * *Fishing*: idle boats fish the nearest patch.
  * *Landings*: when the wave's target is in another land component than the wave, the wave boards the transports
    (Board brings each transport to the shore, ADR 0140), then they unload at the target (the ship sails to the dock
    nearest it) — after everyone is aboard or 60 s; a transport still loaded after 60 s is told again. The military
    executor leaves the wave alone meanwhile and takes it on after the landing.
  * *Patrol*: idle armed ships that are not kept for a landing (and the kept transport while no wave is out) attack
    the nearest enemy ship within 30 tiles, else the nearest of the 8 enemy buildings and units nearest to them that a
    water point of their sea is within their range of, else move-advance to the water off the enemy town.
* **State**: `AiPlayerState` version 2 adds the naval phase, its start tick, the landings count and the transports
  (at most 16; none outside a landing) and the last look whether an enemy is overseas (every 10 s); the system state
  version is 3; `NavalOptions` are validated and part of the
  options fingerprint.
* `HumanVsAiTests` (#36) asserts the AI's hits on the human wherever the castles are reachable by land or across a
  shared sea (its row attribute now accepts ship-reachable castles); Archipelagos plays 12 minutes there, since a passive
  human keeps nothing in a ship's range and the first landing comes a little after 9.
* `AiRandomMapTests` asserts damage on all six map types again and no ships on land-connected ones; `AiNavalTests`
  asserts landings, fishing and soldiers standing on the other island within 12 minutes on Archipelagos, a snapshot
  in the middle of a landing, and rejection of corrupt naval state.

## Consequences

* On Archipelagos the AIs fight at sea within 8 minutes and land their waves within 12; the first transport costs
  them some early growth (UNVERIFIED tuning).
* UNVERIFIED (RE-07, UNK-35): all naval tuning, the reservation, the patrol, and when the original builds ships.
