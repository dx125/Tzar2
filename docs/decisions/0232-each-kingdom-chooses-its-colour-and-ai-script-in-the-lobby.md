# 0232. Each kingdom chooses its colour and AI script in the lobby; the host sets every map and game option

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** maintainer agent (rules SHELL-016, SHELL-017, SHELL-018)
* **Related:** SHELL-016, SHELL-017, SHELL-018, AI-001, ADR 0160, ADR 0162, ADR 0163, ADR 0164, ADR 0168, ADR 0182

## Context

The original's multiplayer setup gives each player a colour and an AI script besides the civilization and the team
(manual "Multiplayer Setup Screen"). Since patch 1.05 only the player chooses his own AI script, and everyone is told
when the host changes one (TZAR110.TXT). The settings screen chooses the random map's options, the game type, the
victory condition, the starting resources, No Fog, Explore, the population limit, and day and night and weather. Tzar2's
lobby had none of the colour, the AI script, the game type, the victory condition or the map options, and the lobby
logic sat in the Godot screen.

A kingdom's colour was its kingdom index, so the lobby showed a colour that the match did not use when slots were
left out. One script (`random.ais`, or the engine default) ran every computer player.

## Decision

* **The slot's choice blob** (`SlotChoice`, opaque to the relay) gains the wanted colour (0–7, or 255 for any free
  colour) and the AI script (an `AI.INI` script, or `""` for the default). They are written only when set, so older
  choices keep their bytes. The relay already lets only the slot's own player change a player's choice, and the host
  change a computer's (ADR 0160). That is SHELL-017's "only the player himself".
* **Colours resolve the same way on every peer** (`LobbyDescription.Colours`). Wishes are granted in slot order, and a
  colour two slots want goes to the lower one. The other kingdoms take the free colours in order, so a lobby without
  wishes keeps the old kingdom-index colours. The match description (`MatchPlayer`) and `PlayerSetup` carry the colour
  and the script. The description writes them in a trailing block only when one is set, so replay headers and saves
  without them are unchanged and older ones still parse. Colours are presentation only: not in the sim and not in the
  fingerprint. The client permutes the player ramp texture, the minimap and the kingdom names by them.
* **AI scripts:** `SkirmishAi` is also a library. `Scripts` lists `Random` (`random.ais`) and the `AI.INI` entries
  whose scripts the package has. `ForScript` compiles one on demand and falls back to the default when a script is
  unknown or does not compile. Each kingdom's AI, including a player's standby AI, which plays when he drops or asks
  for assistance, runs its own program. The per-slot program hashes were already in the fingerprint and the replay
  header (ADR 0164), so a peer without the script fails the loading barrier with the existing message. The engine
  default offers only itself.
* **Announcements:** when a computer's script changes in the lobby, every peer posts a system line ("The host set
  Computer 3's AI script to War").
* **`LobbyScreenModel`** (`Tzar2.Game.Ui`, ADR 0182) gives the screen what to offer. Per slot: the resolved colour,
  the colours still free, the script, and whether this machine may change them (its own slot while not ready, or a
  computer as host). The settings: map type, size, terrain, map resources, guarded town, animals, gateways, magic
  items, cliffs, game type, victory, starting resources, population limit, No Fog, Explore, day and night, weather,
  speed, AI Assist, cheats and ranked, each with its choices and what choosing does to `MatchSettings`. It also says
  when START is active. The Godot `LobbyScreen` draws it, with the settings in a scroll panel.

## Alternatives considered

* **Colours chosen by the host, or refused by the relay when taken:** the relay does not read choice blobs (ADR 0160),
  and refusing would need a protocol change; a deterministic resolution gives every peer the same answer for free.
* **One AI script per match:** what Tzar2 had; it contradicts AI-001 and SHELL-016.
* **Compile every script at load:** slower start for no gain; scripts compile when a kingdom first uses one.

## Consequences

* SHELL-016 and SHELL-017 are done. SHELL-018 stays partial: multiplayer has no custom maps (so no MAP SETTINGS), the
  difficulty is per computer slot, and the time and score limits keep their defaults.
* Single player's random map screen can use the same library for SHELL-010 (`SkirmishAi.Scripts`, `PlayerSetup.AiScript`).
* No golden or bench hash changes: matches without choices are byte-identical.
