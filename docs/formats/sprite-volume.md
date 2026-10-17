# Decoded sprite volume (input for spike M0-S6, REQ-GFX-03)

Measured on GOG v1.10 EN (`Origin/`, 961 RLE files) with
[`tools/re/render_all.py`](../../tools/re/render_all.py) (`--no-png` for numbers only).
MB = 10⁶ bytes. BMP-only art is not included: 444 16-bit BMPs (UI, icons, terrain textures, trees) hold
50.9 Mpx = 102 MB at 2 B/px (203 MB as RGBA8); they are RGB555 without player colour.

## 1. Pixel counts

| Measure | Pixels | Meaning |
|---|---|---|
| Full cells | 357.7 M | `width × cellHeight × cells`, i.e. the sheets as authored |
| **Trimmed cells** | **94.9 M** | sum of per-cell tight bounding boxes (what an atlas packer stores) |
| Opaque pixels | 40.9 M | pixels actually covered by runs |
| Compressed RLE on disk | 71.7 MB | for comparison |

30,339 cells (76 empty). Largest sheet: `UNIT/DRAGON ATTACK.RLE`, 6.7 Mpx untrimmed.

Trimmed pixels by kind and folder:

| Kind | Trimmed px | | Folder | Trimmed px |
|---|---|---|---|---|
| 1 indexed (units) | 69.9 M | | UNIT | 75.9 M |
| 3 RGB555 | 17.3 M | | BUILDING | 8.3 M |
| 7 shadow | 7.8 M | | TERRAIN scenery | 9.3 M |
| | | | ANIMS | 1.3 M |
| | | | SCENERY | 0.2 M |

Units by civilisation (trimmed): common (siege, ships, animals, creatures, heroes) 36.8 M,
Asian 15.6 M, Arabian 13.4 M, European 10.1 M.

Of the 567 RGB555 sheets, 214 use ≤ 256 distinct colours (1.3 M trimmed px); 353 use more
(16.0 M px, up to 2,728 colours) and cannot be indexed losslessly with one palette.

## 2. Bytes per candidate layout (trimmed cells, all 961 sheets resident)

| Layout (TDD §13.2) | B/px | Total |
|---|---|---|
| RGBA8 only (no mask plane) | 4 | 380 MB |
| (a) RGBA8 + R8 mask | 5 | 475 MB |
| (b) 16-bit RGB5A1 + R8 mask | 3 | 285 MB |
| (c) R8 index for kind 1 (player colour and shadow are palette ranges, no extra plane) + RGB5A1 for kind 3 (no player colour there) + R8 for shadows | 1 / 2 / 1 | **≈ 112 MB** (70 + 35 + 8) |
| (c′) as (c) but kind 3 fallback at 3 B/px (b) | | ≈ 127 MB |

(c) also needs one 256-entry palette per indexed sheet (263 × 1 KB, negligible).

Untrimmed (cells kept at full size) multiplies everything by 3.8× (e.g. (a) ≈ 1.79 GB),
so **per-frame trimming is mandatory** whatever layout is chosen.

Residency example (REQ-GFX-05): one civilisation + common units + all buildings, scenery and
effects ≈ 36.8 + 15.6 (worst civ) + 8.3 + 9.3 + 1.5 ≈ 71 Mpx → (a) 357 MB, (b) 214 MB,
(c) ≈ 85 MB.

## 3. Observations for the decision

* 74 % of trimmed pixels are indexed unit sheets that already are 8-bit with a 256-entry
  palette in which player colour (240–255) and baked shadow (a few near-black entries) are
  just index ranges. Layout (c) keeps them lossless at 1 B/px and makes the mask plane
  free (the shader tests the index range).
* Building/scenery/effect sheets (kind 3) have no player colour (buildings use separate
  `_c` overlays), so they need no mask plane: RGB5A1 at 2 B/px is lossless.
* Shadow sheets (kind 7) are 1-bit; R8 (or packed 1 bpp) is enough.
* Numbers for a 4,096² `Texture2DArray` layer: 16.8 Mpx; the full trimmed set needs ≈ 6
  layers before packing overhead (≈ 23 layers of 2,048²).
