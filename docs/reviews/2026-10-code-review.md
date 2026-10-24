# Code review 2026-10: performance, resources and memory; readiness for a fork

Work queue item 16, the owner's order of 2026-10-22. It covers three things:

* **Performance:** the sim's tick budget, asset loading, GPU and texture memory, and leaks.
* **Fork readiness:** the owner will fork the finished remake into a game with other rules, another design, more races
  and more unit progression.
* **Module boundaries:** the coupling between the modules.

The findings below are ranked by impact within each part. Each says what was measured, where it is, and what was done
or is proposed. The fixes land in small PRs. Each PR states its measurements, and this report links them as they
merge.

## How it was measured

* **Sim:**
  * `t2 bench` scenarios b3, b3-groups, b4, s2, gameplay and ai, run locally;
  * the nightly Benchmarks job on GitHub's runners, for timing;
  * `AllocationTests` (Release).
* **A long headless match:** `t2 bench --scenario ai --players 4 --map 256 --ticks 72000 --sample 1200`. That is one
  hour of game time with four Hard AIs on a huge map. `--sample` is new; every 60 s of game time it prints:
  * the window's tick p50/p99/max;
  * the GC counts;
  * the heap, the committed memory and the working set;
  * the entity count;
  * the size of a keyframe snapshot.
* **The Godot client:** `--resource-soak <file.tsv> --matches N --ticks T` is new. It plays N skirmishes at top speed
  and returns to the main menu after each. Every 2 s it writes the managed heap and GC counts, plus Godot's static
  memory, object, resource, node and orphan-node counts, video, texture and buffer memory, and draw calls. After each
  match it writes a row taken after a full GC.
* **Machine:** an Apple M5 (10 cores), macOS 26.5, Godot 4.7.2 .NET, the imported GOG v1.10 package. Six agents shared
  the machine, with a load average of 30–97. So local times are noisy and only indicative. Counts (allocations, GCs,
  objects, memory) are reliable; timing comparisons come from the CI runners.

## Measurements

### The sim

| Scenario | Tick p50 / p99 (ms, local) | Allocated per tick | Notes |
|---|---|---|---|
| s2 (4,000 units, 512², 8 players) | 3.6 / 43 | 2.0 KB | p99 is the loaded machine; the CI median is 2.8 / 5.5 |
| b3 (500 orders on a 512² maze) | 0.32 / 28 | 19.4 KB | planned within 3 ticks |
| b4 (mass death, traffic) | 0.45 / 32 | 0.9 KB | 0 blocked, passability unchanged |
| gameplay (8 kingdoms, 2,400 soldiers) | 1.4 / 5.6 (quiet runs) | 3.7 KB | |
| ai (8 Hard AIs, 256², 20 min) | 0.37 / 1.36 | 0.55 KB | |
| **1 h, 4 Hard AIs, 256²** | **0.19 / 0.70** (max 15.8) | **0.47 KB** (snapshots included) | **one gen0 GC in the hour** |

* `AllocationTests`: 10 of 10 pass. The sim thread allocates nothing in the steady state.
* The long match:
  * the working set stays at 93–120 MB;
  * the heap without a collection grows from 59 to 74 MB over the hour, which is garbage: one GC in the hour;
  * the entity count peaks at 914 (minute 17) and ends at 408;
  * a keyframe snapshot is 56–184 KB, so a recorded hour keeps about 10 MB of keyframes in memory.

  **The sim shows no leak** and spends well under 1 % of its 50 ms tick.
* All hashes match the pinned ones. `--sample` takes snapshots between ticks, and its final hash equals that of a run
  without it.

### The client (`--resource-soak`, 4 kingdoms, huge 256² continental map, seed 7, 1,200 ticks per match)

| | Before | After ADR 0210 |
|---|---|---|
| Video memory in a match | 1,304 MB | 276 MB |
| Texture memory in a match | 1,218 MB | 190 MB |
| Managed heap when the match starts | 1,372 MB | 377 MB |
| Godot static memory at load | 1,152 MB | 140 MB |
| Working set at load | 2,294 MB | 1,170 MB |
| Terrain chunk frames, distinct tiles | 2,204 frames × 512 KB | 42,584 tiles on 11 pages of 8 MB |
| Back in the menu after a full GC (managed heap) | 272 MB | 363 MB |
| Objects, nodes, orphan nodes in the menu after 2 matches | 1,645 / 22 / 0 | 1,644 / 22 / 0 |

