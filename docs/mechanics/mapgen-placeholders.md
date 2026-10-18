# Random map placeholders (UNVERIFIED)

Status: **open**. Register of every parameter and rule of the random map generator (GAME-18,
[ADR 0060](../decisions/0060-random-map-generator.md)) that is not in an original data file and can only be learned from
the running original (its random-map dialogs and the maps it generates). TDD §10 allows a redesign of the algorithm
that matches the "feel"; these values are ours, chosen for playable, fair maps. Replace them with observations of the
original (capture harness, ADR 0030; or decoded `.wmp` maps saved from the editor's random generator, RE-03) rather than
tuning them for taste. Changing any of them moves the golden hashes in `MapGenGoldenHashTests`.

Where the value lives (all in `src/Tzar2.Content/MapGen/`): **Options** = `MapGenOptions.cs`, **Catalog** =
`MapGenCatalog.cs`, **Profile** = `TerrainProfile.cs`, **Builder** = `MapGenerator.cs` / `MapBuilder.Objects.cs`,
**Validator** = `MapGenValidator.cs`.

## What is known from the original data

* Types, sizes, terrain sets and option names: `LOCAL/STRINGS.INI` (`m_type_0…5`, `m_size_0…6`, `t_gslocations`
  "Towers"/"Guarded Town", `t_animals`, `t_rivers`, `t_mitems`, `t_cliffs`, `t_gateways`, `m_res_0…2`), the manual
  (§3.3) and the editor manual (§2: Tiny 64 … Real World 512; Patches, Rocks, Cliffs, Rivers, Trees, Animals, Towers,
  Magic Gateways, Items, Resources).
* Per terrain set: which terrain pairs have transition strips and which terrains trees grow on (`[terrainfiles]`,
  `[treeterrain]`, docs/formats/bmp.md §3) — the generator's profiles restate these structural facts.
* `[randomterrainset]` (e.g. pairs `01`, `21`, `42`, `31` with four numbers each) and `[randomtreeset]` (one tuple of
  eight numbers per map side 64…512) exist in every `<SET>.TERRAIN.INI`; **their semantics are unknown**. The pairs
  suggest which terrains the original paints on which (grass/mud/dirt on the coast terrain, rocks on grass in summer);
  our layers follow that shape but none of the numbers are used.

## Rules (readings of the option names)

| Rule | Our reading | Where | How to resolve |
|---|---|---|---|
| Random type | one of the five other types, drawn from the seed | Builder `MapGenerator.Generate` | generate many Random maps in the original |
| Archipelagos | an island per kingdom (Voronoi channels) + small islands; contact by sea | Builder `ShapeLand`, `ForceStartLand` | original maps |
| Island | one large central island, all kingdoms on it | Builder `ShapeLand` | original maps |
| Continental | one continent filling the map, ocean along the borders | Builder `ShapeLand` | original maps |
| Lakes / Dry | land with lakes / land without water (Dry ignores Rivers) | Builder | original maps |
| Rivers | 1 + side/256 rivers border to border, 3 (≥ 256: 5) tiles wide, a 4-tile ford every 28 steps; none on Archipelagos/Dry | Builder `CarveRivers` | original maps with Rivers on |
| Cliffs | side²/5000 ridges, 1 tile wide, 6…6+side/10 long, ≥ 2 tiles from water, off start areas and walkways; flags only (cliff art UNK-10) | Builder `PlaceCliffs` | original maps with Cliffs on |
| Guarded town / Towers | max(1, players/2) spots of two neutral towers around a magic item (or a gold rock); the most central spot also has two houses | Builder `PlaceGuardedSpots` | original maps |
| Gateways | 1 (≤ 128), 2 (≤ 256), 3 (larger) at fair spots | Builder `PlaceGateways` | original maps |
| Magic items | 2 per kingdom, ≥ max(20, side/5) tiles from every castle | Builder `PlaceFeatures` | original maps |
| Default check boxes | trees, rocks, patches, animals on; cliffs, rivers, guarded town, gateways, items off | Options `MapFeatures.Default` | the dialogs' initial state |
| Starting units | a castle and 5 peasants per kingdom | Catalog `StartingPeasants` | a random map in the original |
| Players per size | Tiny ≤ 4, Small ≤ 6, larger ≤ 8 | Builder `MaxPlayersFor` | the dialogs' limits |
| Neutral owner | towers, houses, animals, resources belong to Nature (0xFF) | Builder | original maps (Rescue/Nature?) |

## Values

| Value | Placeholder | Where |
|---|---|---|
| Footprints of the default catalog | castle 5×5, gold/stone 2×2, tower 2×2, house 2×2, gateway 3×3, tree and ground object 1×1 (a compiled ruleset's footprints replace them via `RestrictTo`) | Catalog `Classic` |
| Entity keys not yet in the classic ruleset | `comm.fishpatch`, `comm.cow`, `comm.bear`, `comm.magicgateway`, `comm.tower_stone`, `comm.item`, `comm.ground_object`, `comm.ground_patch` | Catalog `Classic` |
| Start ring radius | side × 0.27 (Island), 0.33 (Continental), 0.34 (Archipelagos), 0.36 (Lakes, Dry); jitter ±4 % radius, ±1/16 slot angle | Builder `PlanStarts` |
| Start area | forced land radius clamp(side/9, 7, 12) (+4 on land maps); keep-out ring castle/2 + 3 | Builder |
| Land share | Continental top 68 % of noise + 28 % edge band; Island top 45 % of noise + radial falloff; Lakes 13 % water; Archipelagos islands radius start + 5 + side/20, 3 + side/24 extras | Builder `ShapeLand` |
| Channel between Archipelago kingdoms | 3 + side/96 tiles | Builder `ChannelWidth` |
| Pond / speck cleanup | water < 16 tiles → land; land < 10 tiles → water | Builder `Cleanup` |
| Terrain layers (set: terrain on parent, ‰ of parent, blob size) | grass 2/1 700 20, 3/2 150 8, 4/1 60 6; summer 2/1 650 20, 4/2 120 8, 5/2 150 10; desert 2/1 350 16, 3/1 120 10, 5/1 40 6; winter 3/1 300 16, 2/1 100 10, 4/2 250 6 | Profile |
| Starting resources (all kingdoms equal) | gold and stone: 1 each (High: 2) at 8–12 tiles; a forest of min-trees + 10 at 9–15 tiles; 4 cows at 4–8 tiles (Animals); 2 fish patches near the harbor spot (Archipelagos) | Builder `PlaceKingdoms` |
| Resource radius / minima (validator) | 16 tiles, counting only resources whose nearest castle is this one; trees 30/40/60 (Low/Normal/High), 1 gold, 1 stone, 3 cows (Animals); naval: 1 fish within 8 tiles of the kingdom's land (any water, an inland lake counts too) | Validator |
| Castle spacing (validator) | ≥ max(12, side × 0.40/0.29/0.20/0.15 for ≤2/≤4/≤6/more players); nearest-neighbour spread ≤ ¼ of the mean | Validator |
| Map resources | gold and stone: one each per 3,600 / 2,400 / 1,500 land tiles (Low/Normal/High), ≥ 9 tiles apart, ≥ start area + 10 from castles | Builder `ScatterRocks` |
| Forests | 10 % / 15 % / 21 % of tree-capable free land (Low/Normal/High) in noise blobs of 12 + side/64 tiles, plus 0.6 % single trees | Builder `PlaceForests` |
| Fish | one per 120 / 80 / 50 coastal open-water tiles (≤ 3 tiles from land), ≥ 5 tiles apart | Builder `PlaceFish` |
| Animals | wild cows land/6,000 + players (≥ 16 tiles from castles), bears land/8,000 (≥ 24) | Builder `PlaceFeatures` |
| Decorations | ground objects land/1,800 (Rocks), patches land/800 (Patches) | Builder `PlaceFeatures` |
| Attempts | 8, then `MapGenException` | Options `MaxAttempts` |
