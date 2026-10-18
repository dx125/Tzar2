# 0106. Evaluate victory in the sim with allied victory; count statistics from events

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 stream F)
* **Related:** TDD §8.5 step 16, §10 GAME-17, UNK-29, ADRs 0070, 0100, 0102

## Context

Lobby and editor: victory conditions Standard, Destroy Castle(s), Score Limit, Time Limit, Not Specified (editor manual
§8.3); game types Standard and Deathmatch (manual ch. 3; the multiplayer strings also list King of the Hill and Capture
the Flag); allied victory; F4 scores and a statistics file. The session used a placeholder rule (no units and no
buildings = defeated, ADR 0070). The score formula is UNK-29.

## Decision

* **Statistics** (`KingdomState`, hashed every tick) are counted in step 16 from the tick's events: gathered resources
  (per type and total), units trained, buildings completed (and their cost as built value), technologies, captures,
  units and buildings lost, enemy units killed and buildings destroyed (and their cost as kill value; `Killed` carries
  the killer's owner), trade income, tributes sent and received.
* **Score** = gathered / 10 + kill value / 10 + built value / 10 + 100 per technology (UNVERIFIED, all in `world:`).
* **Defeat** (every `victoryIntervalTicks`): Standard, Score and Time Limit — no unit and no building left (walls, gates,
  bridges, fields and ambient life do not count); Destroy Castles — no castle. Resign defeats at once. A defeated
  kingdom's commands are refused; its remaining entities stay.
* **Victory**: teams are kingdoms sharing victory (mutual allies who both offer it, ADR 0102). Standard / Destroy
  Castles: the remaining kingdoms form one team. Score Limit: the first team whose summed score reaches the limit. Time
  Limit: at the limit, the highest team score (ties: the lower slot). Not Specified (the sim default): never; scenario
  triggers (M4) and `hmnext` call the same `Win`. A one-kingdom match never ends by itself. Winners get `Victorious`,
  the rest `Defeated`; commands then stop.
* **Deathmatch** starts every kingdom on `world.deathmatchStartingResources` (classic: High; UNVERIFIED).
* The session's outcome follows the local kingdom's flags; F4 shows every kingdom's score and statistics.

## Consequences

* King of the Hill and Capture the Flag are not implemented (open question); the statistics file format is M6 work.
