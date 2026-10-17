# M2 gameplay placeholders (UNVERIFIED)

Status: **open**. Register of every gameplay value and rule of M2 part C (ADRs 0031-0039) that is neither in an
original data table nor measured in the running game. Product-owner decision for M2: take what the INI/TSV tables
hold; give everything else an explicit placeholder marked `UNVERIFIED` with its unknown, to be replaced by capture
facts (`t2 fidelity`, ADR 0030; RE-05/RE-06). Do not tune these for balance; replace them with measurements.

Where the value lives: **YAML** = `content/engine/rules/classic.yaml` (marked `UNVERIFIED` in a comment; override it
like any rule), **code** = a rule of a sim system (named in the ADR; changing it needs a capture fact and an ADR
update).

## Values

| Value | Placeholder | Where | Unknown | How to resolve |
|---|---|---|---|---|
| SPEED point → px/s | 12 px/s per point (SPEED 4 = 48 px/s) | YAML `gameplay.speedPixelsPerSecondPerPoint` | UNK-12 | capture scenario `50-unit-speed` |
| Group move speed | `slowest` in both rulesets (owner decision) | YAML `gameplay.groupMoveSpeed` | UNK-12 | same captures with mixed groups |
| Time between hits | 6,000 ms / RATE | YAML `gameplay.attackCycleMs` | UNK-07 | attack-timing captures |
| Hit frame | 50% of the cycle | YAML `gameplay.hitDelayPercent` | UNK-07 | `actionrow` vs. captures |
| Minimum damage | 1 | YAML `gameplay.minimumDamage` | UNK-08 | damage grid |
| Melee reach | 8 px edge to edge (buildings: the interaction reach) | YAML `gameplay.meleeReachPx` | UNK-07, UNK-11 | captures of melee contact |
| Worker reach | 32 px from the footprint | YAML `gameplay.interactionReachPx` | UNK-17, UNK-18 | captures of gathering/building positions |
| Carry capacity | 10 units, every resource | YAML `^class.peon.Harvester.capacity` | UNK-17 | gather/carry captures |
| Gather time | 1,000 ms per unit, every resource | YAML `^class.peon.Harvester.gatherMs` | UNK-17 | gather/carry captures |
| Wood per tree | 100 | YAML `comm.tree.ResourceNode.amount` | UNK-17 | tree depletion capture |
| Node search radius | 12 tiles | YAML `gameplay.nodeSearchRadiusTiles` | UNK-17 | observe auto-continue after depletion |
| Queue length | 10 | YAML `gameplay.queueCapacity` | UNK-20 | UI capture |
| Population limit default | 200 | YAML `gameplay.populationLimit` | UNK-20 | lobby default |
| Starting resources default | `medium` | YAML `gameplay.defaultStartingResources` | — | lobby default |
| Refund on cancel | 100% (sites at any progress, and queue items) | YAML `gameplay.*RefundPercent` | — | cancel captures |
| Destroyed producer | its queue is lost without a refund | code (`World.Destroy`) | — | observation |
| Collision radius | 7 px (cavalry 10 px) | YAML `^Unit`/`^Cavalry.Collision.radius` | UNK-11 | dense formation captures |
| Occupancy cells per tile | 2 × 2 | YAML `ruleset.occupancyCellsPerTileAxis` | UNK-11 | same |
| Building damage reduction | damage × 100 / (100 + RESISTANCE); RESISTANCE from the buildings table; defence 0 (owner decision: follow the manual, manual.pdf ch. 4 "The Interface", § 4 "The Selection View", p. 24: "RESISTANCE - reduces the amount of damage a building suffers when attacked") | YAML `gameplay.resistanceScale`, `^Building.Armor` | UNK-08; RESISTANCE also feeds capture later (GAME-04) | damage grid on buildings |
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
| Ranged attacks | land at the hit frame (no projectile flight time) | `CombatSystem` | UNK-07, RE-06 |
| Splash, friendly fire | none | `CombatSystem` | UNK-22 |
| Target choice | nearest by edge distance, lower index on ties; scans every 200 ms | `CombatSystem` | — |
| Simultaneous hits | all hits landing in a tick apply together; units killed in that tick still land theirs | `CombatSystem` | UNK-07 |
| Nature's units | hostile to every kingdom and attackable | `CombatSystem` | — |
| Retaliation, leash | an idle armed unit that is hit fights back; auto targets beyond 2 × sight are dropped | `CombatSystem` | — |
| Construction rate | each builder in reach adds its tick (linear in builders) | `ProductionSystem` | UNK-18 |
| Site health | grows linearly from 1 to the maximum with progress; damage taken stays taken | `ProductionSystem` | UNK-18 |
| Payment | at placement (sites) and when queued (units) | `GameplayCommands` | — |
| Full population | the finished unit waits in the building (`ProductionStalled`) | `ProductionSystem` | UNK-20 |
| Exit point | the free tile next to the building nearest to the rally point, else its south side | `ProductionSystem` | — |
| Mixed loads | a worker carrying another resource delivers it before gathering | `WorkerSystem`/`EconomySystem` | UNK-17 |
| Giving up | after 5 movement attempts without reaching the target | `WorkerSystem` | — |
| Enemy in the way | a mover side-steps around it (right first, then left) and stops (`Blocked`) only when neither side is free; once idle it acquires the blocker, a move-advance engages it at once (owner decision) | `MovementSystem`, `CombatSystem` | UNK-11 |
| Technologies | none can be researched in M2: anything with a `te_*` requirement cannot be built or trained | `GameplayCommands` | — (M3, GAME-06) |
| Field decay | fields never decay | — | UNK-17 (M3) |
