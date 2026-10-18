# `.RLE` sprite sheets

Status: **resolved** for decoding (961/961 files of GOG v1.10 EN decode without error).
Semantics of player colour resolved from data; shadow encoding identified; blend factor measured (UNK-03: each
5-bit channel halved, capture of 2026-10-18, ADR 0111).
Resolves **UNK-01**, **UNK-01a** (data side), contributes to **UNK-03**.
Prototype: [`tools/re/tzrle.py`](../../tools/re/tzrle.py), batch check: [`tools/re/render_all.py`](../../tools/re/render_all.py).

## 1. What the files are

An `.RLE` file is a run-length compressed copy of one sprite sheet that `DATA/IMAGES.INI`
(or a terrain `*.IMAGES.INI`) names as a `.bmp` (e.g. `unit\asian archer walk.bmp` →
`IMAGES/UNIT/ASIAN ARCHER WALK.RLE`). The original BMP sources are not shipped; the game's
"RLE First" option makes it read these precompiled sheets. Name resolution is
case-insensitive; strip `.bmp`, try `.RLE`, then `.BMP`.

Files in the reference build (961):

| Folder | Files | Kinds present |
|---|---|---|
| `IMAGES/UNIT` | 271 | 250 indexed, 11 RGB555 (animals/flyers), 10 shadow |
| `IMAGES/BUILDING` (+ `BRIDGE`, `BUILD`, `DESTROY`, `DUMMY`, `GATES`, `WALLS`) | 213 | RGB555 + shadow |
| `IMAGES/ANIMS` | 38 | 2 indexed, 35 RGB555, 1 shadow |
| `IMAGES/SCENERY` | 4 | RGB555 + shadow |
| `IMAGES/TERRAIN/{COMMON,DESERT/SCENERY,GRASS/SCENERY,SNOW/SCENERY,SUMMER/SCENERY}` | 435 | 11 indexed, mostly RGB555, shadow |

## 2. Byte layout

All integers little-endian.

| Offset | Type | Name | Notes |
|---|---|---|---|
| 0x00 | char[4] | magic | the four bytes `' ','e','l','r'` |
| 0x04 | uint32 | `dirs` | number of directions (columns in `IMAGES.INI`, `cols`); 1 for non-directional sheets |
| 0x08 | uint32 | `frames` | frames per direction (`rows` in `IMAGES.INI`) |
| 0x0C | uint32 | reserved | always 0 |
| 0x10 | char[4] | magic 2 | `R`,`L`,`E`,`\0` |
| 0x14 | uint16 | `width` | sheet width = cell width (max 960 in the build) |
| 0x16 | uint16 | `height` | sheet height = `dirs × frames × cellHeight` (see §4) |
| 0x18 | uint16 | `kind` | 1 = indexed 8-bit, 3 = direct RGB555, 7 = shadow mask |
| 0x1A | uint32 | `dataSize` | bytes from the row table (below) to end of file |
| 0x1E | uint32 | `paletteCount` | 256 for kind 1 (one file: 120); 0 otherwise |
| 0x22 | uint32 | `keyColour` | the transparent colour of the source bitmap: a palette index for kind 1, an RGB555 value for kinds 3/7 (always 0x03E0, pure green). Informational only: the key never occurs in pixel data, transparency is expressed by skips |
| 0x26 | 4 × `paletteCount` | palette | entries are B, G, R, 0 (Windows RGBQUAD order) |
| T = 0x26 + 4·`paletteCount` | uint32[`height`] | row table | offset of each row's data, **relative to T** |
| … | | row data | |

Invariants checked on all 961 files: `T + dataSize == fileSize`; the first row offset is
`4 × height`; offsets are non-decreasing; the last row ends at `dataSize`.

## 3. Row encoding

A row is a sequence of pairs. Pseudocode (decode one row `y`):

```
p   = T + rowTable[y]
end = T + (y + 1 < height ? rowTable[y + 1] : dataSize)
x   = 0
while p < end:
    skip = u8[p]; p += 1; x += skip            // transparent pixels
    if p == end: break                          // (never happens in the build)
    n = u8[p]; p += 1                           // opaque pixel count, may be 0
    kind 1: n bytes, each a palette index       → pixels x .. x+n-1
    kind 3: n uint16 RGB555 values (bit 15 = 0)  → pixels x .. x+n-1
    kind 7: no payload; the n pixels are "in shadow"
    p += n × bytesPerPixel; x += n
assert x == width and p == end
```

* Counts are single bytes; a gap or run longer than 255 is split into several pairs with a
  zero count/skip in between (encoders may emit `skip, 0`).
* Most rows end with a trailing `skip, 0` pair that pads to `width`; 4,842 rows end
  directly after an opaque run. Both forms must be accepted.
* An empty row is `width, 0` (2 bytes).
* Pixels not covered by a run are transparent. Index/colour values are never a key.

## 4. Cells: directions, frames, order

The sheet is one column of `dirs × frames` cells, each `width × cellHeight`, with
`cellHeight = height / (dirs × frames)` (integer division). Cells are stored
**direction-major**: cell index `c = dir × frames + frame`, occupying rows
`c × cellHeight … (c + 1) × cellHeight − 1`.

* Exactly one file, `ANIMS/BRIBE.RLE` (1 × 17 cells of 40 px), has one extra row at the
  bottom (`height` = 681). Readers MUST tolerate `height mod (dirs × frames) ≠ 0` and
  ignore the surplus rows.
