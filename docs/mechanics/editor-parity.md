# Editor parity checklist (M5)

TDD §18.2 (the original Editor Manual's features, all MUST), §18.3 (modern additions), §18.4 (formats), §18.5 (the
legacy converter in the editor), §18.6 (acceptance), §25 M5 ("parity checklist 100 %"). The descriptions are our own
words; nothing is copied from the original manual.

Where things live (ADR 0150): the document model, the tools and the validator are in `Tzar2.Content.Editing`, the
converter integration in `Tzar2.Import.Editing`, playtest and the trigger debugger in `Tzar2.Game.Editing`, and the
Godot editor mode in `src/Tzar2.Client/src/Editor/`, which is a view over them.

Status:
* **done**: works. *Evidence* names the test that proves it (`Class.Method` in `tests/Tzar2.Editor.Tests`, or
  `Client:` for the Godot smoke test).
* **blocked**: cannot be done yet, for the reason given.
* **planned**: not built yet. It must be gone by the end of M5.

`EditorParityTests.The_checklist_is_well_formed_and_backed_by_tests` parses this page. It checks the following:
* every row has a known status;
* every done row names at least one existing test;
* every blocked row gives a reason;
* the summary below matches the rows.

## Summary

<!-- summary:start -->
| | Items | Done | Blocked | Planned |
|---|---|---|---|---|
| All | 104 | 5 | 0 | 99 |

Parity: **4 %** done.
<!-- summary:end -->

## §18.2 New map

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| new-empty | Empty map: start terrain, plus patches, rocks and animals | planned | | |
| new-random | Random map: type, players, per-player kingdom/Tzar name/civ, patches, rocks, cliffs, rivers, trees, animals, towers, gateways, items, resource level | planned | | |
| new-terrain-set | Terrain set chosen at creation, fixed afterwards | planned | | |
| new-sizes | Seven sizes 64–512 plus extended sizes up to 1,024 | planned | | |

## §18.2 Terrain

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| terrain-paint | Paint with 6 brush sizes | planned | | |
| terrain-invalid | Invalid areas shown red | planned | | |
| terrain-water | Water painting (water/shore transitions) | planned | | |
| terrain-cliffs | Cliffs on allowed terrain; painting terrain over cliffs removes them | planned | | |
| terrain-autoscroll | Auto-scroll while painting | planned | | |

## §18.2 Trees

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| trees-specific | A specific tree | planned | | |
| trees-random-type | A random tree of a type | planned | | |
| trees-random-all | A random tree of all types | planned | | |
| trees-delete | Delete trees | planned | | |
| trees-brushes | 5 tree brushes | planned | | |
| trees-density | Forest density | planned | | |
| trees-accumulate | Accumulation mode | planned | | |

## §18.2 Decorations and resources

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| deco-patches | Ground patches: anywhere on green, stackable, random within a group, TAB cycles shapes | planned | | |
| deco-ground | Ground objects (land only) | planned | | |
| deco-water | Water objects (water only) | planned | | |
| deco-special | Special locations | planned | | |
| deco-resources | Resources with an amount per object or per multi-selection; defaults restorable | planned | | |

## §18.2 Buildings, units, items

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| bld-palette | Buildings per civilization plus common ones | planned | | |
| bld-harbor | Harbors only on coasts | planned | | |
| bld-walls | Walls by drag, with auto-scroll | planned | | |
| bld-gates | Gates: Ctrl horizontal/vertical, Alt side, Ctrl+Alt open | planned | | |
| bld-bridges | Bridges: Alt orientation | planned | | |
| unit-palette | Units per civilization, common units, animals/creatures (Nature by default) | planned | | |
| unit-layers | Ground/water/air layers with stacking rules | planned | | |
| unit-rotate | TAB / Shift+TAB rotate | planned | | |
| item-types | 6+ item types | planned | | |
| item-random | Random item within a group | planned | | |
| item-properties | Per-type properties: resource amount; armor, weapon, scroll, spell book, potion | planned | | |

## §18.2 Object properties

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| prop-common | Kingdom, name, title, use-defaults | planned | | |
| prop-building | Buildings: health, max health, resistance, Not Appear | planned | | |
| prop-unit | Units: health, max health, attack, mana, defence, level, icons 44×44/96×96 | planned | | |
| prop-flags | Flags No AI, Persistent, Not Appear | planned | | |
| prop-multi | Multi-edit of several objects | planned | | |

## §18.2 Tools

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| tool-select | Select | planned | | |
| tool-move | Alt-drag move | planned | | |
| tool-copy | Ctrl-drag copy | planned | | |
| tool-delete | Delete | planned | | |
| tool-locator | Object Locator: type/player/named filters, view, properties, delete | planned | | |
| tool-start | Starting location per player (keys 1–8) | planned | | |
| tool-minimap | Mini-map navigation | planned | | |
| tool-docking | Docking panels | planned | | |

