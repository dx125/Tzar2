# 0121. Convert legacy maps and campaigns with one deterministic converter into `.t2map`/`.t2camp`

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent M4-A
* **Related:** TDD §7.2, §12.4, §18.4, §18.5, §22.5, §25 (M4), REQ-EDT-03…12, REQ-LEGAL-01…03, RE-03, RE-04, UNK-04,
  UNK-10, UNK-70…80, RISK-02, ADR 0018, ADR 0120

## Context

M4 needs the original maps and campaigns, and community ones, in our model, with a report that says what was
lost. The binary payload of `.wmp`/`.wcm` was unknown (UNK-04). The original editor can't run on the development
machine, so the editor-diff method of §23.2 wasn't available. The format was recovered from the editor DLL's map API
(function names, parameter types, read/write order), from the trigger classes' serialisation, and from statistics
over the 33 maps of the reference build. Details are in [wmp.md](../formats/wmp.md).

## Decision

### Layers

* **`Tzar2.Formats.Maps`** (read-only, REQ-ARCH): `LegacyMapReader.ReadMap` and `ReadCampaign` decode every member
  into raw legacy records (`LegacyMap`, `LegacyObject`, `LegacyTrigger`…). Unproven fields stay raw (`Raw`,
  `Payload`). The class table `LegacyClasses` is an enumeration of the 144 built-in class names; they equal the
  `class =` values of the data files. Malformed input throws only `LegacyFormatException`; fuzz targets `wmp` and
  `wcm` cover it.
* **`Tzar2.Import.Maps`**: `LegacyMapConverter`, `LegacyCampaignConverter`, `LegacyPackageWriter`, `LegacyCorpus`.
  This is the one converter that the CLI, the editor (M5) and the first-run importer share (REQ-EDT-05). It never
  throws for bad input; problems become `ConversionReport` diagnostics (info, warning, error) with a code and a
  location (REQ-EDT-06).

### Mapping rules

* **Terrain.** The corner grid is rebuilt from each tile's terrain pair and shape by majority vote; disagreements are
  counted as info. `SetTerrainCorners` keeps the set indices. `TerrainCorners` maps water → the ruleset's `water`
  and everything else → `grass` (the classic terrain list). Bit 10 of a tile word → `CliffTiles`.
* **Trees.** Tree tiles with index 4–31 become `comm.tree` objects owned by Nature, appended after the legacy
  objects. Stumps (1–3) become decorations.
* **Objects.**
  * Type keys are `<civ>.<class>` with the civilisation from the record's race (0 `comm`, 1 `euro`, 2 `arab`,
    3 `asia`). With a ruleset, the first key it defines among own civ, `comm` and the other civs is used. A
    user mapping table (`legacy: key` lines, REQ-EDT-10) wins over both.
  * Unresolved classes become **placeholders**: reported, kept in `legacy.json`, not placed.
  * Decorations (ground objects and patches) go to `decorations.json`. When a trigger or conversation refers to
    one, it becomes an object instead.
  * Positions are tile centres in world units. Structures land on their top-left tile (`MapObject` contract).
  * Owners: 1–8 → slots 0–7. Nature (10) and none (0) → Nature. Rescue (9) → Nature plus `ObjectFlags.Rescue`.
* **Object properties.** The title from `objext` is the object's name; its portrait and the " small" variant become
  icons. Stats with a clear "use default" bit become overrides (UNK-78). Item extensions become `ItemInfo`.
* **Triggers.**
  * The 28 action types and 8 event types map one to one onto the ADR 0120 records. Names resolve to indices
    (actions, events, timers, conversations, quests); a missing name is warned about and dropped (or `-1`).
  * Numeric text fields (times, counts, distances) are parsed as integers; anything else is warned about and 0 is
    used.
  * Rectangles are clamped to the map (UNK-80).
  * Order codes become ORDERS words.
  * Next-map references (`MAPS\x.wmp`) become map names.
* **Campaigns.**
  * Map list and entry flags come from `maps.ini`.
  * Edges are rebuilt from Load Map / Player Win / Player Lose, with names matched case-insensitively.
  * Reported: dangling targets, unreachable maps, unlisted embedded maps, and the persistent-unit mapping (units
    flagged Persistent, by name, per map).
