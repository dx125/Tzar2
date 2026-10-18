# Legacy maps and campaigns: the `.wmp` / `.wcm` payload

Status: **resolved for conversion**. All 7 maps and both campaigns of the reference build, including the 26 maps
embedded in the campaigns, decode with zero warnings. Every trigger record decodes, and its fields consume its
payload exactly. The open points are marked ⚠ UNVERIFIED with an UNK id (§10). Resolves most of **UNK-04** (RE-03,
RE-04).

The container (blocks, inodes, directories) is in [hpfs.md](hpfs.md). This document covers what the members mean.
Reader: `Tzar2.Formats.Maps.LegacyMapReader`. Converter: `Tzar2.Import.Maps` ([ADR 0121](../decisions/0121-legacy-map-and-campaign-converter.md)).
Prototype dump: [`tools/re/tzmap.py`](../../tools/re/tzmap.py).

## 0. Method

The original editor can't be run on the development machine, so the editor-diff method of TDD §23.2 was replaced
by two sources of evidence, which were then checked against each other:

1. **The editor's own map API.** `TzarEdit.dll` exports its map API as C++ functions with decorated names: read/write
   of the header, kingdoms, tiles, objects, object extensions and trigger records, plus one creation helper per
   object kind. Their names and parameter types reveal the record structure. Their read/write sequences give the
   field order and widths. The trigger action and event classes of `TzarEdit.exe` (MFC runtime classes, one
   serialisation method each) give the type code and payload layout of every trigger type. The editor's string table
   (in `Tzar.dll`) names the orders, categories and settings.
2. **Statistics over the 33 maps.** Each hypothesis was tested by decoding every record of every map and requiring
   exact length consumption, consistent counts against the header, and plausible value distributions: owners per
   class, races of civilisation-only classes, flag bits of named heroes, and corner agreement between neighbouring
   tiles.

Only facts are recorded here: layouts, codes and the meaning of values. No code or data is reproduced.

## 1. Members

A map container holds, in `MAPS/`, members named `<map>.<part>`. The case of `<map>` may differ between parts;
match it case-insensitively against the `.header` member. Its other directories hold user resources
(`USER/*.bmp|wav|mid`) and, rarely, AI scripts (`DATA/ai/*.ais`).

| Part | Size | Content |
|---|---|---|
| `header` | 1,570 | §2 |
| `description` | header 0x116 | description text, NUL-terminated |
| `kingdominfo` | 8 × 152 | §3 |
| `diplomacy` | 8 × 8 × 4 | relation flags, row = kingdom, column = other kingdom (§3.2) |
| `forbidden.buildings`, `.units`, `.researches`, `.upgrades`, `initial.researches` | var. | 8 name lists, one per kingdom. Each name is NUL-terminated; each list ends with an empty name (one extra NUL). Empty lists = 8 NULs. |
| `tiles` | side² × 2 | §5 |
| `objects` | var. | §4 |
| `objext` | n × 258 | §4.3; byte size in header 0x16 |
| `events`, `actions`, `rules` | var. | §6; record counts in the header |
| `conv` | header 0x122 | §7 |
| `defeat.messages` | 16 | ⚠ UNVERIFIED (UNK-72): 8 words, all zero except in four maps (values 0/0x0100) |
| `MAPS/maps.ini` | — | `[MAPS] <map>=` (the map lists itself) |
| `MAPS/midis.ini` | — | custom music: `[MUSIC] SONGSNUMBER=n`, `SONG1…n = USER\<file>.mid` |

All integers are little-endian. Strings are bytes in the edition's ANSI code page. Fixed-size fields hold a
NUL-terminated string and junk after the NUL (stale editor memory, never meaningful). Variable-length fields are
NUL-terminated.

## 2. Header (1,570 bytes)

