# 0060. Generate random maps in Tzar2.Content from the mapgen stream, on the terrain-set corner model

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 part C)
* **Related:** TDD §7.2, §8.2, §8.3 (REQ-SIM-06), §9.1, §10 GAME-18, §13.5, §22.2, §25 M3; UNK-10; ADR 0012, ADR 0018,
  ADR 0039; docs/formats/bmp.md §3; [mapgen placeholders](../mechanics/mapgen-placeholders.md)

## Context

GAME-18 asks for random maps of six types (Random, Archipelagos, Island, Continental, Lakes, Dry), the sizes of the
editor (Tiny 64 … Real World 512; the lobby offers Medium and up), the four terrain sets and the lobby/editor options
(map resources low/normal/high, guarded town/towers, animals, gateways, magic items, cliffs, rivers, patches, rocks,
trees), deterministic from the seed. The algorithm may be redesigned; the "feel" should match. What the original data
says about it is thin: per-set `[randomterrainset]`/`[randomtreeset]` sections in `<SET>.TERRAIN.INI` (number tuples of
unknown meaning), preview art in `IMAGES/RANDOMMAP`, the option names in `LOCAL/STRINGS.INI` and two manual paragraphs.

Constraints: the map must be byte-identical on every peer (lockstep, §19), so the generator has the determinism rules of
the sim (integer math, ordered iteration, no clock, REQ-SIM-05) and draws only from the `mapgen` stream (REQ-SIM-06).
The sim consumes only `MapData` (REQ-ARCH-01). Terrain is a corner grid; which corner combinations the renderer can draw
is the UNK-10 hypothesis implemented by `TerrainComposer`. The `classic` ruleset still has only the M2 terrain types
`grass` (0) and `water` (1), and its hash is pinned by the gameplay goldens that other branches are changing.

## Decision

* **Placement:** `src/Tzar2.Content/MapGen/` (namespace `Tzar2.Content.MapGen`). Content is already a deterministic
  assembly (ADR 0012, analyzers T2D001-T2D005) and owns the compiled `MapData`/`Ruleset` contract, so the generator is a
  compiler of maps next to the compiler of rules. Every peer, the headless server, the CLI and the Godot client can call
  it without referencing Sim.
