# 0156. Prove §18.6 headlessly with the editor's own tools; keep data-dependent acceptance as local-only tests

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent M5 (editor)
* **Related:** TDD §18.6, §22, §25 (M5 exit criterion), REQ-LEGAL-01/03; ADR 0133, ADR 0134, ADR 0150–0155

## Context

M5 exits when §18.6 passes:

1. Author a 3-map campaign with conversations, a persistent hero, timers, quests, story screens, custom sounds and
   music, using only the new editor, and play it end to end on Windows.
2. Convert the original campaigns and complete every mission.
3. Convert the four locally kept FX Gold Edition campaigns and a community campaign through the converter UI, with
   zero errors, a reviewable report, a correct graph, correct Cyrillic/Spanish text, deduplicated resources, and every
   mission passing the smoke test.

Items 2 and 3 need copyrighted files that exist only on the user's machine (REQ-LEGAL-01). They are neither in CI nor
on the development machine.

## Decision

* **Item 1 is a headless test** (`AcceptanceTests.A_three_map_campaign_authored_with_the_editor_only_plays_end_to_end`).
  * The maps are made with the editor's interaction model, the same calls the Godot map view makes: placement by
    pointer events, starting locations by keys 1–8, the properties dock's edits, triggers built through their
    generated property pages (`TriggerSchema`), and the campaign editor's document.
  * The campaign is saved, reopened byte-identically and validated.
  * The first map's story screen (with music), quest, custom sound and playlist are checked in a playtest session.
  * The whole campaign is played to the end by the campaign runner of ADR 0133/0134: conversation, persistent hero
    (level 3 carried), `#` variable, timers, win/lose transitions, zero scenario faults.
  * The Godot smoke test drives the real screens (edit, triggers, playtest, import, campaign).
  * The manual play-through on Windows, and on tablets in PX, stays a QA step.
* **Items 2 and 3 are written as local-only tests** with the same checks, driven through `LegacyImportSession` (the
  converter UI's model):
  * zero errors, report rows present, every map reachable, no replacement characters in titles, every map passes the
    600-tick smoke test;
  * for the original campaigns, every mission is completed by the campaign runner.

  They run when `TZAR2_ORIGIN`, `TZAR2_FX_CAMPAIGNS` and `TZAR2_CORPUS` point at the user's copies, and are skipped
  otherwise. In the parity checklist these items are **blocked** with that reason: the code and the tests are done,
  but the evidence can only come from a machine with the files.
* **The parity checklist no longer accepts "planned"**:
  * every row is done (with an existing test) or blocked (with a reason);
  * a blocked row that names a test must name an existing one.

## Consequences

* M5 counts as done in CI terms when the checklist test passes. The product owner runs the three local-only tests on
  the machine with the campaigns to close §18.6 (2) and (3).
* If those runs fail, the failures land in the converter, the solver or the engine, not in the editor. The report panel
  and `CampaignAutoplay`'s per-mission trace show where.
