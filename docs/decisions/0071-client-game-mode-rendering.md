# 0071. Draw the skirmish with the viewer's renderer pieces, a presentation catalog from the imported tables, and isolated placeholders

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 stream D)
* **Related:** TDD §13 (REQ-GFX-01…07), §13.5–§13.8, §14 (REQ-AUD-01/02), §15.1; DEC-05, DEC-07, DEC-10, DEC-11;
  UNK-03, UNK-10, UNK-27, UNK-32, UNK-33, UNK-40; ADR 0010, 0013, 0020, 0070

## Context

The game mode must look like the original from the user's imported package only (DEC-05, DEC-07): sprites through the
ADR 0010/0013 path, terrain from the terrain sets, the HUD and dialogs from `IMAGES/SCREENS`, sounds and music. Several
rules of the original are still unknown (terrain placement UNK-10, fog look UNK-27, night rules UNK-32, sound
attenuation UNK-33, cursor hotspots UNK-40).

## Decision

1. **One client, modes (DEC-11).** Without flags the client opens the game (main menu → skirmish setup → game →
   results); `--viewer`, `--smoke` and `--capture` keep the M1 asset viewer. The game runs at the original's logical
   1024×768 (`LAYOUT1024H`) with the window's content scale in integer mode (REQ-GFX-07 "Authentic").
2. **Presentation catalog** (`Logic/PresentationCatalog`, engine-free and tested): ruleset type → object INI section of
   its class and civilization (`image`, `image_idle`, `image_attack`, `image_die`, `image_chop`, `image_mine`,
   `image_dig`, `image_build`, `menu`, `icon`, sound sets), names from `STRINGS.INI [objects]`, command cards from
   `MENU.INI` + `ORDERS.INI` (actions classified into what the sim supports; the rest shown disabled), `ICONS.INI`
   cells, `SOUNDS.INI` sets/weights/priorities and the `KEYS.INI` building classes. No original value is copied into
   code.
3. **Sprites.** `SpriteBank` decodes the entries the match's civilizations use (REQ-GFX-05) plus construction stages
   (`build1x1`…`build8x8`) and the terrain set's tree strip into one `SpriteAtlas`/`GpuSpriteAtlas`; `WorldRenderer`
   draws through two `SpriteRenderer`s (ground decals: corpses, fields; objects: y-sorted by foot line) with `_s`
   shadows and `_c` player-colour overlays, plus a third at 45 % opacity for translucent trees. Units are placed by the
   IMAGES.INI `object` box centre, structures by their footprint top-left minus the `object` offset. Animation: walk,
   idle, attack (phase from the frame's swing time), chop/mine/dig/build, death (corpse age, last frame held); 8-way
   direction from the sim facing (`SpriteRules.Direction`). Positions are interpolated and snapped to whole pixels.
4. **Terrain** is composed per 16×16-tile chunk on the CPU by the viewer's `TerrainComposer` (a new origin overload
   keeps base tiles and variants in map coordinates) and drawn by `Rgb555Surface`s with all water/shore frames
   (`WATERFPS` 7). Every UNK-10 assumption of mapping sim terrain to set terrains (water → 0, shore ring → the set's
   water-transition terrain, land → its first grass, a second grass in hashed patches away from the shore) is in
   `TerrainPresentation`, which guarantees no tile needs three terrains.
5. **Fog and night** are one shader quad over the map: per-tile fog (unexplored black, explored 50 %, linear-filtered
   edges; ⚠ UNK-27) and night darkness by a placeholder day clock (12 min, ⚠ UNK-32) with light discs around own units
   and buildings modulated by the set's `LIGHT.BMP`. The clock globe shows `DAYNIGHT.BMP` by hour.
6. **Weather:** screen-space rain from `ANIMS/RAIN.BMP` and `RAINDROPS.BMP` (density/speed placeholders).
7. **Selection marks and health bars** (⚠ the original look is not measured): an ellipse under units, footprint corners
   on structures, a three-colour bar above the selection box; construction progress under it.
8. **Audio:** `SoundMixer` with 32 voices (`MIXERCHANNELS` default), `SOUNDS.INI` priorities (a full pool drops the
   lowest), weighted variation, category toggles and distance attenuation from the screen centre (no panning,
   ⚠ UNK-33); events → sounds: trained (command voice), completed (building select), hits (attacker's attack set),
   deaths, selection and order acknowledgements, work sounds of visible workers. Music: MIDI through the existing
   MeltySynth player on a `Music` bus (intro in the menu, a track per match, win/outro on the results screen); still
   only with a user SoundFont (ADR 0020).
9. **Cursors** from `IMAGES/CURSORS` (⚠ UNK-40 hotspots: hand top-left, others centred).

## Alternatives considered

* **Godot nodes per entity / AnimatedSprite2D:** against DEC-10 and the M0 spike numbers.
* **GPU terrain chunks with tile UVs:** still premature while UNK-10 is open; CPU composition is exact and cheap at
  match load (a 96² map composes in well under a second).
* **Baked fog bitmaps (`FOG_TRANS.BMP` edge masks):** likely the original's look, but the mask indexing is unknown;
  linear filtering is the honest placeholder until UNK-27 is measured.

## Consequences

* When UNK-10, UNK-27, UNK-32, UNK-33 or UNK-40 are resolved only `TerrainPresentation`, the fog shader constants,
  `GameScreen.Darkness`/`DayLengthMs`, `SoundMixer` or `CursorCache` change.
* MIDI rendering still runs on the main thread (`_Process`, 0.5 s buffer); moving it to a streaming thread (§7.3) is a
  follow-up.
