# M2 gameplay placeholders (UNVERIFIED)

Status: **open**. Register of every gameplay value and rule of M2 part C (ADRs 0031-0039) that is neither in an
original data table nor measured in the running game. Product-owner decision for M2: take what the INI/TSV tables
hold; give everything else an explicit placeholder marked `UNVERIFIED` with its unknown, to be replaced by capture
facts (`t2 fidelity`, ADR 0030; RE-05/RE-06). Do not tune these for balance; replace them with measurements.

Where the value lives: **YAML** = `content/engine/rules/classic.yaml` (marked `UNVERIFIED` in a comment; override it
like any rule), **code** = a rule of a sim system (named in the ADR; changing it needs a capture fact and an ADR
update).

## First capture (2026-10-18, session `capture-20261018-024524`, ADR 0111)

What the session could and could not settle for this register (facts: `tests/fidelity/facts/`):

* **VERIFIED:** population limit default (the single-player setup screen shows 200 for a preset map).
* **Not resolved, re-capture needed:** SPEED → px/s (UNK-12; `unit-speed`: the spawned units stayed unplaced and never
  moved), time between hits, hit frame, minimum damage (UNK-07/08; `attack-timing`: no fight took place). Both scenarios
  are rewritten for the next session (ADR 0110).
* **Not captured:** everything else here (no scenario yet).
* Starting resources: the setup screen of a preset map shows "Default" (the map's own amounts: 1,000 of each on MP2,
  400 on MP3, 2,000 on MP6), which says nothing about the random-map default; still UNVERIFIED.

## Batch A of the second session (2026-10-18, session `capture-20261018-155421`, ADR 0169)

Spawning now works (`hmnopop` lifts the house limit that blocked every `hmspawn` of the first session), so units fought:

* **VERIFIED:** time between hits = 6,000 ms / RATE (two fights, `m2.attack-cycle`); damage = the flat rule for two
  pairs (`m2.damage`).
* **VERIFIED, and changed:** the unit and building tables' **TIME column is 1.25 s per unit**, not 1 s: a peasant
  (TIME 8) is trained every 9.99 s, and one builder raises a lumber mill (TIME 30) at a rate that completes it in
  36.85 s. `LegacyTime.MsPerTimeUnit` = 1,250 (ADR 0169). Research times (`time` in ms) stay unscaled until measured.
* **Consistent with the rules below:** a site's health grows linearly from 1 to the maximum, and the rate doubled when
  a second peasant joined the builder (construction rate linear in builders, not yet measured with more than two).

## Batch B of the second session (2026-10-18, session `capture-20261018-222649`, ADR 0170)

* **VERIFIED:** a peasant carries 10 wood, stone or gold per trip, and **50 food from a cow** (a node may now set its own
  load, `ResourceNode.carry`). Suite: 6 of 30.
* **Not resolved:** unit speeds (SPEED → px/s, UNK-12): the scenario's selection box reached the window edge and scrolled the
  camera away; the scenario is fixed and `unit-speed` is re-captured. Gather times: trips include the walk (batch E).

## Values

