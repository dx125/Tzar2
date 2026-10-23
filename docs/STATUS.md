# Project status

The living status of the rebuild: where each milestone stands against its exit criteria, what is being worked on,
what comes next, and a short log of decisions and events. Read it before starting work; **update it in the same PR
whenever a milestone criterion, the work queue or the decision log changes.** How to build, test and contribute:
[DEVELOPMENT.md](DEVELOPMENT.md). The specification: [TDD.md](TDD.md). Why things are the way they are: the ADRs,
indexed in [decisions/README.md](decisions/README.md).

* **Last updated:** 2026-10-23 (consolidated update after the batch merge of 46 PRs)
* **Maintainer:** handed over on 2026-10-22 to the next agent ([Handover](#handover-2026-10-22) below). The product
  owner also operates the Windows PC with the original game for captures and local-only tests.
* **`main`:** green on CI. Open PRs: none. Open issues: none ([#62](https://github.com/dx125/Tzar2/issues/62), flaky
  `RelayTests`, fixed by ADR 0231).

## Handover (2026-10-22)

The owner hands the work to another agent. Read [AGENTS.md](../AGENTS.md), then [DEVELOPMENT.md](DEVELOPMENT.md) (tools,
how changes land, the rules that keep the project correct, the sources of knowledge, the fidelity loop), then the work
queue below.

**Scope, in order:**

1. The results of the owner's fifth play-test, when they come (work queue item 5): fix what it finds.
2. The UI review (item 15): every screen at every modern resolution, and alike to the original.
3. The codebase review (item 16): performance, resources and memory; modules loose enough for a fork.
4. The rest of the work queue, and the ledger's open rules (`t2 rules list missing partial unclear`).

**Where to look:**

* **State:** the milestones and the work queue below; the decision log (newest first) says what changed and why.
* **The original's rules and what is still open:** [rules/](rules/README.md) and its report.
* **Gameplay values still placeholders:** [mechanics/](mechanics/) (`UNVERIFIED`).
* **Why the code is as it is:** the ADRs, [decisions/README.md](decisions/README.md). The last ones are 0186–0192:
  items, save/load, unit bodies, Shift/Alt orders, ReTzar's rules, technology effects, gates.
* **Questions the original's files cannot settle:**
  [DEVELOPMENT.md § Sources of knowledge](DEVELOPMENT.md#sources-of-knowledge-about-the-original). ReTzar, read but
  never copied.

**The owner's PC** (Windows 11; the repository at `D:\Code\Tzar2`):

* The original is a clean GOG v1.10 EN at `E:\Games\Tzar`. Point `TZAR2_ORIGIN` at it for the local-only tests.
  `OriginImportTests.Reference_build_imports_without_errors_and_deterministically` fails there on purpose: the
  install's `tzar.ini` and a demo file are modified.
* The tools are portable:
  * .NET SDK in `D:\tmp\s1bench\dotnet` (set `DOTNET_ROOT` and put it first on `PATH`);
  * PowerShell 7 at `D:\tmp\s1bench\pwsh\pwsh.exe`;
  * Godot 4.7.2 .NET in `D:\tmp\s1bench\godot\` (`TZAR2_GODOT`: the `_console.exe` for the smoke tests);
  * GitHub CLI at `C:\Program Files\GitHub CLI\gh.exe`.
* **Play:** `tools/play/play.ps1 -TzarPath "E:\Games\Tzar" -Godot <Godot exe> -Reimport` (after importer changes).
* **Capture:** `tools/capture/run-capture.ps1 -TzarPath "E:\Games\Tzar" -Batch <X> -AllowModified`. Batches A–K exist;
  the owner must not touch the PC while one runs, so do not run anything on it then.
* **Language:** the owner writes in Russian; answer in Russian. Docs, code and commits are in English.

**Lessons of this period:**

* Facts from ReTzar unblocked much that captures could not. Check each against our measurements first.
* Re-pin hashes in the same PR with the reason (`tests/Tzar2.Determinism.Tests`, the three benches in
  `.github/workflows/ci.yml`, the trigger mission, `legacy.referenceDataHash`).
* A play-test is the best source of bugs: two of them hid in plain sight (bridges could not be placed, gates were not
  drawn).

## Milestones

Exit criteria from [TDD §25](TDD.md#25-milestones-and-exit-criteria). "Reported" means the previous agent stated it
and it has not been re-run since the handover.

| Milestone | State | Exit criteria and evidence |
|---|---|---|
| M0 Foundations | **Done** | One ADR per spike: S2 (0007), S4 (0008), S3 (0009), S1 (0010), S6 (0013; S5 mobile export is PX); S1 renderer met on Windows (ADR 0010); CI green; no original files tracked (CI job `legal`). |
| M1 Importer & viewer | **Done** (reported) | All 961 RLE sheets and the other formats of the reference build import (ADR 0013, `t2 import`); golden sprite comparisons and the import of the reference build are local-only tests (`TZAR2_ORIGIN`, `TZAR2_GOLDENS`). |
| M2 Sim core & movement | **Done** | Determinism matrix green on CI; ORIG-01/02/03 tests; B3/B4 within budget (ADR 0026). |
| M3 Classic gameplay & skirmish | **In progress** | Skirmish vs. Hard AI on all six map types (PR #36). Hotkey parity checklist complete by its own rules ([hotkey-parity.md](mechanics/hotkey-parity.md): 12 bindings blocked on features). The fidelity gate moved to M8 (ADR 0174). Three play-tests by the owner are done and their findings fixed (2026-10-20/21). Open before M3 closes: the ledger of the original's rules ([rules/](rules/README.md)) and its missing rules (work queue 1), and a fourth play-test. |
| M4 Triggers & campaigns | **In progress** | Converter: all 33 original maps and both campaigns convert (reported). Original campaigns completable: main campaign **19 of 19 measured** (2026-10-22, `t2 campaign run --survey` on the clean GOG install, ADRs 0134, 0220; every map with cheat assistance, none by pure play), tutorial **7 of 7 measured** (2026-10-22). Earlier counts were inflated by AI resignations (ADR 0220). ≥ 90 % of the community corpus converts: **no corpus yet**. Open format unknowns UNK-70…80 need the TzarEdit diff session. |
| M5 Editor | **Code complete**, acceptance open | Parity checklist 100 of 104 done, 4 blocked ([editor-parity.md](mechanics/editor-parity.md)); §18.6 acceptance: `acc-original` verified locally 2026-10-18 (reported), `acc-fx` still needs a local run with the FX campaigns and a community campaign (`TZAR2_FX_CAMPAIGNS`, `TZAR2_CORPUS`), ADR 0156. |
| M6 Multiplayer & replays | **Done** (exit criteria met 2026-10-22) | Relay/lobby, lockstep sessions, drop policy, reconnect, desync reports, replays, MP saves and the MP UI (ADRs 0160–0168, 0230, 0231). **500-match soak: 500 of 500 ok, 0 desyncs, 0 failures** (2026-10-22, macOS, `t2 soak --matches 500 --parallel 10 --seed 62`: 2 bots + 2 Hard AIs, 6,000 ticks at 4x, RTT 200 ms, jitter 10 ms, 5 % loss; 251 disconnects and 251 rejoins, 138 after an AI takeover; 749 replays re-simulated; 3.0 M ticks; 65.6 min). Replays recorded on Linux verify on Windows, Linux and macOS (CI `replay-verify`, same final hashes); the nightly 20-match slice passes on Linux and Windows. Matches are 5 minutes, not two hours (ADR 0167). Since the exit: the multiplayer setup (colours, AI scripts, every map and game option; ADR 0232), custom maps in multiplayer with time and score limits (ADRs 0233, 0234; SHELL-018), and the multiplayer menu, lobby and replays on the original's art and font (UI review). |
| M7 Windows beta | **In progress** | Plan: ADR 0240 (D1–D7). Done: the Windows package (Godot headless export, portable zip, Inno Setup installer, version stamp, uninstall keeps the player's data; ADR 0241), first-run import in the client, the signing step (skipped without secrets; ADR 0242), opt-in crash reports over the Sentry protocol (ADR 0243), the S2 render bench `--bench-render` (D6, ADR 0244; first run on an M5 MacBook at 1080p: 120 FPS, sim p99 14.8 ms above the 12 ms budget, VRAM 3.1 GB). The code review cut the client's memory (terrain tile atlas, ADR 0210; caches and atlas pages freed after a match; a match loaded on a worker thread). Open: the crash server and the signing certificate (owner), the owner's sim bench, installed-build and Windows render-bench runs, the closed beta (D7). Exit: crash-free ≥ 99.5 %, S2, no P0/P1. |
| M8 Modding, polish, release | Not started | Also the **fidelity suite ≥ 80 %** (moved from M3, ADR 0174): **10 of 30 (33.3 %)** today, see below. |
| PX Platform expansion | Post-1.0 (DEC-14) | |

## Fidelity gate (M8 since ADR 0174)

The suite (`t2 fidelity report`, ADR 0112) has 30 checks from TDD §22.3; the gate is passing / all, pending counts as
not passing, so 24 must pass. Since ADR 0174 it is an M8 criterion, and no more unit-by-unit measurements are made. AI script semantics stays pending by owner decision, so 24 of the other 29 must pass.
Each capture batch is a ~15–25 min unattended run on the operator's PC (`tools/capture/run-capture.ps1 -Batch X`).

| Batch | Measures | State |
|---|---|---|
| A | cheats, player colours, combat pairs, shadow overlap, training and construction time | **Done**: analysed and merged (PR #61, ADR 0169). Passing: attack cycle, damage, population limit, training time, construction time. |
| B | 15 unit speeds; gathering wood, stone, cows | **Gathering done** (ADR 0170): carry capacity passes (10 wood/stone/gold, 50 from a cow). **Unit speeds done** (ADRs 0171, 0172): after two failed places, 11 classes measured by our base; the original ignores SPEED (foot 40 px/s, mounted 72). Horse archer, horse samurai, ninja and monk did not spawn (not European). |
| C | 15 more unit speeds (chariot, caravan) | **Done** (ADR 0172): troll 47, ghost 67, cow and bear 26, dwarf, ork, skeleton 40; speed check passes (18 classes). Heavy cavalry, jihad warrior, janissary, chariot (not European), catapults, ballista and caravan did not spawn. |
| D | day length, night share (no input) | **Day length done** (ADR 0173): 37.5 min, read from the clock globe; a match starts at 10:00. The game ended after 12 min (the Hard AI), before the night: **night share open**, needs a 35-min run the AI does not end. |
| E | group speed, research, damage bonuses, capture, far gathering | **Done, one result** (2026-10-19): a mixed group walks at its slowest member's speed (passes). The rest failed: units given to kingdom 2 are taken over by its AI and leave the duel; Feudalism never started; Ctrl+C picked another castle, so the far tree was not the one gathered; the capture analysis saw no colour change. Not repeated: owner decision below. |
| F–I | fire, repair, healing, fields, experience; lightning, mana, loans, score; 12 × 8 duels; trade and gambling | deferred (owner decision below) |
| J | hit points per repair blow on three buildings; how close another kingdom's spy unmasks a disguise; the disguise meter | **Run 1 done** (2026-10-21): the meter measured (one point per 1.25 s). Repairs and unmasking failed: the builder left too early, our knights do not attack our own buildings, MP2 has no kingdom 3. Run 2: buildings damaged, no 4-byte field held their hit points; spies failed on MP6. **Run 3: repairs measured** (ADR 0185: blows of max / (4 × TIME) every 1.25 s, free, no falloff; suite 10 of 30); a third kingdom's spy never unmasks. Run 4: no impersonation. Run 5: the takeover seen with an enemy peasant; a fresh disguise unmasked by the copied kingdom's spy from 6 tiles or more. Run 6: confounded (the detector spawned red and wandered, the camera jumped to a fight). Run 7: MP1's chosen row was forest, no impersonation. Run 8: the impersonation worked every time, but a detector that stood its ground and was handed over never unmasked. The owner's own test: a spy spawned as the enemy's unmasked a disguise at meter 8 from 4 tiles. **Run 10: done** — enemy spies unmask a fresh look at ~3.4 tiles and a full one only next to it; with the owner's 4 tiles at meter 8, Tzar2 now takes units over and unmasks within 4 tiles down to 1 (ADR 0184 amendment 6). |

Facts live in `tests/fidelity/facts/` (numbers only; captures stay on the operator's PC). A measured fact that disagrees
with the engine fails `Tzar2.Fidelity.Tests`, so a fact and the engine fix that makes it pass go in the same PR.

## Work queue

In order. **Maintainer** = code and analysis; **Operator** = runs on the Windows PC with the original game.

Items 1 to 3 are the owner's order of 2026-10-21 (log below), done one after the other; item 4 comes from the fourth
play-test; items 15 and 16 are the owner's order of 2026-10-22 at the handover.

1. **Maintainer:** the ledger's first fill, the data account and the hotkey checklist's update are done (log below).
   Keep the ledger current with every change.
2. **Maintainer:** the rest of the command card, on the card model of ADR 0182 (`Tzar2.Game.Ui.CommandCard`, done).
   Done (log below): the Magic Workshop's crafting, the holders' list of units inside, the kingdom's card with its
   lists and AI assistance, the ninja's uncloak and the spies' impersonate and reveal. Left: nothing on the card but
   the online help. `attack_type` is on no card of the original, so it is left
   out.
3. **Maintainer:** close the ledger's missing and partial rules (`t2 rules list missing partial`), in the owner's
   order. Next: the fifth play-test (item 5), and the owner's list of 2026-10-22 (log below). Done: the curse,
   hypnosis, the ghost's banshee scream and the Messiah's immunity (WAR-009, WAR-010, MAG-008; ADR 0250), cancelled
   sites, broken-off loads and the computer's repairs (BLD-020, ECO-002, AI-003; ADR 0251), trade with any market,
   Asian peasants as traders and caravans that load infantry (TRADE-003, TRADE-004, HOLD-008; ADR 0252), Tournaments
   and Mastery (TECH-028, TECH-029; ADR 0253), spells that skip machines (MAG-022; ADR 0254), soul items bound to a
   unit (ITEM-010; ADR 0255), cows' meat as food items (NAT-004; ADR 0256), the trade profit (TRADE-001; ADR 0257),
   experience on the original's scale (EXP-001, EXP-002, EXP-004; ADR 0258), Jewelry's item market (TECH-030; ADR
   0259), the spells' ranges and mana (MAG-023; ADR 0260), the spells' strength, durations and areas (MAG-024; ADR
   0261), the confirmations of quitting, resigning and disconnecting (SHELL-027), the random map screen rebuilt to the original's layout (kingdom rows with colour, AI script, civilization and team; game type, victory and its limit; map resources and options; kept between runs: SHELL-010/011/012, AI-001), the AI resign flag on the random map screen and in the lobby (SHELL-013; ADR 0262), the controls
   (Shift and Alt orders, turned gates and bridges, bridges and harbors placed as the sim allows; ADR 0189), unit
   bodies (WORLD-015, ADR 0188), save and load (ADR 0187), the inventory (HUD-017, HUD-018), the owner's interface fixes (main menu, minimap), the diplomacy and tribute screens (HUD-012, HUD-013), Landing Tactics (TECH-019), one
   farmer per field (ECO-021), move-sneak (ORD-004), siege weapons hurting their own side (WAR-013), waypoints
   (ORD-005), the hit frame from the data (WAR-014), several items per kind (ITEM-016, ADR 0186).
   * Two values the original's data does not hold, for a capture: the hit points a repairer restores per blow
     (BLD-022) and how close another kingdom's spy must come to unmask a disguised one (MAG-012). Batch J measures
     both. The repairs are measured (run 3, ADR 0185); the distance is left. **Operator:** run 10,
     `run-capture.ps1 -TzarPath "E:\Games\Tzar" -Batch J -AllowModified` (~22 min, hands off).
   * The spy's disguise meter (MAG-021) is done and measured.
   * Impersonation takes the unit over and is unmasked as measured (MAG-012, ADR 0184 amendment 6): done.
4. **Done (2026-10-22, ADR 0188):** units never stand on one another (WORLD-015).
   * What the probes of 2026-10-21 measured:
     * At rest, units keep 16 px apart.
     * While walking or fighting, units come as close as 8 px, with a collision diameter of 14 px and sprites about
       25 px wide.
     * The push apart is soft and capped per tick.
   * What did not work: treating friends walking the same way as hard obstacles jams opposing columns on a narrow
     bridge (ORIG-02). A radius of 12 px alone changes nothing in a crowd and shifts the AI's goldens.
   * The data: the original's units have a 25 × 25 selection box and a 32 × 32 object box, a whole tile.
   * **Operator:** batch K (`-Batch K`, about 5 minutes, scenario `unit-spacing`) measures the original: nine footmen at
     rest, two columns passing head on, a crowd around one target.
   * Plan:
     * a body size from the data;
     * hard contact with rules for who gives way: idle units step aside, units coming the other way squeeze past,
       fighting units hold;
     * free places around an attack target instead of a crowd;
     * ORIG-02's jams at bridges and gates must not come back.
5. **Operator:** a fifth play-test (`tools/play/play.ps1`) once item 4 is merged.
6. **Maintainer (deferred to M8):** a night-share scenario (UNK-32, ADR 0173): 35 minutes or more of a night in a
   view no unit walks through, in a match the Hard AI does not end. Then **Operator:** run it.
7. **Done (2026-10-22, ADR 0190):** walking speed for every class from its walk sheet's stride, by the rule ReTzar read
   from the original's code. Why catapults, ballistas and caravans did not spawn can wait for M8.
8. **Maintainer (deferred to M8):** before batches F and H, move or guard the scenarios that leave soldiers idle in
   MP2's middle next to Nature's cows (experience, combat outcomes; ADR 0171). **Operator:** batches F–I.
9. **Operator:** TzarEdit diff session (maps `base`, `a1`–`e1` for UNK-70…80); 20 random maps from TzarEdit (RE-10);
   (the `rajba` cliff check is settled: only ninjas climb cliffs, WORLD-018).
10. **Maintainer:** decode UNK-70…80, compare random maps with our generator. Done: the main campaign is 19 of 19
    (ADR 0220, 2026-10-22). Follow-ups: ninjas climbing walls, peasants gathering resource items (ReTzar).
11. **Maintainer:** M5 `acc-fx` and the M4 corpus criterion: run locally with the FX Gold campaigns kept in `Origin/`
    (Russian translations, never committed) and find a community map corpus.
12. **Done (2026-10-22):** issue #62 fixed at its cause (ADR 0231); M6 soak 500 of 500 with 0 desyncs; token rotation
    on rejoin, Noise handshake proposed for M7 (ADR 0230).
13. **Maintainer:** M7 (ADR 0240). Done: package, installer, first-run import, signing step, crash reports (ADRs
    0241–0243), the render bench (D6, ADR 0244). **Owner:** pick the crash server and the signing route (ADRs 0242,
    0243); run the sim bench and the installed build ([min-spec-tuning.md](release/min-spec-tuning.md), requests 1
    and 2), and `--bench-render` on Windows (the command: min-spec-tuning.md, "The render bench").
14. **Maintainer (deferred to M8, owner decision 2026-10-21):** the original's side panels (LAYOUT.INI `LAYOUT1024L`
    and `R`: the HUD down the left or right side) as a setting; the card, the tooltips and the selection view already
    place themselves by the layout.
15. **Maintainer (owner's order of 2026-10-22): a full UI review, for every modern resolution and for likeness to the
    original.** Many texts and buttons differ from the original's today.
    * **Resolutions.** Every screen and dialog has to work at 1280 × 720, 1366 × 768, 1600 × 900, 1920 × 1080,
      1920 × 1200, 2560 × 1440, 3440 × 1440 and 3840 × 2160, plus 4:3 (1024 × 768) and Windows scaling of 100–200 %.
      That covers:
      * the shell: main menu, single player, campaign, random and custom map setup, multiplayer, options, credits,
        save and load;
      * in the game: HUD, command card, selection view, minimap, chat, diplomacy, tribute, inventory, quests, the game
        menu, victory and defeat;
      * the editor.

      Nothing may be clipped or overlapping, text must stay readable, and the play area must scale the original's way
      (the 1024 layout of LAYOUT.INI, ADR 0182) without stretching the art.
    * **Likeness.** Compare each screen with the original side by side (the owner's screenshots, or a capture):
      * the fonts (the original's bitmap fonts, `LOCAL/FONTS`, CREDITSFNT.INI);
      * the texts, from STRINGS.INI rather than our own wording;
      * the button art and states (MENU art, ORDERS buttons), layouts and colours.

      Our improvements may stay where the owner chose them (main menu in one column, ADR 0182). Everything else
      follows the original.
    * **Deliverables:**
      * a findings list per screen and resolution;
      * fixes in PRs;
      * a Godot smoke test that opens every screen at each resolution and checks that no control leaves the window;
      * a STATUS log line.

      Ask the owner for screenshots of the original's screens that are not in the art yet.
    * **Progress:** the test (`--ui-smoke`, CI job `godot-smoke`, ADR 0200) and the findings
      ([reviews/2026-10-ui-review.md](reviews/2026-10-ui-review.md)) are done, and the layout findings are fixed (the
      UI chain #135…#177 is merged; after the merge, 0 issues on all 22 displays). The likeness findings wait for the
      owner's screenshots. The diplomacy screen's shared victory check showed COMMON CHK's frames the wrong way round
      (red when offered); the model now picks the frame (HUD-012).
16. **Maintainer (owner's order of 2026-10-22): a full code review for performance, resources and memory, and for a
    fork.**
    * **Performance and resources:**
      * the sim tick budget (the B3/B4 benches, `AllocationTests`), allocations and GC on the hot paths;
      * asset loading: package import, sprite decoding, atlas packing at match start, textures loaded and never used,
        load times;
      * GPU and texture memory, audio streaming and MIDI;
      * leaks: Godot RIDs, nodes and canvas items freed, event handlers detached, caches bounded (frame buffers, sprite
        banks, save lists), threads ended.

      Measure with the existing benches and a long match (an hour, 4 AIs, a huge map), and record before and after.
    * **For a fork.** The owner will fork the finished remake into a game with other rules, another design (perhaps
      another engine), more races and more unit progression. So:
      * rules and maths in data or in one place each (the YAML ruleset, `ModifierStat`s, the combat and economy
        formulas), never scattered constants;
      * modules loosely coupled: Sim, Content, Game, Client, AI, Scripting, Net. The architecture tests
        (`Tzar2.Architecture.Tests`) guard the boundaries; extend them where the review finds leaks;
      * civilizations and classes not hard-coded (look for `"euro"`, `"asia"`, class-name switches, fixed lists of
        three races);
      * the client replaceable behind the frame (`GameFrame`) and the UI models (`Tzar2.Game.Ui`, ADR 0182);
      * replacing a rule, a formula or a picture as a small, safe change, caught by tests and hashes.
    * **Deliverables:**
      * a review report (`docs/reviews/2026-…-code-review.md`) with findings ranked by impact;
      * ADRs for the structural changes;
      * the fixes in small PRs, each with its measurements.
    * **In progress (2026-10-22):** the report is [reviews/2026-10-code-review.md](reviews/2026-10-code-review.md).
      Measured so far:
      * the sim: an hour of 4 Hard AIs on 256² runs at 0.19 / 0.70 ms per tick (p50 / p99) with one GC, and does not
        leak;
      * the client: the terrain took 1.3 GB of video memory on a huge map (ADR 0210 fixes it), and Godot objects and
        nodes do not leak between matches.

      Done since (log below): the nightly Benchmarks' gameplay rows re-recorded, the architecture tests' four gaps,
      the civilization list (ADR 0211, the `Race` bug), the asset caches cleared in the menu, the load off the main
      thread, rules decided by data instead of names in code.
      * 2026-10-23: the host's choice for a computer goes to the relay (issue #190, ADR 0168 amendment). A stale lobby
        view lost it silently.
      * 2026-10-23: the AI reads the world through read-only views (ADR 0213); an architecture test keeps it off the
        writable spans.
      * 2026-10-23: the full state hash hashes its large arrays in 64 KiB chunks on the workers (ADR 0215): 0.56 → 0.18 ms
        on an s2 world. Only the full hashes are re-pinned.
      * 2026-10-23: the network test harnesses' timeouts no longer drop members of loaded CI runners, and the capture
        end-to-end test runs on a virtual clock (issue #179).
      * 2026-10-23: a test checks that the relay and the clients hand their ping interval to LiteNetLib (ADR 0231).

## Decision and event log

Newest first. Technical decisions have ADRs (index: [decisions/README.md](decisions/README.md)); this log also keeps
the process decisions that have no ADR.

* **2026-10-23 — The AI resign flag (SHELL-013; ADR 0262).** MatchSettings.AiResign (the white flag by default, as before) reaches each computer's MayResign; the random map screen draws AIRESIGN.BMP and the lobby an "AI may resign" check. The sword is written as match settings version 3, so an older build refuses it at the version check; the flag up keeps the version 1 and 2 bytes, and no hash changes.

* **2026-10-23 — Random map screen.** S_SINGLERND now draws an engine-free RandomMapScreenModel (Tzar2.Game.Ui): per-kingdom colour, AI script, civilization (or RANDOM, drawn from the seed) and team, game type, victory condition with its limit, map resources and the five map options; the match it describes runs through MatchDescriptor.ToSetup. Positions and caption places from ReTzar's reading of the original. The map preview is dropped (the map options take its place); the AI button steps through the scripts until D_CHOOSEAI is built. SHELL-013 (AI resign flag) waits: it needs a per-match flag in MatchSettings.

* **2026-10-23 — Quitting, resigning and disconnecting ask first (SHELL-027).** The main menu's QUIT asks "Exit Tzar", the game
  menu's QUIT asks to resign alone and to disconnect in multiplayer, in the original's D_MSGWND window with its words
  (STRINGS.INI [q]); the model is `Tzar2.Game.Ui.Confirmations`. No hash changes.

* **2026-10-23 — The client sees the session, its frames and the UI models only (ADR 0212; code review, item 16).**
  * `GameSession.Simulation` and `Ai`, `FrameBuilder` and the match-preparation hook are now internal to Game and its
    hosts.
  * An architecture test checks the client contract's public surface.

* **2026-10-23 — Network and probe tests poll until a deadline (issue #179).**
  * The server test now polls for the Welcome and the lobby listing.
  * The relay harness has generous timeouts; the keep-alive test sets its own.
  * The memory probe's sampling runs on a manual clock.
  * Loaded Windows runners no longer fail them.

* **2026-10-23 — The batch merge of 46 PRs (#132–#178, without #141).**
  * STATUS.md and the generated ADR index were left out of all 46 PRs and are rewritten here, in one change.
  * Golden and bench hashes were re-pinned where the PRs met: the civilization order (ADR 0211), the movement of
    ADR 0220, the ledger chain (ADRs 0250–0261) and the rules decided by data instead of names in code.
  * `HumanVsAiTests`' Archipelagos check plays 20 minutes (#182; first set to 18 in the batch, with a wrong reason). The
    landed wave has hit after 17 minutes since WORLD-015 (#126): 17.2 on `main` before the forests, 18.4 with them.
    The 14 minutes passed only on a xebec's shots at the human's woodcutters, which ADR 0220's corner slide moved out
    of range with the forests (ADR 0220, Consequences).
  * The lobby fix UI-L2 (#140) was superseded by the `LobbyScreenModel` lobby (#160, ADR 0232), and then by the
    lobby rebuilt on the original's art (#177), which also takes #166's custom maps and limits and the ruleset's
    civilizations (#174).
  * Where PRs met in code: `LobbyScreenModel`'s population limits and speeds became `ImmutableArray`s (#145's
    architecture test), and the render bench (#170) uses the loading stages of #146.

* **2026-10-22 — The UI review's test and findings (work queue 15, ADR 0200).** A headless UI smoke test opens 35
  screens and dialogs of the shell, the game and the editor at 22 displays (1024 × 768 to 3840 × 2160, Wide at every
  interface scale and Authentic). It fails when a control leaves the window or covers another. A new CI job
  (`godot-smoke`, Linux) runs it and the other client smoke tests. Findings per screen and display:
  [reviews/2026-10-ui-review.md](reviews/2026-10-ui-review.md). Six layout findings, among them Authentic in a
  720-row window cutting off the HUD's bottom. Twenty-one likeness findings: texts not from STRINGS.INI, the
  multiplayer screens on borrowed art, the scenario dialogs off their boxes. Eight requests for the owner's
  screenshots (two added for HUD-028 and HUD-031) and one decision (the main menu against its carved slots). Fixes
  follow in small PRs.
  * Fixed: UI-L1 (a window smaller than the logical screen shrinks the picture), UI-L2 (the lobby's rows), UI-L3 (the
    editor's windows on 1024-wide screens), UI-L4 and UI-L5 (text wrapped to its box): no open layout finding is left.
  * UI-T1, UI-T2, UI-T3, UI-T5: shell labels from STRINGS.INI.
  * UI-A3, UI-A10, UI-A11, UI-A12: the load selector and the scenario dialogs in their art's boxes.
  * UI-A2: the credits screen (SHELL-020).
  * UI-A4, UI-A9: the end-of-game and options slots as far as the data shows them.
  * UI-A6, UI-A7, UI-F1, UI-T6: the multiplayer menu, lobby and replays rebuilt on the original's art, pieces and
    font.

* **2026-10-22 — Spell magnitudes as the original casts them (ADR 0261).** From ReTzar: heal +6, bless +5 attack to
  friends within 4 tiles, lightning 150, freeze every unit within 3 tiles for 20 s without defence and up to 40
  damage, invisibility 45 s, stone 2-5 min, confusion every unit within 2 tiles, flame bolt 60, magic vision 60 s,
  raise dead every body within 8 tiles of the caster. New rule MAG-024. The gameplay golden and bench are re-pinned;
  fire rain, whirlpool, cooldowns and cast times stay UNVERIFIED.

* **2026-10-22 — Spell ranges and mana as the original dispatches them (ADR 0260).** From ReTzar: fire rain 14 tiles
  and 100 mana, lightning 12 and 50, freeze 12 and 60, heal 3 (5 with Medicine) and 4, confusion 12 and 70, the rest
  8 and 50, summons their creature's mana. New rule MAG-023. The gameplay golden and bench are re-pinned; the other
  magnitudes stay UNVERIFIED.

* **2026-10-22 — Jewelry: the markets sell and buy items (ADR 0259).** From ReTzar: with Jewelry one market of the
  kingdom gets an offer (book, power item, book or potion with a random record) a second after it stands and then
  every minute, the newest 8 kept; any market sells an offer for 4 times its value and buys carried items for theirs.
  Map books now read their spell by the original's numbering. Hashed layout 16, snapshot 18; every golden and the CI
  benches re-pinned. Ledger: 41 open rules before, 40 after.

* **2026-10-22 — Experience on the original's scale (ADR 0258).** From ReTzar: a kill is one point and damage none;
  the first point gives level 1 and level k needs k more (Warfare, Tactics, Sun Tzu: 70, 50, 35 %); dummies a point
  per 75 of damage up to level 8; each academy +10 to the cap; Mastery teaches one point. Replaces UNK-23's scale. The
  AI skirmish is unchanged (no Learning in 10 minutes); the gameplay golden (level-ups 95 to 269) and bench are
  re-pinned.

* **2026-10-22 — The trade profit as the original computes it (ADR 0257).** From ReTzar: load x tiles x 40 / (map
  side x 100), the tiles on the larger axis between the markets, none under 12; it replaces the UNK-28 placeholder
  (1 % a tile, at most 300 %). The gameplay golden and bench are re-pinned.

* **2026-10-22 — Cows grow meat that falls as food items (ADR 0256).** From ReTzar: a cow's meat starts at 300 and
  grows 2 a second up to 600; dead, it falls as food items of at most 100. A peasant takes a resource item as its
  load and carries it home; any other unit brings it in at once. Hashed layout 15, snapshot 17; every golden and the
  CI benches re-pinned. Ledger: 42 open rules before, 41 after.

* **2026-10-22 — Soul items are bound to a unit; the Messiah takes no magic (ADR 0255).** From ReTzar: Soul Bind puts
  a soul bound to its target in the caster's bag; item_kill kills that unit (heroes too), item_berserk sends it
  berserk for 20 s, the soul stays and goes when its unit dies. Magic (spells, souls, mages', dragons' and priests'
  attacks) deals the Messiah no damage. Hashed layout 14, snapshot 16; every golden and the CI benches re-pinned.
  Ledger: 43 open, 42 after.

* **2026-10-22 — Healing, freezing and raise dead skip machines (ADR 0254).** From ReTzar (0x2719, 0x271c): golems,
  siege engines, carpets, ships, ghosts, skeletons and fishing boats are not healed, frozen or cursed, and they and
  the bodiless (bats, mages, dwarves, orcs, jinns, dragons) are not raised. New rule MAG-022. Ruleset hash only.

* **2026-10-22 — Tournaments and Mastery (ADR 0253).** From ReTzar: every 75 points a kingdom's units learn teach
  each Asian peasant, monk, ninja and priest one more; Mastery's priest teaches only those pupils, of a lower level
  than its own. The gameplay golden's ruleset hash is re-pinned. Ledger: 45 open rules before, 43 after.

* **2026-10-22 — Trade with any market; Asian peasants trade (ADR 0252).** From ReTzar and the data: a trader uses
  any kingdom's market, enemies' too, for its own profit; caravans carry 500 (European) or 300 (Arabian), Asian
  peasants 100; caravans load 8 infantry. The gameplay golden and bench are re-pinned. Ledger: 48 open rules before,
  45 after.

* **2026-10-22 — Cancelled sites, broken-off loads and the computer's repairs (ADR 0251).** From ReTzar: a site gives
  back all of its cost before any work, then half of what is left to build; a load is whole only at the end of the
  work cycles; the computer repairs only with idle peasants near the damage. Ledger: 51 open rules before, 48 after.

* **2026-10-22 — The curse, hypnosis, the banshee and the Messiah as ReTzar reads them (ADR 0250).** The curse halves
  attack and takes all defence of enemy non-machines within the caster's sight for 30 s; a ghost's blow curses for
  free in 71 of 1001; hypnosis hands a unit to the caster's kingdom for 30 s in its own colours; lasting spells do not
  take on the Messiah. Hashed layout 13, snapshot 15; every golden and the CI benches are re-pinned. Ledger: 54 open
  rules before, 51 after.

* **2026-10-22 — Custom maps in multiplayer, time and score limits (ADRs 0233, 0234; SHELL-018).** The host
  chooses a random or a custom map; every peer finds the custom map by its content hash among its own (a player
  without it cannot be ready, and the loading barrier stops the match); slot k plays the map's kingdom k; USE MAP
  SETTINGS keeps the map's own settings. Lobbies have eight slots, opened or closed to fit the map. The time and
  score limits are typed in; the game types stay Standard and Deathmatch. ADR 0234 proposes the Noise handshake
  for a public relay (owner questions in it).

* **2026-10-22 — The multiplayer setup: colours, AI scripts and every map and game option (ADR 0232; SHELL-016,
  SHELL-017, SHELL-018).** Each player picks his colour and the AI script of his kingdom's computer player (AI.INI's
  strategies); the host does the same for computers, and everyone is told when he changes one. The host sets the
  map's resources and options, the game type, the victory, the starting resources, the population limit, Explore,
  day and night and weather. The lobby screen draws an engine-free `LobbyScreenModel`. Custom maps in multiplayer
  remain open (SHELL-018 partial).

* **2026-10-22 — A match loads on a worker thread (code review, item 16).**
  * Sprite decoding and packing, the terrain's layout and its composition into the tile atlas now run on a worker
    thread. The loading screen stays up and responsive meanwhile.
  * The main thread only uploads, which takes 60–80 ms. Before, it froze for the whole load: 4 s for an original map
    on the review machine, 12 s for a real-world map.

* **2026-10-22 — Rules no longer decided by names in code (code review, item 16).**
  * The "destroy the castles" victory counts a `Seat` trait, not the class name `castle`.
  * The tavern, gambling and loans name their technologies in `world:` settings, not in Sim or card code.
  * The client's Delete honours `ProtectedDelete` and finds the start camera by `Seat`.
  * Only the ruleset hash moves.

* **2026-10-22 — The main campaign is completable: 19 of 19 (ADR 0220; work queue 10).**
  The survey on `main` said 17 of 19, but AI resignation (DIP-009) had won or broken missions, so the real count was
  15. Scenario kingdoms no longer resign, and scripted orders work for a beaten kingdom. Units aboard count where
  their ship is, and `$cargo` is what is aboard or inside (ReTzar). In movement, allies are friends and units slide
  past corners. UNK-81 is settled by the owner: cliffs stop every ground unit but the ninja, who climbs a band of one
  or two tiles (WORLD-018; a new `climber` locomotor). Goldens are re-pinned. The solver gains tribute, item pickup by
  a unit that can reach it, evacuations, sticky ferries and goal protection.

* **2026-10-22 — Civilizations are an ordered list in the ruleset (ADR 0211; code review, item 16).**
  * `ruleset.civilizations: [euro, arab, asia]` and `commonCivilization: comm` replace an ordinally sorted list.
  * This fixes a bug: the trigger variable `Race` read 1 Arabian, 2 Asian and 4 European; it now reads the original's
    1 European, 2 Arabian, 3 Asian.
  * A kingdom without objects now defaults to euro instead of arab.
  * The gameplay, AI and synthetic-mission hashes are re-pinned; the AI's commands are unchanged.
  * The client's skirmish, custom-map and lobby screens offer the ruleset's playable civilizations instead of three
    keys written in code.

* **2026-10-22 — The S2 render bench (ADR 0244, M7 D6).** `--bench-render` plays the S2 scenario (512² random map,
  eight Hard AIs, 4,000 units, day/night and weather, no fog) with V-Sync off and prints one `RENDER-BENCH` line:
  FPS, frame and sim tick percentiles, memory, load time and the S2 verdict. First run on an M5 MacBook at 1080p:
  120 FPS, sim p99 14.8 ms (above the 12 ms budget), VRAM 3.1 GB; the owner's Windows runs decide.

* **2026-10-22 — Random maps grow the original's forests (WORLD-013, ADR 0201).** `[randomtreeset]` drives a plasma
  field and the chances of a tree as ReTzar reads the original. Forests are tighter, about a fifth of the land instead
  of a third (Medium Continental 1531 → 1001 trees). The map generator's golden hashes are re-pinned; the bench hashes
  do not change.

* **2026-10-22 — M6 exit criteria met; relay keep-alive and token rotation (ADRs 0230, 0231; work queue item 12).**
  Issue #62: peers were dropped as timed out whenever the test thread did not pump for about a second, because
  LiteNetLib pinged once a second against a 1 s timeout; it now pings five times per timeout. The relay spends a
  rejoin's token and sends a new one. Soak: 500 of 500 matches, 0 desyncs.

* **2026-10-22 — M7 plan and the Windows beta's infrastructure (ADRs 0240–0243).** Seven deliverables; PR #132: the
  Windows package (export, zip, Inno Setup, version `0.7.0-beta.<run>+<commit>`), the first-run import, the signing
  step that skips without secrets, opt-in crash reports with sessions for the crash-free rate. Nothing is sent, signed
  or published until the owner chooses the server and the certificate.

* **2026-10-22 — Terrain from a deduplicated tile atlas (ADR 0210; code review, item 16).**
  * Every distinct 32 × 32 composed tile is stored once. A chunk keeps a slot per tile and frame instead of a
    512 × 512 texture per animation frame.
  * On a huge map, video memory falls from 1.3 GB to 0.28 GB and the managed heap at match start from 1.37 GB to
    0.38 GB.
  * A real-world 512² map needs 332 MB of video memory instead of about 2.7 GB of terrain textures.
  * The pixels drawn are the same.

* **2026-10-22 — The code review's first measurements (item 16).**
  * `t2 bench --sample` reports a long match's tick times, GCs, heap and snapshot size over time.
  * The client's `--resource-soak` plays matches back to back and logs Godot's memory, object and video-memory
    counters.
  * The ranked findings are in [reviews/2026-10-code-review.md](reviews/2026-10-code-review.md).

* **2026-10-22 — The client frees what a match no longer needs (code review, item 16).**
  * Hidden shadow viewports stop rendering. Glow and ghost never show shadows, and the Shadows option hides the rest.
  * The sprite atlas drops its CPU pages after the upload, and the upload's Godot images are disposed.
  * The decoded-asset cache is cleared back in the menu.
  * The menu's managed heap after a match falls from 270 to 150 MB. The next match decodes its sprites again, about
    0.4 s.

* **2026-10-22 — Music renders without allocating (code review, item 16).** `MidiPlayer` pushes whole chunks of
  512 frames through one array; it used to allocate a slice every frame.

* **2026-10-22 — The nightly gameplay benchmark rows re-recorded (code review, item 16; ADR 0080 history).**
  * A bisect over #64–#131 on one runner shows s2 flat and the gameplay cost rising only where its play changed. The
    walking speeds of ADRs 0171/0172 brought more fighting, so p99 went from 8.1 to 9.3 ms on the EPYC 7763.
  * There is no code regression. The s2 failures of 10-19 and 10-20 were slow runners.

* **2026-10-22 — The architecture tests close four gaps the code review found (item 16).**
  * The compiled client is now checked on CI. The test read only the `Debug` DLL, but CI builds `ExportRelease`.
  * The sim may use only `Content.Compiled`.
  * The AI may not call `ChangeOwner`, `ConvertType`, `RefreshMaxHealth` or `AddTechnology`.
  * No public static state can be changed. Six public static arrays became `ImmutableArray`s.

* **2026-10-22 — Output no longer depends on the machine's locale (code review, item 16).**
  * A new analyzer rule, T2C001, runs on every project under `src/`. It fails the build when an interpolated string or
    a string concatenation formats a `double`, `float`, `decimal` or date with the current culture, or an integer with
    a culture format such as `N0`. CA1305 does not cover these.
  * It found 48 strings: 35 in the fidelity suite (its verdicts read "37,5 s" under a German or C.UTF-8 locale, which
    failed a test on macOS), the rest in the CLI and the client.
  * They now use the invariant culture.

* **2026-10-22 — Handover to the next agent.** The owner's order: finish, write down what is done, hand over. The
  [Handover](#handover-2026-10-22) section gives the scope, where to look and the owner's PC. The work queue gains the
  UI review (item 15: every modern resolution, likeness to the original's texts, fonts and buttons) and the code
  review (item 16: performance, resources and memory; loose modules and editable rules for a fork with other rules,
  design, races and progression). DEVELOPMENT.md gains the sources of knowledge about the original, ReTzar among them.

* **2026-10-22 — Gates and bridges take the cells of their pictures (ADR 0192).** As ReTzar reads the original: a gate
  is 7 × 1 across and 1 × 6 down (`gw0_anim`, `gw2_anim`), its door the three tiles in the middle, with four facings
  (the map's shape bits); a bridge down is 6 × 11 (`bridge_vert`). Gates are drawn now, raised when closed and lowered
  when open. A type may have a turned footprint of its own. Left: the passage in front of a gate.

* **2026-10-22 — Technology effects as the original computes them (ADR 0191).** ReTzar's reading of the research
  handlers replaces the UNK-21 placeholders: whole points of attack and defence by weapon and armour (Temper Iron +1,
  Chain Mail +2, Engineering +5, Gunpowder +10...), cost cuts (Feudalism 80 %, Horsemanship 75 %), Architecture 70 %
  of the TIME, Fanaticism and Buddhism +50 % resistance, Great Wall +30 % hit points, Concentration's mana, the
  experience speed of Warfare, Tactics and Sun Tzu. Then Supply Storage and Seafood's +25 % population limit
  (TECH-022) and Ore Flotation's mines that lose half of each load.

* **2026-10-22 — Rules read from the original's code by ReTzar (ADR 0190), and the owner's list.**
  * The owner pointed to ReTzar (GPL-3.0, Rust), a remake whose code names the addresses of the original it was read
    from. DEC-09 holds: we read it and write our own code; the ledger names it as a `community` source.
  * Walking speed follows the walk sheet's stride for every class: a pixel every `(32000 / move_len) >> 5` of the
    original's ms. It gives the 18 measured classes within 1 % and replaces SPEED × 10 for the rest (WORLD-012).
  * Crusades gather: 16 at the start, one per cathedral every 12 to 1 s, a ceiling that grows with full crusades, one
    crusader every 500 ms, 4 again after they died (MAG-016, MAG-017).
  * The gathering technologies raise loads by 2 (Irrigation 4) instead of speeding gathering (TECH-006, TECH-010).
    Trade Taxes and Banking add 5 % and 7 % profit (TECH-023, TECH-024). Ship levels shorten the firing period to 800
    at level six (TECH-036).
  * From the owner's list: the yellow stars over a unit with a spell book (ITEM-012), using an item reveals an
    invisible unit (FOG-005), working peasants named by their job (HUD-032).
  * Also: no experience from allies (EXP-003); a computer kingdom resigns when its script says so (DIP-009).
  * Every golden, the trigger mission, the CI benches and `legacy.referenceDataHash` are re-pinned.
  * Found in ReTzar for later: every technology's exact effect (UNK-21), the trade profit's distance rule, the soul
    gem bound to a unit, ore flotation, Tournaments.

* **2026-10-22 — The controls (ADR 0189).** The owner's order, while the fifth play-test runs: first what every game
  uses (the item-book stars, the crusade's recovery, units revealed by using items, the workers' job names, the whole
  of the controls), then every gap that changes play on any map, without asking.
  * Shift queues attacks (CTRL-026), guards (CTRL-027) and patrol points (ORD-006) as it queued moves. Alt takes every
    object of the clicked type on the screen.
  * The selection's way shows while Shift is held (CTRL-025), and the group digit in the corner of the marks
    (CTRL-020).
  * Alt or Ctrl turns a gate's ghost and Alt a bridge's (CTRL-024, BLD-019). A gate may replace pieces of its own wall
    (BLD-018).
  * **Fixed:** the placement ghost judged everything as land, so peasants could never place a bridge, and a harbor far
    from water looked legal. The frame now carries water and land for the ghost.
  * Explore takes an area (ORD-007). The unload pointer shows where units can land (ORD-014). Ctrl+] and Ctrl+[
    without a selection use levels 7 and 8 (EXP-007).
  * The hashed layout goes to 12 and the snapshot format to 14. Every golden and the CI benches (sim DDD2164E92480AF0,
    gameplay FE78B491678EEC15, AI 5AB3FD5C248F6814) are re-pinned; no behaviour of theirs changed.
  * Found, not done: the original's gate is 7 tiles wide with four directions, and Tzar2 does not draw gates.

* **2026-10-22 — Units have the original's body (WORLD-015, ADR 0188; batch K).**
  * Batch K measured the original's footmen. At rest they stand one to a tile, 31–32 px apart. A compact group keeps
    its formation. Head on, two columns pass 27–35 px apart.
  * Each land unit's body is now 7/16 of its IMAGES.INI object box (14 px for 32 × 32), so units keep 28 px apart and
    rest one tile apart.
  * The terrain keeps the old footprint (7 px), so gaps between trees stay open. Units coming the other way squeeze
    past, so columns cross a bridge.
  * The AI's economy is about 8 % slower in crowds and its first waves come later; the AI tests' bounds follow.
  * Every golden, the gameplay and AI benches (20342682D5E845FC, BE0C28868D8D9F99) and `legacy.referenceDataHash`
    (4785FC7F3809937A) are re-pinned.
  * **Operator:** the fifth play-test (work queue item 5) can follow.

* **2026-10-22 — Save and load of every single player game (SHELL-014, SHELL-021, SHELL-022; ADR 0187).**
  * F2, F3, the in-game menu's SAVE GAME and LOAD GAME, and single player's LOAD GAME open the save and load
    dialogs, built on `D_SAVE` and `D_LOAD`.
  * Saves have names and live in `user://saves`. Each continues a campaign mission, a custom map (with its setup) or
    a random map (with its match description). The save format is 2; format 1 files still load.
  * Multiplayer matches are not saved.

* **2026-10-22 — The inventory (HUD-017, HUD-018).**
  * The eye (ORDERS `holder`) and the arrows (`left`, `right`) sit in the three small boxes left of the orders bar.
  * The eye is lit when the selection carries items. A click on it, or `/`, shows the focused unit's items on the card
    with their own icons; the default item is framed.
  * A left click uses an item. A book whose spell needs a target waits for a click on the world; Ctrl uses it at once.
    A right click drops the item, and Alt + click makes it the default item.
  * The arrows and `<` `>` turn the pages of the card and of the inventory. Pages hold eight; the page button is gone.
  * The frame's held items carry their slot, default flag, spell and look.

* **2026-10-22 — The owner's interface fixes.**
  * The main menu is one column of equal buttons: single player, multiplayer, settings, credits, editor, asset
    viewer, quit. CAMPAIGN is gone from it; campaigns stay in single player.
  * The minimap keeps its 128 × 128 box on maps larger than 128 tiles. Its texture has one pixel per tile and had
    grown the control into a black square over the selection view.

* **2026-10-22 — The diplomacy and tribute screens (HUD-012, HUD-013).**
  * DIPLOMACY and F5 open the diplomacy screen, built on the original's `D_DIPLOMACY` art. Each other kingdom has a
    row: its race, colour and name, the three stances and the three sharing buttons. A stance button is lit lower left
    for the player's choice and upper right for the other kingdom's. A sharing button glows green when the other
    kingdom offers it too. The screen also has Shared Victory, TRIBUTE, ACCEPT and CANCEL.
  * TRIBUTE and F6 open the tribute screen (`D_ADVANCED`): gold, food, wood and stone per kingdom. A left click adds
    100, a right click takes 100 away (the step is ours).
  * The models are `Tzar2.Game.Ui.DiplomacyScreen` and `TributeScreen`; the frame now carries the stances and offers.
  * One SEND may carry several tributes in one tick (the engine's interval holds between ticks). The sim's goldens are
    unchanged.

* **2026-10-22 — Several items per kind (ITEM-016, ADR 0186).**
  * The original's maps show what items are. `item_plusplus` is the power item: swords and bows carry an attack of
    7–24, shields a defence of 5–50. Each potion sets one of health, levels or mana. A book names its spell by number,
    read here as the order of STRINGS.INI `[spell_names]` (a hypothesis).
  * Items keep their own effect, spell and look (hashed layout 11, snapshot format 13). The workshop's three kinds
    make a book of any of 20 spells, five potions, and a sword, bow or shield. The order `CraftItem` carries the
    variant in X.
  * The goldens and the three CI benches are re-pinned (sim C24DD5082C0A543E, gameplay 3EB268D2FFC87ECA, AI
    240D80C953319E76); the AI's commands are unchanged. `legacy.referenceDataHash` is 42D6158654D385C7.
  * **Operator, when convenient:**
    * In the original's magic workshop: which spells the book list offers, its gold minimum, and what a potion and a
      shield of some gold give.
    * In TzarEdit: the spell a book with magic type 1 shows.

* **2026-10-22 — The blow lands at the animation's action row (WAR-014).** The importer reads each unit's attack
  animation (IMAGES.INI `actionrow` and frame delays) and lands the blow at that share of the swing (`Armament.hitPermille`;
  footman 60 %, archer 78.5 %, pikeman 66.5 %), not at half of it; the placeholder `hitDelayPercent` stays for types
  without the data. The synthetic package carries the footman's and the archer's animations: the gameplay and AI goldens
  and the gameplay bench (8D8069D6C80FFC52) are re-pinned; the sim and AI benches are unchanged.

* **2026-10-22 — Waypoints (ORD-005).** Shift + right click queues up to 8 more points on a unit's way, each with its
  own mode (Ctrl: move-advance, Alt: move-sneak); another order drops them. The new store raises the hashed layout to
  10 and the snapshot format to 12: every golden stream and final hash and the three CI benches are re-pinned
  (sim 3397C9D0CA2C790C, gameplay 318FD48B314B4211, AI 498368A1CAC5CA1D); the AI's commands are unchanged.

* **2026-10-22 — Work queue item 3: one peasant per field, move-sneak, siege friendly fire.**
  * ECO-021 (help HT_TEND_FIELDS): a field takes one worker; a peasant sent to a tended field goes to the nearest free
    one or stands idle.
  * ORD-004: Alt + right click moves without picking fights, on the way or after; hit, the unit fights back.
  * WAR-013 (help WI_SIEGE_WEAPON): catapult stones hurt their own side too.
  * The AI follows: the wood for the fields its farmers lack counts in its shares, fields have their own limit of
    sites at once, and an idle worker whose resource has no room left goes to the next one. On land its economy is as
    before (about 7,300 delivered in 8 minutes); small islands feed fewer farmers, so the naval test allows 20 minutes
    and one landing, and the tiny land maps 8 minutes for the first hit on a passive human.
  * The AI golden and the CI AI bench (9D18FEEFCD3376F2) are re-pinned; the gameplay golden's ruleset hash too.

* **2026-10-22 — Spies take units over; the unmasking reach as measured (ADR 0184 amendment 6).**
  * Run 10 (`capture-20261022-011438`): kingdom 2's spies, spawned red, walked up to the disguised peasant. With a
    fresh meter they unmasked it at about 3.4 tiles; with a full one only from the next tile. With the owner's 4 tiles
    at a meter of 8, Tzar2 now unmasks within 4 tiles for an empty meter down to 1 for a full one, only by the copied
    kingdom's spies and only in front of them.
  * Impersonating takes the unit over: the spy is removed, and the unit changes hands, keeps its stats and wears its
    old colours. Revealing or being unmasked turns it into a spy. Its card hides build and repair.
  * Only the ruleset hash changes.

* **2026-10-22 — The owner's own test; run 10 spawns enemy spies at set distances.** By hand, at a meter of 8, a spy
  spawned as the enemy's unmasked the owner's disguised spy from 4 tiles. Spies handed over with `hmkingdom` may never
  unmask (a cheat artefact). Run 9 was not run. Run 10 spawns kingdom 2's spies red, one after another, 10 to 2 tiles
  north of the disguised peasant, at three meters.

* **2026-10-22 — Batch J, run 8: a standing, handed-over detector never unmasks.** The impersonation worked in all
  three trials. Kingdom 2's spy, set up as the owner advised (spawned ours, a tile west, stand ground, then `hmkingdom 2`),
  never unmasked the peasant, from 13 tiles to right in front of it, at meters 12-53, 69-100 and 100, and it struck as
  soon as the peasant revealed itself. Run 5's detector, spawned red and never told to stand, unmasked from 6 tiles.
  Run 9 tells the two apart: handed over without stand ground, and spawned red.

* **2026-10-22 — Batch J, run 7: MP1 was forest; run 8 back on MP2.** The row chosen on MP1 was dense snow forest: the
  converter's reading of tree cells missed the snow set's trees, so the spies never got to the impersonation. Run 8
  goes back to MP2's known open band and keeps the owner's set-up: the detector spawned ours after `hmkingdom 1`,
  walked one tile west to face the approach, stood, then given to kingdom 2. The view is put back before every step.

* **2026-10-21 — Batch J, run 6; the owner on what spoils the spy runs.**
  * Run 6 (`capture-20261021-232712`): at a meter of 45 the disguised peasant stood right next to kingdom 2's spy and
    was not unmasked. Revealing turned it into a spy (attack 20, 50/50), its name still counting ("Spy (51)"). The
    camera jumped to a fight at our right-hand castle, and the scenario's later clicks landed elsewhere.
  * The owner: use a map or a part of it without distractions; after `hmkingdom 2` the next spawn is kingdom 2's, so
    deselect and type `hmkingdom 1` before spawning the detector, tell it to stand its ground, then give it to kingdom
    2; and **a spy unmasks only what is in front of it**: right next to its back, nothing happens (MAG-012).
  * Run 7 runs on MP1's open row 36, between the river and our base, with no castles on the map. The detector faces
    west after a one-tile walk, and the disguised peasant comes into its face from the west.

* **2026-10-21 — Batch J, run 5: the takeover seen; the distance bounded from below.**
  * The spy copied a peasant of kingdom 2 and took it over, as the owner said. The peasant stayed, in kingdom 2's
    colours even to us, with its own portrait and attack, as "Tzar 1 / Spy (N)", and a box selected it as ours. Left
    alone, it went to chop wood.
  * Kingdom 2's spy, spawned about 6 tiles off, gave it a spy's look and attack 20 within seconds while the meter was
    about 3. The meter went on counting, and the spy did not attack it. The 5-tile placeholder is too small.
  * The difficulty arrow did nothing (Normal is the lowest).
  * Run 6 keeps the disguised peasant in place, spawns the detector 14 tiles off and walks up one tile at a time.

* **2026-10-21 — Batch J, run 4: nothing measured; run 5 prepared.**
  * The spy, spawned next to kingdom 2's peasant, went for it at once, so the click meant to select it missed and
    the impersonation never happened.
  * In the scenarios that waited 55 and 125 s, kingdom 2's army (Normal) reached our base.
  * Run 5: the spy is spawned first and stands its ground, the peasant two tiles off; the setup screen's difficulty is
    lowered twice before PLAY.

* **2026-10-21 — Batch J, run 3: repairs measured (ADR 0185); only the copied kingdom's spies unmask (ADR 0184
  amendment 5).**
  * A repairer's blow restores max / (4 × TIME), rounded down: 6 on the mill, 5 on the barracks, 16 on the castle,
    one every 1.25 s. Every repairer strikes full blows, about seven of our idle peasants came to help unasked, and
    repairs cost nothing.
  * The owner's falloff (100, 75, 50, 25 %) and cost (half the construction cost) are replaced by the measurements.
    The gameplay golden and the CI gameplay bench are re-pinned. The suite is at **10 of 30**.
  * A spy in its own kingdom's peasant's look was never unmasked by kingdom 2's spy, not even while they fought.
    Tzar2 now lets only the copied kingdom's spies unmask a disguise. Run 4 measures the distance with a kingdom-2 look.

* **2026-10-21 — Batch J, run 2; the owner: a spy takes the unit over (ADR 0184 amendment 4).**
  * The owner: when a spy impersonates a unit, the unit stays and the spy is gone; the player controls the unit, which
    keeps all its stats. Tzar2 will change to that next (MAG-012 partial until then).
  * Run 2 (`capture-20261021-212542`): the buildings stood and were damaged while kingdom 2 held them (mill 440/800,
    barracks 1016/1400, castle 7760/8000), but the 4-byte memory scan found no hit points. Our idle peasants came to
    help with the repair.
  * The spy runs on MP6 failed: the map loads slower than the scenarios wait, so the first cheats were lost; the
    camera starts in the middle; every kingdom there has an AI.
  * Run 3: 2-byte scans and the buildings' selection view during the repair; the unmasking on MP2, with kingdom 2's
    AI driving the detecting spy and the whole view captured every 200 ms while the two close in.

* **2026-10-21 — Batch J, run 1: the disguise meter measured (ADR 0184 amendment 3).**
  * The install differed only in `tzar.ini` and `Demos/tzar.wdm`, which the game wrote when the owner played it; the
    run used `-AllowModified`.
  * The meter is the number after the spy's name, "Spy (N)": 100 in its own look, 0 with a new look, one point per
    1.25 s. Tzar2 now fills it in 125 s and shows it the same way. Only the ruleset hash changes.
  * The spy copied its own kingdom's peasant, which was gone afterwards, and the selection view showed the peasant's
    portrait, stats and card. Run 2 repeats this with another kingdom's peasant before anything changes.
  * Failed, and fixed for run 2:
    * the builder was sent off before it reached the site, so no building stood;
    * our knights do not attack our own castle (the manual: attacks are on enemies), so the building now changes
      hands to kingdom 2 during the attack;
    * `hmkingdom 3` does nothing on MP2, so the spies now run on MP6, whose kingdom 3 exists and has no AI.

* **2026-10-21 — The spy's disguise meter (MAG-021, ADR 0184 amendment 2).**
  * The meter fills from 0 to 100 in 2 minutes in one look; a new look starts it again.
  * Another kingdom's spy must come within (100 − meter) % of its detector radius to unmask the spy, so a full meter
    is never seen through.
  * Its owner sees the meter as a column beside the portrait.
  * The rates are placeholders until batch J. Only the ruleset hash changes.

* **2026-10-21 — Batch J prepared: repairs and spies.** The owner asked for scenarios for the two values the data
  lacks and added two facts:
  * repairs may restore a different amount per building, a share of its hit points or its own number (BLD-022);
  * a disguised spy has a meter by its portrait, up to 100, that grows the longer it keeps one look; at 100 it can
    hardly be unmasked (MAG-021);
  * the mage and the chariot can be copied (MAG-012), as built.

  The batch, in MP2's bottom-left corner (ADR 0171):
  * `repair-mill`, `repair-barracks`, `repair-castle` (HEALTH 800, 1400, 8000) follow the hit points every 100 ms
    while one peasant repairs. Equal steps mean a fixed amount, steps of 1 : 1.75 : 10 a share; a full repair in the
    construction time shows as about 1 : 0.9 : 2.5 per second.
  * `spy-unmask` and `spy-mastery` give the copied peasant and the detecting spy to kingdom 3, which has no castle and
    no AI on MP2 (units given to kingdom 2 are taken over by its AI, batch E). Our disguised spy then walks to the
    detector one tile at a time, sneaking, once fresh and once after 5 minutes in the look. The selection panel is
    captured for the meter.

* **2026-10-21 — The owner's answers on the open questions.**
  * Spies (MAG-012, BLD-013, MAG-018):
    * they copy any man of any civilization from next to him, but no machine, flyer, ship or magic creature (BONUS.INI
      groups; the mage, listed there, is a man and can be copied);
    * fighting keeps the disguise;
    * another kingdom's spy close by unmasks it, towers do not;
    * the 1.05 note's "obsessed by spies peasants" are spies in a peasant's look, which cannot build.
  * AI assistance is in single player too. The war adviser commands the troops, builds the military buildings and
    trains troops; the economy adviser builds the economic buildings, researches and gathers. Our AI now splits its
    construction and production that way (ADR 0183).
  * The magic workshop's panel has three toggles (books, potions, weapons and shields), each with several items; the
    dialog follows the owner's screenshot. Several items per kind is a new rule to build (ITEM-016).
  * Emerge takes the place by right click; with no unit chosen, everyone comes out at once.
  * The building being placed is green, without the kingdom's colours.
  * Right click takes one unit off a queue; Shift+click adds five, Shift+right click takes five off.
  * Every civilization's storage takes gold and stone, like the lumber mill wood. Asian peasants milk cows as the
    others do.
  * Repairs restore a set number of hit points per blow; the data has no value, so it waits for a capture.
  * The gameplay golden's ruleset hash is re-pinned; the scenarios' state hashes and the benches are unchanged.

* **2026-10-21 — The hotkey checklist is current (work queue item 1).**
  * Ctrl+] and Ctrl+[ select by experience level, above or below the average of the selection or of the own units
    on screen. Backspace uses the selected units' first item.
  * Enter is partial: the multiplayer chat line works, but single player has none, so its cheat codes cannot be typed.
  * Still blocked: the diplomacy, tribute and inventory screens, and location messages in the chat.
  * Fixed keys: 28 done, 11 partial, 6 blocked.
  * First of work queue item 3: Landing Tactics gives ships 50 % more room (help HT_USE_SHIPS: 8, 16 and 24 units
    become 12, 24 and 36), not a placeholder 4 places (TECH-019). The ruleset hash only.

* **2026-10-21 — Spies impersonate, ninjas uncloak (ADR 0184; work queue item 2).**
  * A spy walks to another kingdom's unit and takes its look (MENU.INI `spy_add`: impersonate, reveal). Other
    kingdoms see the copied unit and kingdom; its own kingdom sees it in the look but in its own colour (1.05).
  * The copied kingdom and its allies leave it alone until it reveals itself or attacks. It keeps the spy's abilities
    (1.05: impersonating peasants cannot build).
  * The ledger had MAG-013 wrong: `uncloak` is the cloaked ninja's own order (MENU.INI `ninja_cloaked`), not a
    spy's. A cloaked unit's card now takes its menu's cloaked variant.
  * Hashes: the disguise store raises the hashed layout to 9 and the snapshot format to 11. Every golden and bench
    is re-pinned, with no change in behaviour (the AI's commands are the same); old saves are refused.
  * Unverified, for the owner: how close the spy must get, which units it can copy, whether the copied kingdom's
    towers and detectors see through it, and whether attacking ends it.

* **2026-10-21 — The kingdom's card and AI assistance (ADR 0183; work queue item 2).**
  * With nothing selected, the card is now the kingdom's (MENU.INI `ownership`): next idle peasant, quests, the lists
    and, where the match offers it, AI assistance (`ownership_ai`). Before, it was empty: the ledger said the next-idle
    and quest buttons worked, but only their keys and hotkeys did.
  * AI assistance: the human's standby AI runs the economy, the war or both when asked (`SetAiAssist`, two player
    flags), using the AI's existing economy and war halves. The button in force is lit.
  * It is the lobby's "AI Assist" option, as in the original, and is always on in single-player skirmishes.
  * What each half does in the original is unverified; a capture with assistance on would settle it.
  * The kingdom's lists, which the original fills at run time: its heroes, its spies (and ninjas: the units that see
    the invisible) and the items its units carry. An entry selects the unit (an item: its carrier) and shows it.
  * The selection view also shows LEVEL for the player's own units that gain experience (HUD-016).

* **2026-10-21 — A holder's units inside: the list, Emerge one, and Unload at a place (work queue item 2).**
  * A selected castle, outpost, ship or carpet of the player shows INSIDE (how many units of how many) and the
    portraits of the units inside, with their health, in the info area. A thin bar under its own health shows their
    average health (HUD-021, from the guide).
  * A click on a portrait picks that unit, and a second click lets it go. Emerge then waits for a click on the place,
    and only that unit comes out there (ORD-015, manual). The simulation's Unload takes the chosen unit as its target.
  * Without a pick, Emerge lets everyone out next to the building at once, as ORDERS.INI gives emerge no place.
  * A ship's or carpet's Unload now waits for a click on the drop-off place, as the manual says (ORD-014). It used to
    unload at once at the nearest shore.
  * Emerge and Unload are greyed out when nobody is inside.
  * Kept out: picking one unit out of a ship, which docks first. That would change the docking component and the
    golden hashes.
  * The manual says a right click gives the place; ours is a left click, as for every other order that waits for a
    place.

* **2026-10-21 — The magic workshop makes items from the command card (work queue item 2).**
  * Its Build Item order opens the original's D_BUILD panel: the kind of item (bow, book, shield, sword, potion), the
    gold to spend (more gold, a stronger item), BUILD and CANCEL. While an item is being made the order is greyed out
    and the workshop shows its progress.
  * Patch 1.05: spell books cost four times more gold per point of power and take twice as long. Two new combat
    settings, `craftBookCostPercent` and `craftBookTimePercent`, carry it; the ruleset hash is re-pinned.
  * Which picture shows which kind of item, and the power shown in the dialog, are ours (⚠ UNVERIFIED).
  * Ledger: ITEM-008 and ITEM-009 are tested.

* **2026-10-21 — Every technology bonus checked (the owner's request after the fourth play-test).**
  * **The tests:**
    * `TechnologyEffectTests` runs every technology of the classic ruleset that has effects. It checks that each
      effect changes its targets, only for the kingdom that researched it.
    * A row per technology checks that it reaches the right units or buildings: swords and not bows for Temper Iron,
      the trees for the Saw, the markets for trade bonuses...
    * Tests of the mechanics that had none: Medicine's range, the profit bonus of Trade Taxes and Banking,
      Concentration's mana, and Fanaticism making capture slower.
    * A local test on the original's data (`OriginTechnologyTests`) checks that every one of its 87 technologies does
      something: an effect, units or buildings it allows, an order it gates, or a mechanic of the simulation. It also
      checks that every effect reaches something.
  * **Found and fixed:**
    * Banking targeted the castle, but the profit bonus applies to markets, so it did nothing. It now raises the
      profit at markets (the game's own text: "increases trade profit").
    * Gunpowder had no effect. It now raises the cannon towers' attack (a placeholder; TECH-013).
    * The ruleset hash is re-pinned.
  * **Left as they are:**
    * Tournaments does nothing yet (TECH-029). Jewelry gates the market's item orders, which are missing (TECH-030).
    * Piracy and Boarding Ram are named by the manual and the texts but are not in the original's data (TECH-031,
      unclear).
    * Fanaticism's +20 % of a house's RESISTANCE 40 barely changes the damage it takes (under 1 % with the placeholder
      scale, UNK-08); it does make capture slower.
    * The gathering technologies speed up gathering; the help says they raise the load carried (TECH-006, TECH-010,
      unclear).
* **2026-10-21 — Fourth play-test by the owner: six findings and a check.**
  * **Wood went to the mill of the first trip** (ECO-013). A worker kept its drop-off from trip to trip, so after a
    nearer lumber mill went up it still walked to the old one. Every load now goes to the drop-off nearest to where it
    was gathered. The gameplay and AI goldens and the two benches are re-pinned.
  * **A bridge hid the units on it** (WORLD-014). Bridges are now drawn on the ground layer, like fields.
  * **The bridge seemed repaired at once** (BLD-021, BLD-022).
    * Repairing peasants were shown standing: the frame had no activity for repairing. They now show the building
      animation and its sound.
    * The log of the session shows five peasants mended the bridge in 22 s (four repairers work 2.5 times as fast as
      one; UNK-18). Whether the original is slower is a question for the owner.
  * **Esc opens the game menu** when there is nothing left to cancel or deselect (SHELL-028, the owner's request).
  * **The build menu's categories had no shortcuts** (CTRL-029, HUD-034).
    * MENU.INI marks the build menu `capturekeys`: the original's categories have no letters, and the letters of their
      buildings work from the build menu. Tzar2 ignored the mark; the card model now honours it.
    * A category's hint lists the letters of its buildings.
  * **Units stand on one another** (WORLD-015). The collision radius is 7 px, half a sprite's width, and the push apart
    is soft and capped. It becomes work-queue item 4.
  * **The owner's check, that every bonus of buildings and technologies applies**, becomes work-queue item 5.
* **2026-10-21 — The core stays free of engines and of the original; UI logic moves into models (ADR 0182).**
  * **Owner's question:** a new game on the same core, with new balance, units and civilizations, perhaps on a 3D
    engine.
  * **Answer:**
    * The core is ready: it uses no engine, the rules are data, and civilizations are strings.
    * About a thousand lines of game UI logic lived in the Godot client.
    * A few paths name the original's classes.
    * The legacy AI language knows only three civilizations.
    * The frame is shaped for 2D sprites.
  * **Done now:** the command card is an engine-free model, `Tzar2.Game.Ui.CommandCard`, tested by `CommandCardTests`.
    The client only draws it.
  * **Deferred until a new game starts:** splitting the legacy importer into its own assembly, a trait for the
    kingdom's seat, AI civilizations as data, the frame's full heading, and the choice of host engine.
  * The work queue's ADR item is done.
* **2026-10-21 — Every data file of the original is accounted for (ADR 0181, decision 5).**
  * [rules/data-usage.yaml](rules/data-usage.yaml) covers the INI files and unit tables of the reference build, 32
    files or groups:
    * 4 are read whole and 7 not at all;
    * of the names in the others, 142 are used, 4 restated by hand, 18 not needed, and 48 are to do, each pointing at a
      rule.
  * `t2 rules check` checks on CI that every used name is quoted in the code. With the install
    (`--origin` or `TZAR2_ORIGIN`; `RulesLedgerTests` runs locally), every section, key and column must be accounted
    for.
  * Found and added to the ledger:
    * the attack's hit frame (`actionrow`, WAR-014; Tzar2 hits at half the swing);
    * walking strides (`move_len`, WORLD-012);
    * animations of capture, meat, murder, prayer and casting, hero portraits and creation sounds (PRES-005..007);
    * tree positions (PRES-009);
    * the minimap's terrain colours (HUD-030);
    * working peasants named by job (HUD-032);
    * the strength of random items (`RES.INI`, ITEM-015);
    * menu hints (`HELP.INI`, SHELL-026) and quit confirmations (SHELL-027);
    * the credits screen (SHELL-020; CREDITS only printed a path);
    * the player's AI assistance (none, economy, war, full; AI-004) and the kingdom's lists of heroes, spies and items
      (HUD-033).
* **2026-10-21 — The ledger's first fill: 309 rules in 21 chapters.**
  * **Sources:**
    * the manual and the strategy guide;
    * the patch notes (`TZAR110.TXT`; TDD §5.1's patch-note tests never existed, the ledger now carries each line);
    * the in-game help (`HELP/ITEMS.TXT`, 407 topics), the order descriptions of `STRINGS.INI`, `ORDERS.INI`;
    * the three play-tests.
  * **Status:**

    | Tested | Implemented | Partial | Missing | Deviation | Unclear |
    |---|---|---|---|---|---|
    | 131 | 83 | 39 | 33 | 7 | 16 |

    Existing tests now name 131 rules. A status of tested now needs a test that checks the rule itself, not the client's
    flag.
  * **Found:**
    * The simulation ignores the Shift and Alt of an order, so there are no waypoints and no move-sneak; the client's
      tests only checked the flag.
    * No diplomacy, tribute, inventory, save, load or help screens; chat only in multiplayer.
    * The selection view lacks the kingdom, the level and the units inside.
    * The help gives facts that differ from Tzar2's placeholders:
      * Landing Tactics +50 %;
      * one peasant per field;
      * siege weapons hurt their own side;
      * the gathering technologies raise the load, not the speed;
      * ship cannon levels fire faster, not harder.
    * Unhandled orders on the original's cards: online help, impersonate, reveal, uncloak, the souls' kill and berserk,
      Build Magic Item and the Asian market's item trade.
  * The two old checklists (hotkeys, editor) stay as they are.
* **2026-10-21 — A ledger of the original's rules (ADR 0181).** About 40 findings of three play-tests: about 8 are
  in the manual, about 10 in the original's data files nobody read, about 12 known only from playing the original, the
  rest plain defects. The owner asked for an automatic check. Each rule of the original now sits in
  [rules/](rules/README.md) with its source and status, and tests name the rules they check with
  `[Trait("Rule", "ID")]`. `t2 rules check` runs in CI. It fails on an unknown id, on a `tested` rule without a test,
  and on a stale report. The first chapters hold the play-tests' 49 rules, 20 of them tested by tests that existed.
  Owner decisions:
  * the side panels wait for M8;
  * the order of work is the ledger, the data accounting, the command card, then the engine/content split ADR.
* **2026-10-21 — Third play-test: the HUD.**
  * As in the owner's screenshot of the original castle panel, each order button shows how many of its item the
    selected buildings have queued, and the hourglass TIME stat shows the progress of the work in hand. The queue strip
    is gone.
  * An upgrade being made keeps its button with its count (it was hidden, its progress shown apart).
  * Long names ("Watch Tower") and the health line wrap between words beside the stats instead of running under them.
  * Card tooltips: the original's was one line ("Create Knight - 'K' ( Gold:70 Food:120 ) Requires: Stable, Plate
    Armor"). The owner preferred the box, with the hotkey added. It shows:
    * the name with the hotkey on a key cap;
    * the price with the original's resource icons, short ones in red, and the time;
    * what the order does (`orders_add`);
    * why it is greyed out, in the original's words ("Requires: ...").
  * Tooltip behaviour follows common practice:
    * 150 ms before it shows, and at once when moving to the next button;
    * it sits above the card, never over a button, inside the screen;
    * it follows the control the pointer is really over every frame, so a new selection, an emptied slot, a dialog or
      leaving the button takes it away.
* **2026-10-20 — Third play-test: the sim's rules.** ADR 0180, from the owner's knowledge of the original:
  * any builder helps on a site, whatever its civilization;
  * a queue takes any number of any items, one entry per item, and a right click on an order button cancels one of it;
  * loads appear at once when their work is done, while milk still fills the pail;
  * after a node falls, the worker takes the next one it can reach on the way to the drop-off;
  * units appear around the building on free tiles of the area with a way out;
  * idle peasants find work after 10 s.

  Delay and radius are placeholders. The goldens and bench hashes are re-pinned, the hashed layout is version 8, and
  `MultiplayerTests.A_save_with_everyones_consent…` once timed out under the full parallel run but passes alone.
* **2026-10-20 — Third play-test by the owner: what a right click does, and how it shows.** ADR 0179: each selected unit
  does its default action on what is clicked. Peasants capture enemy buildings (manual ch. 5 § 2.3), units board own
  holders that take them, pick up items, and follow an own unit (a herd gathers round the clicked cow); harvesters gather
  only resources they can. The pointer shows the selection's strongest action from the same rules (the hand only to
  capture or pick up; it showed over everything). Every original pointer clicks with its IMAGES.INI `cs_*` hotspot (UNK-40
  resolved for them). A move plays the original's red cross (`anims\cursor`) where the ground was clicked; an order on an
  object blinks its marks. The pause holds the sounds that are playing and starts no work sounds (a chopping sound kept
  playing). The rest of the play-test is work-queue item 1.
* **2026-10-20 — The AI gathers and marches to reachable ground.** The second play-test's log had 270 unreachable
  goals: the AI's gather point (7 tiles from the town towards the map centre) and its waves' move-advance goals (a
  building's centre) often lay in a building, a forest or water. Both now snap to the nearest walkable tile of the
  town's land (ai-placeholders E10/E12 unchanged otherwise); the AI golden is re-pinned.
* **2026-10-20 — The tavern and trade dialogs.** The tavern's gambling (`D_GAMBLE`: the resource, the stake, the dice
  rolling and the original's win or lose picture, the proportions 1:1 to 1:10) and exchange (`D_TRADE1`: ten for one)
  and a trader's route (`D_TRADE`: the goods each way, the load per trip, then a click on the far market; the near one
  is the trader's nearest own market) open in the card's place on the original's panels; what each cell does is ours.
  The original's tavern menus for mercenaries and heroes are empty in MENU.INI: Tzar2 lists the period's offers there
  (the frame now carries them) with the original's training icons, the count, the level and the price. A submenu opens
  when any of its orders works (the loans' menu was greyed out); the tavern's offers show unaffordable too. The odds
  pictures say 1:1, 1:2, 1:5 and 1:10, while the placeholder odds are 2, 3, 5 and 10 (UNK-28; left to the captures).
* **2026-10-20 — Second play-test by the owner; its findings fixed.** On the original map "There is not enough room for
  two of us" every kingdom owns European, Arabian and Asian castles and peasants, but the client loaded the sprites of the
  kingdoms' civilizations only: the others were invisible though selectable. Now the sprites of every civilization on the
  map are loaded too. The production queue shows, as in the original, what is being made with its progress under the
  icon and how many are left on it (no more "71 % (+4)" among the stats). The placement ghost wears the local
  kingdom's colours (it lacked the player-colour overlay). A selection of many units shows one icon per type with the
  count and the average health. The pointer is the original's default cursor `s_def`, a golden spearhead (the owner's
  screenshot of the original; the hand is for taking things), or the system's (SETTINGS → Pointer). The pause froze nothing: the session publishes frames only after ticks, so the client
  now reads the session's pause itself; animations and rain stop. Guard: a click on an own unit or building follows and
  defends it (the guards watch for enemies while walking, keep guarding after a fight and come back), on an enemy
  attacks it, on open ground holds it with a move-advance; before, only a moving unit could be guarded, and the first
  fight ended the order.
* **2026-10-20 — Units touch what they work on; arrows, shells and flames are drawn.** Before the second play-test
  the owner added four findings. Soldiers hit buildings and peasants unloaded a tile away: the interaction reach
  (32 px from the footprint, plus the unit's radius) let them stop as soon as they came near; now a unit still walking
  must touch the structure (12 px), the full reach stays for one that stopped (blocked by others). The selection mark of
  units is the square of the original's selection box (IMAGES.INI `selection`), like the structures' corners, instead
  of an ellipse. Burning buildings show the original's `fire_big` / `fire_small` flames, drawn additively as the
  original's effects are painted on black. Projectiles are in the frame and drawn with the original's pictures
  (arrows and ballista bolts turned along the flight, catapult stones and cannon balls with their shadows, rockets,
  magic stars, flame and freeze bolts) on an arc; stones, shells, rockets and the bolts leave the original's explosions.
  The pairing of projectile kinds and pictures, the flame spots and the arcs are ours (UNVERIFIED).
* **2026-10-20 — Spells, items and the other card orders the sim already had.** The 21 spell orders, raise dead and
  the ninjas' cloak and murder cast at once on the caster (self spells) or on the clicked unit or ground with the
  original's spell cursor; loans and their repayment in the tavern; bribery; the cathedral's crusade; picking up, using
  and dropping items; mine, dig, chop and fishing harvest. The frame carries casters' mana (the selection panel shows
  it beside the original's mana icon). The tooltip of a spell shows its mana cost.
* **2026-10-20 — Single player as in the original, with its maps.** SINGLE PLAYER opens the original's menu
  (`S_SINGLEPLAYER`: CAMPAIGN, CUSTOM MAP, RANDOM MAP, LOAD GAME, BACK). CUSTOM MAP lists the package's original maps
  (`legacy/maps/*.wmp`, converted with ADR 0120's converter behind the loading screen the first time) with a picture
  and the description (`S_SINGLECHOOSEMAP`); the map's setup (`S_SINGLESETUP`) keeps the map's own kingdom,
  stockpiles, population limit and fog unless the player changes them, and sets the computers' difficulty. The
  computers play the original `random.ais`, not the map's own AI script names (not resolved yet). RANDOM MAP moved to
  its own art (`S_SINGLERND`) and gained No Fog and Explore; LOAD GAME loads the last quick save of a campaign mission.
* **2026-10-20 — The AI weighs threats; units on a move-advance answer fire (ADR 0178).** Two causes of "the AI
  ignores towers": a unit on a move-advance did not fight back when hit from beyond its sight (a stone tower hits at
  13 tiles, a footman sees 6), and the military executor never looked at towers or compared strengths. Now a
  move-advance answers fire and resumes its way, and a unit may chase what can hit it. The AI rates every armed object
  by √(health × attack rate) and, by difficulty: answers enemy towers, buildings and sites at its town (Normal and Hard
  only with enough power, else the army grows first), launches waves only with enough power for the target's defence,
  retreats from lost fights, focuses on soldiers and towers before the nearest object, aims at the least defended
  building (Hard), and keeps gatherers away from tower reach. Easy weighs nothing. The tactics are Tzar2's own and
  tunable (ai-placeholders T1–T7); the AI and gameplay hashes are re-pinned.
* **2026-10-20 — Wide screens and SETTINGS (ADR 0177).** The world fills the window: the logical screen is the window
  over the interface scale (Auto: the largest whole number that leaves at least 1024 × 720), and the original HUD
  bitmap is cut into anchored pieces with its vine and marble repeated between them; the original's 1024 × 768
  letterboxed picture stays as the Authentic preset. SETTINGS in the main menu (display preset, interface scale,
  window mode and size, edge and drag scrolling, scroll speed, volumes, game speed, day/night, weather) saves and
  applies at once; menus re-centre and the game rebuilds its HUD when the window changes. The original's side layouts
  and a smooth fractional scale are left for later.
* **2026-10-20 — The original campaigns are in the campaign screen.** The import keeps the install's campaigns and
  maps (`Campaign/*.wcm`, `Maps/*.wmp`) in the package under `legacy/`; the client converts the campaigns when the
  screen first opens (the tutorial's 7 missions and the main campaign's 19: 0.4 s) with the converter of ADR 0120,
  shared with the CLI (`LegacyPlayables`). The screen is laid out on the original art's panels: the missions (paged),
  the briefing, the campaigns, the description with progress and RESTART, and BACK and PLAY in the bar's button slots.
  The reference and synthetic package hashes are re-pinned.
* **2026-10-20 — A match starts in under a second, behind the original's wait screen.** The 13-second freeze was the
  terrain, not the map (28 ms) or the sprites (0.4 s): the composer looked up a terrain's bitmap for every pixel and
  redrew whole chunks for each of the water's 515 animation frames. Now bitmaps are looked up per tile, later frames
  redraw only the tiles that move, and chunks are composed in parallel: 375 ms on the operator's PC. Skirmishes and
  campaign missions show one of the original's wait screens (`WAITSCR`) while they are built; the log has the timings
  of every match load.
* **2026-10-20 — Peasants at work look and behave right.** Milking has its own animation and a peasant carrying a
  load uses the original's carrying sprites (logs, gold, stone, field food, a pail of milk, meat). A cow claimed by a
  kingdom stops (it kept walking to its wander goal, away from its milker), and a milker at a dry cow fills the pail
  at another cow or field nearby, or waits for the milk to regrow when there is none, instead of carrying home one or
  two units a trip. Fields are walkable (`WalkableFootprint`, UNVERIFIED):
  units cross them and each farmer works on a tile of the field instead of digging beside it; nothing can be built on
  a field. Golden and bench hashes re-pinned (gameplay: ruleset hash; AI: streams; benches: gameplay and AI).
* **2026-10-20 — The command card does what the sim can.** Research (castle, guilds, mills), upgrades (towers,
  walls, gates, ships), capture, move-advance and move-sneak, stances, gate control, explore, destroy, emerge and
  unload, become priest or Jihad warrior, attack ground, next idle peasant and the quest log, with costs, research times
  and reasons in the tooltips. Researched technologies and owned upgrades leave the card, as do orders a technology
  unlocks until it is researched (TECHTREE `[orders]`). A card with more than eight orders gets pages of seven and a
  page button (a European peasant has nine); hotkeys reach every page. The frame carries each producer's queue item
  kind (unit, research, upgrade), the entity's upgrades, stance and gate state, the research queued by each kingdom and
  what the local player may research.
* **2026-10-20 — First play-test by the owner; controls and HUD fixed.** A skirmish against the AI and the demo
  campaign's first mission; 15 findings, with causes from the run's log and the client code. Fixed now: a click selects
  a unit anywhere on its drawn figure (the hit box was the INI selection box at its feet); a held right or middle button
  drags the map and a right click gives its order on release; animations and rain follow game time, so they stop on
  pause; the placement ghost has no fill, only the cells that block it turn red; card tooltips show the cost and why a
  button is greyed out (the "unavailable" barracks and lumber mill lacked resources the card did not show); the frame
  carries what the local player can make by the sim's requirement check and its researched technologies, so
  technology-gated buttons no longer stay grey for ever; Repair on the card and by right click on an own damaged
  building; portraits wear the owner's colour (ICONS.BMP uses the sprites' player range 240–255); cursors by action,
  with Tzar2's own pickaxe (gold, stone) and saw (trees) painted in code, since the original has one harvest cursor, a
  shovel, kept for fields; a hand over cows. The rest is work queue items 1–6.
* **2026-10-20 — Owner decision: the AI keeps the original scripts for strategy, new tactics.** `random.ais` keeps
  choosing the build order, technologies and army mix (the computer's character); the tactics are rewritten: threat
  assessment, base defence, target choice, retreat, tower avoidance, with difficulty scaling how smart they are. The
  play-test showed the military executor sending the army onto a watch tower's tile with a move-advance: the goal is
  blocked, the soldiers stand under fire. An ADR comes with the work.
* **2026-10-20 — Main campaign: 14 of 19 missions complete (measured).** The editor's acceptance test played every
  mission with the scripted solver on the clean GOG install and the current rules (walking speeds, gathering, TIME, the
  37.5-minute day): the same 14 as reported before the measurements; the five others time out.
* **2026-10-20 — Playable from the repository; local-only tests green again.** `tools/play/play.ps1` imports the
  owner's copy, builds and starts the client. The importer no longer takes the game's crash dumps (`Logs/`) and the
  installer's files from a played GOG install into the package. The local-only tests had gone stale since #24 because
  CI cannot run them: re-pinned on the clean GOG v1.10 EN install (`legacy.referenceDataHash`, the reference build's
  2,615 INI sections, TIME and speed pins) and restricted to the files of the reference manifest.
* **2026-10-19 — Diagnostics for the play-tests (ADR 0176).** A log folder per run of the client (`game.log`: match
  setup, every command, gameplay events, AI goals and orders, triggers, kingdom summaries every 20 s, Godot's output and
  errors), crash reports with the running match's replay, and a replay of every match including campaign missions.
  Default level: debug everywhere. A traced match at verbose has the same hash as an untraced one. The first trace
  shows the Hard AI ordering two buildings onto one site in a tick.
* **2026-10-19 — Gathering: 900 ms per unit (ADR 0175).** One load of 10 wood took ~9.0 s in the `gather-rate` burst;
  applied to every resource. Batches B and E had timed groups of peasants, so "two or three times faster" was wrong.
* **2026-10-19 — The fidelity gate moves to M8 (ADR 0174, owner decision).** Next: the gathering rate (one
  measurement), tracing for diagnosis, then a playable build for the owner's first play-test.
* **2026-10-19 — Owner decision: stop measuring unit by unit.** Measuring each unit's speed, attack and so on is not
  worth it at this stage. Where the original's tables have a value and a conversion has been measured (TIME 1.25 s per
  unit, carry loads, walking speed by stride, the day), apply the conversion to every entity; where a value is missing
  (e.g. a rate), measure it once on one object and apply the result to all. The fidelity tests are left for the final
  stages. The maintainer reviews what the project really lacks before choosing the next work.
* **2026-10-19 — Batch E: groups walk at their slowest member's speed.** Group speed verified (suite 9 of 30). The other
  scenarios failed for reasons now known (kingdom 2's AI takes over units given to it; Feudalism did not start; Ctrl+C
  chose another castle; the capture analysis missed the colour change); not repeated, by the decision above.
* **2026-10-19 — Batch E prepared.** Batch A's melee duels gave no hits because the enemy defender, idle while memory
  was scanned, walked off to Nature's cows in MP2's middle and the attack click hit empty ground. `damage-bonuses` now
  fights by our base (row 53, ADR 0171's view); the other batch E scenarios use peasants, buildings or our base already.
* **2026-10-19 — A day lasts 37.5 minutes; a match starts at 10:00 (ADR 0173).** Batch D: the clock globe (24 frames,
  one per hour) stepped every 93.7 s; the day-clock analysis now reads the globe and ignores brightness that units
  spoil. The Hard AI ended the passive game after 12 minutes, before the night: the night's share needs a longer run.
  Suite **8 of 30**.
* **2026-10-19 — Walking speed is per class, not from SPEED (ADR 0172).** Batch C and the third `unit-speed` run, both
  clean: every foot class walks 40 px/s whatever its SPEED, mounted 72, troll 47, ghost 67, cows and bears 26; speed
  follows the walk sheet's stride. Measured classes get their own speed; the speed check passes: **7 of 30**. The
  gameplay benchmark starts each kingdom's first house damaged so the golden stream still covers repair.
* **2026-10-19 — The 144-entry class table stays (product owner).** The previous agent's open question about "the 144
  class names in the converter" is `LegacyClasses` (PR #33): the original's built-in object class order, which map
  files index into. Owner decision: it is a format fact and stays; recorded in
  [original-build/README.md](original-build/README.md#1-repository-policy).
* **2026-10-19 — Unit-speed run at the new place missed it (ADR 0171 amendment).** The minimap click (0.1, 0.95) fell
  outside MP2's map, which the minimap draws in its middle 64 px; the view stayed at the start and the path crossed our
  own cows. Facts not committed; the scenarios click map tile (6, 60) and a test guards MP2 minimap clicks.
* **2026-10-19 — Unit speeds: SPEED 4 = 40 px/s; engine 10 px/s per point (ADR 0171).** Peasant, archer, priest and
  mage walked 39.9–40.1 px/s both ways. The soldiers attacked Nature's cows in MP2's middle instead of walking; the
  speed scenarios moved next to our base and the analysis rejects short or wrong-way fits. The gameplay benchmark bot
  repairs with the nearest busy peasant so the golden stream still covers repair; hashes re-pinned. The capture ran on
  a Russian keyboard layout: the harness's switch to US (PR #60) works.
* **2026-10-19 — Batch B: a node sets its own load; cows carry 50 (ADR 0170).** Wood, stone and gold verified at 10 per
  trip. Unit speeds failed because the scenario dragged a box into the edge-scroll zone; fixed, to be re-captured; a test
  now keeps scenario mouse positions 16 px from the edges.
* **2026-10-19 — Project docs (PR #64).** This file, [DEVELOPMENT.md](DEVELOPMENT.md), the ADR index and `AGENTS.md`,
  so that another agent or developer can continue the work.

* **2026-10-19 — Batch A merged; TIME is 1.25 s per unit (ADR 0169).** Training (peasant 9.99 s for TIME 8) and
  construction (lumber mill 36.85 s for TIME 30) both measured 1.25 s per unit; research times (ms in
  TECHNOLOGIES.INI) stay unscaled until batch E measures one. Golden hashes and CI bench hashes re-pinned.
* **2026-10-19 — Harness fixes from the operator runs (PR #60).** `hmnopop` before every spawn (spawning obeys the
  house limit), DirectDraw device count = `DirectDrawEnumerateA` + 1, typing on the US layout whatever the
  operator's layout. `hmkingdom N` changes the owner of the selected unit.
* **2026-10-18 — PR clean-up after the handover.** The agent's open PRs were merged after a combined local build and
  test run; stacked PRs that conflicted after squash merges (#54, #55, #59) were replaced by the same commits replayed
  on `main` (#58, #63) instead of force-pushing. #57's failing allocation gate was a test precondition, not a
  regression (fixed in the PR).
* **2026-10-18 — Handover.** The implementation agent is no longer available; the maintainer continues. Workflow
  chosen by the product owner: branches and PRs through the GitHub CLI, squash merge only after green CI.
* **2026-10-18 — Original game on the operator's PC.** Clean GOG v1.10 EN; captures need GOG's compatibility database
  removed for the session (`sdbinst -u`, the harness prints the command), `DIRECTXDEVICE=0` and cnc-ddraw with
  `devmode` (ADRs 0110, 0111; [DEVELOPMENT.md](DEVELOPMENT.md#capture-sessions-on-the-operators-pc)).
* **2026-10-17/18 — M0–M6 built by the implementation agent** (PRs #1–#53; ADRs 0001–0168).
* **2026-10-11 — Original files stay local.** Nothing from `Origin/` is committed; manifests and hashes only; the
  Spanish FX Gold Edition campaigns (local Russian translations) are kept locally as a test corpus
  ([original-build/README.md](original-build/README.md)).
* **2026-10-11 — Windows first (DEC-14).** 1.0 ships for Windows x64; macOS, Linux, Android and iOS follow in the
  platform-expansion phase, with the cross-platform constraints kept from the start.
* **2026-10-11 — Technical design (TDD).** C# core + Godot 4 .NET, deterministic lockstep, importing assets from the
  user's install (DEC-01…DEC-14).