* On the real-world 512² map (8 kingdoms), the terrain has 5,352 chunk frames, which was 2.7 GB of textures. Now it is
  67,220 tiles and 332 MB of video memory in all.
* Objects, resources, nodes and video memory come back to their menu values after every match. **No Godot object, node
  or RID leaks.**
* Godot's texture-memory monitor underflows after a match is left: it reads 2⁴⁴ MB in the menu. This is an engine
  accounting quirk; video memory is correct.

### The client's memory in the S2 render bench (the owner's PC, 2026-10-23)

The owner's run of `--bench-render` showed a working set of 2.3 GB (min-spec-tuning.md § Results). The bench line now
splits it into `managed=` (the .NET heap) and `engine=` (Godot's static memory). A heap dump (`dotnet-gcdump`) was
taken 75 s into a run. The client was a Debug build on the same PC, 4,000 units at 2,560 × 1,440.

| | Before | After this change |
|---|---|---|
| Working set (peak) | 2.35 GB | 2.55 GB |
| Managed heap size (peak) | 1.39 GB | 1.36 GB |
| Live managed objects (dump) | 518 MB | 501 MB |
| `Godot.StringName` objects alive | 161,495 | 3,902 |

* **What is live** (about 0.5 GB):
  * the frame buffers, three `FrameEntity[]` of 16 MB;
  * byte buffers: about 115 MB in 1 MB, 128 KB and 64 KB arrays;
  * int arrays: about 50 MB;
  * the sim's and the AI's stores: about 20 MB;
  * the terrain's tile colours: two `Color[]` of 4 MB;
  * the rest in small objects.
* **What is not live:** the heap is about 2.7 times the live data. It is the GC's allocation budget, which .NET sizes
  from the CPU's L3 cache (96 MB on this Ryzen 7 9800X3D), plus garbage between collections. On a smaller-cache CPU the
  budget, and the heap, are smaller.
* **Fixed here:** the per-frame shader parameters (every visible terrain chunk's water frame, the fog's flag and
  darkness) passed a string. Each call made a `StringName`, which Godot tracks until it is finalized, so 161,000 were
  alive at any time. They now use cached names and are sent only when they change.

## Findings: performance, resources and memory

1. **Terrain textures grew with the map's area: 1.2 GB on a huge map, 2.7 GB on the largest.** Fixed by ADR 0210 (a
   deduplicated tile atlas; see the table).
   * Every 512 × 512 chunk uploaded every frame of the least common multiple of its water and shore cycles.
   * Every frame was also held at once in managed memory and in an undisposed Godot `Image`.