| Offset | Type | Meaning |
|---|---|---|
| 0x000 | u32 | magic, the ASCII bytes `2mzt` |
| 0x004 | u16 | 0 |
| 0x006 | u16 | map side in tiles (64, 96, 128, 192, 256, 384, 512) |
| 0x008 | u32 | number of object records |
| 0x00C | u16 | number of rules |
| 0x00E | u16 | number of actions |
| 0x010 | u16 | number of events |
| 0x012 | u32 | ⚠ UNVERIFIED (UNK-71): varies 82…2,230, unrelated to member sizes |
| 0x016 | u32 | byte size of `objext` |
| 0x01A | u32 | byte size of `tiles` |
| 0x024 | u32 | junk (uninitialised or a seed) |
| 0x04C | u8 | 2 in every map, ⚠ UNVERIFIED (UNK-71) |
| 0x04D | u8 | player count shown by the editor |
| 0x050 | u8 | difficulty: 0 easy, 1 normal, 2 hard |
| 0x051 | u8 | 8 (kingdom slots) |
| 0x056 | char[64] | title |
| 0x096 | char[64] | author |
| 0x0D6 | char[64] | terrain set (`grass`, `summer`, `desert`, `snow`) |
| 0x116 | u32 | byte size of `description` |
| 0x11A | u32 | ⚠ UNVERIFIED (UNK-71): small counter, 5…469 |
| 0x122 | u32 | byte size of `conv` |
| 0x128 | u16 | population limit (200; the tutorial intro has 50) |
| 0x12A | u16 | 30, presumably the time-limit default in minutes (UNK-71) |
| 0x12E | u16 | 100, presumably the score-limit default (UNK-71) |
| 0x132 | u32 | map file version, 7. The editor refuses other versions. |
| 0x138–0x13A | u8 × 3 | ⚠ UNVERIFIED (UNK-71): 0/3, 0/1, 0/1. Campaign maps set 0x13A, the tutorial maps also 0x138/0x139. Candidates: game type, victory condition, fog, explore. |
| 0x51A–0x621 | — | runtime state of the editor's map handle (container handle, temp path, object cursor); ignore |

The game type, victory condition, No Fog and Explore settings are not located yet (UNK-71). The converter writes
the defaults (Standard, Standard, fog on, unexplored).

## 3. Kingdoms

### 3.1 `kingdominfo`: 8 records of 152 bytes

| Offset | Type | Meaning |
|---|---|---|
| 0x00 | char[32] | kingdom name |
| 0x20 | char[32] | Tzar name |
| 0x40 | u8 | active (1/0) |
| 0x42 | u8 | colour slot 1–8 |
| 0x43 | u8 | equals the kingdom number in every map, ⚠ UNVERIFIED |
| 0x44 | u32 × 4 | starting resources. The order is ⚠ UNVERIFIED (UNK-71); the tutorial's numbers suggest **gold, stone, food, wood**. |
| 0x54 | u16, u16 | start location tile (x, y); 0xFFFF = none |
| 0x58 | u8 | civilisation: 1 European, 2 Arabian, 3 Asian. 0 (random) is never seen. |
| 0x59 | char[32] | AI script file name (one of `DATA/AI/*.ais`); empty = default |
| 0x79 | u8 | ⚠ UNVERIFIED (UNK-71): 0–3, read as bit 0 economy AI, bit 1 war AI |
| 0x7A | u8 | ⚠ UNVERIFIED (UNK-71): 0/1, read as player type (0 human-or-AI, 1 AI only, 2 rescue) |

### 3.2 `diplomacy`: 64 × u32 flags

Bit 0x40 = ally, 0x20 = neutral (⚠ assumed; never seen), 0x10 = enemy. For allies: 0x01 shared vision,
0x02 shared resources, 0x04 command units. The diagonal holds 0x48 for active kingdoms and 0 for inactive ones;
bit 0x08 is read as "shared victory" (⚠ UNVERIFIED, UNK-71). The trigger action *Change Diplomacy Settings* uses
the same flags.

## 4. Objects

### 4.1 Records

`objects` is a sequence of records, as many as header 0x08 says:

| Offset | Type | Meaning |
|---|---|---|
| 0 | u8 | record kind (0 in every map) |
| 1 | u32 | class, encoded as `(group << 16) | (index − first index of the group)` |
| 5 | u32 | payload size |
| 9 | bytes | payload |

