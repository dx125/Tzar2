# Original build: reference definition and repository policy

## 1. Repository policy

| What | Commit? | Why |
|---|---|---|
| Any file of the original game (executables, DLLs, data INI/TXT, sprites, sounds, music, movies, maps, campaigns, manuals) | **No** | Copyrighted by third parties (Haemimont Games et al.). This repo is public on GitHub, so committing them would be redistribution. Keep them in the local, git-ignored `Origin/` folder (TDD REQ-LEGAL-01). |
| Third-party additions found in the local install (HD patch, repack uninstaller, community utilities, FX expansion campaigns, crash logs) | **No** | Not part of the original game, and owned by others. They are not used as references either. |
| Spanish FX expansion campaigns (local Russian-translated copies) | **No** | Copyrighted commercial content, modified copies, over GitHub's file-size limit. Kept locally; hashes only (§5). |
| File manifests: paths, sizes, MD5 hashes (`manifests/*.tsv`) | **Yes** | Metadata only, taken from GOG's public content-system manifests. Used to verify a local copy and to fingerprint editions (TDD REQ-IMP-01, RE-13). |
| Our own specs and notes derived from studying the original (`docs/formats`, `docs/mechanics`) | **Yes** | Written in our own words (TDD REQ-LEGAL-02). |
| Identifiers a format needs to be read: the 144-entry object class table in its built-in order (`src/Tzar2.Formats/Maps/LegacyClasses.cs`, recovered from the editor DLL; the names are the `class =` values of `DATA/OBJECTS/*.INI`) | **Yes** (product owner, 2026-10-19) | A fact of the map format, not code or assets: map files store classes as indices into this table, so without the order no map can be converted. |

`.gitignore` at the repository root excludes `Origin/`.

CI enforces this with `tools/ci/check-no-original-files.sh` (self-tested by `tools/ci/test-check-no-original-files.sh`)
on every blob added or modified in a push or PR range: nothing under `Origin/`, nothing byte-identical to a manifest
entry (or, when run locally, to any file in your `Origin/` folder), no original-only extensions (`.wdt .wmp .wcm .wsv
.wdm .ddv .rle .exe .dll .fnt .pal`) and no `.bmp/.wav/.mid/.ini/.txt/.dat/.ai/.ais` under original-style folders
(`DATA/ IMAGES/ SOUND(S)/ MUSIC/ LOCAL/ HELP/ MOVIES/`). Synthetic test fixtures that trip the name heuristics go in
`tools/ci/original-files-allowlist.txt`. Run it locally before pushing.

## 2. Reference build (“the original”)

**GOG “Tzar: The Burden of the Crown”, version 1.10, English. Build `49982740489443525`, published 2017-03-15** (GOG Galaxy generation-2 build; product ID `1207659252`).

* Reference file list: [`manifests/gog-1.10-en.reference.tsv`](manifests/gog-1.10-en.reference.tsv). It contains **2,916 files, 269.4 MB**: the English depot plus the language-independent depot. GOG Galaxy metadata (`goggame-*`) is excluded, and the `app/` prefix GOG uses for `tzar.ini` and `Demos/tzar.wdm` is removed (those files live in the game root).
* Raw per-depot lists for edition fingerprinting are in `manifests/gog-depot-{en,common,de,es,pl,pt}.tsv`:

| GOG depot | Installer version | Files | Notes |
|---|---|---|---|
| English | 1.10 | 2,854 + common | Loose files (no `.wdt` packs). **Reference.** |
| German | 1.10 | 2,865 + common | As EN, plus `HELP/HTWI.TXT` and alternative terrain transition tiles `IMAGES/TERRAIN/*/[1-4]8_2.BMP` |
| Spanish | 1.10 | 2,868 + common | As DE (without `HTWI.TXT`), plus `Maps/CID.wmp`, `MOVIES/LOGO1-3.DDV` (FX Interactive logos) |
| Polish | 1.01 | 27 + common | Packed: `Packs/{data,help,images,local,movies,sounds,terrains,v101}.wdt`, plus `HeadOff/HoTL100.exe` |
| Portuguese | 1.01A | 24 + common | Packed like PL, plus `Packs/IMAGES2.wdt` |
| Common (all languages) | — | 67 | `Hero Icons/` (58 portraits), `manual.pdf`, `Editor_Manual.pdf`, `Strategy_Guide.pdf`, `Setup.exe`, `TzarCampaign.exe`, `TZAREXT.DLL`, `tzar.ini` |

