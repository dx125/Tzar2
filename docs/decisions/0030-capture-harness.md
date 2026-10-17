# 0030. Measure the original with a black-box capture harness in `t2 fidelity`, driven by YAML scenarios, run by the operator in one command

* **Status:** Accepted (unverified on the target machine until the first session returns)
* **Date:** 2026-10-17
* **Deciders:** implementation agent (RE capture harness), product owner (operator set-up)
* **Related:** TDD §0.3 (measured behaviour is source #1), §3.1, §13.2, §13.3, §13.5, §22.3, §23.1–§23.4; REQ-RE-01/02/03,
  REQ-LEGAL-01/02/03; UNK-01a, UNK-03, UNK-07, UNK-08, UNK-10, UNK-12, UNK-39; ADR 0006 (verify-original), 0011
  (packages per assembly); [tools/capture/README.md](../../tools/capture/README.md)

## Context

M3 needs numbers that only the running original can give: the shadow darkening (UNK-03), the player slot → colour
ramp mapping, terrain variant selection (UNK-10), the RGB555 → screen expansion (UNK-39), unit speeds (UNK-12) and
attack timing (UNK-07/08). Nobody on the team runs Windows; the co-owner will run the game on his Windows 10/11 PC
(RTX 4070 Ti). His time is scarce: one command per session, clear progress, one zip back. The harness must not
patch `Tzar.exe` or copy original code (REQ-LEGAL-02); reading the screen and memory and sending input is fine.

What the executable itself offers (from its strings and imports, our conclusions only): it is a DirectDraw/GDI
game reading input through window messages and `GetAsyncKeyState` (no DirectInput); command-line switches
`-map <name>` (loads `MAPS\<name>.wmp`), `-explore`, `-nofog`, `-nomovies`, `-multiinst`, `-lockview`, `-record`,
`-demo`, `-dumpvideo`; **F11 writes a snapshot of its own 16-bit surface** to `ScrnShots\tzarNNNN.bmp`; debug chat
commands (`reveal`, `nofog`, `spawn`, `kingdom`, `god`, …, presumably with the `hm` prefix of TDD GAME-19). Map
headers name their terrain set in plain text. The GOG build ships no DirectDraw wrapper (only an `EmulateHeap` shim).

## Decision

1. **Where.** `t2 fidelity` in `Tzar2.Tools.Cli` (TDD §7.2 lists `fidelity capture`): `capture` (Windows), `analyze`,
   `check`, `scenarios`, `selftest`. Platform-neutral logic (scenario model, runner, image decoding, memory-scan
   logic, all analyses) is tested on macOS/Linux with a fake game; the Win32 part (`Fidelity/Capture/Windows`,
   source-generated `LibraryImport`, `[SupportedOSPlatform("windows")]`) is compiled everywhere and exercised by
   `t2 fidelity selftest` on `windows-latest` in CI (own window painted with known colours → PrintWindow/BitBlt
   capture checked pixel-exactly; SendInput mouse/keyboard checked against the messages received; ReadProcessMemory
   scan of a planted value). No new packages (ADR 0011): a small YAML subset parser/writer lives in the tool.
2. **One script for the operator.** `pwsh tools/capture/run-capture.ps1 -TzarPath <dir>`: checks Windows/.NET 10/the
   folder, builds t2, runs `t2 verify-original` (stops on a modified install unless `-AllowModified`, REQ-RE-03),
   runs the self-test, runs the capture, prints the zip. Ctrl+C stops safely and still writes a zip.
3. **The install is never modified.** A **work copy** in `%LOCALAPPDATA%\Tzar2Capture\game` (incremental sync) receives
   the per-trial `tzar.ini` (layout 1024L, day/night and weather off, no language prompt, sound off), the wrapper and
   the snapshots. For the session, `HKCU\Software\Haemimont\Tzar\Path` points at the copy (restored afterwards);
   the native renderer also gets a per-exe `~ HIGHDPIAWARE` layer in HKCU. No administrator rights, no UAC
   virtualisation surprises (Program Files installs).
4. **Renderer.** Default: **cnc-ddraw 7.1.0.0** (MIT), downloaded at capture time from the GitHub release and accepted
   only with the pinned SHA-256 of the zip and of `ddraw.dll`, installed in the work copy only, configured windowed
   with the **GDI renderer and no scaling** (the client area is the game surface 1:1). `-Renderer existing` keeps an
   install's own wrapper (e.g. the GOG Windows 11 patch); `-Renderer native` removes wrappers from the copy (full
   screen; F11 snapshots still work, window captures may be black). If the download fails, an existing cnc-ddraw is
   used, else native.
5. **Two capture sources.** (a) The game's **F11 snapshot**: the 16-bit surface itself, lossless (BMP with bit-field
   masks, e.g. RGB565). This is the primary source for everything colour-exact. (b) **Window capture**
   (PrintWindow with `PW_RENDERFULLCONTENT`, screen BitBlt fallback, 32-bit, DPI-aware process): what Windows shows,
   with QueryPerformanceCounter timestamps, used for bursts (motion; stored cropped and as canonical RGB555 to keep
   the zip small) and, paired with (a), for UNK-39. Every format is
   reduced to canonical RGB555 by keeping each channel's top 5 bits; that is exact for any expansion that preserves
   the top bits (bit replication, shift), which the colour-depth analysis checks rather than assumes.
