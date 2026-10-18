# M3 gameplay placeholders, stream A: GAME-01 … GAME-06 (UNVERIFIED)

Status: **open**. Register of every gameplay value and rule of M3 stream A (ADRs 0040-0045) that is neither in an
original data table nor measured in the running game. Owner decision: no captures from the running original yet; take
what the INI/TSV tables and the manuals say; give everything else an explicit placeholder marked `UNVERIFIED` with its
unknown, to be replaced by capture facts (`t2 fidelity`, ADR 0030; RE-05). Do not tune these for balance: replace them
with measurements. The M2 register ([m2-placeholders.md](m2-placeholders.md)) still holds for the values it lists.

Where the value lives: **YAML** = `content/engine/rules/classic.yaml` (marked `UNVERIFIED` in a comment; override it
like any rule), **code** = a rule of a sim system (named in the ADR; changing it needs a capture fact and an ADR
update), **import** = how the importer reads the data (a hypothesis about a column or key).

## From the data (not placeholders)

Costs, times and names of the 87 technologies and 12 upgrades; per-civ prerequisites (`TECHTREE [depend]`,
`[required]`, `[orders]`); research sites (command cards: `OBJECTS menu` → `MENU.INI` → `ORDERS.INI`); upgrade targets
(`ACTION = UPGRADE <class>`); every unit and building row of all civilizations; tower armament and hit points (unit
table); capture flags (`OBJECTS type = …, capture`); RESISTANCE and EMIT pairs; mana pools (`max_manna`); mana costs
(`m` suffix); locomotors from `teren` (0 air, 2 water) and `type = unit, mounted`; fish patch amount (`RES.INI`).
From the manual (source priority 3): which resources each civilization's storage keeps; that guilds and the castle need
Shift + Delete; what each technology does (not how much).

## Values

