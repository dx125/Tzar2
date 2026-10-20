# Project status

The living status of the rebuild: where each milestone stands against its exit criteria, what is being worked on,
what comes next, and a short log of decisions and events. Read it before starting work; **update it in the same PR
whenever a milestone criterion, the work queue or the decision log changes.** How to build, test and contribute:
[DEVELOPMENT.md](DEVELOPMENT.md). The specification: [TDD.md](TDD.md). Why things are the way they are: the ADRs,
indexed in [decisions/README.md](decisions/README.md).

* **Last updated:** 2026-10-19 (batch E; owner decision on measurements)
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
| M3 Classic gameplay & skirmish | **In progress** — blocked on fidelity | Skirmish vs. Hard AI on all six map types (PR #36). Hotkey parity checklist complete by its own rules ([hotkey-parity.md](mechanics/hotkey-parity.md): 12 bindings blocked on features). **Fidelity suite ≥ 80 %: 9 of 30 (30 %)**, see below. |
| M4 Triggers & campaigns | **In progress** | Converter: all 33 original maps and both campaigns convert (reported). Original campaigns completable: main campaign 14–15 of 19 missions, tutorial 7 of 7 (reported 2026-10-18, scripted solver, ADR 0134). ≥ 90 % of the community corpus converts: **no corpus yet**. Open format unknowns UNK-70…80 need the TzarEdit diff session. |
| M5 Editor | **Code complete**, acceptance open | Parity checklist 100 of 104 done, 4 blocked ([editor-parity.md](mechanics/editor-parity.md)); §18.6 acceptance: `acc-original` verified locally 2026-10-18 (reported), `acc-fx` still needs a local run with the FX campaigns and a community campaign (`TZAR2_FX_CAMPAIGNS`, `TZAR2_CORPUS`), ADR 0156. |
| M6 Multiplayer & replays | **In progress** | Relay/lobby, lockstep sessions, drop policy, reconnect, desync reports, replays, MP saves and the MP UI merged (ADRs 0160–0168). 500-match soak with 0 desyncs: 168 matches, 0 desyncs (reported); nightly soak job runs. |
| M7 Windows beta | Not started | Installer, signing, crash reporting, min-spec tuning, closed beta. |
| M8 Modding, polish, release | Not started | |
| PX Platform expansion | Post-1.0 (DEC-14) | |

## M3 fidelity gate

The suite (`t2 fidelity report`, ADR 0112) has 30 checks from TDD §22.3; the gate is passing / all, pending counts as
not passing, so 24 must pass. AI script semantics stays pending by owner decision, so 24 of the other 29 must pass.
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

1. **Operator:** batch E (`-Batch E`, ~19 min). **Maintainer:** analyse it.
2. **Maintainer:** a night-share scenario (UNK-32, ADR 0173): 35 minutes or more of a night in a view no unit walks
   through, in a match the Hard AI does not end (find a setup without an attacking AI). Then **Operator:** run it.
3. **Maintainer:** a speed scenario for the classes not measured yet (UNK-12, ADR 0172): Arabian and Asian units need
   a kingdom of that civilisation (MP4 has one of each; spawning only works for the own civilisation), and find out why
   catapults, ballistas and caravans did not spawn.
4. **Maintainer:** before batches F and H, move or guard the scenarios that leave soldiers idle in MP2's middle next to
   Nature's cows (experience, combat outcomes; ADR 0171). **Operator:** batches F–I; **Maintainer:** analyse each.
5. **Operator:** TzarEdit diff session (maps `base`, `a1`–`e1` for UNK-70…80); 20 random maps from TzarEdit (RE-10);
   the diagonal-rock passability check on campaign map `rajba` (blocks a campaign mission).
6. **Maintainer:** decode UNK-70…80, compare random maps with our generator, fix the passability case; run the
   original-campaign harness locally (`t2 campaign run`, `TZAR2_ORIGIN`) to replace the reported 14–15/19 with a
   measured figure and push it to 19/19.
7. **Maintainer:** M5 `acc-fx` and the M4 corpus criterion: run locally with the FX Gold campaigns kept in `Origin/`
   (Russian translations, never committed) and find a community map corpus.
8. **Maintainer:** issue #62 (flaky `RelayTests`); M6 soak to 500 matches.
9. M7 planning: installer, code signing, crash reporting, min-spec hardware.

## Decision and event log

Newest first. Technical decisions have ADRs (index: [decisions/README.md](decisions/README.md)); this log also keeps
the process decisions that have no ADR.

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
