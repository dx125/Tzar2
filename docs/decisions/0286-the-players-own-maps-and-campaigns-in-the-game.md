# 0286. The player's own maps and campaigns in the game; no editor work lost

* **Status:** Accepted
* **Date:** 2026-11-10
* **Deciders:** maintainer (Claude), the owner's answers of the editor review (2026-11-08)
* **Related:** SHELL-018; TDD §18.3, §18.4; ADR 0120, ADR 0133, ADR 0150, ADR 0153, ADR 0233; STATUS item 26.5

## Context

The editor review found gaps that lose the player's work or make it unusable in the game:

* **Maps and campaigns made in the editor could not be played from the game.** They ran only as a playtest.
  * CUSTOM MAP offered only the original maps converted from the package.
  * The campaign screen offered the original campaigns, the demo, and a campaign converted in this run.
  * The multiplayer lobby already found custom maps by their content hash (ADR 0233), but its list was CUSTOM MAP's.
* **Unsaved work could be lost.** New, Open, Close editor and the window's close button dropped unsaved changes without
  a word, and there was no autosave.
* **The Lua tab** edits scripts that the game does not run before M8, and nothing said so. The owner decided to keep
  the tab, with a warning.
* **The map's difficulty** was an open question: what it means in the original.

## Decision

* **The player's library.** `UserLibrary` (in `Tzar2.Game`, engine-free and tested) reads the `.t2map` and `.t2camp`
  files at the top of three user folders: `maps`, `campaigns` and `imported` (the converter's output).
  * A map's id is its path there without the extension, such as `maps/ford`. It never collides with an original map's
    id, which is the bare file name. Saves of a custom map find it by this id.
  * A file that does not read is reported to the log and skipped. So is a map with a blocking finding.
  * A campaign's maps are built when its mission starts, as in a campaign playtest.
* **The menus:**
  * CUSTOM MAP offers the original maps (converted once per run), then the player's.
  * The campaign screen adds the player's campaigns.
  * Both read the folders again each time they open, so a map just saved in the editor is there.
  * The lobby's list is CUSTOM MAP's. As the owner decided, a custom map plays in multiplayer only where every player
    has the file: the lobby already tells a player who lacks the map, or has another version of it (ADR 0233).
    Sending the map is left for later.
* **The editor's dialogs** start in those folders: maps in `maps` (or the open file's folder), campaigns in
  `campaigns`.
* **No work lost:**
  * New, Open, Open autosave, Close editor and the window's close button first ask about unsaved work: Save,
    Don't save or Cancel. This covers a standalone map and the open campaign with its maps.
  * Closing the campaign window asks about the campaign.
  * `EditorAutosave` writes unsaved work every 3 minutes of editing, if it changed since the last autosave. It writes
    to the user folder `autosave`, never over the player's file. File > Open autosave opens one as a new, unsaved map.
* **Lua:**
  * The Lua tab says that the game does not run the scripts yet.
  * The validator warns about a map that has scripts (`script.notrun`).
* **The map's difficulty** is what the original's custom-map setup starts with under USE MAP SETTINGS (ReTzar's
  reading of the header byte 0x50). Tzar2's CUSTOM MAP already starts from it, so nothing changes.
  * ReTzar reads the value 3 as "fixed by the map", at easy.
  * The original's maps and campaign maps use only 0-2: one easy, 31 normal, one hard, counted on the owner's
    install.

## Alternatives considered

* **A folder chooser in the game's menus:** more freedom, but every menu and the lobby would need it. The default
  folders cover the editor's own work and the converter's output.
* **Autosave over the player's file:** it would save changes the player did not ask to save.
* **Removing the Lua tab until M8:** the owner chose to keep it, with the warning.

## Consequences

* A map or campaign saved in the editor plays from the game's menus at once. A map plays in multiplayer where every
  player has the same file.
* An editor crash or an unexpected close loses at most 3 minutes of work.
* Custom maps in multiplayer still need the file on every machine. A chunked transfer from the host can come later
  (ADR 0233).
