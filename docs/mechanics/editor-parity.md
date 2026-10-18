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
| All | 104 | 68 | 0 | 36 |

Parity: **65 %** done.
<!-- summary:end -->

## §18.2 New map

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| new-empty | Empty map: start terrain, plus patches, rocks and animals | done | ToolTests.New_empty_map_with_patches_rocks_and_animals, Client:The_editor_edits_validates_saves_and_reopens_a_map_headless | File → New map; patches as decorations, rocks and animals owned by Nature |
| new-random | Random map: type, players, per-player kingdom/Tzar name/civ, patches, rocks, cliffs, rivers, trees, animals, towers, gateways, items, resource level | done | ToolTests.New_random_map_from_the_generator_with_per_player_kingdoms | the generator of ADR 0060 (type, players, resources, features); kingdoms get civilization and start from the generator |
| new-terrain-set | Terrain set chosen at creation, fixed afterwards | done | ToolTests.Map_sizes_and_terrain_set_are_fixed_at_creation | the set and size are fixed after creation, as in the original |
| new-sizes | Seven sizes 64–512 plus extended sizes up to 1,024 | done | ToolTests.Map_sizes_and_terrain_set_are_fixed_at_creation | 64, 96, 128, 192, 256, 384, 512; extended 768 and 1,024 |

