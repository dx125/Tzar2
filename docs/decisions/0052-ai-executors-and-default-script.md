# 0052. Turn script goals into commands with five executors; ship an engine-default AI script

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 part B)
* **Related:** TDD §16.1 (REQ-AI-02, REQ-AI-04, REQ-AI-05), §16.2, §21 (B1), §25 (M3 "Skirmish vs. Hard AI"),
  REQ-DATA-02, REQ-LEGAL-03, RE-07, UNK-35, UNK-36; ADR 0050, ADR 0051; docs/mechanics/ai-placeholders.md

## Context

The scripts only state goals ("have 10 footman", "have 2 barracks", "have 1000 gold"); everything else — which peasant
gathers what, where a building goes, when and where the army attacks — was hard-coded in the original executable
(REQ-AI-02). Nothing of it is measured yet (RE-07), so the executors implement the most plausible behaviour with every
tuning value in `AiOptions` and listed as UNVERIFIED. The sim of M3 has gathering, construction, production, movement
and combat; technologies, capture, repair, trade, magic, ships and diplomacy come later.

## Decision

* **`IAiExecutor`** (`Run(AiExecutorContext)`) is the extension point: the context gives read-only access to the world,
  the census, the bound script and its goals in priority order, a resource budget that the run's commands have already
  spent from, the goal's `needs_locked` mask, claims (a unit ordered by one executor is left alone by the next) and a
  command budget (`MaxCommandsPerRun`). Executors keep no state across runs except in `AiPlayerState` (snapshots).
  `AiOptions.ExtraExecutors` appends more (research, magic, naval, trade, resignation when the sim gets them).
* Built-in executors, in this order per run:
  1. **Construction** — a site without builders gets one; the first unmet building goal that is affordable, whose
     requirements are met and that has a site starts one construction per run with the nearest free workers (idle
     first; 1-3 builders by footprint area). At most `2 + workers/15` sites at a time.
  2. **Production** — each unmet unit goal queues what is missing (completed + queued count) in the own producer with
     the most free slots, at most 2 per command, within population room and the budget.
  3. **Research** — each unmet `have te_*` goal is queued (CommandKind.Research) in the own building with the
     shortest queue that may research it now, within the budget and the goal's lock, at most 2 per run; for unit and
     building goals the civilization's tree blocks, the first missing technology is researched too. (Construction
     likewise builds the first missing required building of a blocked goal, e.g. a stable for knights.)
  4. **Economy** — gatherers are split over the resources with a node and an own drop-off: base shares (food 30, wood
     30, gold 20, stone 20; dropped for a resource stocked beyond 800) plus 100 shared in proportion to what the first
     16 unmet goals still need. Idle workers go to the resource furthest below its share; two workers per run move from
     the most over- to the most under-staffed one. Nodes come from a per-resource shortlist ranked by distance to the
     nearest own drop-off (≤ 32 tiles), plus a quarter of the walk and two tiles per worker already there; at most 2
     workers per small node (trees), 8 per rock, 1 per field. Food comes from fields: while the food share exceeds the
     kingdom's fields, one more field is built next to the farm.
  5. **Military** — producers of soldiers rally at a gather point 7 tiles in front of the town; enemy soldiers within 8
     tiles of the town's buildings are attacked (move-advance) by the soldiers at home; after 4 minutes, when at least
     10 (+4 per wave, up to 40) soldiers are at home and 60 s have passed since the last wave, they move-advance to the
     nearest building of the nearest enemy; idle wave soldiers then attack the enemy object nearest to the wave
     (outright within 12 tiles, else move-advance); a wave below 25 % of its launch size goes home.
* **Build placement** (`AiBuildPlacement`): square rings around an anchor — the town center for most buildings, the
  nearest resource node for single-resource drop-offs (lumber mill, storage), the farm for fields — first fit, at most
  1,200 candidates. A site is valid when the sim would place it and the one-tile ring around it is walkable, so towns
  keep their walkways. Roles for walls, gates and towers are added with those buildings.
* **Map settings** (REQ-AI-05): "Economy AI" off disables construction, production and economy; "War AI" off disables
  the military executor; the script still runs. **Difficulty** changes only what the scripts see (ADR 0050; no
  resource bonus, UNK-36).
* **Engine-default script** `content/engine/ai/skirmish.ais` (Tzar2's own, MIT; embedded with the engine defaults,
  REQ-DATA-02). The game uses the original scripts from the user's base package; this one runs when they are missing,
  in public CI and in benchmarks (REQ-LEGAL-03), and doubles as an example of the language. It names only object
  classes of the classic subset the rules compiler imports today.
* **`AiSkirmishScenario`** (`t2 bench --scenario ai`): AI against AI on open grass, each kingdom with a castle, six
  peasants, a forest, a gold and a stone rock; nothing but the AI issues commands. Functional gate: every kingdom
  delivers, builds, trains and deals damage; nightly gate: 8 kingdoms, p99 ≤ 12 ms.

## Consequences

* Measured (Apple M5, macOS arm64, Release, .NET 10): two Hard AIs with the default script on 96², 12 minutes: each
  delivers ~19,000 resource units, completes ~50 buildings, trains ~120 units, launches 1-3 waves and kills 30-40
  enemies. With `TZAR2_ORIGIN`, the original `random.ais` (easy, normal: fights within 10 minutes; hard: saves food
  first as its start module asks) and `dm.ais` play the same map on the synthetic ruleset.
* On generated random maps (ADR 0060; Medium, seed 7, 8 minutes) Hard AIs develop on all six types (~9,000 resource
  units, ~40 buildings, ~65 units each) and fight on Continental, Lakes, Dry and Random; on Island and Archipelagos
  only where land connects them (no ships in the sim yet; superseded by ADR 0144: on Archipelagos the AIs land by ship).
* Diplomacy (GAME-13): waves, targeting and `enemy` conditions consider only kingdoms the AI's stance is Enemy
  towards; defence also answers neutral soldiers; allies are never targeted (tested with a team of two AIs against a
  third). The executors do not consult fog (GAME-12): counting and targeting are global, like the scripts' `enemy`
  conditions (UNVERIFIED whether the original's AI saw through fog).
* Follow-up when the sim grows: repair and capture (GAME-03/04), tavern conversion and
  trade (GAME-14), magic and religion (GAME-11), ships, walls and towers, diplomacy and resignation (GAME-13), "AI
  Only"/"Rescue" kingdoms and the "No AI" unit flag (REQ-AI-05, UNK-37).

## Alternatives considered

* **One monolithic AI controller:** harder to extend per milestone; separate executors with claims compose and can be
  replaced or added by mods (§16.2).
* **Plan builds with a reservation system for every goal:** the original scripts already express saving with
  `needs_locked`; an implicit reservation would change their behaviour.