* 76 of 30,339 cells are completely empty (e.g. late frames of debris/explosions).
* Direction index → screen direction (8-direction sheets): 0 = down (S), 1 = down-right
  (SE), 2 = right (E), 3 = up-right (NE), 4 = up (N), 5 = up-left (NW), 6 = left (W),
  7 = down-left (SW), i.e. counter-clockwise on screen. Established by visual inspection of
  decoded cow and knight sheets (head direction); confirm the mapping of movement vectors to
  indices with captures in RE-06. Some projectiles/birds use 24 directions (`SEAHAWK`, `EAGLE`,
  `ARROW*`), and `cols` in `IMAGES.INI` always equals `dirs` (971/971 references checked).

## 5. Colours

### 5.1 Kind 3 (RGB555)
`uint16`: bits 10–14 red, 5–9 green, 0–4 blue, bit 15 unused (0 in every file). Expansion
to 8 bits: bit replication `c8 = (c5 << 3) | (c5 >> 2)` (TDD §13.2; UNK-39 still applies).

### 5.2 Kind 1 (indexed)
Palette is per file (up to 256 RGB888 entries, BGR0 order). Fixed regions shared by all
262 full-palette files:

| Indices | Content | Meaning |
|---|---|---|
| 240–255 | 16-step grey ramp 23,38,…,240,255 | **player colour**. Index `240 + k` is drawn as entry `112 + 16·player + k` of `IMAGES/GAME.PAL` (see [pal.md](pal.md)) |
| 224–239 | 16-step dark neutral grey ramp (identical to GAME.PAL 224–239 = the 8th player ramp) | used for dark neutral parts (armour, ropes, outlines); ⚠ treated as **static colour**, not recoloured (visual check only) |
| 0–223 | sheet-specific colours | static |

The only short-palette file (`UNIT/ASIAN HORSARCHER IDLE2.RLE`, 120 entries) has no player
ramp in its palette; its pixels with index ≥ 120 do not exist.

Player colours were checked visually: recolouring 240–255 with the blue ramp tints tabards,
plumes, banners and ship flags of every civ unit, and nothing else.

### 5.3 Kind 7 (shadow sheets)
Separate shadow sprites (`* S.RLE`, `*_S.RLE`, `* SHADOW.RLE`): same geometry as the
matching colour sheet, payload-free runs mark shadowed pixels. Drawn as a darkening pass;
the factor is ½ per 5-bit channel (`c >> 1`, measured); the overlap rule is UNK-03 (TDD §13.3). 131 files.

### 5.4 Baked unit shadows (kinds 1 and 3)
Ground units have **no** separate shadow sheet; their shadow is painted into the sprite in a
near-black, slightly green colour (the source art used a dark-green shadow colour that was
then quantised). Observed values:

* RGB555 sheets (bear, cow, …): exactly `0x0020` (R5 = 0, G5 = 1, B5 = 0); black `0x0000`
  is used for real outlines in the same sheets and is **not** shadow.
* Indexed sheets: one or two palette entries such as (2,9,1), (3,19,5), (0,20,0), (1,1,1),
  (0,0,0); quantised to RGB555 they all satisfy R5 = 0, B5 = 0, G5 ≤ 2.

Recommended importer rule (implemented as `is_shadow_rgb` in the prototype): a pixel of a
**unit** sheet is a shadow pixel when its colour quantised to RGB555 has R5 = 0, B5 = 0 and
G5 ≤ 2; for kind 3 use only `0x0020`. Write such pixels into the shadow plane instead of the
colour plane. Visual check over 12 representative units: the rule captures the cast
shadows; a few very dark cloak pixels of black-clad units (spy, mage, maceman) are also
captured. ⚠ Whether the original engine renders these pixels as translucent shadow or as
opaque colour is **not** confirmed (UNK-03); keep a per-sheet override in the manifest.
Boats use a dark blue water shadow (e.g. (5,7,9)) that the rule does not catch.

## 6. Relation to other data

* `.DAT` next to the `.RLE`: per-cell bounding boxes (redundant, see [dat.md](dat.md)).
* `IMAGES.INI` entry (see [ini-tsv.md](ini-tsv.md#imagesini)): `rows` = `frames`,
  `cols` = `dirs`, `object` = x, y, w, h of the logical footprint box inside a cell
  (units: usually 32 × 32; buildings: tile footprint), `selection` = selection box, `delay`
  (ms per frame, `frames` values; 149 of 159 entries sum to `anim_time`), `actionrow`
  (frame index of the action event), `move_len`, `fps`, `startrow`, `startcol`, `hotspot`.
* Building colour sheets are kind 3 and carry no player colour; the player-coloured parts
  are separate `_c` overlays (`IMAGES/BUILDING/MASK/* C.BMP`, see [bmp.md](bmp.md#building-masks)).

## 7. Edge cases a reader MUST handle

1. `height` not a multiple of the cell count (BRIBE) → ignore surplus rows.
2. Rows that end without a trailing `skip, 0`.
3. Empty cells.
4. `paletteCount` < 256.
5. Trailing data: none observed; treat `T + dataSize != fileSize` as corruption.

## 8. Statistics (GOG v1.10 EN)

| Kind | Files | Compressed bytes | Opaque pixels |
|---|---|---|---|
| 1 indexed | 263 | 49.9 MB | 29.6 M |
| 3 RGB555 | 567 | 20.3 MB | 9.0 M |
| 7 shadow | 131 | 1.6 MB | 2.2 M |
| **All** | **961** | **71.7 MB** | **40.9 M** |

Decode of all 961 files with the prototype: 0 errors, 30,339 cells, ≈ 17 s including PNG
export. Volume figures for spike M0-S6 are in [sprite-volume.md](sprite-volume.md).
