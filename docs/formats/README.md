# Legacy format specifications

Format specs of the original game's files, written from our own analysis of the GOG v1.10
English reference build (verified 2,916/2,916 files) for the importer (`Tzar2.Formats`,
milestone M1). Work items RE-01, RE-02 and the container half of RE-03 (TDD §23).
Prototype decoders live in [`tools/re/`](../../tools/re/) (Python 3, stdlib + Pillow; run
with `python3 -I`, point them at an install folder, write outputs outside the repo).

Legal: these documents describe the formats in our own words; they contain no original
data beyond a few illustrative values (REQ-LEGAL-01/02).

## Documents

| Spec | Files covered | Status | Evidence |
|---|---|---|---|
| [rle.md](rle.md) | `IMAGES/**/*.RLE` sprite sheets (961) | resolved | 961/961 decode, 0 errors; PNG contact sheets checked visually |
| [dat.md](dat.md) | `IMAGES/**/*.DAT` sprite metadata (621) | resolved (grid values partial) | 621/621 parse; 98.1 % of frame boxes equal recomputed boxes |
| [pal.md](pal.md) | `IMAGES/GAME.PAL` | resolved | 9 ramps identified, recolouring checked visually |
| [bmp.md](bmp.md) | 16/8-bit BMPs, terrain textures, transitions, water, trees, light maps, building `_c` masks | resolved / partial (terrain selection) | 531 BMP headers surveyed; transition corner order derived for all 4 sets |
| [fnt.md](fnt.md) | `LOCAL/FONTS/*.FNT` (7) | resolved | 7/7 decode; specimen sheets correct (CP1252) |
| [ini-tsv.md](ini-tsv.md) | `DATA/**/*.INI`, `*.TXT` tables, `IMAGES.INI`, terrain INIs | resolved (syntax, code page, sprite linkage) | 177/177 INI parse, 0 warnings; 971/971 `IMAGES.INI` → RLE frame/direction counts match |
| [hpfs.md](hpfs.md) | `.wmp`, `.wcm` containers | container resolved, payload open | 7 maps + 2 campaigns + 26 embedded maps read, block accounting consistent |
| [sprite-volume.md](sprite-volume.md) | measurement for spike M0-S6 | done (decision: [ADR 0013](../decisions/0013-spike-s6-sprite-memory.md)) | — |
| [t2pkg.md](t2pkg.md) | our output: `.t2pkg` base package, `.t2spr`, `.t2img` | implemented (M1) | `t2 import` of the reference build: 0 errors, deterministic |

Not covered: `.wdt` packs (none in the EN loose-file build; Appendix B.3.1 has the
community description, needs a PL/PT GOG sample), `.DDV` (RE-11, M8), `.wdm` (DEC-12).

## Unknowns register status

| UNK | Topic | Status | Where |
|---|---|---|---|
| UNK-01 | RLE run opcodes | **resolved** | [rle.md §2–4](rle.md) |
| UNK-01a | player-colour encoding | **resolved (data)**: indices 240–255 → `GAME.PAL` ramp `112 + 16·p`; slot→ramp mapping in-game still to capture | [rle.md §5.2](rle.md), [pal.md](pal.md) |
| UNK-01b | meaning of `_c` mask images | **resolved**: player-colour overlay patches, same ramp, offset/animation from `IMAGES.INI` | [bmp.md §2](bmp.md#building-masks) |
| UNK-02 | `.DAT` fields | **resolved** (frame boxes, footprint rect); footprint grid values 0/2 hypothesis, 1 unknown | [dat.md](dat.md) |
| UNK-03 | shadow blend | **partial**: encoding known (kind-7 sheets; baked near-black-green pixels in unit sheets, heuristic); darkening factor and overlap need captures | [rle.md §5.3–5.4](rle.md) |
| UNK-04 | HPFS / map schema | **partial**: container resolved; member list known; `tiles` = side² uint16; rest open | [hpfs.md](hpfs.md) |
| UNK-06 | `.FNT` glyph format | **resolved** | [fnt.md](fnt.md) |
| UNK-10 | terrain storage / transitions | **partial**: transition tile shape order and keys known; per-tile vs per-corner storage and variant choice open (needs map `tiles`) | [bmp.md §3](bmp.md#3-terrain-sets) |
| UNK-31 | BMP transparency key | **resolved**: `0x03E0` green; terrain transitions also `0x7C1F` magenta | [bmp.md §1.2](bmp.md) |
| UNK-39 | RGB555 → RGB888 | open (bit replication used; captures needed) | [rle.md §5.1](rle.md) |
| UNK-40 | cursor hotspots | **mostly resolved**: `IMAGES.INI` `hotspot` for cursor entries | [ini-tsv.md §4](ini-tsv.md#imagesini) |
| UNK-07 | `actionrow` semantics | data side clarified (frame index, may repeat); timing open | [ini-tsv.md §4](ini-tsv.md#imagesini) |
| UNK-12 | `SPEED`/`move_len` | open (`move_len` documented as data only) | [ini-tsv.md §4](ini-tsv.md#imagesini) |

## Other findings worth knowing

* Unit sheets are direction-major in `.RLE` but frame-major in `.DAT`.
* Direction 0 = facing down (S), then counter-clockwise on screen (SE, E, NE, N, NW, W, SW).
* One RLE (`ANIMS/BRIBE.RLE`) has a surplus row; one font (`FIXSHAD.FNT`) under-reports its
  glyph count; campaigns are truncated inside their last block; `ICONS.INI` has two
  conflicting duplicate sections (`galleon`, `item_sword`). Readers must tolerate all of it.
* The EN build's text files are pure ASCII (only `CREDITS.TXT` has one CP1252 byte).

## C# implementation

`Tzar2.Formats` ports the prototypes (namespaces `Rle`, `Dat`, `Bmp`, `Pal`, `Fnt`, `Hpfs`, `Ini`, `Tsv`, `Audio`,
`Text` for the built-in CP1250/1251/1252 tables). Malformed input only ever throws `LegacyFormatException`;
`Fuzzing.FuzzTargets` are the SharpFuzz/property-test entry points. Golden sprites for the M1 exit criterion come from
`make_goldens.py` (below) and are compared in `Tzar2.Import.Tests` when `TZAR2_ORIGIN` is set.

## Reproducing

```sh
python3 -I tools/re/render_all.py <install> <scratch>/rle        # decode + PNG every RLE, volume.json
python3 -I tools/re/tzdat.py <install>                            # DAT parse + frame box check
python3 -I tools/re/check_images_ini.py <install>                 # IMAGES.INI ↔ RLE cross-check
python3 -I tools/re/tzfnt.py <install>/LOCAL/FONTS/SMALL.FNT <scratch>/small.png
python3 -I tools/re/tzini.py <install>/DATA/IMAGES.INI
python3 -I tools/re/hpfs.py <install>/Campaign/Tzar.wcm --recurse
python3 -I tools/re/make_goldens.py <install> tests/local-goldens/sprites   # ignored by git; read by the golden test
```