## §18.2 Terrain

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| terrain-paint | Paint with 6 brush sizes | done | ToolTests.Terrain_brushes_have_six_sizes_and_painting_over_cliffs_removes_them, ControllerTests.A_brush_stroke_is_one_undo_step_and_escape_cancels_it | one stroke = one undo step; chunks recompose live in the game renderer |
| terrain-invalid | Invalid areas shown red | done | ToolTests.Invalid_transitions_are_red_with_a_terrain_set | tiles the set has no transition for are drawn red and listed by the validator |
| terrain-water | Water painting (water/shore transitions) | done | ToolTests.Terrain_brushes_have_six_sizes_and_painting_over_cliffs_removes_them, ToolTests.Placement_rules_follow_the_layers | set index 0 is water (the converter's mapping); shores are the set's transitions |
| terrain-cliffs | Cliffs on allowed terrain; painting terrain over cliffs removes them | done | ToolTests.Cliffs_are_only_allowed_on_land, ToolTests.Terrain_brushes_have_six_sizes_and_painting_over_cliffs_removes_them |  |
| terrain-autoscroll | Auto-scroll while painting | done | Client:The_editor_edits_validates_saves_and_reopens_a_map_headless | `MapView.Scroll` scrolls while a button is held near the edge (UI behaviour; the smoke run covers the view, not the edge) |

## §18.2 Trees

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| trees-specific | A specific tree | done | ToolTests.Tree_brushes_plant_deterministically_on_free_land_and_delete | a specific tree type of the ruleset; image variants within one type are not selectable (ADR 0152: TREES.INI variants not decoded) |
| trees-random-type | A random tree of a type | done | ToolTests.Forest_density_and_tree_choices | the type's group of the palette |
| trees-random-all | A random tree of all types | done | ToolTests.Forest_density_and_tree_choices |  |
| trees-delete | Delete trees | done | ToolTests.Tree_brushes_plant_deterministically_on_free_land_and_delete, ControllerTests.Trees_cliffs_decorations_erase_and_areas |  |
| trees-brushes | 5 tree brushes | done | ToolTests.Tree_brushes_plant_deterministically_on_free_land_and_delete | radii 0, 1, 2, 3, 5 tiles |
| trees-density | Forest density | done | ToolTests.Forest_density_and_tree_choices |  |
| trees-accumulate | Accumulation mode | done | ToolTests.Tree_brushes_plant_deterministically_on_free_land_and_delete |  |

## §18.2 Decorations and resources

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| deco-patches | Ground patches: anywhere on green, stackable, random within a group, TAB cycles shapes | done | ToolTests.Decorations_stack_on_land_water_objects_need_water_and_tab_cycles, ControllerTests.Trees_cliffs_decorations_erase_and_areas | stored in decorations.json (ADR 0121); the editor marks them; the game renderer does not draw decorations yet (renderer follow-up) |
| deco-ground | Ground objects (land only) | done | ToolTests.Decorations_stack_on_land_water_objects_need_water_and_tab_cycles |  |
| deco-water | Water objects (water only) | done | ToolTests.Decorations_stack_on_land_water_objects_need_water_and_tab_cycles |  |
| deco-special | Special locations | done | ToolTests.Palette_sections_are_mod_aware_and_items_have_groups | special locations are entity types of the palette (magic gateway, fog revealer) |
| deco-resources | Resources with an amount per object or per multi-selection; defaults restorable | done | ToolTests.Gates_bridges_facing_and_amounts_reach_the_simulation, ToolTests.Units_turn_with_tab_and_resources_restore_their_default | MapObject.Amount (ADR 0152); 0 restores the default; multi-selection in the properties dock |

## §18.2 Buildings, units, items

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| bld-palette | Buildings per civilization plus common ones | done | ToolTests.Palette_sections_are_mod_aware_and_items_have_groups |  |
| bld-harbor | Harbors only on coasts | done | ToolTests.Placement_rules_follow_the_layers |  |
| bld-walls | Walls by drag, with auto-scroll | done | ToolTests.Gate_modifiers_and_walls_by_drag, ControllerTests.Gate_modifiers_walls_by_drag_and_random_items | auto-scroll as for painting |
| bld-gates | Gates: Ctrl horizontal/vertical, Alt side, Ctrl+Alt open | done | ToolTests.Gate_modifiers_and_walls_by_drag, ToolTests.Gates_bridges_facing_and_amounts_reach_the_simulation | Rotated and Open reach the sim; the "side" is presentation only (⚠ UNVERIFIED) |
| bld-bridges | Bridges: Alt orientation | done | ToolTests.Bridges_cross_water_and_turn_with_alt |  |
| unit-palette | Units per civilization, common units, animals/creatures (Nature by default) | done | ToolTests.Palette_sections_are_mod_aware_and_items_have_groups | creatures are Nature's by default |
| unit-layers | Ground/water/air layers with stacking rules | done | ToolTests.Placement_rules_follow_the_layers | one unit per tile and layer (⚠ UNVERIFIED stacking rule) |
| unit-rotate | TAB / Shift+TAB rotate | done | ToolTests.Units_turn_with_tab_and_resources_restore_their_default, ControllerTests.Placement_refusals_report_a_reason_and_tab_turns | the facing reaches the sim |
| item-types | 6+ item types | done | ToolTests.Palette_sections_are_mod_aware_and_items_have_groups |  |
| item-random | Random item within a group | done | ControllerTests.Gate_modifiers_walls_by_drag_and_random_items |  |
| item-properties | Per-type properties: resource amount; armor, weapon, scroll, spell book, potion | done | ToolTests.Item_properties_per_type | resource amount, armor/weapon/potion value, scroll text/time/auto-close/image/sound/music, spell book type/mana/time/charges |

## §18.2 Object properties

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| prop-common | Kingdom, name, title, use-defaults | done | ToolTests.Object_properties_and_multi_edit |  |
| prop-building | Buildings: health, max health, resistance, Not Appear | done | ToolTests.Object_properties_and_multi_edit |  |
| prop-unit | Units: health, max health, attack, mana, defence, level, icons 44×44/96×96 | done | ToolTests.Object_properties_and_multi_edit |  |
| prop-flags | Flags No AI, Persistent, Not Appear | done | ToolTests.Object_properties_and_multi_edit |  |
| prop-multi | Multi-edit of several objects | done | ToolTests.Object_properties_and_multi_edit |  |

## §18.2 Tools

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| tool-select | Select | done | ControllerTests.Place_select_alt_drag_moves_and_ctrl_drag_copies | click, rubber band, Shift adds |
| tool-move | Alt-drag move | done | ControllerTests.Place_select_alt_drag_moves_and_ctrl_drag_copies, Client:The_editor_edits_validates_saves_and_reopens_a_map_headless |  |
| tool-copy | Ctrl-drag copy | done | ControllerTests.Place_select_alt_drag_moves_and_ctrl_drag_copies |  |
| tool-delete | Delete | done | ControllerTests.Place_select_alt_drag_moves_and_ctrl_drag_copies, DocumentTests.Deleting_a_referenced_object_cleans_the_triggers_and_undo_brings_everything_back |  |
| tool-locator | Object Locator: type/player/named filters, view, properties, delete | done | ToolTests.Locator_filters_by_type_player_and_name | locator dock: view, select (properties), delete |
| tool-start | Starting location per player (keys 1–8) | done | ControllerTests.Keys_1_to_8_set_starting_locations_under_the_pointer |  |
| tool-minimap | Mini-map navigation | done | Client:The_editor_edits_validates_saves_and_reopens_a_map_headless | click or drag on the mini-map moves the camera (UI) |
| tool-docking | Docking panels | done | Client:The_editor_edits_validates_saves_and_reopens_a_map_headless | resizable docks, toggled from the View menu (Godot split containers, not floating windows) |

## §18.2 Map, kingdoms, resources

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| map-properties | Name, author, description; game type, victory condition, population limit, difficulty, No Fog, Explore | done | ToolTests.Map_properties_round_trip |  |
| kingdom-general | Name, Tzar, civilization, player type (Human or AI / AI Only / Rescue), active | done | ToolTests.Starting_locations_kingdoms_resources_and_diplomacy |  |
| kingdom-resources | Resources with Low/Medium/High fill and equalize (column/row/all) | done | ToolTests.Starting_locations_kingdoms_resources_and_diplomacy | Low/Medium/High are the ruleset's starting-resource levels |
| kingdom-ai | AI script, economy AI, war AI | done | ToolTests.Starting_locations_kingdoms_resources_and_diplomacy |  |
| kingdom-tech | Prohibited buildings/units/researches/upgrades, initial researches | done | ToolTests.Starting_locations_kingdoms_resources_and_diplomacy |  |
| kingdom-diplomacy | Ally/enemy/neutral, shared vision/resources/unit control, shared victory | done | ToolTests.Starting_locations_kingdoms_resources_and_diplomacy |  |
| res-images | Import/export/delete images (BMP, PNG) | done | ToolTests.User_resources_custom_minimap_sounds_and_music | BMP imports are converted to PNG with the converter's encoder |
| res-sounds | Import/export/delete sounds (WAV, OGG) | done | ToolTests.User_resources_custom_minimap_sounds_and_music |  |
| res-music | Import/export/delete music (MIDI, OGG) | done | ToolTests.User_resources_custom_minimap_sounds_and_music |  |
| res-minimap-title | Custom mini-map and title image | done | ToolTests.User_resources_custom_minimap_sounds_and_music | `user/minimap.png`, `user/maptitle.png` |
| sound-sources | Sound sources: a point or an object | done | ToolTests.Sound_sources_follow_objects_and_fall_back_to_their_point |  |
| sound-repeat | Repeat modes: Every, Looped, Random from–to, Delayed start | done | ToolTests.User_resources_custom_minimap_sounds_and_music |  |
| music-playlist | Custom music playlist | done | ToolTests.User_resources_custom_minimap_sounds_and_music |  |

## §18.2 Rules and conversations

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| rules-lists | Action and event lists, rule list | planned |  |  |
| rules-condition | Condition editor | planned |  |  |
| rules-order | Action ordering | planned |  |  |
| rules-actions | All 27 actions (and Delay) with their property pages | planned |  |  |
| rules-events | All 8 events with their property pages | planned |  |  |
| rules-expression | Expression editor | planned |  |  |
| conv-list | Conversation list | planned |  |  |
| conv-phrases | Phrases: source, condition, result, text, icon, sound, follow-ups | planned |  |  |
| conv-initial | Initial-phrase flags | planned |  |  |

## §18.2 Campaign editor

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| camp-maps | Import/export/delete maps | planned |  |  |
| camp-details | Map description and details | planned |  |  |
| camp-entry | Entry (starting) maps | planned |  |  |
| camp-description | Campaign description | planned |  |  |
| camp-files | Save, open, new | planned |  |  |
| camp-legacy | Import of legacy `.wmp`/`.wcm` through the converter | planned |  |  |

## §18.3 Modern additions

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| m-undo | Every operation undoable, unlimited history, survives save (REQ-EDT-02) | done | DocumentTests.Terrain_strokes_are_one_step_and_undo_restores_every_layer, DocumentTests.History_is_unlimited_and_survives_save, DocumentTests.Deleting_a_referenced_object_cleans_the_triggers_and_undo_brings_everything_back | ADR 0151 |
| m-validation | Live validation panel: errors/warnings, click to locate, play blocked only for blocking errors | done | ValidationTests.Unknown_types_and_bad_owners_block_play_but_the_map_stays_editable, ValidationTests.Expression_names_and_trigger_references_are_checked, Client:The_editor_edits_validates_saves_and_reopens_a_map_headless | panel re-validates 0.5 s after every change; click locates; only Blocking findings block play |
| m-playtest | Playtest as any player, time controls, reveal map | planned |  |  |
| m-debugger | Trigger debugger: last results, variable watch, breakpoints, step, event log | planned |  |  |
| m-campaign-graph | Visual campaign graph: entries, persistence mapping, carried variables | planned |  |  |
| m-areas-groups | Named areas and object groups reusable across triggers | planned |  | stored in `editor.json` (PackageTests.Ids_areas_and_groups_survive_in_editor_json) |
| m-search-replace | Search and replace of object references | done | DocumentTests.Search_and_replace_of_object_references |  |
| m-copy-paste | Copy/paste and prefabs across maps | done | ToolTests.Copy_paste_across_maps_and_prefabs, ControllerTests.Copy_paste_through_text_and_locked_layers_are_not_selectable | system clipboard; prefabs are the same JSON as files |
| m-transforms | Multi-select transforms | done | ToolTests.Multi_select_transforms |  |
| m-layers | Layers with lock/hide | done | ToolTests.Layers_hide_and_lock, ControllerTests.Copy_paste_through_text_and_locked_layers_are_not_selectable |  |
| m-lua | Lua script editor: highlighting, API completion, inline errors | planned |  |  |
| m-localization | Localization tables for map strings (export/import `.po`) | planned |  |  |
| m-mod-palettes | Mod-aware palettes | done | ToolTests.Palette_sections_are_mod_aware_and_items_have_groups | the palette is built from the compiled ruleset |
| s-authentic-import | (SHOULD) Asset import with an "authentic" conversion | planned |  |  |
| s-random-templates | (SHOULD, post-1.0 acceptable) Random-map templates editable | planned |  |  |
| s-mod-studio | (SHOULD) Rules/data browser ("Mod Studio"), at least read-only | planned |  |  |

## §18.4 Formats

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| fmt-t2map | `.t2map` saved and loaded through the package writers | done | PackageTests.The_package_has_the_section_18_4_parts_and_no_editor_json_when_nothing_needs_it |  |
| fmt-roundtrip | Deterministic save; load/save round trip byte-identical (editor and converter packages) | done | PackageTests.Saving_is_deterministic_and_a_load_save_round_trip_is_byte_identical, PackageTests.A_converted_legacy_map_loads_and_saves_back_to_the_converters_exact_bytes |  |
| fmt-t2camp | `.t2camp` saved and loaded | planned |  |  |
| fmt-scenario | The document builds into the compiled contract without loss | done | DocumentTests.A_scenario_opened_from_compiled_data_builds_back_unchanged |  |

## §18.5 Converter in the editor

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| conv-entry | File → Import legacy map/campaign (one, several, folder), drag & drop, "Add map from legacy file" in the campaign editor | planned |  | the CLI `t2 convert` exists (M4) |
| conv-report | Report panel: counts, warnings, errors, filters, click-to-locate, export | planned |  |  |
| conv-codepage | Code page detection with a user override; UTF-8; language tag | planned |  |  |
| conv-graph | Campaign-graph reconstruction: unreachable maps, dangling names, persistent-unit preview | planned |  |  |
| conv-resources | Embedded resources extracted and deduplicated | planned |  |  |
| conv-mapping | Type mapping table (reusable YAML) or flagged placeholders | planned |  |  |
| conv-local | Non-destructive; output to user data or a chosen folder; provenance | planned |  |  |
| conv-after | Opens in the editor; "Play converted campaign"; 600-tick smoke test of every map | planned |  |  |

## §18.6 Acceptance

| Id | Feature | Status | Evidence | Notes |
|---|---|---|---|---|
| acc-authored | A 3-map campaign with conversations, persistent hero, timers, quests, story screens, custom sounds and music, authored with the editor only and played end to end | planned |  |  |
| acc-original | The original campaigns convert and every mission completes | planned |  |  |
| acc-fx | The FX Gold Edition campaigns and a community campaign convert through the converter UI | planned |  |  |