**Classes.** Objects, spawn actions and object specifications name classes by an index into a built-in table of
144 class names. The names are the `class =` values of `DATA/OBJECTS/*.INI`, e.g. 1 `peon`, 8 `knight`, 44
`castle`, 110 `cow`. Groups start at indices 0, 1, 3, 39, 44 and 74; group 0 is unused. The table is reproduced as an
enumeration in `LegacyClasses`, the one piece of the original's internal ordering the converter has to know. Only
the record header's class is authoritative. Inside the payload, word 0x02 holds the creating class: decorations
store `animation` there.

### 4.2 Common payload (56 bytes, every class)

| Offset | Type | Meaning |
|---|---|---|
| 0x00 | u16 | payload size again |
| 0x02 | u16 | creating class index |
| 0x04 | u16 | flags (below) |
| 0x08 | i16 | tile x (structures: top-left tile of the footprint) |
| 0x0A | i16 | tile y |
| 0x0C | u8 | owner: 0 none (resources, decorations, items), 1–8 kingdoms, **9 Rescue**, **10 Nature** (animals, birds, monsters) |
| 0x0D | u8 | race: 0 common, 1 European, 2 Arabian, 3 Asian |
| 0x0E | u16 | object id. Triggers, conversations and `objext` refer to it. 0xFFFF = none. |
| 0x10 | u16 | facing 0–7 (⚠ assumed sprite order: 0 south, counter-clockwise, UNK-82) |
| 0x12 | u16 | health |
| 0x14 | u16 | max health |
| 0x16 | u16 | attack |
| 0x18 | u16 | defence |
| 0x1A | u16 | sight |
| 0x1C | u16 | level, ⚠ UNVERIFIED (0 in every map) |
| 0x20 | u16 | ⚠ UNVERIFIED (once 30,000) |
| 0x24 | u16 | mana |
| 0x28–0x37 | — | 0 |

Flags: 0x0040 health, 0x0080 max health, 0x0100 attack, 0x0200 defence, 0x0400 level (units) / resistance
(buildings) ⚠, 0x0800 mana are **"use default"** bits. A clear bit means the value above was set in the editor.
Then 0x1000 = **Persistent**, 0x2000 = **No AI**, 0x4000 = **Not Appear**.

Evidence: every named hero has 0x1000; the "No AI" groups of footmen and ships have 0x2000; buildings use only
0x4000; a mage with custom mana has 0x0800 clear.

### 4.3 `objext`: title and portrait (258 bytes per record)

| Offset | Type | Meaning |
|---|---|---|
| 0 | u16 | object id |
| 2 | char[128] | title. This is the object's name for triggers and campaign persistence. |
| 130 | char[128] | portrait path, e.g. `USER\<name>.bmp`; a small variant `USER\<name> small.bmp` exists beside it. |

### 4.4 Class extensions (from offset 0x38)

| Classes | Size | Extension |
|---|---|---|
| `wall_stone`, `wall_wood` | 58 | u16 segment shape |
| `gate_stone`, `gate_wood` | 58 | u8 placement: bit 1 = vertical, bit 0 = the other side; u8 open (1) |
| `bridge` | 58 | u8 axis: 0 = east-west (horizontal sprite), 1 = north-south (vertical sprite) |
| `fishpatch` | 60 | u32 amount |
| `res_gold`, `res_stone` | 72 | u16 resource (0 gold, 1 stone), u32 amount, u8 × 2 variant; then junk |
| `ruin` | 88 | char[32] image name |
| `ground_object`, `ground_patch` (decorations) | 116 | char[32] image name, …, u16 pixel x at 0x66, u16 pixel y at 0x68 (sub-tile placement) |
| `item`, `item_plusplus`, `item_health`, `item_soul` | 96/106 | char[40] item name; 106-byte records add u16 value at 0x60 |
| `item_magic` | 112 | char[40] name; at 0x60: u8 magic type, u8, u8, u8 needs-mana, u16 time restriction, u16 max charges |
| `item_scroll` | 1,318 | char[40] name; char[1,024] text; char[64] image, sound, music; u32 scroll time (ms) at 0x520; u8 auto-close |
| `resitem` | 100 | char[40] name; u8 resource type at 0x60, u16 amount at 0x62 |

