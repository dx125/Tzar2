# 0020. Build the M1 asset viewer as a client mode on the production sprite path; play MIDI with MeltySynth and a user-supplied SoundFont

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M1 viewer)
* **Related:** TDD §7.2, §12.3, §13.2–§13.5, §14 (REQ-AUD-01/02), §25 M1; DEC-07, DEC-09, DEC-10, DEC-11; REQ-GFX-01,
  REQ-GFX-03, REQ-GFX-04; UNK-01a, UNK-03, UNK-10, UNK-39; ADR 0010 (renderer), 0011 (package map), 0013 (sprite
  layout); [docs/formats/bmp.md](../formats/bmp.md), [t2pkg.md](../formats/t2pkg.md); [tools/soundfont](../../tools/soundfont/README.md)

## Context

M1 asks for a dev asset viewer: all sprites, animations and directions in the eight player colours with shadows;
terrain sets with transitions and water animation; fonts; sounds; MIDI music. DEC-11 makes tools modes of the one
Godot client, and the viewer is the first real consumer of the renderer decided in ADR 0010/0013, so it should exercise
that path rather than a throw-away one. Two parts of the data are not fully understood: terrain placement rules
(UNK-10) and the shadow blend (UNK-03). MIDI needs a synthesizer and a General MIDI SoundFont; REQ-AUD-02 names
MeltySynth and asks to verify a SoundFont's license before bundling one.

## Decision

1. **Client mode.** The viewer is the M1 mode of `Tzar2.Client` (`src/Viewer`). The package comes from
   `--package <path>` or a file dialog and is remembered in `user://viewer.cfg`. Without a package (or when it cannot be
   opened) a first-run screen explains `t2 import` and offers the dialog (the import wizard stays a later milestone).
   `--smoke` exercises every page and exits (headless test); `--capture <dir>` writes a fixed screenshot set.
2. **Sprites use the ADR 0010/0013 path unchanged:** resident sheets are packed at load time into 2,048² pages per GPU
   format (`SpriteAtlas`), uploaded as `Texture2DArray`s (R8 indices, RG8 RGB5A1 words, RGBA8, R8 masks), and drawn as
   instanced quads by `RenderingServer` canvas MultiMeshes in CPU radix-sorted order. One shader handles all formats:
   each instance carries a cell index into a cell table texture (two RGBA32F texels: page rectangle; layer, format,
   player range, baked-shadow colour), so mixed formats (a building and its indexed `_c` overlay) share one sorted
   MultiMesh. Palette lookup, player-ramp recolouring (indices 240–255 → `GAME.PAL` ramps) and RGB555 bit-replication
   expansion happen in the shader with `texelFetch` (nearest, integer zoom, whole-pixel positions).
   `Tzar2.Content.Assets.SpriteShading` is the tested CPU reference of that shader.
3. **Shadows:** kind-7 shadow sheets (`_s`, `_shadow`, aligned with the colour cell's top-left; they have the same cell
   size) and, optionally, the importer's baked-shadow hints of unit sheets are drawn as white silhouettes into a
   `SubViewport` mask and composited once at 50 % black. The factor is a placeholder until UNK-03 is measured; the
   viewer has toggles for shadows and for treating baked hints as shadow.
4. **Terrain is composed on the CPU** by `Tzar2.Content.Terrain.TerrainComposer` into lossless RGB555 frames
   (uploaded as RG8 layers, expanded by the same bit replication), on a corner-grid demonstration map that contains all
   14 transition shapes. The production terrain renderer (32×32-tile chunk meshes, §13.5) is M2 work; the viewer only
   needs a faithful picture of the bitmaps and the known rules. Every rule that rests on UNK-10 is a named, isolated
   assumption, listed in the composer's documentation and in the viewer panel: corner storage, base tile
   `(x mod cols, y mod rows)`, magenta = lower terrain, variant hash, water/shore frame synchronisation (cycle = least
   common multiple), and the cliff pseudo-terrains 8/9, which are shown as raw bitmaps only.
5. **Fonts** are drawn 1:1 from the imported glyph strips (coverage → alpha; FIXSHAD with its keyed palette).
   **Sounds** are played from the package's PCM WAVs (`WaveData`, lossless).
6. **MIDI: MeltySynth** (MIT, pure C#, NuGet `MeltySynth` 2.4.1) renders into an `AudioStreamGenerator`. It is allowed
   for `Tzar2.Client` only (ADR 0011 map extended in `DependencyRulesTests`).
7. **No SoundFont is bundled.** The path is user-configurable (`--soundfont`, the Music page's dialog, `TZAR2_SOUNDFONT`).
   For development, `tools/soundfont/fetch-soundfont.{sh,ps1}` downloads **GeneralUser GS v2.0.3** (GeneralUser GS
   License v2.0: free use, modification and redistribution, including in software) from the author's GitHub repository
   at a pinned commit with a SHA-256 check into the git-ignored `soundfonts/`, which the client also probes. Without a
   SoundFont the Music page says so and playback is disabled; nothing else depends on it.
8. Godot-free client logic (`src/Tzar2.Client/src/Logic`: argument parsing, radix sort) is compiled into
   `tests/Tzar2.Client.Tests`, which also runs the client headless on a generated package when a Godot 4.7 .NET binary
   is available (`TZAR2_GODOT`); CI has none yet, so there the smoke test is skipped.

## Alternatives considered

* **A separate viewer project/app:** contradicts DEC-11 and would not test the production renderer.
* **One MultiMesh per pixel format (simpler shaders):** breaks painter's order between a building and its overlay.
* **Per-instance UV/layer in `INSTANCE_CUSTOM` + instance colour:** 4 floats are not enough for layer, rectangle,
  palette row and player; a cell table keeps the instance at 12 floats (ADR 0010's stride) and is cheap to sample.
* **Terrain as GPU tile chunks now:** premature before UNK-10 is resolved (the per-cell variant and storage are
  unknown); the CPU composer is exact and testable, and the M2 chunk renderer can reuse its tile rules.
* **Bundling GeneralUser GS:** its license permits it, but the author cannot guarantee every sample's origin and
  REQ-AUD-02 asks for a verified license; a user-supplied SoundFont keeps builds free of that question. FluidR3_GM (MIT,
  141 MB) was the other candidate; GeneralUser GS is a quarter of the size and is the TDD's named candidate.
* **TinySoundFont / FluidSynth:** native code; MeltySynth is pure C# and AOT-friendly.

## Consequences

* The viewer is a pixel-level check of the renderer: screenshots of units at zoom 1 and 2 compare equal to
  `t2 dump` (the `SpriteCompositor` reference) on every opaque pixel, and composed terrain base tiles equal the bitmaps.
* When UNK-03 or UNK-10 is resolved, only the composite factor, `SpriteShading`/shader shadow classes or the composer's
  named rules change.
* MIDI rendering runs on the main thread in the viewer (0.5 s buffer); the game moves it to a streaming thread (§7.3).
* Revisit the SoundFont decision if the product owner wants music out of the box (then: bundle GeneralUser GS after a
  sample-provenance review, or pre-render MIDI at import, REQ-AUD-02).
