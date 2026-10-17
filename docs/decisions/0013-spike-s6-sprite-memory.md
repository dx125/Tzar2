# 0013. Store sprites as trimmed, lossless indexed8 / RGB5A1 / R8 cells and pack them at load time into 2,048² texture arrays

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M1 importer)
* **Related:** spike M0-S6, ADR 0010 (S1 renderer), ADR 0011 (allowed packages: Content needs none for this), REQ-GFX-03, REQ-GFX-04, REQ-GFX-05, DEC-07, DEC-10, TDD §13.2–13.3, §21, UNK-01a, UNK-03, UNK-39,
  [docs/formats/sprite-volume.md](../formats/sprite-volume.md), [docs/formats/rle.md](../formats/rle.md)

## Context

REQ-GFX-03 requires lossless sprite storage on the GPU and names three candidate layouts: (a) RGBA8 + R8 mask plane,
(b) RGB5A1 + R8 mask, (c) R8 palette index where a sheet has ≤ 256 colours, falling back to (b). Block compression is
not allowed in the Authentic preset. The memory budget (§21) is ≤ 1.5 GB VRAM on desktop and ≤ 600 MB texture residency
on mobile (PX).

The RE prototype measured the decoded volume ([sprite-volume.md](../formats/sprite-volume.md)). The C# pipeline of this
PR re-measured it while importing the reference build (GOG v1.10 EN, all 961 RLE sheets; `t2 import` report,
`spriteMemory` section), including atlas packing with the shelf packer that the client will use:

| Source kind | Sheets | Cells | Full cells | Trimmed | Opaque | Stored (chosen layout) | 2,048² layers (occupancy) | 4,096² layers (occupancy) |
|---|---|---|---|---|---|---|---|---|
| 1 indexed (units) | 263 | 25,991 | 287.7 Mpx | 69.9 Mpx | 29.6 Mpx | 69.9 MB (1 B/px) | 18 (92.5 %) | 5 (83.3 %) |
| 3 RGB555 | 567 | 2,835 | 33.6 Mpx | 17.3 Mpx | 9.0 Mpx | 34.6 MB (2 B/px) | 5 (82.4 %) | 2 (51.5 %) |
| 7 shadow | 131 | 1,513 | 36.4 Mpx | 7.8 Mpx | 2.2 Mpx | 7.8 MB (1 B/px) | 3 (61.6 %) | 1 (46.2 %) |
| **All** | **961** | **30,339** | **357.7 Mpx** | **94.9 Mpx** | **40.9 Mpx** | **112.2 MB** | **26** | **8** |

Same pixels in the other layouts: (a) 474.7 MB, (b) 284.8 MB. Untrimmed cells would multiply everything by 3.8.
Worst-case residency (all buildings, scenery, effects and shadows + common units + the largest civilisation, Asian):
**88.7 MB** in the chosen layout.

Facts that shape the choice (rle.md §5, pal.md, bmp.md §2):

* 74 % of trimmed pixels are indexed unit sheets that already are 8-bit with a per-sheet 256-entry palette. Player
  colour is the index range 240–255 (recoloured from `GAME.PAL` ramps, UNK-01a) and baked shadow is a handful of
  near-black palette entries. In an index layout both are lookups, so no mask plane is needed.
* RGB555 sheets (buildings, scenery, effects, a few animals) carry no player colour (buildings use separate 8-bit `_c`
  overlays with the same 240–255 ramp). 353 of the 567 use more than 256 colours, so they cannot be indexed losslessly;
  RGB555 plus one opacity bit is exactly lossless at 2 B/px.
* Shadow sheets are 1-bit masks.
* Every RLE kind-1 sheet leaves its colour-key index unused, so that index can mark transparency (the importer checks
  this per sheet and picks another unused index, or falls back to RGBA8, when it does not hold).

## Decision