Gate and bridge bytes were checked against their surroundings in every map. Of 42 gates, 17 of the 18 with bit 1 set
sit in walls running north-south (one has no wall beside it), and 22 of the 24 without it sit in east-west walls (one
is in a north-south wall, one stands alone); the second byte marks gates placed open. All 30 bridges with value 1
span water along their north-south axis, and 20 of the 21 with 0 along the east-west one (one is undecided).
A bridge's top-left tile is the record's x/y; the vertical sprite covers about 6 × 11 tiles, the horizontal one
11 × 7.

Custom ambient sounds are editor objects of class `sound_object`. None of the 33 maps has one, so their layout is
⚠ UNVERIFIED (UNK-77). The converter would keep them as raw placeholders.

## 5. Tiles

`tiles` holds side × side words, row-major. Each word describes one tile:

| Bits | Meaning |
|---|---|
| 0–3 | transition shape `s` (0–13) |
| 4–9 | terrain pair `a·8 + b` (a, b = terrain-set indices 0–7, 0 = water) |
| 10 | cliff: then bits 0–9 are **not** terrain (below) |
| 11–15 | tree: 0 none, 1–31 = cell of the set's tree strip (1–3 stumps, 4–5 bushes, 6–10 pines, 11–31 deciduous; `[treename]` counts) |

A **cliff tile** (bit 10) stores a cliff piece instead of terrain in bits 0–9. Pieces are 2 × 2 tiles: bit 0 is the
column and bit 4 the row inside the piece, bits 1–3 a variant, bits 5–8 the shape (4 corner/end, 5 horizontal run,
6 vertical run, up to 10), and bit 9 selects the second cliff graphic set. The terrain under a cliff is not stored;
the converter takes it from the nearest non-cliff corner. Whether every cliff tile blocks movement, or only part of a
piece, is ⚠ UNVERIFIED (UNK-81): in one campaign map, cliff rings fully enclose a goal area that is presumably
reached on foot.

When a = b the tile is solid. Otherwise its corner mask is `m = s + 1`, with bits TL = 1, TR = 2, BR = 4, BL = 8. A
set bit means that corner is terrain `a`, a clear bit means `b`. This matches the transition-strip rows of
[bmp.md §3.2](bmp.md), where `a` is the magenta key. Corners derived this way agree between neighbouring non-cliff tiles
for more than 99 % of the shared corner pairs in the reference maps. The converter builds the (side+1)² corner grid
by majority vote and reports the disagreeing corners. The variant column of a transition is not stored (UNK-10).

## 6. Triggers

### 6.1 Records (`events`, `actions`, `rules`)

| Offset | Type | Meaning |
|---|---|---|
| 0x00 | char[32] | name |
| 0x20 | u16 | events/actions: type code (§6.2); rules: number of actions |
| 0x22 | u16 | payload length |
| 0x24 | bytes | payload |

A **rule** payload is the condition text followed by the action names, all NUL-terminated. Keep Rule is an ordinary
named action. Actions and events are referred to **by name** everywhere: by rules, by Remove Quest and Stop Timer,
by expressions (event names, timer names = Set Timer action names), and by the Conversation action (conversation
names).

### 6.2 Type codes

Actions: 0 Add Quest, 1 Remove Quest, 2 Change Diplomacy Settings, 3 Change Object Owner, 4 Set Object Properties,
5 Expression, 6 Set Timer, 7 Stop Timer, 8 Player Lose, 9 Player Win, 10 Keep Rule, 11 Load Map, 12 Lock View,
13 Send Chat Message, 14 Show Story Screen, 15 Show Message, 16 Spawn Object, 17 Unlock View, 18 Object Order,
19 Scroll View, 20 Conversation, 21 Destroy Object, 22 Explore Area, 23 Appear Object, 24 Change Player Status,
25 Lock/Unlock Units, 26 Erase Object, **27 Delay** (not in the manual; UNK-76).

