# 0168. Multiplayer menu, lobby, in-game overlay and replay viewer in the client, on Game-layer models only

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (M6)
* **Related:** REQ-NET-03, REQ-NET-05…10, REQ-UI-02, §19.3-19.5, G-01, ORIG-06, ADR 0073 (client contract), ADR 0162, ADR 0164, ADR 0166

## Context

The client may use only Game, Content, Import and the contract namespaces (§7.2, ADR 0073); the compiled client is
checked type by type. Multiplayer needs: the internet lobby (create, join by code, the relay's public list), the LAN
browser and host, the lobby itself (slots, civilizations, teams, ready flags, host settings, chat), in-game chat with
recipients, the player list with pings, the drop-policy wait vote, save consent, reconnect after a crash, and the
replay viewer (seek, speed, perspective).

## Decision

* **Game-layer models only**: `NetworkMatch` and its views (ADR 0162), `LanHost`, `LanBrowserService`,
  `LobbyDirectory`, and `ReplayPlayback`, a `GameSession` whose ticks come from a replay at 0.25x-16x, with pause,
  seek, perspective and hash checks. The client never sees a Net, Sim or Replay type; the architecture test of the
  compiled client enforces it (it caught a `World` access in the viewer during development).
* **Screens** (code-built Godot controls over the original screen art where it exists):
  * `MultiplayerMenuScreen`: name, relay address, create, join by code, the internet list, host LAN, the LAN list,
    reconnect (the last match's ticket, kept in `user://multiplayer.cfg`), replays.
  * `LobbyScreen`: slot rows with colour, player or computer, civilization, team, ready flag and ping. The host
    opens, closes, kicks and fills slots with Easy/Normal/Hard computers, and sets map type, size, terrain, speed, the
    dropped-player rule, no fog, cheats and ranked (ranked turns cheats off). The screen also has lobby chat, READY,
    START and LEAVE. Victory stays Standard for now (its enum lives in Sim).
* **Loading barrier with assets**: the client reports "loaded" (`NetworkMatch.ReportLoaded`, `DeferLoadReport`) only
  after the game screen has loaded sprites and sounds, so the first tick starts when every machine can show it
  (REQ-NET-05, ORIG-06).
* **`MultiplayerOverlay`** over the game screen:
  * chat log and chat line: Enter opens it, Tab cycles all, allies and enemies, Esc closes it, and the HUD chat
    button opens it too;
  * `hm*` codes go to the sim like in single player and are refused unless the host allowed cheats;
  * player list with ping, controller (human, computer, dropped with seconds, neutral, defeated) and the local input
    delay;
  * prompts for a dropped player (WAIT LONGER), a save proposal (AGREE/REFUSE; F2 proposes one, host only) and the
    end of the match (LEAVE).
* **Replays**: every skirmish and multiplayer match is written to `user://replays` when it ends or is left (REQ-NET-09).
  `ReplayBrowserScreen` lists them. The viewer is the game screen over the playback session with `ReplayControls`:
  play/pause, speed −/+, a seek bar (forward seeks fast-forward, backward ones restore a keyframe into a new session and
  the screen is rebuilt), the view (everything, or any kingdom's fog), the time, and a desync warning if the replay no
  longer matches. Viewers' clicks issue no commands.
* The existing screens change only where they hand over: the main menu's MULTIPLAYER button, saving the skirmish replay,
  and the chat button in multiplayer. The new screens are new files (the editor work happens in the same client).

## Alternatives considered

* **Re-create the original multiplayer dialogs from their bitmaps now:** their layouts are not measured yet (RE-09,
  UNK-15); functional controls first, the authentic dialogs with the rest of REQ-UI-02.
* **A separate replay screen with its own renderer:** the game screen already draws frames from a session; a replay is
  one more kind of session.

## Consequences

* Not yet: map transfer through the relay (scenario maps in multiplayer), spectator slots (G-08), observer stats overlay,
  the original's chat-recipient dialog, location pings (Ctrl+Enter), authentic dialog layouts.
* G-01 (immediate local feedback for orders under input delay) stays with the game screen's existing order markers.

## Amendment (2026-10-23): the host's choice for a computer goes to the relay

`NetworkMatch.SetComputerChoice` used to send nothing unless the host's own view of the lobby showed a computer in the
slot. That view can lag one broadcast behind, for example when a player joins while the host adds a computer, so a
host's click could be lost without a trace (issue #190). The client now sends the host's choice, and the relay checks
the slot: it already refuses a wrong one with a notice, which the host sees as a system line.
