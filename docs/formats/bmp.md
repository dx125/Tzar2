# BMP images, terrain textures and building masks

Status: container **resolved**; transparency keys **resolved** (UNK-31); terrain texture
layout **resolved**; terrain transition *selection* rules **partial** (UNK-10);
building `_c` masks **resolved** (UNK-01b).
Readers: any standard BMP reader (Pillow was used); shape analysis inline in this
document's verification notes.

## 1. Container

Plain Windows BMP: `BITMAPFILEHEADER` (14 bytes, magic `BM`) + `BITMAPINFOHEADER`
(40 bytes), bottom-up rows (positive height in every file), rows padded to 4 bytes,
compression `BI_RGB` (0) only. Pixel data starts at `bfOffBits`.

Surveyed: every `*.BMP` under `IMAGES/` (531 files).

| Depth | Files | Use |
|---|---|---|
| 16 bpp | 444 | UI screens, icons, items, cursors (some), terrain textures, trees, effects |
| 8 bpp (palette in file) | 87 | building `_c` masks (68), cursors (11), terrain `LIGHT.BMP` (4), `PALETTE.BMP`, misc |

### 1.1 16 bpp pixels
No bit-field masks are present, so by the BMP rules the format is **RGB555** (x1R5G5B5).
Bit 15 is 0 in all sampled pixels of all 444 files. Expand channels with bit replication
(TDD §13.2). There is no RGB565 file in the reference build.

### 1.2 Transparency keys (UNK-31)
* **`0x03E0` (pure green, RGB 0,255,0 after expansion) is the transparent colour** for
  sprites, icons, items, cursors, trees and terrain overlays. It is also the value stored
  in the `keyColour` field of every RLE of kind 3/7 and is the background index colour of
  8-bit masks and fonts (`00 FF 00`).
* **`0x7C1F` (magenta)** is a second key used only in terrain transition tiles (§3.2).
* UI screen backgrounds (`IMAGES/SCREENS/**`) are opaque full-screen images; some UI
  parts also use the green key (35 of the 256 screen BMPs start with it). Treat green as
  transparent for all non-terrain-base BMPs.

## 2. Building `_c` masks (UNK-01b) <a id="building-masks"></a>

`IMAGES/BUILDING/MASK/* C.BMP` (68 files, 8 bpp) are **player-colour overlays**: small
images of the parts of a building that show the owner's colour (banners, flags, shields).
They are referenced from `IMAGES.INI` entries named `<building>_c`:

```ini
[e_barrack_c]
image = building\mask\euro barracks c.bmp
rows = 6          ; optional: animation frames stacked vertically (flags waving)
object = 23,3     ; x, y offset of the overlay inside the building sprite
fps = 4           ; optional animation rate
```

* Each file has its own 256-entry palette. In **all 68** files indices **240–255 hold the
  same 16-step grey player ramp as the indexed RLE sheets** and are recoloured exactly like
  them (see [rle.md](rle.md) §5.2 and [pal.md](pal.md)); other indices are static colours.
* A mask is mostly an **opaque rectangular patch**: the non-player pixels repeat the
  building pixels underneath (checked visually on `ARAB BARRACKS C`). 53 files contain a
  pure-green palette entry (at varying indices: 1, 57, 111, …); pixels using it are
  transparent. 15 files have no green entry and are drawn fully opaque.
* Frames: `rows` cells stacked vertically, cell height = image height / rows.
* Draw order: after the building sprite, at the `object` offset relative to the sprite's
  top-left corner.
* 4 masks have a `.DAT` companion with frame boxes ([dat.md](dat.md)).

## 3. Terrain sets

Each set (`IMAGES/TERRAIN/{GRASS,SUMMER,DESERT,SNOW}`) is described by
`<SET>.TERRAIN.INI` (syntax in [ini-tsv.md](ini-tsv.md)). The tile size is **32 × 32**.

### 3.1 Base textures `NN.BMP` (N = terrain index, `00` = water)
* `11`…`77`: 1024 × 1024 or 512 × 512 RGB555, an atlas of `cols × rows` 32-px tiles given
  by `[terrainsize] NN = cols, rows, 0`. Grass set: `11`, `22` = 32 × 32 tiles; the others
  16 × 16. ⚠ `GRASS/33.BMP` and `GRASS/44.BMP` are 1024 px wide but declared `16,16` —
  either only the top-left 512 × 512 is used or the declaration is ignored; check in the
  engine/editor (open).
* Tile choice within the atlas is per map cell (map data, UNK-10). TDD §13.5 assumes a
  deterministic hash; the map binary will show whether the variant is stored.