Events: 1000 Object Exist, 1001 Object Destroyed, 1002 Objects Enter Holder, 1003 Object Leave Holder,
1004 Object In Area, 1005 Player Defeated, 1006 Player Won, 1007 Object Expression.

### 6.3 Shared payload parts

**Object specification:**

| Field | Type | Meaning |
|---|---|---|
| typed | u8 | 0 = explicit set of objects, 1 = class/category with players and a count |
| at most | u8 | count comparison: 0 ≥, 1 ≤ |
| class | u16 | class index, or a category: 0xFFFF any object, 0xFFFE units, 0xFFFD military units, 0xFFFC buildings, 0xFFFB "major objects" |
| players | u16 | player mask, bit 0 = player 1 |
| all | u8 | quantifier, read as 1 All / 0 Any (⚠ UNVERIFIED, UNK-75) |
| race | u8 | race of the class (typed form) |
| n | u16 | number of object ids |
| ids | u16 × n | object ids (explicit form) |
| count | string | the count (decimal) |

**Area:**

| Field | Type | Meaning |
|---|---|---|
| kind | u8 | 0 rectangle, 1 around an object |
| left, top, right, bottom | u32 × 4 | tile rectangle |
| object | u16 | object id (kind 1) |
| distance | string | radius in tiles around the object (kind 1) |

The editor lets rectangles reach one tile past the right or bottom edge, and occasionally to −1. Whether right and
bottom are inclusive is ⚠ UNVERIFIED (UNK-80). The converter clamps to the map.

**Holder:** u8 kind (0 a specific object, 1 any holder of a class owned by players), u16 class (0xFFFF any),
u16 object id, u16 player mask.

### 6.4 Payloads per type

Numbers that the editor takes from text boxes are stored as text, including times, counts and distances. They are
decimal in every map.

| Type | Payload |
|---|---|
| Add Quest | text, image |
| Remove Quest | Add Quest action name |
| Change Diplomacy | u8 kingdom, u8 kingdom, u32 relation flags (§3.2) |
| Change Object Owner | objects, u8 kingdom |
| Set Object Properties | objects, expression |
| Expression | expression |
| Set Timer | initial value (expression), u8 count down |
| Stop Timer | Set Timer action name |
| Player Lose / Player Win | u16 players, text `next map|?`. The second part is ⚠ UNVERIFIED (UNK-79); both parts are empty in every original map. |
| Keep Rule, Unlock View | — |
| Load Map | map path, `MAPS\<name>.wmp` |
| Lock View | time (ms), area |
| Send Chat Message | text, u16 players |
| Show Story Screen | text, image, sound, music, scroll time (ms), u8 auto-close |
| Show Message | title, text, image |
| Spawn Object | u16 class, count, u8 owner, area, u8 race |
| Object Order | objects, area, u8 order (§6.5) |
| Scroll View | area from, area to |
| Conversation | conversation name |
| Destroy / Erase / Appear Object | objects |
| Explore Area | area, time (ms), u8 reveal fog |
| Change Player Status | u8 kingdom, u8 cannot win, u8 cannot lose |
| Lock/Unlock Units | objects, u8: **0 lock, 1 unlock** |
| Delay | time (ms) |
| Object Exist / Destroyed | objects |
| Enter / Leave Holder | objects, holder |
| Object In Area | objects, area |
| Player Defeated / Won | u16 players |
| Object Expression | objects, expression |

### 6.5 Order codes (Object Order)

0 default action, 1 move, 2 attack, 3 capture, 4 load, 5 unload, 6 pick items, 7 patrol, 8 move advance (attack
move), 9 guard, 10 stand ground, 11 attack ground, 12 stop, 13 drop items. These follow the editor's order list.
The corpus confirms 0, 1, 2, 5, 6, 7, 8 and 9: the authors' action names describe digging, unloading, patrolling,
guarding and so on.

## 7. Conversations (`conv`)