2. **The nightly Benchmarks job fails on main.**
   * 2026-10-22: gameplay p50 is 3.198 ms against a 3.158 ms limit (baseline 2.871), and p99 is 10.38 against 10.32.
   * 10-19 and 10-20: s2's p50 was 4.41 against 3.12, and 2.91 against 2.58.
   * **Bisected, and not a regression.** One EPYC 7763 runner ran 28 main commits (#64–#131), three runs each:
     * s2 is flat (p50 2.69–2.80 ms), so its failures were slow runners.
     * gameplay rises only where the scenario's play changed: #66 and #69, the walking speeds of ADRs 0171/0172
       (kills 1,200 → 1,655; p50 3.01 → 3.28, p99 8.1 → 11.3). It falls again with unit bodies (#126).
   * The gameplay rows are re-recorded (#156, ADR 0080 history).
3. **Match load runs entirely on the main thread** (`GameScreen._Ready`): sprite decoding, atlas packing, terrain
   composition (2–4 s on a huge map and 12 s on the real-world map, on the loaded machine) and SoundFont parsing.
   * The window freezes behind the loading screen.
   * A replay seek backwards rebuilds the whole `GameScreen` (`GameApp.Multiplayer.cs` `ShowPlayback`), so it pays
     the load again.
   * **Proposed:** compose and decode on a worker thread, then upload on the main thread. Keep the `GameScreen` across
     replay seeks and swap only the session.
4. **The decoded-asset caches never shrink.**
   * `ContentStore._sheets` and `_images` keep every decoded sheet and image for the whole run; `ClearCaches` is never
     called.
   * After a match the menu keeps 270–360 MB of managed heap. Matches with other civilizations or terrain sets add
     theirs.
   * `GpuSpriteAtlas.Atlas` keeps the CPU atlas pages and `SpriteBank` keeps the sheets for the whole match, although
     they are on the GPU.
   * **Proposed:** release the match's sheets and CPU pages after the upload, and keep only the UI's images across
     matches.
5. **Five full-screen shadow viewports render every frame** (`SpriteRenderer`: a `SubViewport` with
   `UpdateMode.Always` per renderer). Glow and ghost hide their shadows, and the Shadows option hides all of them, but
   hiding only hides the composite, so the hidden viewports still render.
   * **Proposed:** `UpdateMode.Disabled` when hidden, and no viewport for glow and ghost.
6. **Per-frame work and allocations in the HUD and audio:**
   * `UiAssets.Text` builds a key string per call and an `ImageTexture` per distinct string (health and percentages in
     a fight), up to its 4,096-entry cap.
   * `MidiPlayer` allocates a `Vector2[]` every frame.
   * Each match parses its own copy of the SoundFont while the menu keeps another.
   * `GameScreen.Describe` and `UpdateHud` allocate lists and strings every frame.
   * **Proposed:** cache by value, reuse the buffers, share one synthesizer.
7. **The replay recording keeps its keyframes in memory**: about 10 MB per hour of a 4-AI match, more with more units.
   It is bounded by the match and acceptable today.
   * **Revisit** if matches of several hours with 8 kingdoms become common: stream the recording to the file.
8. **The sim's worker pool waits for every released helper**, even one that wakes after all jobs are done
   (`DeterministicWorkerPool.RunCore`). On a CPU shared with other work this gives 10–20 ms ticks: the loaded machine
   shows p99 spikes of exactly 10 and 20 ms in movement and visibility. That is within the 50 ms tick, and it does not
   happen on the CI runners.
   * **Proposed:** wait only for jobs taken, not for helpers woken, with a generation counter so a late helper cannot
     take a job of the next fork.
9. **Smaller items:**
   * the cursor is rebuilt through `Input.SetCustomMouseCursor` on every change;
   * the skirmish setup screen generates the whole map on every option change;
   * `_mission` keeps the finished session of the last mission;
   * Godot sometimes prints "1 ObjectDB instance was leaked at exit": it is the music's
     `AudioStreamGeneratorPlayback` (`--verbose`, 3 of 4 runs with a SoundFont). It happens once at quit and never
     per match. Disposing the playback reference in `MidiPlayer.Stop` and `_ExitTree` did not remove it, so the engine
     still holds the playback at teardown. Harmless, and left as it is.
   * `SecondSessionAnalysisTests.Construction_time_uses_the_health_bar_rate_and_the_time_unit` fails under a process
     culture other than English (`C.UTF-8` on macOS): the fidelity analysis formats a number with the current
     culture.

## Findings: readiness for a fork

1. **Civilizations are an index into the ordinally sorted list of the ruleset's `civ` keys** (`[arab, asia, comm, euro]`).
   There is no civilization definition. The order leaks:
   * **Bug:** the legacy trigger variable `Race` returns index + 1 (arab 1, asia 2, euro 4), but the original's maps
     number 1 European, 2 Arabian, 3 Asian (`LegacyTriggerSystem.Expressions.cs`, `LegacyMap.cs`).
   * A kingdom with no objects defaults to the first non-common civilization, `arab` (`World.AssignCivilizations`).
   * Adding a civilization renumbers the others.
   * **Proposed (ADR):** civilizations as a ruleset list with their own keys and legacy numbers, and `comm` named as
     the shared civilization in data.
2. **Three races are hard-coded in about 30 places:**
   * the scenario model's `enum Civilization { European, Arabian, Asian, Random }` and its switches (`CampaignPlay`,
     `MapSetupEdits`);
   * the client's lists `["euro", "arab", "asia"]` (`Screens.cs`, `MultiplayerScreens.cs`);
   * the diplomacy icon row (`DiplomacyScreens.cs`) and the name suffix (`PresentationCatalog.cs`);
   * map generation (`MapGenCatalog`: the civilizations and `"euro.house"`);
   * `"euro"` defaults in Game and the CLI.
3. **Rules decided by names in code:**
   * the destroy-castles victory checks `LegacyClass == "castle"` (`VictorySystem.cs`);
   * the tavern checks the technologies `te_mercenaries`, `te_heroesquest` and `te_fame` (Sim and `CommandCard`);
   * `te_gambling` and `te_usury` (`KingdomCommands.cs`);
   * the orders `"bribe"` and `"preach_crusade"` (`CombatMagicCommands.cs`).
   * **Proposed:** traits or ruleset fields (a `VictoryTarget` trait; the tavern's offers and the kingdom orders named
     in YAML).
4. **Formula constants in code, not in the ruleset:**
   * the experience curve's shape (`CombatStats.cs`: base × n(n+1)/2) and its linear and half-step bonuses;
   * ~~the crusader's arrival times (`MagicSystem.cs`)~~ (#205);
   * ~~`LeashFactor = 2`~~ (#205);
   * the bless and curse caps (1000 %, 100 %) and the potion cap (1000);
   * the summon radius and the coast reach.
   * The AI's resource weights and wave sizes are C# option records.
   * Unit progression is global (one `combat:` block), with no per-class curve.
   * **Proposed:** move them into the `combat:`, `gameplay:` and `ai:` blocks, and allow a curve per class for the
     fork's progression.
5. **Technology effects are data-driven** (ADR 0191: `effects` in YAML, applied through `ModifierStat`); this part is
   ready. A new kind of effect needs a new `ModifierStat` and the code that reads it. `AbilityPower` is declared and
   never read.

## Findings: module boundaries

The project graph is clean:

* Sim → Content and Core;
* AI, Scripting and Replay → Sim;
* Game → all of the core;
* Client → Game, Content and Import.

`Tzar2.Architecture.Tests` guards the project references, the packages, Godot, the AI's and Scripting's use of Content,
twelve AI mutators and the client's namespaces. Its gaps:

1. **The client's compiled-assembly check never runs on CI.** It reads `.godot/mono/temp/bin/Debug/Tzar2.Client.dll`,
   but CI builds Release, so the test skips. Only the source regex runs, and it misses calls through
   `session.Simulation...`.
2. **`GameSession` hands the client the `Simulation` and the `LegacyAiSystem`** (public properties). `GameFrame`'s
   public API names `Sim.Kingdoms` types (`Stance`, `Sharing`, `KingdomState`), which the client may not name.
3. **The AI's mutator deny-list is incomplete:** `ChangeOwner`, `ConvertType`, `RefreshMaxHealth`, `SetCivilization`
   and `SimulationConfig.AddTechnology` are missing. `World.Players`, `World.Kingdoms` and `ComponentStore.Components`
   return writable spans.
4. **The client converts the original's maps on the play path** (`GameApp.SinglePlayer`, `GameApp.Campaign` call
   `Tzar2.Import`).
5. **Sim's use of Content is not pinned to `Content.Compiled`**, as the AI's and Scripting's are.
6. **Public mutable statics:** `GameSession.SpeedSteps`, `ReplayPlayback`'s array and `Placement`'s arrays.
7. **Rules still in the client** (ADR 0182 is not finished):
   * `GameScreen` builds commands;
   * it spares the castle on Delete by its class name;
   * it decides the build ghost's legality, select-by-class and the rejection texts;
   * the selection view's `Describe`.

## Fixes

| PR | What | Measured |
|---|---|---|
| #136 (this PR) | `t2 bench --sample`, the client's `--resource-soak`, this report | |
| #138, ADR 0210 | terrain from a deduplicated tile atlas | video memory 1,304 → 276 MB, managed heap at load 1,372 → 377 MB |
| #141 | analyzer rule T2C001 (culture-dependent formatting) and the 48 strings it found | Tools.Cli.Tests pass under C.UTF-8 and de_DE |
| #145 | architecture tests: compiled client on CI, Sim on `Content.Compiled`, more AI mutators, no public mutable statics | |
| #156 | the nightly's gameplay rows re-recorded after a bisect | 16 probe runs |
| #163, ADR 0211 | civilizations as an ordered list in the ruleset; `Race` reads the original's numbers | hashes re-pinned, AI commands unchanged |
| #161 | hidden shadow viewports stop rendering; atlas pages and upload images freed; caches cleared in the menu | menu heap 272 → 151 MB |
| #162 | MidiPlayer chunks, UI text cache key | gen0 GCs in a match 7 → 4 |
| #172 (on #138) | a match loads on a worker thread; the main thread only uploads | main thread 4.2 s → 77 ms (original map) |
| #174 (on #163) | the client's civilization choices from the ruleset | |
| #176 | the `Seat` trait and the feature technologies in world settings | ruleset hash only |
| #181, #200, #202 | network test flakes (issue #179): polling instead of sleeps, harness timeouts a loaded runner cannot trip, the capture end-to-end test on a virtual clock, the ping interval checked on the `NetManager` | the failing run's relay log showed `member.drop Timeout` during set-up, before the stall: starvation, not a keep-alive bug |
| #185, ADR 0212 | the client sees the session, frames and UI models only; `ClientContractTests` | |
| #186, ADR 0213 | the AI reads the world through read-only views; `AiReadOnlyTests` | hashes unchanged |
| #187 | scenarios and map generation take civilization keys from the ruleset | mapgen goldens re-pinned (random civilizations draw from the ruleset order) |
| #188, ADR 0214 | the worker pool waits for the jobs, not the helpers it woke | CI macOS (3 cores, loaded), tail after a fork's last job: p50 10.1 / p99 135 / max 19,355 ms before; review Mac after: p50 0.003–0.007 / p99 1.3–10 ms. CI macOS now passes the p99 < 50 ms assertion (its log does not print the numbers) |
| #191 | issue #190: the host's choice for a computer goes to the relay even on a stale lobby view | |
| #193, ADR 0215 | the full state hash hashes its large arrays in 64 KiB chunks on the workers | full hash 0.56 → 0.18 ms (s2 world); full hashes re-pinned |
| #204 | a kingdom's civilization is one with a seat, asked of the ruleset (`SeatTypeId`, `CanFoundKingdom`); the render bench takes the ruleset's order | 7 `"<civ>.castle"` look-ups and 2 `"euro"` fallbacks in Game removed; no hash moves; render bench numbers before #204 are not comparable (ADR 0244 amendment) |
| #205 | the crusade gathering pace, the crusaders' arrival interval and the chase leash in YAML | 3 constants out of Sim; ruleset hash only |
| #209 | `legacy.referenceDataHash` re-pinned; importer PRs run the `TZAR2_ORIGIN` tests after merging main | 5 local Import tests failing → 0 (bisect: #168 moved the hash unpinned, #133 and #139 pinned values from stale branches) |
| #211 | `CivilizationKeyTests`: civilization keys and type-key suffixes only in a list of files that may only shrink | 20 files listed (see below) |

## For a fork: the second sweep (2026-10-23)

Sim, Game, AI and the client were searched for rules and formulas outside the ruleset and `ModifierStat`, for
switches on class and unit names, for the civilization keys and race names, and for fixed lists of the three races.
Ranked by how likely a fork hits them and how cheap the move is:

| # | Finding | Where | Status |
|---|---|---|---|
| 1 | Whether a civilization can found a kingdom was decided by a type named `"<civ>.castle"`, with `"euro"` as the fallback | `MatchSetup`, `CampaignPlay` (3), `DesyncReports` (2), `RenderBench`, client `GameApp` and render bench | **done** (#204, #211): `Ruleset.SeatTypeId`, `CanFoundKingdom`, `FirstKingdomCivilization`, `KingdomCivilizations`, read from the `Seat` trait |
| 2 | Game rules written as literals next to settings that were already data | crusade gathering pace and arrival (`MagicSystem`), chase leash (`CombatSystem.LeashFactor`) | **done** (#205) |
| 3 | Fallback lists `["euro", "arab", "asia"]` and the `"euro"` default where no ruleset is at hand | `SlotChoice.Default`, `RandomMapScreenModel`, `CustomMapScreenModel`, client `Screens` | checked 2026-10-23: the fallback applies only when a caller passes no list; every screen passes the ruleset's `KingdomCivilizations`, so a fork's own races reach them. Left as a last resort; pass the ruleset's `KingdomCivilizations` in, or resolve an empty key at the match (as #187 does for scenarios) |
| 4 | The client's per-civilization art: sprite name suffix (`" e"`, `" r"`, `" a"`) and diplomacy race picture index | `PresentationCatalog`, `DiplomacyScreens` | remaining: a presentation field per civilization in the package manifest |
| 5 | Map builders place `"<civ>.castle"` and `"<civ>.peon"` by key | `MapGenCatalog` templates, `SkirmishTestMap`, `AiSkirmishScenario`, the client smokes | remaining: the seat from `SeatTypeId`; the starting worker needs a trait (`Harvester` plus a "starting unit" flag) |
| 6 | Test and demo scenarios spell `euro.*` types | `GameplayScenario` (Sim), `DemoCampaign`, `CampaignAutoplay` | left as is: fixtures for the classic ruleset; the ratchet keeps them from spreading |
| 7 | Translators of the original's race names (`european`, `Arabian`, the help's E/R/A) | `AiParser`, `AiRulesetModel`, `ScenarioParts`, `OnlineHelp` | left as is: they read the original's files; a fork's own formats use keys |
| 8 | Remaining numeric rules in Sim | the experience curve's shape, the cloak's one-hour bound (a restore bound), the bless, curse and potion caps, the summon radius and the coast reach | checked 2026-10-23: the experience step and the coast reach are data now (#225, #226). What is left is storage bounds (a curse takes at most 100 %, a bless fits its `short`, a potion's levels fit the experience counter) and the summon's free-spot search of 3 tiles, which is engine tuning like row 9. Left in code |
| 9 | Engine cadences in Sim: chase and repath intervals, replan counts, search radii, shore crowding | `CombatSystem`, `MovementSystem`, `Shore`, `NavalSystem` | not rules (agreed with the coordinator): engine tuning, stays in code |

The core formulas already come from data: damage (`minimumDamage`, `resistanceScale`, BONUS.INI), the swing cycle,
reaches, the experience amounts (the curve's shape is still code, row 8), crafting, the market and the technology
effects (`ModifierStat`).

**Module boundaries, second look.** A read-only sweep found no leak that the architecture tests miss: project
references, `Content.Compiled`, the client contract (ADR 0212), the AI's read-only views (ADR 0213), public mutable
statics, the editor layers, and floats, the wall clock and `System.Random` in Sim, AI and Scripting. For a fork, the leak
that matters is code that spells data. `CivilizationKeyTests` (#211) now keeps civilization keys, race names and
`".castle"`/`".peon"` suffixes out of Sim, Game, AI and the client, except in 20 listed files (rows 3–7) whose counts
may only go down.

**Local-only tests.** Running them with the original found the reference data pin stale on main (#209). The
long campaign acceptance test (Editor) loses alex1, bigisland, v_02 and rajba after three attempts; the
campaign-19 work is bisecting v_02.

## Remaining findings and plan

Agreed with the coordinator (2026-10-22). Items 1–4 are done (#185, #187, #186, #188; see Fixes).

1. **The client's view of the match (module boundaries 2–3).** `GameSession` stops handing out `Simulation` and `Ai`,
   and `GameFrame` stops naming `Sim.Kingdoms` types. The client reads only frames, UI models and commands. This gets an
   ADR, and the architecture test checks the public surface the client sees.
2. **Civilizations everywhere (fork 2).**
   * The scenario model's `Civilization` enum gives way to keys or indices into the ruleset's list (ADR 0211).
   * Map generation takes the ruleset's list. Random maps change, so the mapgen goldens are re-pinned with the reason.
3. **The AI's read-only world (module boundaries 3).** `World.Players`, `World.Kingdoms` and
   `ComponentStore.Components` get read-only views for the AI, and the writable spans stay with the systems.
4. **The worker pool's late helpers (performance 8).** First an ADR and a stress test on CI runners. The primitive
   changes only if the test shows real stalls there.

Not planned yet:

* the rest of the formula constants in code (fork 4; second sweep rows 3–5 and 8);
* the AI's tuning in C# options;
* the diplomacy icon and name-suffix maps of the original's art;
* the replay recording's in-memory keyframes;
* the cursor and setup-screen costs (performance 7 and 9).
