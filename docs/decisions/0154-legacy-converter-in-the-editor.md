# 0154. Drive the one legacy converter from the editor through an import session with a located report

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent M5 (editor)
* **Related:** TDD §18.5 (REQ-EDT-04…12), §18.6, Appendix C.9; ADR 0121, ADR 0133, ADR 0150, ADR 0151, ADR 0152

## Context

§18.5 makes converting legacy maps and campaigns a user feature of the editor:

* entry points in the File menu, drag and drop, and the campaign editor;
* a report panel with counts, warnings and errors, filters, click-to-locate and export;
* a code page override, and correct Cyrillic and Spanish text;
* a reusable type mapping table;
* campaign-graph reconstruction with flags for unreachable maps and dangling names, and a persistent-unit preview;
* deduplicated resources;
* non-destructive output with provenance;
* opening the result in the editor, playing it, and a 600-tick smoke test.

There must be one converter (REQ-EDT-05), shared with `t2 convert` and the importer. The client may use Import but not
Formats (§7.2), so it cannot name `Formats.Text.CodePage`. The converter's report locations are text
(`object #N (…)`, `rule 'X'`…).

## Decision

* **`Tzar2.Import.Editing`** drives the converter of ADR 0121 and adds no second conversion path.
  * `LegacyImportSession` queues files and folders. A folder contributes its legacy files in sorted order, and a file
    is queued only once.
  * It converts each file with `ImportSettings`: the ruleset, the code page as a plain number, and a
    `TypeMappingTable`.
  * It writes the packages with `LegacyPackageWriter` into a folder of the user's choice. It refuses to overwrite a
    source, and the sources are only read; provenance and MD5 are in the package.
  * It exports the whole report as JSON.
* **`TypeMappingTable`** is the reusable YAML table of `t2 convert --mapping` (`legacy: key` lines, sorted, with a
  header comment). The session lists the classes that stayed placeholders, so the user maps them and converts again.
  Nothing is dropped (REQ-EDT-10).
* **Report panel model.** `ImportItem.Rows()` flattens a map's or a campaign's reports into `ReportRow`s with a
  severity, a code and a message, plus an `EditorLocation`:
  * `object #N` becomes the converted object (through the new `MapConversion.LegacyObjectIndices`; ids are index + 1,
    as `MapPackage` loads them);
  * `rule`/`event`/`action`/`conversation … phrase …` are found by name in the converted scenario;
  * `map 'M'` becomes a campaign map.

  Opening a row opens the converted map in the editor and locates it. `Counts()` sums the counts, including
  `sharedAssets` (deduplication, REQ-EDT-09).
* **`CampaignGraph`** rebuilds a campaign's graph from the converter's `CampaignData`:
  * reachability from the entry maps;
  * dangling next-map names;
  * persistent units by name and the maps that have them.

  The Godot `CampaignGraphView` (a `GraphEdit`) draws it. The campaign editor uses the same view.
* **Code pages (REQ-EDT-07).** The detector of ADR 0121 judged "runs of non-ASCII bytes" and mistook a Spanish name
  with two accented letters in a row ("Peñón") for Cyrillic. It now classifies words: a word written mostly in the
  upper half reads as Cyrillic, a word with a few such bytes among ASCII letters as Latin. CP1251 wins when Cyrillic
  words outnumber Latin ones. The reference-build conversion is unaffected in CI (no original data). It should be
  re-run locally with `TZAR2_ORIGIN`.
* **Smoke test (REQ-EDT-12).** `Tzar2.Game.Editing.MapSmoke` loads a map as a scenario match (triggers, AI,
  resources), runs 600 ticks, and reports exceptions and scenario faults.
* **Playing a converted campaign.** The converted maps become an `InMemoryCampaignSource`, and the game mode starts it
  through the campaign flow of ADR 0133. The campaign screen lists it for the session.
* **Entry points.**
  * File → Import legacy map/campaign (files, several files, a folder).
  * Dropping `.wmp`/`.wcm` files or folders on the editor window (a `.t2map` opens directly).
  * The campaign editor's "Add map from legacy file" (ADR 0155).
  * `t2 convert` stays the headless entry point.

## Alternatives considered

* **Parse locations in the client:** the location formats belong to the converter; Import is the place that knows them
  and can be tested headlessly.
* **Structured locations in `ConversionDiagnostic`:** cleaner, but it changes the converter's report model and
  `conversion-report.json`. The text form is stable and covered by tests; revisit if new location kinds appear.

## Consequences

* The editor, `t2 convert` and the first-run importer share one converter and one report format.
* Locally kept FX and community campaigns can be checked through the UI. The acceptance tests that need them read
  `TZAR2_FX_CAMPAIGNS` and `TZAR2_CORPUS` and are skipped in CI (§18.6, ADR 0156).
