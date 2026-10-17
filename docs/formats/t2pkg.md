# Imported content: `.t2pkg`, `.t2spr`, `.t2img`

Our own formats written by `t2 import` (M1). Code: `Tzar2.Content` (`Packages/`, `Assets/`), importer in
`Tzar2.Import.Pipeline`. Sprite layout rationale: [ADR 0013](../decisions/0013-spike-s6-sprite-memory.md).

## 1. Package (`.t2pkg`, TDD REQ-DATA-01)

A ZIP archive (readable by any ZIP tool) written by `PackageWriter` so that the same input gives the same bytes on
every OS and runtime (REQ-IMP-02): entries sorted by path (ordinal), **stored** (no compression), fixed timestamp
1980-01-01 00:00, "version made by" 2.0/MS-DOS, UTF-8 names, no extra fields, no ZIP64 (< 65,535 entries, < 4 GiB).
`PackageReader` also reads deflated entries, for packages produced by other tools.

Entry paths are normalised content paths (`ContentPath`): lower case, `/`, no `.`/`..`. The VFS mounts packages and
folders in priority order; a later source wins for the same path.

`package.yaml`: `id`, `name`, `version`, `kind`, `engineApi`, `dependsOn`, `conflictsWith`, `provides`,
`gameplayHash` (SHA-256 over every `tables/` entry: path, NUL, int64 length, bytes, in path order), `sourceEdition`.

### Base package layout (`base.original.t2pkg`)

| Path | Content |
|---|---|
| `art/<path under IMAGES>.t2spr` | every `.RLE` (key: the IMAGES.INI logical path, e.g. `art/unit/euro knight.t2spr`) |
| `art/<path under IMAGES>.t2img` | every `.BMP` under `IMAGES/` |
| `art-extra/<path>.t2img` | BMPs elsewhere (`HELP/`, `Hero Icons/`) |
| `palettes/game.json` | `GAME.PAL`: 256 `rrggbb` entries, ramp base 112, 16 entries × 8 players |
| `fonts/<name>.json`, `fonts/<name>.t2img` | metrics + `[code, unicode, glyph, width]` per character; glyph strip (R8 coverage, or Indexed8 for `FIXSHAD`) |
| `sound/**.wav`, `music/*.mid` | unchanged copies |
| `tables/<source path>.json` | every INI (`sections` → `entries` `[key or null, value]`, order and duplicates kept) and TSV (`header`, `rows`, blank separator rows as `[]`), decoded with the edition's code page |
| `text/<source path>` | other text (`CHANGES.TXT`, help, AI scripts) as UTF-8 |
| `manifests/sprites.json` | per sheet: asset, source, format, kind, directions, frames, cell size, player range, shadow hints, trimmed pixels, DAT cross-check |
| `manifests/sprite-index.json` | every IMAGES.INI entry (data + terrain sets) resolved to its asset, with all keys as string lists |
| `manifests/images.json`, `footprints.json`, `sounds.json`, `music.json`, `fonts.json`, `tables.json`, `terrain.json`, `edition.json` | indexes; `footprints.json` holds the DAT footprint rectangles and grids |

Maps and campaigns (`.wmp`/`.wcm`) are validated but not converted (M4); movies and demos are skipped (M8, DEC-12).
The import report (`import-report.json`/`.txt`) is written next to the package, never into it, because it contains
timings.

## 2. Sprite sheet (`.t2spr`, version 1)

Little-endian. Header 64 bytes:

| Offset | Type | Field |
|---|---|---|
| 0 | char[4] | `T2SP` |
| 4 | u16 | version = 1 |
| 6 | u8 | pixel format: 1 Indexed8, 2 RGB5A1, 3 RGBA8, 4 R8 |
| 7 | u8 | source RLE kind (1, 3, 7) or 0 |
| 8 | u16 × 4 | directions, frames per direction, cell width, cell height |
| 16 | u16 | palette count (256 for Indexed8, else 0) |
| 18 | u16 | first player-colour index (240) or 0xFFFF |
| 20 | u16 | player-colour count (16) or 0 |
| 22 | u16 | baked-shadow RGB555 value (0x0020 in unit RGB555 sheets) or 0xFFFF |
| 24 | u16 × 2 | reserved, 0 |
| 28 | u8[32] | bit set of palette indices that are baked shadow |
| 60 | u32 | pixel data size |
| 64 | RGBA × palette count | palette; alpha 0 = the transparent index |
| … | 12 B × cells | per cell (direction-major): i16 x, i16 y, u16 w, u16 h, u32 offset — the trimmed box inside the cell |
| … | | pixel data, each cell row-major |

Pixels: Indexed8 = palette index; RGB5A1 = u16, bit 15 opaque, bits 0–14 the original RGB555; RGBA8 = R, G, B, A
(fallback for an indexed sheet that uses all 256 indices); R8 = 0/1 shadow mask. Surplus rows below the last cell
(`ANIMS/BRIBE.RLE`) are dropped.

## 3. Image (`.t2img`, version 1)

`T2IM`, u16 version, u8 format, u8 0, u32 width, u32 height, u16 palette count, u16 0, palette RGBA, pixels (top-down,
row-major). Colour keys (bmp.md §1.2) only clear the opacity bit (RGB5A1) or the palette alpha (Indexed8); the colour
stays, so the original pixel values are recoverable: green `0x03E0` is transparent everywhere except terrain base
textures (`NN.BMP` with equal digits); terrain transitions also key magenta `0x7C1F`.