* **Randomness:** `MapGenerator.Generate(options)` derives the `mapgen` stream from `options.Seed`
  (`RngStreams.Derive(seed, MapGen, 0)`, the same state as a match's `RngStreams.MapGen`); `Generate(options, ref
  stream)` draws from a match's stream by reference. The random type, the random civilizations and one 64-bit seed per
  attempt are drawn from it; an attempt's own xoshiro256** (and the salts of its integer value noise) come from that
  seed. No other source of randomness exists.
* **Integer only:** value noise with smoothstep in 1/4,096 steps, quantile thresholds by sorting, `IntMath.ISqrt`
  distances, `Trig` LUT angles. Iteration is row-major or in player order; the only dictionary is a key lookup.
* **MapData, additive:** `MapData` gains an optional terrain set id and a `SetTerrainCorners` layer ((W+1)×(H+1)
  terrain-set indices, 0 = water, as in `<SET>.TERRAIN.INI`), the layer the renderer selects base and transition tiles
  from. The sim keeps reading `TerrainCorners` (ruleset ids). Both are hashed when the set is present; maps without a set
  hash exactly as before (no `HashVersion` bump, existing goldens unchanged). The ruleset ids are bound by the options
  (`LandTerrainId`/`WaterTerrainId`, default classic 0/1; `BindTo(ruleset)` looks them up and restricts the catalog).
* **Terrain painting:** each set has a `TerrainProfile`: its terrain count, the pairs that have a transition strip and
  the terrains trees grow on (structural facts of `[terrainfiles]`/`[treeterrain]`; cliff pseudo-terrains 8/9 left out),
  and painting layers forming a tree rooted at water: 0 → 1 (coast) → painted terrains. After painting, an erosion pass
  demotes any corner that borders a terrain other than its own, its parent or its child, so every tile has at most two
  corner terrains and they have a transition. The rule "drawable" lives in one place, `TerrainComposer.IsDrawnByRule`,
  which the viewer's composer now calls too; the validator checks every tile with it.
* **Pipeline of an attempt:** plan starts on a ring (teammates in neighbouring slots, small jitter) → shape land/water
  per type → rivers with fords → force land around the starts (Archipelagos: Voronoi channels give every kingdom its own
  island) → remove ponds/specks → bridge land starts that ended on different land → paint the corners → reserve
  walkways (a spanning tree of shortest land paths between the starts; on Archipelagos a path from each castle to a
  harbor spot on the open sea) → cliffs (off walkways and start areas) → castles, peasants and the same starting
  resources for everyone in the same distance bands (gold, stone, a forest, cows, fish on naval maps) → guarded spots
  and gateways at fair spots (as far and as equidistant from the castles as possible) → forests, scattered gold/stone,
  ground objects, fish, animals, patches, items.
* **Types:** Archipelagos = an island per kingdom plus small islands, contact by sea; Island = one large central island;
  Continental = a continent filling the map, ocean along the borders; Lakes = land with lakes; Dry = no water; Random =
  one of the five, drawn from the stream. (Readings, not facts: see the placeholders register.)
* **Validation and termination:** `MapGenValidator` checks the finished `MapData`: terrain set and ids consistent, every
  tile drawable, footprints on land and not overlapping, units on free land and fish on open water, a castle centred on
  each start, castles at least `MinSeparation` apart and evenly spaced. Reachability treats every footprint as a
  blocker, castles included (a kingdom is the set of walkable components next to its castle): land maps — every kingdom
  shares a component with player 0's; naval maps — every kingdom has its own island and its land reaches the open sea.
  Starting gold, stone, trees, cows and fish must be reachable (trees cut) near each castle, and a resource counts only
  for the castle it is nearest to, so on crowded small maps a neighbour's starting kit does not count; the builder
  places starting resources only where their own castle is the nearest one. An attempt that fails a construction step or validation is retried
  with the next attempt seed; after `MaxAttempts` (default 8) `MapGenException` is thrown. Earlier failures are kept in
  `GeneratedMap.Notes`.
* **Objects:** a `MapGenCatalog` maps roles (castle, peasant, tree, gold, stone, fish patch, cow, bear, gateway, tower,
  town house, item, ground object, ground patch) to ruleset keys (`{civ}` templates) and footprints. `Classic` names
  the classic keys with placeholder footprints so maps can be generated and hashed without original data;
  `RestrictTo(ruleset)` keeps the civilizations and roles a compiled ruleset defines and takes the envelope (widest ×
  tallest) of their footprints across the kept civilizations, so every civilization's real footprint fits the planned
  rectangle. Starting
  resources of the kingdoms (RES.INI levels) stay a match setting of the sim, not map content.
* **Tooling:** `t2 mapgen --type --set --size --seed --players [--teams --civs --resources --features] --out <dir>`
  writes a debug dump (`.t2mapgen`: "T2MG", version 1, the layers and objects, the content hash; not the `.t2map`
  package of §18.4), a JSON summary and a PNG preview with invented colours; `--sweep` runs every type × set of a size
  and reports times.
* **Goldens:** `Tzar2.Determinism.Tests/MapGenGoldenHashTests` pins the `ContentHash` of all 6 types × 4 sets × 7 sizes
  (168 maps, all features on) and runs in the cross-OS determinism job; it also loads a generated map, bound to the
  synthetic classic ruleset, into the `Simulation` and runs it.

Measured (Apple M-series, Release, .NET 10): Real World 512² with 8 players and every feature 60-250 ms per map
(Lakes slowest, it sometimes needs a second attempt); Medium 4-25 ms. 690 maps over all sizes, types and many seeds:
0 failures, 6 retries.

## Alternatives considered

* **Tzar2.Sim:** the sim consumes maps, it does not make them; other agents are changing Sim systems; the client and
  server would need Sim just to make a map.
* **Tzar2.Game:** not a deterministic assembly (no analyzer coverage), and it references everything.
* **A new `Tzar2.MapGen` assembly:** cleaner, but one more project, CI and dependency rule for ~3,000 lines that only
  need Core and the compiled contract.
* **Changing the classic ruleset to the terrain-set indices:** the honest end state (§9.1), but it changes the pinned
  ruleset hash other branches are editing and the M2 test maps' ids; deferred to the terrain-set conversion.
* **Per-tile terrain and a smoothing pass for transitions:** cannot express the 14 corner shapes (UNK-10).
* **Reproducing the original algorithm:** unknown; the `[randomterrainset]` numbers have no documented meaning.

## Consequences

* Matches can start from a random map: the lobby draws nothing itself; `Game` calls the generator with the match's
  `RngStreams.MapGen` and the bound ruleset.
* The renderer can draw generated maps from `MapData.TerrainSet` + `SetTerrainCorners` with `TerrainComposer`.
* Animals, fish patches, gateways, towers, items, ground objects and patches are placed only when the ruleset defines
  their types; today `RestrictTo(classic)` keeps castles, peasants, trees, gold and stone. Others appear as the ruleset
  grows, without generator changes (the golden hashes use the full `Classic` catalog and do not move).
* Any change to the generator, profiles, catalog or `MapData` hashing moves the 168 goldens on purpose.
* Revisit when RE resolves UNK-10 (per-corner storage, cliffs 8/9), when `.wmp` maps from the original random
  generator can be decoded (RE-03) to compare feel, and when the ruleset gets the per-set terrain types.

## Questions for the product owner

* Type semantics: is the original "Island" one shared island (our reading) or one island per kingdom, and
  "Continental" one continent (ours) or several? Is "Random" one of the other types (ours) or a type of its own?
* GAME-18 lists "guarded town" and "towers" as separate options; the strings file names one setting (`t_gslocations`)
  "Towers" in one dialog and "Guarded Town" in another, so we have one `GuardedTowns` flag. Are they one option?
* Default check-box states of the random-map dialogs (we: trees, rocks, patches, animals on; cliffs, rivers, guarded
  town, gateways, magic items off).