Verify a local copy against the reference (PowerShell, from the repo root):

```powershell
$ref = Import-Csv docs/original-build/manifests/gog-1.10-en.reference.tsv -Delimiter "`t"
$ref | ForEach-Object {
  $f = Join-Path Origin $_.path
  if (-not (Test-Path -LiteralPath $f)) { "MISSING  $($_.path)" }
  elseif ((Get-FileHash -LiteralPath $f -Algorithm MD5).Hash -ne $_.md5.ToUpper()) { "MODIFIED $($_.path)" }
}
```

## 3. Status of the local `Origin/` folder (checked 2026-10-11)

The local copy was installed from a third-party **“Tzar - The Burden of the Crown (Repack)”**. It is the GOG v1.10 English build with a Russian localization (“Огнем и мечом”) on top, plus FX expansion campaigns, community utilities, the community HD patch v3.4, and locally generated crash logs. All 3,083 files were MD5-compared with the GOG manifests:

| Category | Files | Meaning |
|---|---|---|
| Identical to the reference | **2,859** | Original, untouched. This includes `Tzar.exe`, `Tzar.dll`, `TzarEdit.exe/.dll`, `TzarCampaign.exe`, `TZAREXT.DLL`, `Setup.exe`, the manuals, all `DATA/AI/*`, `DATA/OBJECTS/*`, `IMAGES.INI`, `BONUS.INI`, all sprites, sounds and music, and `Hero Icons/`. |
| Original file, **modified** | **57** | See 3.1. The clean versions are needed. |
| HD-patch copies of original files | 17 | `resolutions/origin/*`, `resolutions/*/S_DEF.BMP`, icons in `sources/`. Same content as reference files. |
| Not part of any GOG build | 89 | See 3.2 |
| Crash logs | 61 | `Logs/` (generated locally, 2025) |
| Reference files missing locally | 0 | Only GOG Galaxy metadata (`goggame-*`) is absent, which is irrelevant |

### 3.1 Modified original files (57)
* **Russian localization (53):**
  * `LOCAL/`: `STRINGS.INI`, `STRINGS2.INI`, `STRINGS3.INI`, `HELP.INI`, `CREDITS.TXT`, and all 7 `FONTS/*.FNT`.
  * `HELP/`: `HK.TXT`, `HELP_STR.INI`, `ITEMS.TXT`.
  * Screens with baked-in text: `IMAGES/SCREENS/TITLE.BMP`, `BYEBYE.BMP`, `S_MAIN/BACK.BMP`, `S_SCRPLAY/BACK1.BMP`, `WAITSCR/SCREEN*.BMP` (7).
  * Movies: `MOVIES/TZAR.DDV`, `PAN.DDV`.
  * Maps and campaigns: `Maps/intro.wmp`, `MP1–MP6.wmp`; `Campaign/Tzar.wcm`, `TZARTUTORIAL.WCM`.
  * Data tables with translated display names: `DATA/{EURO,ASIA,ARAB,COMM} {UNITS,BUILDINGS}.TXT`, `TECHNOLOGIES.INI`, `UPGRADES.INI`, `HEROES.INI`, `TAUNTS.INI`.
  * Terrain sets: `IMAGES/TERRAIN/{GRASS,SUMMER,DESERT,SNOW}.TERRAIN.INI`.
  * ⚠ Whether any **numbers** (balance) in the data tables were changed, not just names, cannot be determined without the clean files.
* **HD patch (2):** `DATA/LAYOUT.INI`, `IMAGES/SCREENS/S_VIEW/SCREEN 1024 LEFT.BMP`. The originals are preserved byte-identical in `resolutions/origin/`.
* **User/runtime (2):** `tzar.ini` (settings), `Demos/tzar.wdm` (a demo recorded locally in 2025, replacing the shipped one).

### 3.2 Files that are not part of the original build (89 + 61 logs) — excluded
| Group | Files |
|---|---|
| Community HD patch v3.4 | `TzarHD.exe`, `TzarRunner.exe`, `TzarSettings.exe`, `HighResolution.dll`, `readme.txt`, `sources.zip`, `sources/**`, `resolutions/**` (per-resolution `LAYOUT.INI` + HUD bitmaps, patched EXEs for v1.00–1.13b) |
| Repack installer | `unins000.exe`, `unins000.dat` |
| Community utilities | `utils/PackFileEditor/Arch.EXE` (WDT pack tool), `utils/ChangePlayerReplay/replaay-v2.3.exe`, `utils/Запуск_в_окне/*` (windowed-mode kit: a DirectDraw wrapper `ddraw.dll`, `dxcfg.*`, plus a 1999 `Setup.exe`/`Tzar.dll`), `utils/Запись реплея/*` (replay recording examples), `utils/Tzar cmdline.txt`, Russian readmes |
| FX Interactive expansion campaigns: **Russian community translations** of the Gold Edition campaigns, not the originals (see §5) | `Campaign/Excalibur y el rey Arturo.wcm`, `La Reconquista.wcm`, `Los Dominios de la Magia.wcm`, `El Imperio de Gengis Khan.wcm` |
| Crash logs | `Logs/**` |

## 4. Getting a clean reference copy
A clean copy is needed for the `classic` reference values (TDD REQ-RE-03), golden captures and fidelity measurements. The repo only *references* the GOG build through the manifest, so each developer or agent machine obtains the files itself:
1. Install GOG v1.10 English (offline installer or Galaxy) and place it in `Origin/` (or point `TZAR2_ORIGINAL_PATH` at it). On machines without Windows/Galaxy, download the offline installer with your own GOG account (e.g. with `lgogdownloader`) and unpack it with `innoextract`. Credentials stay local and are never committed.
2. Or keep the current `Origin/` and replace only the 57 modified files listed in 3.1 with their clean versions, then run the verification snippet above. It should report nothing.

## 5. Spanish FX expansion campaigns (Gold Edition / Anthology)
* **What they are:** *Tzar Gold Edition* (a.k.a. *Anthology*, 2002) is **game version 1.11** published by FX Interactive. It adds four voiced campaigns released only in Spain (two of them also in Italy): *El Imperio de Gengis Khan*, *Excalibur y el rey Arturo*, *La Reconquista*, *Los Dominios de la Magia*. It is **not sold on GOG or Steam** (GOG lists it only as a dreamlist request), so it is commercial content with no current legal source.
* **Local copies:** the four `.wcm` files in `Origin/Campaign/` are **Russian community translations**. Mission texts are in Russian, with only remnants of the Spanish (e.g. map names and descriptions). Their hashes are recorded in [`manifests/local-fx-campaigns-ru.tsv`](manifests/local-fx-campaigns-ru.tsv).
* **Decision (2026-10-11):** keep them **local only** (git-ignored with the rest of `Origin/`). The repo keeps only their hashes and provenance. Reasons: they are copyrighted commercial content, they are modified copies rather than originals, and `Excalibur y el rey Arturo.wcm` (120 MB) exceeds GitHub's 100 MiB per-file limit anyway.
* **Use:** players bring such campaigns themselves and convert them with the editor's built-in legacy converter (TDD §18.5). Locally, these four files are a test corpus for that converter (its codepage handling, campaign-graph reconstruction and resource deduplication) and for the HPFS format work (RE-03). The original Spanish files from a Gold Edition / Anthology retail disc would be preferable when available; add their hashes as `manifests/fx-gold-1.11-es.tsv`.
