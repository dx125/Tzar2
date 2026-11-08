# 0284. The map's own pictures, sounds and music reach the game

* **Status:** Accepted
* **Date:** 2026-11-08
* **Deciders:** maintainer (Claude), at the owner's request (the editor review)
* **Related:** SCN-003, SCN-004; TDD §18.2 "User resources", "Custom sounds & music"; ADR 0121, ADR 0151; ReTzar
  `src/gameplay.rs` (`map_sounds`), `src/frontend/gameplay.rs`

## Context

The editor stores a map's own resources under `user/` in its package. The converter extracts them from the original's
maps, and the scenario names them:

* the pictures, sounds and music of scrolls and story screens;
* the voices of phrases;
* the sound sources and the music playlist;
* each placed object's 44 × 44 and 96 × 96 icons and its title.

The game used none of them:

* It looked for pictures in the base package (`maps/user/…`), where they never are.
* It played no scenario sound, voice or song.
* It never ran the sound sources.
* It ignored the map's playlist, the icons and the titles.

The original campaigns from the install were converted without their pictures.

## Decision

* **Carrying the resources.** `MapAssets` holds a map's resources by the path its scenario names them with,
  case-insensitively, also found under `user/`, with a campaign's shared resources after the map's own. It travels with
  `MissionContent` into `MatchSetup.MapAssets`. It is presentation only: never hashed, never in the simulation. The
  sources are:
  * the editor's playtest, from the document;
  * a campaign playtest, with the campaign's shared resources;
  * the converter's playables (`PlayableLegacy.Resources`), whose pictures the client now converts too;
  * the import window's "Play converted campaign".
* **The client:**
  * shows the pictures, decoded from PNG, BMP or JPEG;
  * plays scroll sounds and phrase voices (`SoundMixer.PlayMapSound`);
  * plays a scroll's music while its window is open;
  * plays the map's MIDI playlist shuffled, song after song, and the package's song without one;
  * shows a placed object's icon and "name title" in the selection view.
  
  A phrase without a picture of its own shows its speaker's large portrait.
* **Sound sources.** `MapSoundScheduler` (in `Tzar2.Game`, engine-free and tested) runs each source on its own timer, as
  ReTzar reads the original:
  * every so often, looped, at random times between two, or once after a delay;
  * a source still sounding waits a second;
  * a source whose object left the game stops;
  * a source not appeared yet is not heard.
  
  Times are game time. The client's own random numbers draw the random intervals, so the simulation stays deterministic.

## Alternatives considered

* **Mounting each map's package into the content VFS:** paths would collide between maps and campaigns, and the
  converter's playables have no package at all.
* **Real time scaled by the speed, as the original:** sound sources would run on during a pause. Game time follows the
  play; the difference is small at normal speed.

## Consequences

* Story screens show their pictures. Heroes speak with their voices and show their portraits. A map's sounds and music
  play.
* OGG music is not played yet; the client's music player plays MIDI only.
* Converting the original campaigns at the campaign screen now also converts their pictures (a few dozen BMPs).
