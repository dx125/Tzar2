# HPFS containers (`.wmp` maps, `.wcm` campaigns)

Status: **container resolved** (all 7 maps, both campaigns and the 26 maps embedded in the
campaigns read with zero inconsistencies). Map payload schema: **open**, only the outline
below is known. Partially resolves **UNK-04** and RISK-02.
Prototype: [`tools/re/hpfs.py`](../../tools/re/hpfs.py) (`--recurse` opens embedded maps,
`--extract <dir>` writes the members; extract only into a scratch directory, never the repo).

HPFS is a small block file system with 512-byte blocks, inode blocks and directories.
(The name has nothing to do with OS/2 HPFS.) Saves (`.wsv`) are presumably the same, no
sample exists.

## 1. Header (block 0)

| Offset | Type | Meaning |
|---|---|---|
| 0x00 | char[4] | `HPFS` |
| 0x04 | uint32 | block size, always 512 |
| 0x08 | uint32 | total block count `N` (including block 0) |
| 0x0C | int32 | −1 in all files (⚠ unknown; free-list sentinel?) |
| 0x10 | uint32 | number of free blocks (maps: 1–87; campaigns: 0) |
| 0x14 | uint32 | maps: index of the free-list block (always `N − 1`); campaigns: the ASCII tag `TZAR` |
| 0x18 | uint32 | 8 (⚠ unknown) |
| 0x1C | uint32 | 1 (⚠ unknown) |
| 0x11A | uint32 + string | maps only: a small record (value 3) followed by a NUL-terminated path such as `MAPS\<name>tzar` (editor's internal temp name); ignore |

The root directory inode is **block 1**. The rest of block 0 is zero.

File size: maps are exactly `N × 512` bytes. **Campaigns (and maps stored inside them) are
truncated**: the last block is only partially present (both campaigns end 163 bytes into
their last block). Readers MUST zero-pad to `N × 512` before reading.

## 2. Inodes

An inode occupies one block:

| Offset | Type | Meaning |
|---|---|---|
| 0 | uint32 | `size` — content length in bytes |
| 4 | uint32 | `level` — 0 = direct, 1 = single indirect (≥ 2 never seen; see below) |
| 8 | uint32 × 126 | block pointers |

* `level 0`: the first `ceil(size / 512)` pointers are the data blocks, in order (max
  126 × 512 = 64,512 bytes).
* `level 1`: the first `ceil(ceil(size / 512) / 128)` pointers are **index blocks**, each
  holding 128 uint32 data-block pointers; concatenate them and take the first
  `ceil(size / 512)` (max ≈ 8.2 MB).
* Generalisation for `level n` (untested, no sample): n levels of 128-way index blocks.
  The 12.8 MB campaign stores its largest member (1.2 MB) at level 1; a file over 8 MB
  would need level 2.
* Unused pointer slots are garbage (often stale); never read beyond the needed count.
* Content beyond `size` in the last block is garbage.

In the reference build 1,151 inodes are level 0 and 52 are level 1.

## 3. Directories

A directory is an inode whose content is a packed list of entries (no padding, no
terminator; the inode `size` ends the list):

| Offset | Type | Meaning |
|---|---|---|
| 0 | uint32 | inode block of the child |
| 4 | uint16 | kind: 0 = file, 1 = directory |
| 6 | uint16 | name length `L` |
| 8 | char[L] | name, no NUL, ANSI code page of the edition (ASCII in EN) |

Standard tree written by the editor: `DATA/`, `MAPS/`, `IMAGES/`, `SOUND/`, `MUSIC/`,
`LOCAL/`, `USER/`, plus `AI/` and `MISC/` in some files and `DATA/ai/` once. Map members live
in `MAPS/`; user resources (`*.BMP`, `*.MID`, `*.WAV`) in `USER/` (reference build:
84 + 36 BMPs, 260 + 1 MIDs).

## 4. Free space

Maps keep deleted members' blocks: e.g. strings of old `mp1.events`/`mp1.rules` survive in
unreferenced blocks. The free-list block (`N − 1`) starts with a uint32 0 followed by a
stack of free block numbers; the header's free count says how many are valid (⚠ exact
convention unverified; irrelevant for reading). Campaigns are packed by `TzarCampaign.exe`
and have no free blocks. Validation in the prototype: every block is referenced at most
once; maps have exactly their free count unreferenced, campaigns none.

## 5. Map members (outline for RE-03)

A map (`<name>.wmp`, top level or `MAPS/<name>.wmp` inside a campaign) contains, in
`MAPS/`, files named `<MAPNAME>.<part>`:

| Part | Seen in | Notes |
|---|---|---|
| `header` | all 33 maps | always 1,570 bytes |
| `description` | all | plain text |
| `kingdominfo` | all | 1,216 bytes in the small maps (8 kingdoms × 152?) ⚠ |
| `diplomacy` | all | 256 bytes = 8 × 8 × uint32? ⚠ |
| `forbidden.buildings/.units/.researches/.upgrades`, `initial.researches` | all | 8 bytes when empty |
| `tiles` | all | **`side × side` uint16** (side ∈ 64, 96, 128, 192, 256, 384, 512 — exactly the editor's map sizes); tile word encoding open (UNK-10) |
| `objects`, `objext` | all / 21 | object placements ⚠ |
| `conv` | all | conversations |
| `events`, `rules`, `actions`, `defeat.messages` | campaign maps | triggers (RE-04) |
| `maps.ini`, `midis.ini` | all | INI (see [ini-tsv.md](ini-tsv.md)); campaigns add `campaign description.ini` |

Campaigns contain `MAPS/maps.ini`, `MAPS/campaign description.ini` and the chapter maps as
whole nested HPFS images (`MAPS/*.wmp`).
