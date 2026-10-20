# Project status

The living status of the rebuild: where each milestone stands against its exit criteria, what is being worked on,
what comes next, and a short log of decisions and events. Read it before starting work; **update it in the same PR
whenever a milestone criterion, the work queue or the decision log changes.** How to build, test and contribute:
[DEVELOPMENT.md](DEVELOPMENT.md). The specification: [TDD.md](TDD.md). Why things are the way they are: the ADRs,
indexed in [decisions/README.md](decisions/README.md).

* **Last updated:** 2026-10-20 (play-test fixes: spells, items and the other card orders)
* **Maintainer:** Claude (AI), working with the product owner, who also operates the Windows PC with the original
  game for captures and local-only tests. The implementation agent that built M0–M6 is no longer available (handover
  2026-10-18; see the log below).
* **`main`:** green on CI. Open PRs: none. Open issues: [#62](https://github.com/dx125/Tzar2/issues/62) (flaky
  `RelayTests`).

## Milestones

Exit criteria from [TDD §25](TDD.md#25-milestones-and-exit-criteria). "Reported" means the previous agent stated it
and it has not been re-run since the handover.

| Milestone | State | Exit criteria and evidence |
|---|---|---|
| M0 Foundations | **Done** | One ADR per spike: S2 (0007), S4 (0008), S3 (0009), S1 (0010), S6 (0013; S5 mobile export is PX); S1 renderer met on Windows (ADR 0010); CI green; no original files tracked (CI job `legal`). |
| M1 Importer & viewer | **Done** (reported) | All 961 RLE sheets and the other formats of the reference build import (ADR 0013, `t2 import`); golden sprite comparisons and the import of the reference build are local-only tests (`TZAR2_ORIGIN`, `TZAR2_GOLDENS`). |
| M2 Sim core & movement | **Done** | Determinism matrix green on CI; ORIG-01/02/03 tests; B3/B4 within budget (ADR 0026). |
| M3 Classic gameplay & skirmish | **In progress** | Skirmish vs. Hard AI on all six map types (PR #36). Hotkey parity checklist complete by its own rules ([hotkey-parity.md](mechanics/hotkey-parity.md): 12 bindings blocked on features). The fidelity gate moved to M8 (ADR 0174). The owner's first play-test is done (2026-10-20); open before M3 closes: its fixes (work queue 1) and a second play-test (item 2). |
| M4 Triggers & campaigns | **In progress** | Converter: all 33 original maps and both campaigns convert (reported). Original campaigns completable: main campaign **14 of 19 measured** (2026-10-20, editor acceptance test on the clean GOG install, scripted solver, ADR 0134; not completed: `alex1`, `bigisland`, `v_02`, `vsaa`, `rajba`), tutorial 7 of 7 (reported). ≥ 90 % of the community corpus converts: **no corpus yet**. Open format unknowns UNK-70…80 need the TzarEdit diff session. |
| M5 Editor | **Code complete**, acceptance open | Parity checklist 100 of 104 done, 4 blocked ([editor-parity.md](mechanics/editor-parity.md)); §18.6 acceptance: `acc-original` verified locally 2026-10-18 (reported), `acc-fx` still needs a local run with the FX campaigns and a community campaign (`TZAR2_FX_CAMPAIGNS`, `TZAR2_CORPUS`), ADR 0156. |
| M6 Multiplayer & replays | **In progress** | Relay/lobby, lockstep sessions, drop policy, reconnect, desync reports, replays, MP saves and the MP UI merged (ADRs 0160–0168). 500-match soak with 0 desyncs: 168 matches, 0 desyncs (reported); nightly soak job runs. |
| M7 Windows beta | Not started | Installer, signing, crash reporting, min-spec tuning, closed beta. |
| M8 Modding, polish, release | Not started | Also the **fidelity suite ≥ 80 %** (moved from M3, ADR 0174): **9 of 30 (30 %)** today, see below. |
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

Facts live in `tests/fidelity/facts/` (numbers only; captures stay on the operator's PC). A measured fact that disagrees
with the engine fails `Tzar2.Fidelity.Tests`, so a fact and the engine fix that makes it pass go in the same PR.

## Work queue

In order. **Maintainer** = code and analysis; **Operator** = runs on the Windows PC with the original game.

Item 1 is the fix left from the owner's first play-test (2026-10-20; log below).

1. **Maintainer:** the rest of the command card: the market's and the caravans' trade, the tavern's gambling and hiring,
   the Magic Workshop's crafting (dialogs `D_TRADE`, `D_TRADE1`, `D_GAMBLE` in the card's place), the holders' list of
   units inside; the spies' impersonate, uncloak and reveal-self and the attack-type and AI-mode orders need the sim
   first.
2. **Operator:** a second play-test (`tools/play/play.ps1`): the fixes merged so far, and the remaining items as they land.
3. **Maintainer (deferred to M8):** a night-share scenario (UNK-32, ADR 0173): 35 minutes or more of a night in a
   view no unit walks through, in a match the Hard AI does not end. Then **Operator:** run it.
4. **Maintainer:** walking speed for every class from its walk sheet's stride (`move_len`; ADRs 0172, 0174) instead of
   more speed scenarios; why catapults, ballistas and caravans did not spawn can wait for M8.
5. **Maintainer (deferred to M8):** before batches F and H, move or guard the scenarios that leave soldiers idle in
   MP2's middle next to Nature's cows (experience, combat outcomes; ADR 0171). **Operator:** batches F–I.
6. **Operator:** TzarEdit diff session (maps `base`, `a1`–`e1` for UNK-70…80); 20 random maps from TzarEdit (RE-10);
   the diagonal-rock passability check on campaign map `rajba` (blocks a campaign mission).
7. **Maintainer:** decode UNK-70…80, compare random maps with our generator; make the five main-campaign missions the
   solver does not complete pass (`alex1`, `bigisland`, `v_02`, `vsaa`, `rajba`; all time out, `rajba` on the
   diagonal rocks) to reach 19/19.
8. **Maintainer:** M5 `acc-fx` and the M4 corpus criterion: run locally with the FX Gold campaigns kept in `Origin/`
   (Russian translations, never committed) and find a community map corpus.
9. **Maintainer:** issue #62 (flaky `RelayTests`); M6 soak to 500 matches.
10. M7 planning: installer, code signing, crash reporting, min-spec hardware.

## Decision and event log

Newest first. Technical decisions have ADRs (index: [decisions/README.md](decisions/README.md)); this log also keeps
the process decisions that have no ADR.

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