| Value | Placeholder | Where | Unknown | How to resolve |
|---|---|---|---|---|
| TIME column (units, buildings) | ~~1 s per unit~~ — **VERIFIED: 1.25 s per unit** (capture-20261018-155421: `train-time` 9.99 s for TIME 8, `build-time` 36.85 s for TIME 30; ADR 0169) | importer `LegacyTime.MsPerTimeUnit` | UNK-20, UNK-18 | done (research times: UNK-21, batch E) |
| Walking speed | **VERIFIED per class, not from SPEED** (capture-20261019-163231 and -190321; ADR 0172): every measured foot class 40 px/s (SPEED 2–4), knight and crusader 72, troll 47, ghost 67, cow and bear 26 — YAML `^class.*.Mobile.pixelsPerSecond`. Unmeasured classes: **by the walk sheet's stride** (`move_len`): a pixel every `(32000 / move_len) >> 5` of the original's ms, 1.25 of ours (ReTzar's reading of the original; ADR 0190), which gives the 18 measured classes within 1 %; SPEED × 10 px/s only without a stride | importer `Mobile.stride`; `EntityCompiler.StrideUnitsPerSecond`; YAML `gameplay.speedPixelsPerSecondPerPoint` (fallback) | UNK-12 | a capture of a horse archer or a catapult would confirm the rule |
| Group move speed | `slowest` — **VERIFIED** (capture-20261019-221300, `group-speed`: a knight, 72 px/s alone, and a pikeman, 40, walked 40.2 and 39.9 px/s together) | YAML `gameplay.groupMoveSpeed` | UNK-12 | done |
| Time between hits | 6,000 ms / RATE — **VERIFIED** (capture-20261018-155421, `attack-timing`: two series within tolerance) | YAML `gameplay.attackCycleMs` | UNK-07 | done |
| Hit frame | **from the data** (WAR-014): the share of the attack animation before IMAGES.INI `actionrow`; 50% of the cycle only for types without it | `Armament.hitPermille` (importer), YAML `gameplay.hitDelayPercent` | UNK-07 | whether `actionrow` counts from 0 or 1 (one frame) |
| Minimum damage | 1 | YAML `gameplay.minimumDamage` | UNK-08 | damage grid |
| Melee reach | 8 px edge to edge (buildings: the interaction reach) | YAML `gameplay.meleeReachPx` | UNK-07, UNK-11 | captures of melee contact |
| Worker reach | 32 px from the footprint once stopped; 12 px while still walking there, so units go up to the wall (owner play-test 2026-10-20: they stood a tile away) | YAML `gameplay.interactionReachPx`, `interactionTouchPx` | UNK-17, UNK-18 | captures of gathering/building positions |
| Carry capacity | 10 units — **VERIFIED for wood, stone and gold** (capture-20261018-222649, `gather-*`: every delivery 10); a node may set its own load, cows 50 (m3 register, ADR 0170) | YAML `^class.peon.Harvester.capacity`, `ResourceNode.carry` | UNK-17 | done (fields: not measured) |
| Gather time | ~~1,000 ms~~ — **900 ms per unit, measured once on wood and applied to every resource** (capture-20261019-232318, `gather-rate`: one load of 10 chopped in ~9.0 s ± 0.5 s; ADRs 0174, 0175). Batches B and E timed deliveries of several peasants at once, not trips of one | YAML `^class.peon.Harvester.gatherMs` | UNK-17 | M8: more loads, other resources |
| Wood per tree | 100 | YAML `comm.tree.ResourceNode.amount` | UNK-17 | tree depletion capture |
| Node search radius | 12 tiles; the next node is the one within reach (a walkable tile of the worker's area touches it) with the shortest way from the last node plus from it to the nearest drop-off, so cutting moves toward the lumber mill (owner's third play-test: peasants walked to far trees; ADR 0180) | YAML `gameplay.nodeSearchRadiusTiles` | UNK-17 | observe auto-continue after depletion |
| Queue length | ~~10~~ — **any number of any items** (owner's third play-test, 2026-10-20: the original queued 10 archers, 10 swordsmen and 100 knights at once); 24 different items at a time (ours: queue entries, ADR 0180) | YAML `gameplay.queueCapacity` (different items) | UNK-20 | — |
| Idle work | a peasant idle for 10 s takes the nearest work within 12 tiles: an own site, an own damaged building, a resource it gathers that an own drop-off takes (owner's third play-test: the original's peasants did; delay and radius UNVERIFIED; ADR 0180) | YAML `gameplay.idleWorkAfterMs`, `idleWorkRadiusTiles` | — | capture: idle peasants next to a site, a tree and a damaged building |
| Population limit default | 200 — **VERIFIED** (capture-20261018-024524, `smoke/plain` window capture `setup`: the setup screen's default) | YAML `gameplay.populationLimit` | UNK-20 | done |
| Starting resources default | `medium` | YAML `gameplay.defaultStartingResources` | — | lobby default |
| Refund on cancel | queue items 100%; sites all before any work, then half of what is left (**ReTzar's reading of the original, ADR 0251**) | YAML `gameplay.*RefundPercent`; `GameplayCommands.SiteRefundBasisPoints` | — | cancel captures |
| Destroyed producer | its queue is lost without a refund | code (`World.Destroy`) | — | observation |
| Collision radius | **from the data** (ADR 0188): 7/16 of the IMAGES.INI object box, 14 px for 32 x 32 (batch K: 32 px apart at rest, 27+ px head on); the terrain footprint stays 7 px (cavalry 10, siege 12) | importer; YAML `Collision.terrain` | UNK-11 | a crowd around one target |
| Occupancy cells per tile | 2 × 2 | YAML `ruleset.occupancyCellsPerTileAxis` | UNK-11 | same |
| Building damage reduction | damage × 1000 / (1000 + RESISTANCE) (owner decision, PR #21: the first placeholder, scale 100, left castles with RESISTANCE 1000 at 1 damage per footman hit — two footmen needed ~80 min; 1000 halves damage to a castle and takes a third off barracks); RESISTANCE from the buildings table; defence 0 (owner decision: follow the manual, manual.pdf ch. 4 "The Interface", § 4 "The Selection View", p. 24: "RESISTANCE - reduces the amount of damage a building suffers when attacked") | YAML `gameplay.resistanceScale`, `^Building.Armor` | UNK-08; RESISTANCE also feeds capture later (GAME-04) | damage grid on buildings |
| Corpse lifetime | 120 s | YAML `ruleset.corpseTtlMs` | — (REQ-PATH-08 default) | capture |
| Idle peasants fight back | no auto-acquire for peasants | YAML `^class.peon.Armament.autoAcquire` | — | observation |
| Storage accepts stone | stone, gold, iron (manual: "iron and gold") | YAML `^class.storage.DropOff` | — | observation |
| Field footprint | 3 × 3 from the selection box (no `object` box) | importer | UNK-02 | editor placement |
| DAT footprint value 1 | treated as blocked | importer | UNK-02 (dat.md §3) | editor placement/pathing |

## Rules in code

| Rule | Placeholder | System | Unknown |
|---|---|---|---|
| Damage | `max(min, attack × (1 + bonus) − defence)`, truncated toward zero; buildings then × scale / (scale + RESISTANCE) (owner decision: keep the flat formula until the damage capture) | `CombatSystem` | UNK-08 |
| Several BONUS.INI matches | summed | `LegacyBonuses` | UNK-08 |
| Ranged attacks | ~~land at the hit frame~~ — M3 stream E: projectiles with flight time (ADR 0091, m3-placeholders.md) | `CombatSystem` | UNK-07, RE-06 |
| Splash, friendly fire | ~~none~~ — M3 stream E: splash per projectile, no friendly fire (ADR 0091) | `CombatSystem` | UNK-22 |
| Target choice | nearest by edge distance, lower index on ties; scans every 200 ms | `CombatSystem` | — |
| Simultaneous hits | all hits landing in a tick apply together; units killed in that tick still land theirs | `CombatSystem` | UNK-07 |
| Nature's units | hostile to every kingdom and attackable | `CombatSystem` | — |
| Retaliation, leash | an idle armed unit that is hit fights back; auto targets beyond 2 × sight are dropped | `CombatSystem` | — |
| Construction rate | each builder in reach adds its tick (linear in builders) | `ProductionSystem` | UNK-18 |
| Site health | grows linearly from 1 to the maximum with progress; damage taken stays taken | `ProductionSystem` | UNK-18 |
| Payment | at placement (sites) and when queued (units) | `GameplayCommands` | — |
| Full population | the finished unit waits in the building (`ProductionStalled`) | `ProductionSystem` | UNK-20 |
| Exit point | a tile around the building no unit stands on, on the ring nearest to its walls, nearest to the rally point (else its south side), in the connected area with the most room around it: a pocket shut in by walls or buildings is left out (owner's third play-test; ADR 0180) | `Interaction.SpawnPoint` | — |
| Loads | the whole load appears at once when the work for it is done (one gather time per unit); milk fills the pail unit by unit; work broken off is lost (owner's third play-test; ADR 0180) | `EconomySystem` | UNK-17 |
| Helping on a site | any builder, whatever its civilization; placing one needs a builder of its type (owner's third play-test; ADR 0180) | `GameplayCommands` | — |
| Mixed loads | a worker carrying another resource delivers it before gathering | `WorkerSystem`/`EconomySystem` | UNK-17 |
| Giving up | after 5 movement attempts without reaching the target | `WorkerSystem` | — |
| Enemy in the way | a mover side-steps around it (right first, then left) and stops (`Blocked`) only when neither side is free; once idle it acquires the blocker, a move-advance engages it at once (owner decision) | `MovementSystem`, `CombatSystem` | UNK-11 |
| Technologies | ~~none can be researched in M2~~ — resolved in M3 stream A (ADR 0040; magnitudes: m3-placeholders.md) | `GameplayCommands` | — |
| Field decay | ~~fields never decay~~ — M3 stream A: neglect decay (ADR 0042, m3-placeholders.md) | `EconomySystem` | UNK-17 |
