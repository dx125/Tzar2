# 0187. Save games of every single player game, by name, in the user folder

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer)
* **Related:** ledger SHELL-014, SHELL-021, SHELL-022; ADR 0133 (mission saves), ADR 0162 (match descriptions);
  REQ-NET-10

## Context

ADR 0133 saved campaign missions only, into one quick-save slot (F2), loaded by F3 and by LOAD GAME. The original
works differently:

* F2 opens a save dialog (`D_SAVE`: NEW, SAVE, DELETE, CANCEL; STRINGS `[save]`).
* F3 opens a load dialog (`D_LOAD`: LOAD, DELETE, CANCEL). Its control switches between single player games,
  campaigns and demos (STRINGS `[load] type_0..2`).
* The in-game menu also has LOAD GAME and SAVE GAME.

Random maps and custom maps could not be saved at all.

## Decision

* **The save file.** `SaveGame` format 2 adds a `Kind`, and the setup that rebuilds the match around the snapshot:
  * **Campaign:** as before, the campaign, the map and the progress.
  * **Custom map:** the package map's id and the custom setup: difficulty, civilization, starting resources,
    population, No Fog, Explore. The AI script is the loading client's.
  * **Skirmish:** the match description (`MatchDescriptor`, ADR 0162). A match whose map cannot be described cannot
    be saved, and the dialog says so.

  Format 1 files still load as campaign saves.
* **The library.** `SaveLibrary` keeps the saves as `<user>/saves/<name>.t2sav`, listed newest first.
  * A name is letters, digits, spaces, `-` and `_`, at most 40 characters, so it is always a plain file name.
  * A file is written to `.tmp` and then moved into place.
  * Unreadable files are not listed. ADR 0133's `quick.t2sav` shows as "quick".
* **The screens.** The models are `SaveGameScreen` and `LoadGameScreen` (ADR 0182); the client draws them on `D_SAVE`
  and `D_LOAD`.
  * F2, F3 and the in-game menu's SAVE GAME and LOAD GAME open them over the paused match.
  * Single player's LOAD GAME opens the load dialog.
  * The load dialog shows the single player games, or the campaign saves first during a mission.
  * Loading stops the running match and continues the save, of whatever kind.
* **Multiplayer.** Matches are not saved there (no capture function is given).

## Alternatives considered

* **Keep one quick slot per kind.** The original names its saves, and players expect several.
* **Store the whole `MatchSetup`.** It holds generated maps and code objects (AI programs). The match description
  already rebuilds random maps exactly (ADR 0162), and custom maps rebuild from the package.

## Consequences

* Restarting a match continued from a save loads that save again, since its first options are not kept.
* The list reads each save file whole, snapshots included. That is fine for tens of saves; a separate header index
  can come later if needed.
* The demo type of the original load control is not offered (there are no demos to save).