* **Text and code pages.**
  * The code page is detected per file (REQ-EDT-07): CP1251 when the non-ASCII bytes come in runs, CP1250 when
    Central European letters appear, else CP1252.
  * A heuristic language tag goes with it. Ambiguous detection is a warning, and `--codepage` overrides.
  * Embedded maps without non-ASCII text inherit the campaign's code page.
  * All text becomes UTF-8. Translatable strings go to `strings/<lang>.po`, keyed by a stable `msgctxt`.

### Packages (TDD §18.4)

`.t2map` holds:

* `package.yaml` (kind `map`)
* `map.bin`: `MapBinary` in `Tzar2.Content.Maps`, a versioned binary of `MapData`
* `scenario.json`: `ScenarioJson`, ADR 0120
* `decorations.json`, `legacy.json`
* `strings/<lang>.po`, `assets/user/…`
* `provenance.json`: source name, size, MD5, code page, language, converter version (REQ-EDT-11)
* `conversion-report.json`

`.t2camp` holds `campaign.json` (`CampaignJson`), `maps/<id>.t2map`, and `assets/` for every resource whose path
and bytes are identical in all maps that have it. That deduplication saves 6.9 MB on the main campaign
(REQ-EDT-09). The deviations from §18.4's file names (`map.bin` instead of `terrain.bin`, JSON instead of YAML) are
deliberate: binary and JSON writers are canonical and AOT-safe (ADR 0120).

### Determinism (REQ-EDT-05)

* No timestamps and no machine data.
* JSON with a fixed key order and LF newlines.
* Stored ZIP entries sorted by `PackageWriter`.
* User BMPs become PNG (RGB8, RGB555 expanded by bit replication, which is reversible), with **stored** deflate
  blocks, so no compressor version can change the bytes.
* The same source and converter version give byte-identical packages; tests check this on synthetic maps and on the
  reference campaigns.

### CLI

`t2 convert <file|folder> [--out] [--codepage] [--mapping] [--package <base.t2pkg>] [--json] [-q]` and
`t2 convert --corpus [folder]` (default `$TZAR2_CORPUS`).

* `--package` compiles the `classic` ruleset over the engine defaults, as `RulesetLoader` does, so keys line up
  with the game.
* Exit codes: 0 all clean, 1 some file had errors, 2 usage.

### Exit criterion

A file passes when it converts with zero errors. The corpus runner reports the pass rate.

* On the reference build: **9/9 files (7 maps, 2 campaigns, 33 maps in all), 100 %, 0 errors.** With the imported
  classic ruleset there are also 0 placeholders.
* The remaining warnings are genuine source problems: a missing portrait, an empty Stop Timer, a conversation
  cycle, unknown names in expressions.
* There is no community corpus in the repository or on the development machine. `OriginConversionTests` asserts
  ≥ 90 % when `TZAR2_CORPUS` is set.

## Alternatives considered

* **Disassemble the game's map loader instead of the editor's:** the editor's exported, named API and its per-class
  serialisers were much quicker to read, and the game shares the DLL.
* **Keep BMPs as BMP:** smaller than stored PNG, but REQ-EDT-09 asks for PNG. Compressed PNG would need a pinned
  deflate implementation to stay byte-stable; that can come later without changing the format.
* **Drop unresolved objects or decorations:** forbidden by REQ-EDT-10. Placeholders and decorations are kept and
  reported.

## Consequences

* Stream B (trigger engine, campaigns) loads `map.bin` + `scenario.json` + `campaign.json`, or calls the converter
  directly. The editor (M5) reuses the converter and the report model for its import panel.
* The classic ruleset has no entity type for referenced decorations (signs, ruins that triggers make vanish). With a
  ruleset, these references are dropped with a warning in three campaign maps. A visual-object type in the ruleset
  would fix it.
* The open format points (UNK-70…80) default conservatively and are listed in every affected report. A later editor
  experiment on Windows (capture harness machine) can settle them.
* Packages are larger than the sources (stored PNG); compression is a follow-up.
