# 0233. Play custom maps in multiplayer from every peer's own copy, found by its content hash

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** maintainer agent (rule SHELL-018)
* **Related:** SHELL-018, SHELL-007, ADR 0120, ADR 0133, ADR 0160, ADR 0162, ADR 0164, ADR 0232

## Context

The original's multiplayer settings choose a random or a custom map. For a custom map, USE MAP SETTINGS keeps the
map's own settings, and only No Fog, Explore, day and night and weather change (manual "Multiplayer Settings Screen").
Its value field types the time or score limit (`MPS_VALUE`). Tzar2's lobby offered only random and test maps.
`MatchSettings` could not name a custom map, and the match description of a scenario map was empty, so neither replays
nor desync reports could rebuild one.

A custom map is large (terrain, objects, a scenario with triggers). Every player of the original game has the original
maps; community maps are files that players exchange.

## Decision

* **`MatchSettings` format 2** adds the custom map (`MapSourceKind.CustomMap`): its id (shown), its content hash
  (`MissionContent.ContentHash` = map hash + scenario hash) and USE MAP SETTINGS. Settings without a custom map are
  still written as format 1, so lobbies, replays and saves without one keep their bytes.
* **Found, not sent:** every peer looks the map up by its hash among the maps it has (`NetworkMatchOptions.CustomMaps`;
  the client gives the package's original maps, converted as for CUSTOM MAP). The lobby model tells a player who lacks
  the map, or has another version of it, and does not let him be ready. If he starts anyway, his load fails with the
  same message and the loading barrier stops the match. The map hash and the scenario are also in the fingerprint
  (ADR 0162).
* **Kingdoms:** lobby slot k plays the map's kingdom k. An open or closed slot's kingdom stays on the map, played by
  nobody (`ControllerKind.Passive`). A player or computer in a slot beyond the map's kingdoms blocks START. The map's
  civilization stands until a player chooses another (SHELL-016). The map's Rescue kingdom is added as in single player
  (ADR 0133).
* **Settings:** `ScenarioMatch.Multiplayer` builds the match like single player's custom map, with the lobby's
  kingdoms, seed, teams, colours and scripts. With USE MAP SETTINGS (the default) the map keeps its victory, game type,
  population limit and starting stockpiles. Without it the lobby's apply. No Fog, Explore, day and night and weather
  always come from the lobby; choosing a map starts them from its own No Fog and Explore.
* **Replays and reports:** the description carries the settings with the map's hash, so `ReplayPlayback.SetupOf` and
  `MatchDescriptor.ToSetup` rebuild the match when given the maps. Without them they fail with a message that names
  the map. The client's replay browser passes its maps.
* **Limits:** the time limit (minutes, 1–10,000) and the score limit (1–100,000,000) are typed values
  (`LobbySetting.IsNumber`), enabled for their victory condition.

## Alternatives considered

* **Send the map through the relay:** works for maps nobody else has. But the relay would carry and store hundreds of
  kilobytes per lobby against its byte budgets (ADR 0160), and every original map is already on every machine. It is
  left for community maps: a chunked transfer from the host, checked against the same hash, can be added without
  changing the settings format.
* **Find maps by name:** two different files with one name would desync; the hash cannot.
* **Compact the kingdoms as on random maps** (the occupied slots in order): the map's kingdoms have places, triggers
  and diplomacy tied to their numbers, so slot k must be kingdom k.

## Consequences

* SHELL-018 is done except that the difficulty is set per computer slot rather than once.
* `t2 replay verify` and the soak verify random and test maps; a custom-map replay needs the maps (the CLI does not
  load them yet).
* Follow-up: map transfer for community maps (with the corpus, M4/M8).