1. **Pixel layouts** (`Tzar2.Content.Assets.PixelFormat`), chosen per sheet by its source kind:
   * kind 1 → **Indexed8**: 1 B/px index + the sheet's 256 × RGBA8 palette; palette alpha 0 marks the transparent
     index. The sheet records the player range (`playerFirst = 240`, 16 entries) and, for unit sheets, the palette
     indices the baked-shadow heuristic flags (`shadowIndices`). GPU: `R8` array + one palette row per sheet in a
     256 × N RGBA8 palette texture.
   * kind 3 → **RGB5A1**: 2 B/px little-endian, bit 15 = opaque, bits 0–14 = the original RGB555 value; unit sheets
     record `shadowColour = 0x0020`. GPU: `RG8` array (Godot has no RGB5A1 texture format), unpacked in the shader
     with the bit-replication expansion of TDD §13.2.
   * kind 7 → **R8** mask (0/1). GPU: `R8` array.
   * 8-bit BMPs (building `_c` overlays, cursors, light maps) use Indexed8 the same way; 16-bit BMPs use RGB5A1, where a
     colour-key pixel keeps its colour and only clears bit 15; 24-bit BMPs use RGBA8.
   Shadow hints are data, not baked decisions: the renderer may ignore them (UNK-03 is still open) and the manifest can
   override them per sheet.
2. **Per-frame trimming is mandatory.** Each cell is stored as its tight opaque bounding box plus its offset inside
   the full cell (`.t2spr`, direction-major like the original). Empty cells take no pixels.
3. **Atlas pages are built at load time, not at import time.** The package stores trimmed cells per sheet; the client
   packs only the resident sheets (REQ-GFX-05) into one `Texture2DArray` per GPU format. **Layer size 2,048²** on every
   platform (mobile-safe, 92 % occupancy for the dominant indexed set, ~4 MB per R8 layer, so residency changes move
   small units); 4,096² is not needed for capacity (26 layers hold everything). Packing is deterministic
   (`AtlasPacker`, shelf packing by height).
4. Nearest-neighbour sampling only. Smooth/sharp-bilinear presets filter in the shader after palette lookup or RGB5A1
   unpacking, never with hardware filtering on indices or packed words.

## Alternatives considered

* **(a) RGBA8 + R8 mask:** simplest shader, but 4.2× the memory (475 MB for everything, 357 MB residency per the
  prototype) and the mask plane duplicates information the palette already has.
* **(b) RGB5A1 + R8 mask everywhere:** 285 MB; loses nothing but wastes 2 B/px on sheets that are 8-bit already and
  still needs a mask plane for player colour.
* **(c) with per-sheet requantisation of RGB555 sheets to 8 bit:** only 214 of 567 sheets fit in 256 colours;
  mixing would complicate the pipeline for ~17 MB.
* **Pre-packed atlases in the package:** fixes the atlas at import, defeats per-match residency and makes mods that add
  one unit re-pack the base content.
* **One frame per array layer** (the alternative ADR 0010 asked S6 to re-measure): all layers of a texture array share
  one size, and the build has 30,339 cells of very different sizes (up to 960 px wide). Sizing layers to the largest
  trimmed cell would waste most of each layer, and even the resident set of one match holds far more frames than the
  layer limit of target GPUs (2,048 on desktop GL/Vulkan/Metal; GLES 3.0 guarantees only 256). Packing many trimmed
  cells per 2,048² layer (instance → layer + UV rect + pivot, as TDD §13.3 and ADR 0010 already assume) needs 26
  layers for everything.
* **4,096² layers:** fewer layers (8) but 16–64 MB per layer upload granularity and weaker occupancy for the small
  RGB555/shadow sets; not mobile-safe on older GPUs.

## Consequences

* The sprite shader has three variants (index + palette, RGB5A1 unpack, mask) and must apply the player ramp
  (palette row of `GAME.PAL` ramps, REQ-GFX-04) and the shadow hints. `SpriteCompositor` in `Tzar2.Content` is the CPU
  reference for what the shader computes; golden tests compare it to the RE prototype pixel by pixel.
* Package size is dominated by uncompressed pixels: the whole base package is 279 MB, because `.t2pkg` entries are
  stored without compression so the bytes are identical on every OS and runtime (no dependency on a zlib build) and
  can be memory-mapped. Compressing pixel payloads with our own deterministic codec is a possible follow-up. Memory for the whole sprite set (112 MB) fits every budget, so residency management is an
  optimisation, not a requirement, on desktop.
* Revisit when: UNK-03 shows that baked shadows render translucent differently from the heuristic (only the hints
  change), the S1 renderer spike finds `RG8` unpacking too slow (fallback: RGBA8 for RGB5A1 sheets, +35 MB), or HD
  sprite packs (TDD §13.4) need a 32-bit layout (they would add RGBA8 sheets; the format enum already has it).
