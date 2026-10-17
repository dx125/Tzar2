# `.DAT` sprite metadata

Status: **resolved** (layout: 621/621 files parse; frame boxes verified against decoded RLE cells).
Semantics of the footprint-grid values: **partial**. Resolves **UNK-02**.
Prototype: [`tools/re/tzdat.py`](../../tools/re/tzdat.py) (parser + verification against the RLE).

A `.DAT` sits next to most `.RLE`/`.BMP` sprite files (621 in the reference build:
`IMAGES/UNIT` 272, `BUILDING/**` 152, `ANIMS` 37, `ITEMS` 28, `SCENERY` 4, `TERRAIN/**` 128).
It is a cache produced by the original tools from the sheet plus its `IMAGES.INI` entry.
**Every value can be recomputed from the RLE and `IMAGES.INI`; the importer SHOULD treat
the DAT as optional and recompute** (some DATs are stale, §4).

## 1. Layout

All integers little-endian, signed 16-bit unless noted.

| Offset | Type | Name |
|---|---|---|
| 0 | int16 × 4 | footprint rectangle `x0, y0, x1, y1` (inclusive, in sprite pixels). All four 0 = no footprint |
| 8 | uint8 × (gw × gh) | footprint grid, row-major, `gw = (x1 − x0 + 1) div 32`, `gh = (y1 − y0 + 1) div 32`; absent when the rectangle is all zero |
| 8 + gw·gh | uint16 | `boxCount` (optional; the section may be missing entirely) |
| … | `boxCount` × int16 × 4 | frame boxes `x0, y0, x1, y1` (inclusive, cell coordinates) |

Parsing rule: after the grid, the remaining length is either 0 (no box list) or exactly
`2 + 8 × boxCount`. Integer division for the grid size is required: `EURO OUTPOST.DAT` has a
130-pixel-high rectangle (y −1…128) with a 4-row grid.

Observed shapes:

| Shape | Files | Typical owner |
|---|---|---|
| no footprint, box list | 458 | units, animations, items, shadows, many building sheets |
| footprint + grid, no box list | 133 | buildings, terrain scenery (mountains, lava pits, skeletons…) |
| footprint + grid + box list | 30 | buildings |

(Total 621 = 458 + 133 + 30; `EURO OUTPOST.DAT` is the one that needs integer division.)

3 unit DATs (`ARAB PEASANT CAPTURE` etc.) have no image next to them (orphans).

## 2. Frame boxes

`boxCount` = `dirs × frames` of the sheet. Boxes are stored **frame-major**
(`index = frame × dirs + dir`), i.e. transposed relative to the RLE cell order
(`dir × frames + frame`). Each box is the tight inclusive bounding box of all opaque pixels
of that cell (shadow pixels included). For an empty cell the box is either the full cell
(`0, 0, w−1, h−1`) or garbage.

Verification (`tzdat.py`): over all DATs with a box list and an RLE companion, 28,948 of 29,494
boxes (98.1 %) equal the recomputed bounding box exactly (all boxes match in 95 % of files). The
23 mismatching files are either empty-cell cases or stale caches whose RLE was re-rendered
later (`EURO SPY`, `ARAB PEASANT KIRKA`, `ASIAN ARCHER IDLE`, `FREEZE EXPL`, `BAT S`, …).
**Use recomputed boxes.**

## 3. Footprint rectangle and grid

* The rectangle is the building's tile footprint expressed in sprite pixels; its width and
  height are multiples of 32 (one map tile = 32 px). In 35 of 38 buildings it equals the
  `object = x, y, w, h` value of the `IMAGES.INI` entry (as `x, y, x+w−1, y+h−1`); the
  3 differences are stale DATs. **`IMAGES.INI` `object` is authoritative**; the DAT copy is
  a cross-check.
* Grid cell values (5,691 cells): `0` (4,073), `2` (1,609), `1` (9).
  * `0` = tile occupied by the building.
  * `2` = tile inside the footprint rectangle but **not** occupied (cut corners of
    rectangular footprints, castle courtyards), ⚠ HYPOTHESIS from the patterns (e.g. most
    4×4 and 5×5 buildings have `2` in one or more corners; the Arab castle has a ring of
    `2` with a free interior).
  * `1` = appears only in farms (`EURO FARM` centre 2×2 + row) ⚠ meaning unknown
    (field area?).
  Confirm with the original editor's placement/pathing (RE-06/RE-03). Terrain scenery uses
  the same grid for passability.

## 4. Edge cases

* All-zero rectangle with no box list does not occur; a file is never empty.
* Negative rectangle coordinates occur (footprint starts left of / above the sprite).
* Some buildings (`ARAB CASTLE`, `ASIA FARM`, `ASIA LUMBERMILL`, `EURO TAVERN`, …) have
  no box list.
