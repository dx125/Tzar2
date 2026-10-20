# Legacy AI placeholders (UNVERIFIED)

Status: **open**. Register of every behaviour of the legacy AI (M3 part B, ADRs 0050-0052) that is neither in the
original data nor measured in the running game. Each entry is the most plausible reading of the original scripts and
manuals, implemented in one place and covered by a test; RE-07 replaces it with measurements (script experiments in the
original: a test script per point, observed with the capture harness, ADR 0030). Do not tune these for balance.

Where it lives: **VM** = `Tzar2.AI.Runtime.AiInterpreter` / `AiBinding` (script semantics), **options** = a field of
`AiOptions` (`Tzar2.AI.Runtime`), `EconomyOptions` or `MilitaryOptions` (`Tzar2.AI.Executors`), **executor** = code of
the named executor.

First capture (2026-10-18, ADR 0111): no AI scenario yet, nothing here changed. Script experiments need a working
`hmkingdom` (to watch a computer kingdom) and day/night-free long runs; see the smoke scenario of the next session.

## Script semantics (UNK-35 unless noted)

| # | Behaviour | Placeholder | Where | How to resolve |
|---|---|---|---|---|
| S1 | Evaluation cadence | a full pass starts every 1 s (20 ticks), staggered by player | options `ThinkIntervalTicks` | script with `say` + `time`, count messages per second |
| S2 | Instruction budget | 512 rule visits + conditions + actions per AI per tick; a pass may span ticks | options `InstructionBudgetPerTick` | not observable; ours only (determinism budget, REQ-AI-03) |
| S3 | Condition order | left to right with short-circuit (a `random` after a false condition does not draw) | VM | `random` frequency with a false first condition |
| S4 | Actions of an active rule | re-run every pass (level): flags set/cleared at once, visible to later rules of the same pass | VM | flag toggles observed with `say` |
| S5 | `say` | speaks when its rule becomes active, not again while it stays active | VM | count repeated messages (`hmai talk on`) |
| S6 | Flag names | case-insensitive | VM | corpus uses `trigger peon` for `Peon`; test both spellings |
| S7 | `critical` | gate fails when the rule is inactive or any of its goals is unmet; skips to the end of the block; a forgotten critical rule does not gate | VM | build-order experiments with a missing prerequisite |
| S8 | `include` | textual: spliced into the current block (a module's top-level `critical` gates the including block) | compiler | module with a top-level critical rule |
| S9 | Goals | maintained (collected every pass; losses rebuilt); priority = script order | VM, executors | kill units, watch rebuilding |
| S10 | Counts | completed buildings and live units; sites and queued units neither satisfy goals nor count in conditions | VM | conditions while a building is under construction |
| S11 | Percentages | share of the population limit (own; enemies: their total), goals rounded up | VM | `have 10% footman` with different limits |
| S12 | Plurals | `-s`, `-es`, `-men` → `-man` (Appendix D.2) | binding | — |
| S13 | `units` | own units including peasants (not buildings) | VM | `when units N` with peasants only |
| S14 | `enemy` | sum over the active players the AI's stance is Enemy towards (GAME-13; neutrals and allies excluded), whatever its fog hides | VM | do enemies hidden in fog count? (GAME-12) |
| S15 | Resource conditions | stock ≥ N; a `%` is ignored; `enemy` with a resource is false | VM | — |
| S16 | `random N` | true with N % per evaluation (with or without `%`), from `ai[p]` | VM | frequency count |
| S17 | `time N` | game time in ms ≥ N | VM | — |
| S18 | `needs_locked` | unmet resource goals before it in the pass lock those resources for the goals after it; workers exempt | VM, production executor | save-then-build experiment |
| S19 | Technologies | the players' research state (GAME-06, `AiTechnologies.Sim`); a name the ruleset does not have counts as researched (conditions true, goals met); a queued research is not yet researched | VM, binding | — (only the unknown-name fallback is a guess) |
| S20 | Unknown names | count 0, goals ignored (warning AI3001); class without a type of the civilization → goals ignored | binding | — |
| S21 | `convert` | collected, never blocks a gate, no executor (no tavern in the sim) | VM | GAME-14 |
| S22 | `resign` | sets `ResignRequested` when the lobby's "AI resign" flag allows; no sim command yet | VM | GAME-13 resign command |
| S23 | `forget NAME` | disables every copy of the named rule (a module included twice has two) | VM | — |
| S24 | `extern` arithmetic | 32-bit saturating, division by zero 0, unknown names 0, `$aik` = 1-based kingdom (UNK-41) | VM | editor expression experiments |
| S25 | Difficulty | changes only what scripts see; no resource or speed bonus (UNK-36) | settings | compare Hard AI stockpiles with a human's |
| S26 | "Economy AI" / "War AI" map flags | economy = construction, production, gathering; war = defence and attacks | executors | editor experiment (UNK-37 neighbour) |

## Executors (RE-07; the original's war and economy AI are hard-coded)

| # | Behaviour | Placeholder | Where |
|---|---|---|---|
| E1 | Executor cadence | every 0.5 s (10 ticks) per AI, staggered; at most 24 commands per run | options `ExecutorIntervalTicks`, `MaxCommandsPerRun` |
| E2 | Gatherer shares | base food 30 / wood 30 / gold 20 / stone 20; +100 in proportion to what the first 16 unmet goals need; base share dropped above 800 in stock | `EconomyOptions` |
| E3 | Reassignment | up to 12 idle workers sent and 2 workers moved between resources per run | `EconomyOptions` |
| E4 | Workers per node | trees and nodes ≤ 500: 2; rocks: 8; fields: 1; nodes > 32 tiles from an own drop-off unused | `EconomyOptions` |
| E5 | Fields | one more while the food share exceeds the fields (one worker per field), next to the farm | economy executor |
| E6 | Construction | one new site per run, 1-3 builders by footprint area (≤ 4, ≤ 9, larger tiles), at most 2 + workers/15 sites, at most 3 site searches per run | construction executor |
| E7 | Unaffordable goal | skipped for this run; the next goal may build or train; each class is worked on by its first unmet goal only, once per run | construction, production executors |
| E8 | Training | at most 2 per command, producer with the most free queue slots | production executor |
| E8a | Research | `have te_*` goals queued in the own building with the shortest queue that may research it, at most 2 per run; for unit/building goals the tree blocks, the first missing technology is researched and the first missing required building is built (a stable for knights) | research, construction executors |
| E9 | Placement | rings around the town (radius 24), the nearest node for drop-offs (≤ 30 tiles, radius 10), the farm for fields (radius 14); 1-tile walkable ring around every building | `AiBuildPlacement` |
| E10 | Gather point | 7 tiles from the town center toward the map center; producers of soldiers rally there | `MilitaryOptions` |
| E11 | Defence | soldiers of any kingdom the AI may fight (enemy or neutral stance, never allies) within 8 tiles of the town's bounding box; soldiers at home move-advance to the nearest (how many: T2; towers and buildings: T1) | `MilitaryOptions` |
| E12 | Waves | first after 4 min; 10 soldiers at home, +4 per wave, max 40; 60 s apart; target: nearest building of the enemy (Enemy stance only) with the nearest town | `MilitaryOptions` |
| E13 | Targeting | idle wave soldiers attack the enemy object nearest to the wave (outright within 12 tiles, else move-advance) | military executor |
| E14 | Retreat | a wave below 25 % of its launch size goes back to the gather point | `MilitaryOptions` |
| E15 | Town center | the completed drop-off accepting the most resources (the castle), else the first building, else the first unit | census |
| E16 | Naval need | only while an enemy town is not reachable by land (looked at every 10 s, at once while ships or a landing are in play); the naval executor runs first | naval executor (ADR 0144) |
| E17 | Harbor | one, on the coast nearest to the town (40 tiles, 8,000 candidates), the land side of its ring walkable | `AiBuildPlacement` |
| E18 | Ships | 1 transport from 240 s before the first wave (the cheapest that carries), 2 fishing boats when fish is within 24 tiles of the harbor, then 1 warship (the hardest hitter); the first transport and boat are saved for (the run's other executors leave that stock), the rest bought from spare stock | `NavalOptions` |
| E19 | Landings | a wave whose target is overseas boards the transports, then unloads at the target after everyone is aboard or 60 s; unload told again after 60 s; the military executor waits meanwhile | `NavalOptions` |
| E20 | Patrol | idle armed ships not kept for a landing attack the nearest enemy ship within 30 tiles, else the nearest enemy building or unit (of the 8 nearest) in range of their sea, else move-advance off the enemy town | naval executor |

## Tactics (ADR 0178; Tzar2's own, not placeholders: tune them for play)

The owner chose to keep the original scripts for strategy and give the AI new tactics scaled by difficulty
(2026-10-20). Power = √(health × average attack per second), summed over a group (`AiThreats`); "around" = 12 tiles
(`TacticsOptions.ThreatRadiusTiles`).

| # | Behaviour | Easy | Normal | Hard | Where |
|---|---|---|---|---|---|
| T1 | Defence also answers enemy towers reaching into the town box (+8 tiles), else enemy buildings and sites in it (explicit attack) | yes | yes | yes | `TacticsLevel.DefendBuildings` |
| T2 | Power sent against a threat at home, % of the enemy power within 6 tiles; against soldiers all go if too few, against a building they wait | 0 (all) | 100 | 150 | `TacticsLevel.DefendPercent` |
| T3 | A wave starts only with this % of the power around its target; at `MaxWaveSize` it starts anyway | 0 (no check) | 100 | 150 | `TacticsLevel.LaunchPercent` |
| T4 | A wave goes home below this % of the enemy power around it (besides E14) | off | 50 | 70 | `TacticsLevel.RetreatPercent` |
| T5 | Idle wave soldiers attack by danger: enemy soldiers, towers reaching the wave, other towers, workers (within 12 tiles); then E13 | no | yes | yes | `TacticsLevel.FocusTargets` |
| T6 | A wave aims at the enemy building with the least power around it | no | no | yes | `TacticsLevel.WeakestTarget` |
| T7 | Gatherers leave and avoid nodes a hostile tower reaches (+1 tile): a safe node of the same resource, else home | no | yes | yes | `TacticsLevel.AvoidTowers` |

## Not implemented yet (no placeholder; follow-ups)

Repair, capture (GAME-03/04), tavern conversion and trade (GAME-14), magic and religion
(GAME-11), walls, gates and towers, healing on retreat, diplomacy and resignation (GAME-13), "AI
Only" and "Rescue" kingdoms and the per-unit "No AI" flag (REQ-AI-05, UNK-37), scripts embedded in maps and campaigns
(M4 converter).
