# 0291. The editor's pickers, recent files and playtest recordings

* **Status:** Accepted
* **Date:** 2026-11-13
* **Deciders:** maintainer (Claude), at the owner's request (the rest of the editor review's section 3, 2026-11-13)
* **Related:** TDD §18.2, §18.3; ADR 0016, ADR 0153, ADR 0168, ADR 0284, ADR 0285, ADR 0286, ADR 0290; STATUS item 26.9

## Context

The owner asked for the editor review's section 3 proposals not chosen on 2026-11-11. This batch covers three of
them:

* **Picks instead of typing.** Several fields of the editor were typed by hand:
  * an entity type key, an order key, a next map's name;
  * a sound source's point;
  * the paths of an object's icons, of a scroll's picture, sound and music, of a custom sound, and of the music
    playlist.

  Objects were chosen by selecting them first and then pressing "Use selected". A sound or a picture could not be heard
  or seen before it was used.
* **Recent files.** The editor forgot the last files between sessions.
* **A playtest as a file.** A map maker who finds a bug in a playtest cannot hand it to someone else. Replays (ADR 0016,
  ADR 0168) carry a description of the match; a scenario map cannot be described that way, and a playtest's map is the
  editor's document, perhaps never saved. The playtest's rewind (ADR 0285) also replaces the session, so the match's own
  recording ends at the first rewind.

## Decision

* **Pickers** (`Pickers` in the client; models in `Tzar2.Content.Editing`):
  * a resource of the map is chosen from the map's own resources of its kind, with a thumbnail for a picture and a play
    and a stop button for a sound or music (`AssetPreview`: WAV and OGG; MIDI through the player's SoundFont);
  * a type key is typed, picked from the palette by words of its key or legacy class (`EditorPalette.Search`), or taken
    from the selected object;
  * an order key offers the orders Object Order tells apart (`TriggerSchema.OrderKeys`: move, attack, patrol, stop,
    default), and a next map the names of the campaign's maps (`TriggerSchema.MapNames`); any other value can still
    be typed;
  * "Pick on map" waits for one click: an object for a holder, an object area, a named object list (added to it), a
    sound source and the references to replace; a tile for a sound source's point. Esc cancels;
  * the music playlist is a list of the map's music with a check box and a play button each.
* **Recent files.** `RecentFiles` keeps the twelve last maps, campaigns and recordings opened or saved, newest first,
  in `editor-recent.txt` of the user data. File → Open recent opens each with its own editor; a file that is gone
  leaves the list.
* **Playtest recordings** (`PlaytestRecording`, `PlaytestRecorder` in `Tzar2.Game.Editing`):
  * the recorder starts with the playtest's first tick and keeps every tick's command set and hashes. It follows the
    playtest's rewinds: a rewind drops what was recorded after its point. The recording is the timeline the tester
    ended on, and it replays exactly, since the restored match plays as the original did;
  * a `.t2play` file holds the map package as it was played (later edits are not in it), the playtest's settings and
    the replay. The map rebuilds the match, so the replay's header needs no setup description. A replay whose map hash
    or ruleset differs is refused;
  * Play → "Save the last playtest as a recording…" writes it (to the user folder `recordings` by default); "Watch a
    playtest recording…" shows it in the replay viewer (speeds, seeking, a kingdom's view). The editor then comes back
    with the recorded map as a new, unsaved document;
  * a campaign played from the campaign editor is not recorded this way: its next map depends on what the campaign
    carries over.

## Alternatives considered

* **A replay header that describes scenario maps:** the header's setup is capped at 64 KiB, and a playtest's map may
  never have been saved.
* **Re-simulating the playtest from a command log at save time:** it needs the same log and takes seconds for a long
  playtest; the recorded hashes already prove the timeline.
* **A free-text field with a separate list for every pick:** the suggestions sit next to the field, so typing stays
  possible for keys the list does not know.

## Consequences

* A bug in a scenario reaches another person as one file that shows it happening.
* Fewer fields of the editor take typed paths, numbers or keys.
* The client's playtest keeps a command log beside the rewind's snapshots: a few bytes a tick.