### 3.2 Transitions `AB.BMP` (A < B)
`[terrainfiles] AB = file.bmp` maps a terrain pair to a file (several pairs share one file,
e.g. `13 = 12.bmp`); `[terrainsize] AB = 2,14,F`:

* Layout: **2 variant columns × 14 shape rows** of 32-px tiles (64 × 448). With `F > 0`
  frames (shores, `01`/`02`/`04`: `2,14,4`) the width is `2 × F` tiles (256 px) and
  columns are **variant-major**: column `v × F + f` = variant `v`, frame `f` (verified by
  similarity: columns 0–3 and 4–7 form two families).
* Pixels: green key `0x03E0` and magenta key `0x7C1F` mark where the two underlying base
  textures show through; other pixels are the hand-drawn blend fringe. ⚠ HYPOTHESIS:
  magenta = terrain A (lower index), green = terrain B; confirm with a map (UNK-10).
* Shape row `r` (0…13) encodes the 4-corner mask `m = r + 1` with bits **TL = 1, TR = 2,
  BR = 4, BL = 8**, a set bit meaning the corner is magenta. Masks 0 and 15 (uniform
  tiles) are not stored. Derived from the dominant key colour in each corner of every
  transition tile of all four sets (`12`, `23`, `01`); shore tiles agree where keys are
  visible.
* Draw: base(B) → base(A) through the magenta key → overlay pixels. Equivalently, the
  importer can bake the 14 × 2 tiles per pair into a mask atlas.
* Consequence used in code: a tile is drawable only when its corners hold one terrain, or two that have a transition
  file (`TerrainComposer.IsDrawnByRule`). The viewer reports other tiles; the random map generator paints only
  drawable tiles ([ADR 0060](../decisions/0060-random-map-generator.md)).

### 3.3 Water `00.BMP`
512 × 2560 = **5 animation frames** of 512 × 512 (16 × 16 tiles), stacked vertically
(mean absolute difference between frames ≈ 3.5/255, i.e. subtle shimmering). Played at
`WATERFPS` (`tzar.ini`, default 7). Shore tiles (`01`) have 4 frames (§3.2). How 5 water
frames and 4 shore frames are synchronised is open.

### 3.4 Cliffs / overlays `18 19 28 29 38 39 48 49 .BMP`
192 × 448 RGB555 with green key: large cliff/hill ring outlines drawn on 32-px tiles
(6 × 14 tiles). Referenced only for some pairs (`48 = 48.bmp` in grass, `28 = 18.bmp`,
`48 = 38.bmp` in desert). Pseudo-terrain indices 8/9 are not named in `[terrainname]`.
Semantics open (UNK-10; needed for M2 terrain only if test maps use cliffs).

### 3.5 Trees `TREES.BMP`, `TREES_S.BMP`
Horizontal strips: `TREES.BMP` 2976 × 128 = 31 cells of 96 × 128; `TREES_S.BMP`
3968 × 96 = 31 shadow cells of 128 × 96 (green key; shadow cells drawn as darkening).
`[treename]` gives counts in strip order (`Stump = 3, Bush = 2, Pine = 5, Deciduous = 21`
→ 31). `[trees] pos_tree`/`pos_shadow` are anchor offsets (tiles) ⚠ unconfirmed;
`[treeterrain]` lists the terrains a tree may grow on.

### 3.6 Light maps `LIGHT.BMP`
512 × 512, 8 bpp, 28–32 grey levels (indices 0–31, palette grey ramp; grass/summer ramp
runs bright→dark, desert/snow dark→bright). Used by the day/night light composite
(UNK-32). Import as an 8-bit intensity texture using the **palette grey value**, not the
index.

## 4. Other folders

`ICONS` (`ICONS.BMP` 48 × 10608 = a vertical strip of 221 icons of 48 × 48, indexed by
`ICONS.INI`; small 20 × 20 stat glyphs; `HERO.BMP` 48 × 48), `ITEMS`, `ORDERS`, `CURSORS` (hotspots in
`IMAGES.INI` `hotspot = x, y` for 62 entries, resolving most of UNK-40), `SCREENS/<dialog>/`
(UI, opaque), `RANDOMMAP` (white key `0x7FFF` at the corner, preview art),
`SCREENS/COMMON/COLOR BARS.BMP` (13 × 104: eight flat 13-px bars in player order blue, red,
green, purple, yellow, cyan, orange, grey; probably minimap/UI colours, see [pal.md](pal.md)).
