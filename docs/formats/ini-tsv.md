# INI and TSV text tables

Status: **resolved** for syntax and code page of the reference build; field semantics of
the rule tables stay with the gameplay RE (Appendix B, RE-05).
Prototypes: [`tools/re/tzini.py`](../../tools/re/tzini.py) (tolerant INI reader),
[`tools/re/check_images_ini.py`](../../tools/re/check_images_ini.py) (sprite cross-check).

## 1. Encoding and line endings

* Survey of all 177 `*.INI` plus all `*.TXT`, `*.AI`, `*.AIS` files of GOG v1.10 EN:
  **every file is pure ASCII except `LOCAL/CREDITS.TXT`** (one byte 0xF6, `ö` in
  Windows-1252). All use CRLF; no BOM; no UTF-8.
* Therefore the EN build is **Windows-1252** (consistent with the CP1252 glyph repertoire
  of its fonts, [fnt.md](fnt.md)). Localized builds use their ANSI code page
  (RU/BG: 1251, PL/CZ: 1250); the importer picks the code page from the edition
  fingerprint (REQ-IMP-01) and converts to UTF-8. Never guess from content for EN files —
  they are ASCII anyway.

## 2. INI syntax

```
file    := line*
line    := ws* ( section | pair | bare | comment | empty ) CRLF
section := '[' name ']'            ; name may contain spaces, '_', digits ("ORG  MENU.INI": two spaces in a file name, not in syntax)
pair    := key ws* '=' ws* value   ; split at the FIRST '='; value may be empty, may contain ',', '\', spaces
bare    := text without '='        ; e.g. [accept] allunits, [euro] hero names in HEROES.INI
comment := ';' anything            ; also allowed after a value: cut at the first ';'
```

Rules checked over all 177 INI files with `tzini.py`: 2,619 sections, 0 syntax warnings,
318 bare lines.

* **Case-insensitive** section and key names (`IMAGES.INI` uses lower case, `DATA/OBJECTS`
  mixed; file paths inside values are case-insensitive Windows paths with `\`).
* **Duplicate sections** occur only in `ICONS.INI` (`galleon`, `stone_wall_tower`,
  `a_church`, `item_sword`). Two are exact repeats; `galleon` (index 32 vs 130) and
  `item_sword` (19 vs 196) conflict. Which one the engine uses (first or last) is ⚠ open —
  keep both in the import report and verify on screen (RE-09).
* **Duplicate keys** inside a section occur in 7 sections (`IMAGES.INI` `actionrow` in
  `e_peon_attack`, `e_peon_attack2`, `e_spy_attack`; `OBJECTS/OUTPOST.INI` `menu`;
  `SOUNDS.INI` `spy_kill`; `LOCAL/STRINGS2.INI` `delete`). A reader MUST keep all values in
  order; for `actionrow` (several action frames) and sound lists, the duplicates are
  meaningful lists, so the rules converter should treat them as multi-valued.
* A `;` inside a value is a comment start (no quoting in INI).
* Values with spaces around commas occur (`hotspot = 0, 0`); trim each list element.

## 3. TSV tables (`DATA/{EURO,ASIA,ARAB,COMM} {UNITS,BUILDINGS}.TXT`)

* Tab-separated, CRLF, first line is the header. Empty lines and lines of only tabs
  separate groups.
* Column counts vary per line within a file (trailing empty cells are sometimes present,
  e.g. 9 vs 11/12 columns in `* BUILDINGS.TXT`). Pad to the header length; ignore extra
  empty cells.
* Cells may be wrapped in double quotes when they contain commas (Excel export), e.g. cost
  `"90f, 40g"`, emit `"+400,-200"`. Strip one pair of surrounding quotes; no escaped quotes
  occur.
* Header names differ between files for the same column (column 12 is `SPECIAL`, `BUILD`,
  or `BUILD AT`); map by position, not by name.

## 4. `IMAGES.INI` <a id="imagesini"></a>

`DATA/IMAGES.INI` (699 sections) plus the terrain sets' `IMAGES/TERRAIN/<SET>.IMAGES.INI`
and `COMMON.IMAGES.INI` (435 sections) define every sprite. Keys:

| Key | Count | Meaning |
|---|---|---|
| `image` | 1134 | logical path relative to `IMAGES\` with `.bmp`; resolves to `.RLE` first, then `.BMP` (case-insensitive). Terrain-set entries resolve relative to `IMAGES\TERRAIN\<dir>\` first |
| `rows` | 450 | frames per direction (= RLE `frames`) |
| `cols` | 395 | directions (= RLE `dirs`), default 1 |
| `object` | 500 | units/scenery: `x, y, w, h` of the logical footprint box inside a cell (anchor for sorting and placement); buildings: tile footprint `x, y, w, h` in sprite pixels (w, h multiples of 32); `_c` overlays: `x, y` offset |
| `selection` | 371 | `x, y, w, h` selection box in cell pixels |
| `anim_time` | 245 | total cycle in ms (one entry misspells it `anime_time`) |
| `delay` | 180 | per-frame delays in ms, one per frame (159/159 counts match; 149 sums equal `anim_time`) |
| `actionrow` | 129 | frame index of the action event (hit, release, death); may repeat |
| `move_len` | 77 | walk sheets: ⚠ pixels moved per cycle (UNK-12) |
| `hotspot` | 62 | cursors and some effects: `x, y` hotspot (UNK-40) |
| `startrow`, `startcol` | 41, 6 | first frame / direction used from a shared sheet |
| `fps` | 39 | frame rate for constant-delay loops (flags, effects) |
| `step` | 1 | ⚠ unknown |

Verification (`check_images_ini.py` on the reference build): 1,134 entries; 971 resolve to an
RLE and **all 971 have `rows`/`cols` equal to the RLE `frames`/`dirs`**; 155 resolve to a
BMP; 8 reference files that do not exist in the build (`cursors\h_none`, `scenery\ember1/2`,
`anims\freeze_r`, `items\item_coal`, `scenery\res door`, `scenery\trees`,
`scenery\trees shadow` — unused leftovers; `TREES.INI` points at the terrain-set trees).

### 4.1 From object class to sprite
`DATA/OBJECTS/<CLASS>.INI`: `[general] class = …, type = unit|building|…`; per civilisation
sections `[euro]`, `[asia]`, `[arab]` (and `[comm]`) with `image` (walk/base),
`image_attack`, `image_die`, `image_idle`, `image_c` (building colour overlay), … whose
values are **section names in `IMAGES.INI`** (`e_archer1`, `a_archer_attack`, …). Prefixes
`e_`, `a_`, `r_` = European, Asian, Arabian ("r" for aRab). The chain is:
object class → civ section → `image*` key → `IMAGES.INI` section → `image` path → `.RLE`
(+ `.DAT`).

## 5. Terrain set INI (`<SET>.TERRAIN.INI`)
Sections and keys as in Appendix B.1; layout of the referenced bitmaps in
[bmp.md](bmp.md#3-terrain-sets). Keys in `[terrainfiles]`/`[terrainsize]` are two-digit
terrain pairs written as strings (`01`, `12`); parse as two digits, not as an integer.
