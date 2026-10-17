# Appendix A — Original install inventory (`Origin/`)

Snapshot of the research install taken on 2026-10-11. It was installed from a third-party **“Tzar - The Burden of the Crown (Repack)”**: the **GOG v1.10 English build** (2,859 of its 2,916 files are byte-identical) with a **Russian localization** (“Огнем и мечом”) on top, plus the community HD patch v3.4, Spanish FX expansion campaigns, community utilities and local crash logs. Total ≈ 763 MB. **Local only, never commit** (REQ-LEGAL-01). The per-file classification (original / modified / non-original) is in [`docs/original-build/README.md`](../original-build/README.md).

## A.1 Top level

| Path | Files | Size | Notes |
|---|---|---|---|
| `Tzar.exe` | 1 | 1.75 MB | Game v1.10 (build timestamp 2002-03-02). Strings reference the `DATA\*.ini/txt` tables, `MAPS\maps.ini`, `MAPS\midis.ini`, `MAPS\campaign description.ini`, `setup\tzar\packs\images.wdt`, `Logs\Game.txt` |
| `TzarHD.exe` | 1 | 1.75 MB | HD-patched executable (same size, patched) |
| `Tzar.dll`, `TZAREXT.DLL` | 2 | 131 KB, 28 KB | Resource/extension DLLs |
| `TzarEdit.exe` + `TzarEdit.dll` | 2 | 569 KB + 393 KB | Original map editor (MFC-style UI, see `Editor_Manual.pdf`) |
| `TzarCampaign.exe` | 1 | 49 KB | Campaign packer |
| `TzarRunner.exe`, `TzarSettings.exe`, `HighResolution.dll` | 3 | — | HD patch launcher/settings (C#/.NET 2.0 + C++ hook DLL). Sources in `sources/` |
| `Setup.exe`, `unins000.*` | — | — | Installer leftovers |
| `manual.pdf` (64 p.), `Editor_Manual.pdf` (48 p.), `Strategy_Guide.pdf` (29 p.) | 3 | — | Primary documentation (text extracted during research) |
| `TZAR110.TXT` | 1 | 4.6 KB | Official patch notes 1.05 → 1.10 |
| `readme.txt` | 1 | — | HD patch readme (v3.0–3.4 notes) |
| `tzar.ini` | 1 | — | User settings: `[DEFAULTS] LAYOUT=LAYOUT1024L, HWCURSOR, USERNAME, SCROLLSPEED, WATERFPS=7, WEATHER, DAYNIGHT, LANGUAGE`; `[VIDEO]` DirectX device options; `[SOUND]` DirectSound/3D mixer/EAX/channels (`MIXERCHANNELS=32`), volumes, nature/building/unit sound toggles, `CDAUDIO`, `CPUSPEED` calibration |

## A.2 Folders

| Folder | Files | Size | Content |
|---|---|---|---|
| `DATA/` | 207 | 0.27 MB | Rule tables and definitions (Appendix B), `AI/` (38 scripts), `OBJECTS/` (136 object INIs + `TZAREDIT/` editor lists `RESOURCES.INI`, `UNITS.INI`) |
| `IMAGES/` | 2,123 | 178 MB | `UNIT` (543 files, 53 MB: `.RLE` + `.DAT` per animation; 151 unit sprite sets), `BUILDING` (434, 8.5 MB; `*_S` shadows, `mask/` `*_c`), `ANIMS` (98, 3.6 MB; effects), `TERRAIN` (643, 63 MB; `COMMON`, `DESERT`, `GRASS`, `SNOW`, `SUMMER` + `*.TERRAIN.INI`/`*.IMAGES.INI`; `SCENERY` subfolders), `SCENERY` (12), `ICONS` (22), `ITEMS` (66), `ORDERS` (7), `CURSORS` (29), `SCREENS` (256 BMPs in 44 dialog folders, 46 MB), `RANDOMMAP` (9) |
| `SOUND/` | 438 | 55 MB | WAV 22,050 Hz/16-bit/mono in 17 categories: ATTACKS 80, BUILDINGS 44, COMMAND 49, CREATURES 3, DIE 53, EFFECTS 8, FIRE 5, GAMBLE 2, GATE 2, NATURE 103, NOTIFY 8, RAIN 3, SELECT 1, SPECIAL 3, SPELLS 26, TAUNTS 3, WORK 24 |
| `MUSIC/` | 14 | 0.3 MB | `1–10.MID`, `INTRO.MID`, `OUTRO.MID`, `WIN1.MID`, `WIN2.MID` |
| `MOVIES/` | 3 | 24.5 MB | `HMM.DDV` (Haemimont logo), `PAN.DDV` (publisher logo), `TZAR.DDV` (intro). DDV header: 320×240, 25 fps |
| `LOCAL/` | 12 | 0.4 MB | `STRINGS.INI`, `STRINGS2.INI`, `STRINGS3.INI`, `HELP.INI`, `CREDITS.TXT` (CP1251), `FONTS/*.FNT` (ARIAL10_AA, ARIAL12B_AA, ARIAL14B_AA, AV10, CGB_22B, FIXSHAD, SMALL) |
| `HELP/` | 39 | 3.8 MB | `HELP_STR.INI`, `HK.TXT` (hotkeys), `ITEMS.TXT`, `BMP/`, `HELP_SCR/` |
| `Hero Icons/` | 58 | 0.7 MB | `Big/` (96×96) and `Small/` (44×44) hero portraits (official; GOG common depot) |
| `Maps/` | 7 | 1.2 MB | `MP1–MP6.wmp`, `intro.wmp` (HPFS containers) |
| `Campaign/` | 6 | 341 MB | `Tzar.wcm` (12.8 MB, original campaign), `TZARTUTORIAL.WCM`, and the FX Gold Edition campaigns “El Imperio de Gengis Khan” (53 MB), “Excalibur y el rey Arturo” (120 MB), “La Reconquista” (85 MB), “Los Dominios de la Magia” (67 MB). These are large because they embed custom images/sounds/music. All six files here are **Russian-translated** (the two original campaigns differ from GOG v1.10; the four FX campaigns are community translations, hashes in `docs/original-build/manifests/local-fx-campaigns-ru.tsv`) |
| `Save/` | 0 | — | `.wsv` saves (none present) |
| `Demos/` | 1 | 1.7 MB | `tzar.wdm` (recorded demo, `medt` header) |
| `Logs/` | 61 | 125 MB | 30 crash-report folders (29 with `error.log`, each with a 4 MB `error.bmp` screenshot), June–July 2025. 22 crashes are access violations inside `ntdll.dll` (typical heap-corruption signature), 7 are null/invalid reads in `TzarHD.exe`. Each log records system info and game settings (Video Renderer, HW cursors, “RLE First”, Weather, Day/Night, Scroll Speed, “Light Map”, “Fog Problems”) |
| `resolutions/` | 47 | 46 MB | HD patch: per resolution (1280×720 … 1920×1200) a `LAYOUT.INI` + `SCREEN 1024 LEFT.bmp` + `S_DEF.BMP`; `origin/` (original 1024 layout); `patched_exe/v1_00, v1_01, v1_01a, v1_10, v1_11, v1_13b` (patched EXEs for those versions) |
| `sources/` | 33 | 0.3 MB | HD patch sources: `TzarSettings` (C# WinForms), `TzarRedraw` (C++ launcher + `HighResolution` hook DLL that injects a Ctrl keypress every 15 ms to force redraws; sets `__COMPAT_LAYER=16BitColor WINXPSP2`) |
| `utils/` | 14 | 3.1 MB | `PackFileEditor/Arch.EXE` (“view/create/unpack game archives (.wdt)”), `ChangePlayerReplay/replaay-v2.3.exe` (Polish tool to switch the viewed player in replays) |

## A.3 Observations relevant to implementation
* The 12 `DATA` stat/tech tables, the 4 `*.TERRAIN.INI` files and all `LOCAL`/`HELP` texts differ from GOG v1.10 because of the Russian localization (translated display names). Whether any numbers were changed is unverified, so validate `classic` values against the clean reference files (REQ-RE-03).
* All 38 AI files (`DATA/AI`) are present and identical to GOG v1.10, including the strategy scripts listed in `AI.INI` and the included modules (`START.AI`, `RESIGN.AI`, `TALK.AI`, `R_*.AI`).
* `DATA/CHANGES.TXT` and `DATA/ORG  MENU.INI` are part of the official build (identical to GOG).
* `IMAGES.INI` references `.bmp` paths (e.g. `unit\asian archer walk.bmp`) while the files on disk are `.RLE` + `.DAT`. The game resolves a precompiled RLE cache (“RLE First”). The editor writes `DATA\Backup\Images<timestamp>.ini`.
* The exe references `DATA\sounds.ini`, `taunts.ini`, `technologies.ini`, `techtree *.ini`, `upgrades.ini`, `Res.ini`, `General.ini`, `Equations.ini`, `Keys.ini`, `layout.ini`, `menu.ini`, `orders.ini`, `icons.ini`, `heroes.ini`, `bonus.ini`, `creditsfnt.ini`, the four `* units.txt` and four `* buildings.txt` tables, `HELP\help_str.ini`, `HELP\hk.txt`, `HELP\items.txt`, `LOCAL\strings*.ini`, `LOCAL\Help.ini`, `LOCAL\credits.txt`, `IMAGES\terrain\%s.terrain.ini`, `%s.images.ini`, `common.images.ini`.
* File extensions known to the executables: `.wmp` (map), `.wcm` (campaign), `.wsv` (save), `.wdm` (demo), `.wdt` (data pack), `.wav`.