| Field | Type |
|---|---|
| conversation count | u16 |
| per conversation: name | string |
| phrase count | u16 |
| per phrase: id | u16 (follow-ups refer to it) |
| speaker | u16 object id; 0 = none |
| initial | u8 |
| name, condition, result, icon, sound, text | 6 strings |
| follow-up count, follow-up ids | u16, u16 × n |

The original allows **8 initial phrases** per conversation. The manual says 6; the editor's own message says 8.

## 8. Campaigns (`.wcm`)

`MAPS/campaign description.ini` is not an INI file. Its first line is the campaign title and the rest, up to a NUL,
is the description. `MAPS/maps.ini` lists the maps in order under `[maps]`: `<name>=yes|no`, where `yes` marks a
starting map. Each map is embedded whole as `MAPS/<name>.wmp`, a nested HPFS image. User resources live inside each
map, so maps share many identical files. The campaign order itself is only in the maps' Load Map actions: in the
reference campaign, 19 maps and 19 Load Map edges, all resolvable, with none unreachable.

## 9. Expressions

Expressions are text in the language of Appendix C.6, plus what the game's own name table and the maps show:

* **Statements** are separated by line breaks, `;` or a top-level `,`. A line break next to an operator or inside
  parentheses continues the expression (`a && b &&⏎!c`).
* **Reserved variables** (spelled as the game spells them): `$health $maxhealth $attack $defence $level $manna
  $maxmanna $kingdom $race $gold $stone $food $wood $difficulty $underfog $flags $program $inholder $isselected
  $cargo $canusediplomacy $candelobjects`. `$x`/`$y` (from the manual) are not in the game's table, ⚠ UNVERIFIED
  (UNK-73).
* **Functions**, case-insensitive: `@gold @stone @food @wood @rnd @max @min @count @inprogress @setnoattacktime
  @setgold @setstone @setwood @setfood @build @setpoplimit @hassharedres @hassharedunits @hassharedvision @isenemy
  @isneutral @isally @getdiplomacy @gettime @settime @god @clearselection @unselect @select @setai
  @kingdomcanrescue @str @playmovie`. `@playmovie(<path of a movie>)` takes raw text. Semantics beyond C.6 are
  UNK-73.
* **`#name`** variables appear in the campaign (`#Name = 1`); presumably campaign-scoped (UNK-74).
* **Timers are assignable** (`<timer> = 0` resets a timer).
* Unknown `@names` (`@Name = 1`) are accepted by the original editor. The game reports "Assigning value to
  const/function" at run time; the converter treats them as unknown names that evaluate to 0.

## 10. Unknowns (this document)

| UNK | Topic |
|---|---|
| UNK-70 | Placement of decorations: the meaning of pixel x/y (0x66/0x68) relative to the tile anchor |
| UNK-71 | Header 0x012, 0x04C, 0x11A, 0x138–0x13A (game type, victory, fog, explore); kingdom resource order, AI flags, player type, shared victory |
| UNK-72 | `defeat.messages` |
| UNK-73 | Semantics of the reserved variables and functions beyond Appendix C.6; `$x`/`$y` |
| UNK-74 | `#variables`: campaign scope? |
| UNK-75 | The quantifier byte of object specifications (All vs Any) |
| UNK-76 | The Delay action |
| UNK-77 | Custom sound objects (`sound_object` layout) |
| UNK-78 | Which "use default" bit covers level and resistance; whether recorded values with a clear bit always override the ruleset |
| UNK-79 | The second part of Player Win/Lose's `next map|?` |
| UNK-80 | Area rectangles: inclusive or exclusive right/bottom; rectangles past the edge |
| UNK-81 | Cliff passability: whether all four tiles of a cliff piece block, and how units cross cliff rings |
| UNK-82 | Object facing (0x10): assumed to use the sprite direction order (0 = south, counter-clockwise) |

## 11. Reproducing

```sh
python3 -I tools/re/tzmap.py <install>/Maps/MP1.wmp               # summary of one map
python3 -I tools/re/tzmap.py <install>/Campaign/Tzar.wcm --triggers  # every embedded map, with trigger records
dotnet run --project src/Tzar2.Tools.Cli -- convert <install> --out <scratch>/converted   # the C# converter
```
