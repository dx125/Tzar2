# 0181. Keep a ledger of the original's rules, and check it against the tests

* **Status:** Accepted
* **Date:** 2026-10-21
* **Deciders:** agent (maintainer); product owner (asked for an automatic check of the original's rules, 2026-10-21)
* **Related:** TDD §10, §22; ADRs 0112 (fidelity suite), 0174, 0179, 0180; `docs/mechanics/hotkey-parity.md`,
  `docs/mechanics/editor-parity.md`

## Context

The owner's three play-tests (2026-10-20/21) found about 40 differences from the original. Roughly:

* **About 8 are written in the manual.** A right click on an enemy building captures it, and on an own damaged one
  repairs it. The hand pointer is for picking up items. Guarding, and loading by a right click on the ship.
* **About 10 are in the original's data files, which nothing read:**
  * the red cross of a move order (`IMAGES.INI [cursor]`);
  * the pointers' hotspots and the default pointer;
  * the flames of burning buildings and the projectiles' pictures;
  * the "Requires" string.
* **About 12 are known only from playing the original:**
  * a queue takes any number of any units;
  * loads appear at once;
  * idle peasants find work;
  * units appear around the building;
  * any peasant helps on a site.
* **About 10 are plain defects.** The pause, the click area of units, a tooltip that stayed, a clipped name.

The specification (TDD §10) lists systems, not rules. Features were built from it, and nobody went through the manual,
the guide, the patch notes and the data line by line. The checks that exist do not cover this gap:

* The fidelity suite (ADR 0112) compares measured numbers.
* The golden hashes catch changes, not mistakes.
* Two checklists cover only hotkeys and the editor.

The owner asked for a way to check the game against the original's rules automatically. The options were tests, a
review against the documents, or a class that hard-codes the original's rules.

## Decision

1. **A ledger of rules** in `docs/rules/<chapter>.yaml`, one file per chapter (schema `tzar2-rules/1`). Each rule
   has:
   * an id `PREFIX-nnn` that never changes or gets reused;
   * the rule in our own words, never the manual's text;
   * one or more sources, each starting with its kind:

     | Kind | What it is |
     |---|---|
     | `manual`, `guide`, `editor-manual` | the original's manual, strategy guide and editor manual |
     | `patch` | `TZAR110.TXT`, `DATA/CHANGES.TXT` |
     | `data` | a file, section or key of the original's data |
     | `help`, `strings` | in-game help and text |
     | `owner` | the owner's knowledge, with the date |
     | `capture` | a fact measured from the running game |
     | `community` | community knowledge |
   * a status (see 2);
   * optionally ADRs, a note and the work item that closes it.
2. **Statuses:**

   | Status | Meaning | Note required | Tests |
   |---|---|---|---|
   | `tested` | done, and tested | no | at least one |
   | `implemented` | done as far as the code shows, no test yet | no | none |
   | `partial` | done in part | says what is missing | allowed |
   | `missing` | not done | no | none |
   | `deviation` | different on purpose | names the ADR or owner decision | allowed |
   | `unclear` | the sources disagree or say too little | says what would settle it | none |
3. **Tests name the rules they check** with xUnit's own trait: `[Trait("Rule", "PROD-001")]`, on a method or a class.
   `dotnet test --filter Rule=PROD-001` runs one rule's tests.
4. **`t2 rules check` runs in CI** (`RulesLedgerTests`, Tools.Cli tests). It fails when:
   * a chapter is malformed;
   * a test names an unknown rule;
   * a `tested` rule has no test, or an `implemented`, `missing` or `unclear` one has tests;
   * the report in `docs/rules/README.md` is out of date.

   `t2 rules report` rewrites that report:
   * a table of statuses by chapter;
   * the lists of missing, partial and unclear rules;
   * the deliberate differences.

   `t2 rules list missing` prints the rules with a given status.
5. **The original's data is accounted for.** A local-only test (it needs the install) records which sections and keys
   of the original's INI files and tables the import and the client read. It fails on any key that is neither read nor
   listed as not needed, with the reason, in `docs/rules/data-usage.yaml`. Its ledger holds names only, never the
   original's values (REQ-LEGAL-01). It lands in its own change.
6. **How work uses the ledger:**
   * A feature starts from its chapter.
   * A play-test finding about the original becomes a rule with a test in the change that fixes it.
   * A deliberate difference needs an ADR or an owner decision in its note.
   * The owner's statements about the original are sources of their own (`owner YYYY-MM-DD`).
7. **The first fill goes through every source:**
   * the manual, the strategy guide and the patch notes;
   * the order descriptions in `STRINGS.INI` and the item help;
   * the decisions and play-test findings in `docs/STATUS.md`.

   The status of each rule comes from reading the code, and existing tests get their rule ids.

   The two older checklists keep their formats and tests: hotkeys in `hotkey-parity.md` (checked by
   `HotkeyTests.The_parity_checklist_lists_every_binding`) and the editor in `editor-parity.md`. The ledger does not
   repeat them.

## Alternatives considered

* **A class that hard-codes the original's rules.**
  * The numbers already come from the original's tables at import (ADRs 0032, 0040). A second copy in code would
    drift from them.
  * Behaviour ("any peasant helps on a site") is not a constant and can only be checked by running the game.

  What the idea gets right is kept: a test computes what it expects on its own, from the rule and the table, instead of
  calling the code under test.
* **A one-off review against the documents.** It finds today's gaps but guards nothing afterwards. It is the first
  fill of the ledger.
* **Markdown checklists like `hotkey-parity.md`.** Every checklist needs its own parser test, and a checklist does
  not link a rule to its tests. One format for every chapter keeps one check.
* **A custom `[Rule]` attribute.** xUnit 2 needs a trait discoverer in a shared assembly for custom traits.
  `[Trait("Rule", ...)]` filters the same and needs no code.

## Consequences

* The report shows the owner, before a play-test, which rules are missing, partial or unclear.
* Every rule-related change also touches a chapter file, and the report has to be regenerated.
* A `tested` status is only as good as its tests. Reviews check that a named test checks the rule, not something near
  it.
* Rules about the screen (the card, tooltips, pointers) can only be tested once their logic leaves the Godot client.
  Moving the command card's logic into `Tzar2.Game` is the first step of the command-card work (ADR 0182).
* Follow-up:
  * the data-usage test (decision 5);
  * folding the hotkey and editor checklists into the ledger when they next change.
