# Appendix B — Legacy data files & formats reference

What was established by surface inspection during research. Everything marked ⚠ must be confirmed by RE (TDD §23). Formal format specs go to `docs/formats/*.md` as RE progresses.

## B.1 Text data files (`DATA/`)

General syntax: Windows INI (`[section]`, `key = value`, `;` comments), CRLF line endings, **codepage of the edition** (CP1251 in the research install). Some sections contain bare values (`[accept]` → `allunits`; `[euro]` in `HEROES.INI` → one name per line). Keys may repeat across sections with per-civ variants: `[general]`, `[euro]`, `[asia]`, `[arab]`, `[comm]`.

| File | Purpose | Key structure (observed) |
|---|---|---|
| `{EURO,ASIA,ARAB,COMM} UNITS.TXT` | Unit stats per civ (+ common: siege, ships, towers, animals, creatures) | TSV columns: *display name* \| `CLASS` \| `COST` (e.g. `"90f, 40g"`; suffixes f=food w=wood s=stone g=gold m=mana) \| `TIME` (s) \| `SIGHT` \| `RANGE` \| `HEALTH` \| `ATTACK` \| `DEFENCE` \| `SPEED` \| `RATE` \| `SPECIAL` (producer, e.g. `Barracks`, `Spawn`, `From Peasant`, `Gate`) \| `DEPEND ON` (tech id or `*`). Blank lines separate groups. |
| `{EURO,ASIA,ARAB,COMM} BUILDINGS.TXT` | Building stats | TSV: name \| `CLASS` \| `COST` \| `TIME` \| `SIGHT` \| `RESISTANCE` \| `HEALTH` \| `EMIT (own/other)` (e.g. `"+400,-200"`; ⚠ influence on capture resistance) \| `DEPENDS ON` |
| `TECHNOLOGIES.INI` | 87 technologies | `[te_xxx]` → `displayname`, `time` (ms), cost keys (`gold`, …) |
| `UPGRADES.INI` | 12 upgrades | `[to_xxx]` → `displayname`, `time`, cost (e.g. `to_wtower2`, `to_stower`, `to_stower2_1/2`, `to_wall_stone`, `to_gate_stone`) |
| `TECHTREE {EURO,ASIA,ARAB}.INI` | Dependencies | `[depend]` → `unit_or_building_or_tech = required_tech` |
| `BONUS.INI` | Damage bonus matrix | `[groups]` → `group = list of unit classes` (an optional civ prefix `e `, `a `, `r ` restricts to a civ); then `[attacker_group_or_class]` → `target_group_or_class = ±N%` |
| `RES.INI` | Starting resources (Low/Medium/High: gold, stone, iron, food, wood), resource node sizes (`golden rocks = 20000`, `iron rocks`, `rocks = 10000`, `fish patch = 500`), `[ResItem] amount = 100` | |
| `HEROES.INI` | Hero name pools per civ (`[euro]`, `[arab]`, `[asia]`, `[comm]`) | bare lines |
| `SOUNDS.INI` | Sound events and `[priority]` per category (e.g. `attack_close = 170`, `fire = 255`, `death = 200`) | |
| `TAUNTS.INI` | `n = file.wav/text` | |
| `KEYS.INI` | `[Ctrl]` build hotkeys `A = stable`, `B = barrack`, … (comma lists = per-civ alternatives) | |
| `MENU.INI` (+ `ORG  MENU.INI` backup) | Command-card contents per object/menu with hotkey letters (`[e_tavern] trade1 = T, gamble = G, …`) | |
| `ORDERS.INI` | 293 orders: `ACTION`, `PLACE` (cursor/placement object), `IMG` (icon index), `SUBMENU` | |
| `ICONS.INI` | 240 icon definitions | |
| `IMAGES.INI` | 699 image definitions (see B.2) | |
| `LAYOUT.INI` | HUD layouts `[LAYOUT800H/L/R]`, `[LAYOUT1024…]`: `layout` (1 down, 2 left, 3 right), `image`, `view` rect, `minimap`, `mscr1`, resource fields, buttons, `clock` | |
| `GENERAL.INI` | `[general] ver=9` | |
| `EQUATIONS.INI` | `[Equations] EnableQuestButton = QuestCount` (UI enable conditions) | |
| `TREES.INI` | Tree strip images and positions, `chopped = 3`, `bushes = 8` | |
| `CREDITSFNT.INI`, `TEMP.INI` | Misc | |
| `CHANGES.TXT` | Balance-change notes shipped with the official build (not read by the game) | |
| `OBJECTS/*.INI` (136) | Object classes. `[general] class = …, type = unit / building, capture / …`; per-civ sections with `image`, `image_die/attack/idle`, `image_c`, `menu`, `order`, `icon`, `icon_hero`, `buildicon`, `sound_*`, `max_obj_in_holder`, `pop_count`; `[damage] 00/01/02 = %take,%distribute`; `[accept]` | |
| `OBJECTS/TZAREDIT/*.INI` | Editor palettes (`RESOURCES.INI`, `UNITS.INI`) | |
| `AI/*.ai, *.ais`, `AI.INI` | AI scripts (Appendix D) | |