## §18.2 Map, kingdoms, resources

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| map-properties | Name, author, description; game type, victory condition, population limit, difficulty, No Fog, Explore | planned | | |
| kingdom-general | Name, Tzar, civilization, player type (Human or AI / AI Only / Rescue), active | planned | | |
| kingdom-resources | Resources with Low/Medium/High fill and equalize (column/row/all) | planned | | |
| kingdom-ai | AI script, economy AI, war AI | planned | | |
| kingdom-tech | Prohibited buildings/units/researches/upgrades, initial researches | planned | | |
| kingdom-diplomacy | Ally/enemy/neutral, shared vision/resources/unit control, shared victory | planned | | |
| res-images | Import/export/delete images (BMP, PNG) | planned | | |
| res-sounds | Import/export/delete sounds (WAV, OGG) | planned | | |
| res-music | Import/export/delete music (MIDI, OGG) | planned | | |
| res-minimap-title | Custom mini-map and title image | planned | | |
| sound-sources | Sound sources: a point or an object | planned | | |
| sound-repeat | Repeat modes: Every, Looped, Random from–to, Delayed start | planned | | |
| music-playlist | Custom music playlist | planned | | |

## §18.2 Rules and conversations

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| rules-lists | Action and event lists, rule list | planned | | |
| rules-condition | Condition editor | planned | | |
| rules-order | Action ordering | planned | | |
| rules-actions | All 27 actions (and Delay) with their property pages | planned | | |
| rules-events | All 8 events with their property pages | planned | | |
| rules-expression | Expression editor | planned | | |
| conv-list | Conversation list | planned | | |
| conv-phrases | Phrases: source, condition, result, text, icon, sound, follow-ups | planned | | |
| conv-initial | Initial-phrase flags | planned | | |

## §18.2 Campaign editor

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| camp-maps | Import/export/delete maps | planned | | |
| camp-details | Map description and details | planned | | |
| camp-entry | Entry (starting) maps | planned | | |
| camp-description | Campaign description | planned | | |
| camp-files | Save, open, new | planned | | |
| camp-legacy | Import of legacy `.wmp`/`.wcm` through the converter | planned | | |

## §18.3 Modern additions

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| m-undo | Every operation undoable, unlimited history, survives save (REQ-EDT-02) | done | DocumentTests.Terrain_strokes_are_one_step_and_undo_restores_every_layer, DocumentTests.History_is_unlimited_and_survives_save, DocumentTests.Deleting_a_referenced_object_cleans_the_triggers_and_undo_brings_everything_back | ADR 0151 |
| m-validation | Live validation panel: errors/warnings, click to locate, play blocked only for blocking errors | planned | | the model is done (ValidationTests); the panel is next |
| m-playtest | Playtest as any player, time controls, reveal map | planned | | |
| m-debugger | Trigger debugger: last results, variable watch, breakpoints, step, event log | planned | | |
| m-campaign-graph | Visual campaign graph: entries, persistence mapping, carried variables | planned | | |
| m-areas-groups | Named areas and object groups reusable across triggers | planned | | stored in `editor.json` (PackageTests.Ids_areas_and_groups_survive_in_editor_json) |
| m-search-replace | Search and replace of object references | done | DocumentTests.Search_and_replace_of_object_references | |
| m-copy-paste | Copy/paste and prefabs across maps | planned | | |
| m-transforms | Multi-select transforms | planned | | |
| m-layers | Layers with lock/hide | planned | | |
| m-lua | Lua script editor: highlighting, API completion, inline errors | planned | | |
| m-localization | Localization tables for map strings (export/import `.po`) | planned | | |
| m-mod-palettes | Mod-aware palettes | planned | | |
| s-authentic-import | (SHOULD) Asset import with an "authentic" conversion | planned | | |
| s-random-templates | (SHOULD, post-1.0 acceptable) Random-map templates editable | planned | | |
| s-mod-studio | (SHOULD) Rules/data browser ("Mod Studio"), at least read-only | planned | | |

## §18.4 Formats

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| fmt-t2map | `.t2map` saved and loaded through the package writers | done | PackageTests.The_package_has_the_section_18_4_parts_and_no_editor_json_when_nothing_needs_it | |
| fmt-roundtrip | Deterministic save; load/save round trip byte-identical (editor and converter packages) | done | PackageTests.Saving_is_deterministic_and_a_load_save_round_trip_is_byte_identical, PackageTests.A_converted_legacy_map_loads_and_saves_back_to_the_converters_exact_bytes | |
| fmt-t2camp | `.t2camp` saved and loaded | planned | | |
| fmt-scenario | The document builds into the compiled contract without loss | done | DocumentTests.A_scenario_opened_from_compiled_data_builds_back_unchanged | |

## §18.5 Converter in the editor

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| conv-entry | File → Import legacy map/campaign (one, several, folder), drag & drop, "Add map from legacy file" in the campaign editor | planned | | the CLI `t2 convert` exists (M4) |
| conv-report | Report panel: counts, warnings, errors, filters, click-to-locate, export | planned | | |
| conv-codepage | Code page detection with a user override; UTF-8; language tag | planned | | |
| conv-graph | Campaign-graph reconstruction: unreachable maps, dangling names, persistent-unit preview | planned | | |
| conv-resources | Embedded resources extracted and deduplicated | planned | | |
| conv-mapping | Type mapping table (reusable YAML) or flagged placeholders | planned | | |
| conv-local | Non-destructive; output to user data or a chosen folder; provenance | planned | | |
| conv-after | Opens in the editor; "Play converted campaign"; 600-tick smoke test of every map | planned | | |

## §18.6 Acceptance

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| acc-authored | A 3-map campaign with conversations, persistent hero, timers, quests, story screens, custom sounds and music, authored with the editor only and played end to end | planned | | |
| acc-original | The original campaigns convert and every mission completes | planned | | |
| acc-fx | The FX Gold Edition campaigns and a community campaign convert through the converter UI | planned | | |