6. **Memory probes are read-only**: VirtualQueryEx over committed writable regions, aligned integer scans,
   narrowing scans (changed/unchanged/decreased/increased), background sampling to a `T2MS` file. No addresses are
   hard-coded; scenarios find them from values in the reference data (HEALTH of a spawned unit).
7. **Scenarios are data** (`tools/capture/scenarios/*.yaml`, schema `tzar2-capture-scenario/1`, defaults in
   `session.yaml`): launch arguments, trials with variables and ini/wrapper overrides, steps (`wait`, `key`, `text`,
   `chat`, `move`, `click`, `drag`, `minimap`, `snapshot`, `burst`, `memscan`, `memsample`, `note`) and the analyses
   to run. Unknown keys are errors with line numbers; CI validates the files. Camera control uses minimap clicks
   (layout geometry from `LAYOUT.INI` 1024L); the mouse rests on the HUD for snapshots (no edge scrolling). Failed
   steps are logged and the trial continues; a crashed or windowless game ends the trial, not the session.
8. **Session 1 (default set, ≈ 25 min):** `smoke` (verifies `-map`, `-explore`/`-nofog`, F11, the chat commands; a
   snapshot after every step), `colour-depth` (UNK-39; RGB565 and RGB555 surfaces), `terrain-tiles` (UNK-10; MP1
   snow, MP2 grass, MP4 summer, 3×3 camera tour), `shadow-blend` (UNK-03; MP3, MP6), `player-colours` (8 kingdoms
   of MP6 via `kingdom N`), `unit-speed` and `attack-timing` (experimental: depend on `spawn`/`kingdom` behaving as
   assumed). `combat-pairs` is a draft, not run by default.
9. **Analyses produce facts, never images** (`t2 fidelity analyze <zip> --original <install>` →
   `tests/fidelity/facts/<scenario>.yaml`, schema `tzar2-fidelity-facts/1`, provenance per REQ-RE-02). Terrain
   cells are identified exactly against the install's terrain images (row-strip votes give the 32-px grid, full-tile
   hashes and row votes identify base tiles, non-key pixels identify transitions); screen grids are aligned with the
   map's `tiles` words (word → terrain consistency, ties broken by word → tile consistency and the minimap position).
   On top of that: shadow rule fitting (k/32 floor/round, subtraction; double application for overlaps), ramp counts
   per tagged slot (ramp colours that also occur in the terrain are excluded), centroid tracking for speeds, step
   detection in HP samples for attack periods. Each analysis reports `ok`, `inconclusive` or `skipped` with notes.
10. **Legal.** Session zips, extracted captures and memory samples stay local (`capture-*.zip`,
    `tests/fidelity/local-captures/` git-ignored); the analysis reads the install locally and writes numbers only.
    cnc-ddraw is never committed or redistributed by us.

## Alternatives considered

* **AutoHotkey scripts** (TDD §23.2 suggestion): another runtime to install, no memory access, weaker testing. The C#
  injector shares code and tests with the analysis.
* **Menu navigation to start skirmishes:** fragile across resolutions and languages; `-map` avoids it. Editor-made
  test maps would allow controlled layouts but cost the operator time and need TzarEdit; deferred to a later session
  if session 1 leaves gaps.
* **Screen capture only (no F11):** a 32-bit desktop capture of a 16-bit game mixes in the display path's conversion
  and DPI scaling; the game's own snapshot is the exact surface.
* **Patching the exe or injecting a DLL into it:** excluded by the brief (black-box only); cnc-ddraw replaces the
  system DirectDraw through the normal DLL search order in the work copy, the exe is unchanged.
* **YamlDotNet in t2:** would add a package to the CLI (ADR 0011) and needs the static generator for AOT; the
  scenario subset is small.

## Consequences

* Nothing Windows-side has run against the real game yet. Session 1 is partly a discovery session: `smoke` tells us
  whether `-map`, F11 and the chat commands behave as inferred; scenarios are YAML, so fixes for session 2 are
  data edits.
* Risks: DirectDraw under modern Windows (cnc-ddraw is the mitigation; native as fallback), the game possibly
  ignoring `-map` or needing a dismiss click after loading, the `hm` cheat prefix/spelling, `spawn` placing units
  elsewhere than the cursor, F11 writing elsewhere (the harness watches the copy and the install), timing being wall
  clock at the default game speed.
* Follow-ups: write `docs/mechanics/*.md` and update the UNK register from the first facts; `Tzar2.Fidelity.Tests`
  comparing the sim/renderer with the facts; an outcome analysis for `combat-pairs`; possibly editor-made test maps.

## Questions for the product owner

* Is a ~25-minute, hands-off session acceptable for the co-owner, and may follow-up sessions (after fixing the
  scenarios) be asked for? Default: yes, one session at a time, each with a short Russian note on what to run.