| Value | Placeholder | Where | Unknown | How to resolve |
|---|---|---|---|---|
| Effect magnitude of every technology | per technology in YAML (e.g. Rotating Fields +25% field gathering, Temper Iron +10% sword attack, Scale/Chain Armor +1 infantry defence, Feudalism −20% peasant cost, Architecture +25% build rate, Fanaticism/Buddhism +20% building resistance, Supply Storage/Seafood +2 per house, Great Wall +25% stone wall/tower HP) | YAML `te_*.Technology.effects` | UNK-21 | per-technology captures (stat before/after) |
| Effects consumed later | Learning +4 / Heroism +1000 experience cap, Warfare/Tactics/Sun Tzu +25% experience, Body Building +5 HP per level, Philosophy +2 starting level, Leadership/Honor ability power, Inquisition, Medicine, Landing Tactics +4 holder slots, Trade Taxes/Banking income | YAML | UNK-23/24/26/28 | GAME-08/09/11/14 captures |
| Ship cannon levels | +5 attack per level | YAML `to_ship_level2..6` | UNK-21 | galleon attack per level |
| Ship level order | level N needs level N−1 (`after`) | YAML | — | upgrade menus `e_galleon2…` (data) confirm the order; per-ship effects need capture |
| Iron Mining / Ore Flotation targets | iron rocks (`res_iron`) — no such object exists | YAML | UNK-14 | play: is there any iron source? |
| Research/upgrade refund on cancel | 100% | YAML `gameplay.researchRefundPercent` | — | cancel captures |
| Mana regeneration | 60 per minute for every pool | YAML `gameplay.manaRegenPerMinute` | UNK-26 | mana bar captures |
| Concentration | +50% mana regeneration | YAML | UNK-26 | same |
| Cow milk | the cow's `max_manna` is the food it holds (⚠ HYPOTHESIS) | import `legacy.milkClasses` | UNK-17 | milk a cow dry, count the food |
| Milk regrowth | 1 unit per 4 s, kept when empty | YAML `^class.cow.ResourceNode` | UNK-17 | captures of a cow over time |
| Milking time | 1.5 s per unit | YAML `^class.cow.ResourceNode.gatherMs` | UNK-17 | gather captures |
| Meat | 100 food, rots after 120 s, a Nature node | YAML `comm.meat` | UNK-17 | butcher captures |
| Fishing boat | carries 20, 1 s per unit | YAML `^class.fishing_boat.Harvester` | UNK-17 | fishing captures |
| Harbor drop-off | food from boats only | YAML `^class.harbor.DropOff.access` | — | observe peasants with food near a harbor |
| Field decay | 60 s untended, then 30 HP per minute | YAML `^class.field.Decay` | UNK-17 | neglect captures |
| Field builder farms it | after completing a field | YAML `^class.field.DropOff.autoGather` | — | observation |
| Peasants gather iron | 1 s per unit, like other resources | YAML `^class.peon.Harvester.gatherMs.iron` | UNK-14/17 | — |
| Repair speed | a full repair by one peasant takes the build time | YAML `gameplay.repairTimePercent` | UNK-18 | repair captures |
| Repair cost | free | code (`ProductionSystem`) | UNK-18 | stockpile during a repair |
| Fire threshold | below 50% of the hit points | YAML `gameplay.burnThresholdPercent` | UNK-42 | damage a building step by step |
| Fire damage | 60 HP per minute | YAML `gameplay.burnDamagePerMinute` | UNK-42 | time a burning building |
| Burning types | every building but fields, walls, gates and bridges | YAML (`Burnable`) | UNK-42 | observation |
| Ruin lifetime | the corpse decal lifetime (120 s) | code (`World.Kill`) | — | observation |
| Wall drag | at most 64 segments; builders move on within 6 tiles | YAML `gameplay.maxWallSegments`, `wallContinueRadiusTiles` | — | editor/game captures |
| Gate footprint | 3 × 1 with the middle tile as the passage | YAML `^class.gate_*` | UNK-02 | editor placement |
| Gate opening | automatic by default; opens for the owner's units within 32 px | YAML `gameplay.gateOpenReachPx` | — | observation |
| Bridge footprint | the sprite box (`bridge_horz`), ends on land | import + code | UNK-02 | editor placement |
| Harbor water | within 2 tiles of the footprint | code (`GameplayCommands.CoastReachTiles`) | UNK-02 | editor placement |
| Shore reach | +1 tile for boats at buildings and peasants at ships | code (`Interaction.WorkerReaches`) | UNK-11 | captures (and the terrain-set conversion) |
| Capture rate | 10 points per digging peasant per second | YAML `gameplay.capturePointsPerSecond` | UNK-19 | capture captures |
| Capture reset | progress lost after 5 s without diggers | YAML `gameplay.captureResetMs` | UNK-19 | same |
| EMIT influence | own buildings' EMIT adds, others' subtracts, linearly to 0 at 16 tiles (⚠ HYPOTHESIS) | YAML `gameplay.influenceRadiusTiles` + code (`CaptureSystem`) | UNK-19 | capture times near and far from towns |
| Minimum capture resistance | 1 | YAML `gameplay.minimumCaptureResistance` | UNK-19 | — |
| Armored chariot, caravan speed | SPEED 4 (the tables have none) | YAML `^class.chariot`, `^class.trader` | UNK-12 | speed captures |
| Ship / siege collision radius | 16 / 12 px | YAML `^Ship`, `^Siege` | UNK-11 | captures |
| Siege locomotor | large | YAML `^Siege` | UNK-11 | — |

## Rules in code

| Rule | Placeholder | System | Unknown |
|---|---|---|---|
| Modifier formula | `(base + Σadd) × (10,000 + Σbp) / 10,000`, truncated, ≥ 0; several effects add up | `PlayerStats` | UNK-21 |
| Who a tree applies to | the actor's civilization (common types: the owner's) | `World.CivilizationOf` | — |
| A player's civilization | the first own object's at match start, else the first non-common one (until the lobby sets it) | `World.AssignCivilizations` | — |
| Research once | a technology is never queued twice for a player (1.10 fix) | `GameplayCommands.Research` | — |
| Research site | the first listed building that can research it now | `GameplayCommands.Research` | — |
| Costs after a technology | refunds use the current price | `ResearchRules` | — |
| Conversion keeps damage | the new type's maximum minus the damage taken | `World.ConvertType` | — |
| One conversion at a time | per entity | `GameplayCommands.Upgrade` | — |
| Butchering | an explicit attack on an own cow | `CombatSystem`, `GameplayCommands.Attack` | — |
| Contested capture | the first digging player keeps the progress while digging | `CaptureSystem` | UNK-19 |
| Captured building | loses its queue and rally point | `World.ChangeOwner` | — |
| Rally default action | harvest the target node / help build the site / attack the enemy, else move | `ProductionSystem` | — |
| Gates for enemies | anyone passes an open gate (per-player passability is a pathfinding follow-up) | `StructureSystem` | — |
| Bridge deck | land locomotors cross, ships do not; a site carries nobody | `MapState` | — |
