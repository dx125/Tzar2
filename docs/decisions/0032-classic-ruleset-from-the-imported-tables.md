# 0032. Build `classic` from the user's imported tables; mark everything else UNVERIFIED

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** product owner (data source, placeholders, group speed), implementation agent (M2 part C)
* **Related:** TDD §0.3, §3.3, §10, §11.3, REQ-RE-03, REQ-LEGAL-01/03, DEC-05, DEC-06, docs/formats/ini-tsv.md,
  docs/formats/dat.md, Appendix E, docs/mechanics/m2-placeholders.md

## Context

Owner decision for M2: capture data from the running original is not available yet; take values from the original
INI/TSV tables, and give anything only measurable in the running game an explicit placeholder marked `UNVERIFIED` with
its UNK reference. The importer already stores every table as data in the base package (`tables/<source>.json`, plus
the IMAGES.INI index and the DAT footprints). The repository must contain no original data (DEC-05), so `classic` cannot
ship numbers; CI has no original install (REQ-LEGAL-03).

## Decision

* **The `legacy:` section of `classic.yaml` drives an importer (`LegacyImporter`)** that turns tables into the bottom
  rules layer (ADR 0031), one entry `<civ>.<class>` per row of a listed class:
  * `DATA/<civ> UNITS.TXT` columns by position: cost (`40f, 20w` with the suffix map), time (s → ms), sight and range
    (tiles), health, attack, defence, speed (SPEED points), rate (RATE), producer (column 12 through `producers:`),
    depends-on (a `te_*` technology or a building class of the same civilization);
  * `DATA/<civ> BUILDINGS.TXT`: cost, time, sight, health, depends-on; the footprint from the `OBJECTS/*.INI` `[civ]`
    image → `IMAGES.INI` `object` box (else `selection`; w/32 × h/32 tiles) and the DAT grid when its size matches
    (`2` = free, `0`/`1` = blocked); `pop_count` from the object INI;
  * resource rocks from `resourceNodes:` with amounts from `RES.INI [Others]`; starting-resource levels from
    `RES.INI [Low]/[Medium]/[High]`; attack bonuses from `BONUS.INI` (ADR 0038).
* Each generated entry inherits `^Unit`/`^Building`/`^Resource` and, if the YAML defines it, `^class.<class>`; that is
  where classic adds what the tables lack (drop-offs, harvesting, builder rights, locomotors, collision radii).
* **M2 compiles a subset:** units peon, footman, archer, maceman, pikeman, knight, heavy_cavalry, horse_samurai;
  buildings castle, house, barrack, storage, lumber, farm, field; gold and stone rocks; a tree entry defined in YAML.
  M3 extends the lists (a list edit, no code).
* **Placeholders.** Every constant not in a table is a YAML value with an `UNVERIFIED (UNK-nn)` comment, or a code rule
  listed in docs/mechanics/m2-placeholders.md. `groupMoveSpeed: slowest` in both rulesets until UNK-12 is captured
  (owner decision, ADR 0024 question).
* **Without the imported package** compiling `classic` fails with "mount the imported base package (t2 import)".
* **Reference check** (owner decision): the compiler hashes what it read from the tables (the generated layer plus the
  raw BONUS.INI and RES.INI entries; `CompiledRuleset.LegacyDataHash`). `legacy.referenceDataHash` pins the value of the
  clean GOG v1.10 EN build (all 790 data files read by the compiler match the reference manifest). Another value adds
  "modified installation" to `CompiledRuleset.Warnings`, and the importer compiles `classic` after every import and
  copies those warnings into the import report. It is a warning, never an error: modded installs stay playable and
  lobbies still compare ruleset hashes. The pinned value changes whenever the import mapping or the class lists change
  (update it with the origin test, `OriginRulesetTests`).
* **Building RESISTANCE** (buildings column 6) compiles into `Armor.resistance` (ADR 0038).
* **CI** compiles `classic` from `SyntheticLegacyContent`: tables of the importer's shape with invented numbers for one
  civilization that differ from the original in every column (`SyntheticTablesGuardTests` checks this against a local
  install when `TZAR2_ORIGIN` is set; PR #14 review B3). Tests, the gameplay benchmark and the determinism CI therefore run the real compiler and rules. A test
  with `TZAR2_ORIGIN` imports only the data files of the reference build (seconds) and checks the compiled values
  against Appendix E.2.1 and the data files (`OriginRulesetTests`).

## Alternatives considered

* **Re-type the reference values into `classic.yaml`:** TDD §11.3 allows reference data, but it is redundant with the
  user's install; a pinned hash detects modified installs without shipping numbers. Re-typed values would add which
  values differ, if the import report ever needs that.
* **Import every row:** ships, towers, spawned creatures and siege need traits that do not exist yet; listing classes
  keeps M2 honest.
* **Guess missing numbers from manuals/community:** source priority 3-4 (§0.3) where data exists; where nothing exists
  a clearly marked placeholder is safer than a plausible-looking number.

## Consequences

* The compiled `classic` hash depends on the user's tables, so peers with different (modded) installs get different
  ruleset hashes and cannot play together, which is what lobbies need (REQ-DATA-05).
* Replacing a placeholder is a one-line YAML change plus its capture fact; the register tells what is still open.