Terrain set files (`IMAGES/TERRAIN/`):
* `<SET>.TERRAIN.INI`: `[terrain] water, ground, name, start, dir, clouds, snow, rain`; `[terrainname] index = name`; `[terrainfiles] pair = file.bmp` (e.g. `12 = 12.bmp` transitions between terrain 1 and 2; some pairs reuse files); `[terrainsize] pair = cols,rows,frames` (`11 = 32,32,0` → a 32×32-tile base texture; `12 = 2,14,0` → 2 variants × 14 transition shapes; `01 = 2,14,4` → animated shore with 4 frames); `[trees]`, `[treename]`, `[treeterrain]`, `[randomterrainset]`.
* `<SET>.IMAGES.INI` and `COMMON.IMAGES.INI`: scenery/ground objects (`image`, `selection`, `object`, `terrains = land|water`, `displayname`).
* Per-set BMPs: `00.BMP` (water, 512×2560), `11/22/33/44.BMP` (1024×1024 base textures), `55/66/77.BMP` (512×512), transitions (`12.BMP` 64×448, `18/19/28/29/48/49.BMP` 192×448), `LIGHT.BMP` (512×512 light pattern), `TREES.BMP` (2976×128), `TREES_S.BMP` (3968×96 shadows), `SCENERY/`.

Localization (`LOCAL/`): `STRINGS*.INI` (`[general] key = text`), `HELP.INI`, `CREDITS.TXT`, all codepage-dependent.

## B.2 `IMAGES.INI` sprite definitions

```ini
[a_archer_die]
image = unit\asian archer die.bmp     ; logical path; resolved to IMAGES\UNIT\ASIAN ARCHER DIE.RLE (+ .DAT)
object = 53,32,32,32                  ; ⚠ x,y offset of the object box within a frame + its size
selection = 56,36,25,25               ; selection box (x,y,w,h)
rows = 16                             ; frames
cols = 8                              ; directions
anim_time = 25150                     ; total ms (= sum of delays)
delay = 100,150,150,150,100,100,100,150,150,4000,2500,2500,3500,3500,4000,4000
actionrow = 9                         ; ⚠ frame index of the action (hit/death event)
move_len = 50                         ; (walk sheets) ⚠ pixels moved per animation cycle
```

Buildings use `object = x,y,w,h` plus `selection`. `_c` entries (`building\mask\… c.bmp`) carry an `object = x,y` offset, and `_s` entries are shadows.

## B.3 Binary formats (surface observations)

### B.3.1 `.wdt` data pack
Documented by the community (QuickBMS script `tzar.bms` by Luigi Auriemma; Arch.exe):
* Optional wrapper: magic `"LZSS"`, `uint32 totalSize`, `uint32 chunkSize`, `uint16 lzssType`, then a table of `uint32` chunk offsets. Chunks are stored raw if `compressed size == chunkSize`, otherwise compressed with a Tzar-specific LZSS variant.
* Inner (decompressed) image: magic `"HMMSYS PackFile"`; at offset `0x20`: `uint32 fileCount`, `uint32 ?`; then per file: `uint8 nameLen`, `uint8 sharedPrefixLen` (front coding against the previous name), `nameLen - sharedPrefixLen` name bytes, `uint32 offset`, `uint32 size`.
* Implement from this description (our own code). Do not copy QuickBMS source (GPL).

### B.3.2 `.RLE` sprite sheet
```
00: 20 65 6C 72        " elr"  (magic, "rle " byte-reversed)
04: uint32             directions? (8 for units, 1 for single-direction)
08: uint32             frames per direction? (e.g. 0x0D = 13)
0C: uint32             0
10: "RLE\0"
14: uint16 w?, uint16 h? …   (e.g. 0x0032,0x1450 / 0x00B7,0x00AA)
…   pixel-format masks incl. 0x03E0 (RGB555 green) …
…   frame offset table, run-length encoded pixel data
```
⚠ All field meanings beyond the magics need RE (UNK-01). Verify with the community converter RLEsBMPs (RLE ↔ BMP) as an oracle.

### B.3.3 `.DAT` (sprite metadata)
* Unit DATs: ≈ 8 bytes header, then repeated small records of `uint16` values (e.g. `68 00 11 00 02 00 23 00 2e 00 10 00 …`). ⚠ Hypothesis: per-frame bounding boxes/offsets (x, y, w, h) per direction.
* Building DATs (18–72 bytes): e.g. `00 00 00 00 9f 00 9f 00 02 00 00 00 02 00 …`. ⚠ Hypothesis: footprint/size and anchor data.

### B.3.4 BMP
Standard Windows BMP, 16 bpp, `BI_RGB` (→ RGB555). ⚠ Transparency color key to confirm (UNK-31).

### B.3.5 `GAME.PAL`
`"PALF"` + header + 256 × 4 bytes (1,036 bytes total). ⚠ Usage unknown (minimap/player colors?).

### B.3.6 HPFS containers (`.wmp`, `.wcm`; `.wsv` presumably — no sample)
```
00: "HPFS" 00 02 00 00 | uint32 (e.g. 0x46 / 0x61F6) | FF FF FF FF | …
```
Campaigns embed entries with readable names: `maps.ini`, `midis.ini`, `USER\<name>.bmp`, `USER\<n>.mid`, map files, AI scripts (`random.ais`). ⚠ Directory structure, compression and the map binary schema are unknown (UNK-04). Suggested method: create minimal maps in the original editor, apply one change at a time, save and diff.

### B.3.7 `.wdm` demo
Magic `"medt"`. Not needed (DEC-12).

### B.3.8 `.DDV` video
```
00: "DDV\0" | uint32 1 | uint32 3 | uint32 25 (fps) | uint32 251 (frames) | uint32 0 | uint32 320 | uint32 240 | … frame offset table …
```
⚠ Codec unknown (UNK-05).

### B.3.9 `.FNT` font
Begins with the font face name (`"Small Fonts"`), followed by metrics and glyph bitmaps. ⚠ UNK-06.

## B.4 Audio
* WAV PCM 22,050 Hz, 16-bit, mono (story-screen sounds may be stereo per the editor manual).
* MIDI type 0/1 (General MIDI). User resources in maps: BMP (high color; icons 44×44, big icons 96×96, scroll/story images ~295×390), WAV (22 kHz 16-bit recommended), MIDI.
