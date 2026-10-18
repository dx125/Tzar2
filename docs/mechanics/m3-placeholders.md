# M3 gameplay placeholders, streams A, E and F: GAME-01 … GAME-19 (UNVERIFIED)

Status: **open**. Register of every gameplay value and rule of M3 stream A (ADRs 0040-0045) that is neither in an
original data table nor measured in the running game. Owner decision: no captures from the running original yet; take
what the INI/TSV tables and the manuals say; give everything else an explicit placeholder marked `UNVERIFIED` with its
unknown, to be replaced by capture facts (`t2 fidelity`, ADR 0030; RE-05). Do not tune these for balance: replace them
with measurements. The M2 register ([m2-placeholders.md](m2-placeholders.md)) still holds for the values it lists.

Where the value lives: **YAML** = `content/engine/rules/classic.yaml` (marked `UNVERIFIED` in a comment; override it
like any rule), **code** = a rule of a sim system (named in the ADR; changing it needs a capture fact and an ADR
update), **import** = how the importer reads the data (a hypothesis about a column or key).

## First capture (2026-10-18, session `capture-20261018-024524`, ADR 0111)

None of the values below was in the session's scope (it measured rendering, speeds and attack timing; see
[m2-placeholders.md](m2-placeholders.md)), so all stay UNVERIFIED. Two rows gained data:

* **Cheat codes (UNK-30):** the full list of code names from the executable's strings is now in
  [original-switches-and-cheats.md](original-switches-and-cheats.md). Only `hmreveal` and `hmnofog` were observed
  working; `hmspawn` and `hmkingdom` did not behave as scripted (see there). The resource amounts of `hmdvaleva` /
  `hmpetleva` and the semantics of `hmgod`, `hmbuildozer`, `hmnotech`, `hmnext` stay UNVERIFIED.
* **Day length:** not captured (the session switches day/night off for clean colours); needs its own scenario.

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
| Repair crews | each further repairer adds 25 points less (100%, 75%, 50%, 25%, then 0; owner decision) | YAML `gameplay.repairerFalloffPercent` | UNK-18 | repair captures with 1-8 peasants |
| Repair cost | a full repair costs 50% of the construction cost, pro rata; pauses when unpaid (owner decision; the strategy guide only says repairing under attack works and repairers get hurt) | YAML `gameplay.repairCostPercent` | UNK-18 | stockpile during a repair |
| Fire threshold | below 50% of the hit points | YAML `gameplay.burnThresholdPercent` | UNK-42 | damage a building step by step |
| Fire damage | 60 HP per minute | YAML `gameplay.burnDamagePerMinute` | UNK-42 | time a burning building |
| Burning types | every building but fields, walls, gates and bridges | YAML (`Burnable`) | UNK-42 | observation |
| Ruin lifetime | the corpse decal lifetime (120 s) | code (`World.Kill`) | — | observation |
| Wall drag | at most 64 segments; builders move on within 6 tiles | YAML `gameplay.maxWallSegments`, `wallContinueRadiusTiles` | — | editor/game captures |
| Gate footprint | 3 × 1 with the middle tile as the passage | YAML `^class.gate_*` | UNK-02 | editor placement |
| Gate opening | automatic by default; opens for the owner's units within 32 px, closes when none is within 64 px | YAML `gameplay.gateOpenReachPx`, `gateCloseReachPx` | — | observation |
| Gate change rate | at most one change per gate per second; at most 4 changes per tick (owner decision: bounded work; 16 cost 15 ms p99 under 200-gate spam) | YAML `gameplay.gateChangeIntervalMs`, `maxGateChangesPerTick` | — | observation |
| Bridge footprint | the sprite box (`bridge_horz`), ends on land | import + code | UNK-02 | editor placement |
| Harbor water | within 2 tiles of the footprint | code (`GameplayCommands.CoastReachTiles`) | UNK-02 | editor placement |
| Shore reach | +1 tile for boats at buildings and peasants at ships | code (`Interaction.WorkerReaches`) | UNK-11 | captures (and the terrain-set conversion) |
| Building damage scale | damage × 1000 / (1000 + RESISTANCE) (was 100 in M2: castles were practically immune to melee; owner decision on PR #21) | YAML `gameplay.resistanceScale` | UNK-08 | damage grid on buildings |
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
| Costs after a technology | refunds return what was paid (the cost modifier is stored per queue slot and site) | `ResearchRules` | — |
| Conversion keeps damage | the new type's maximum minus the damage taken | `World.ConvertType` | — |
| One conversion at a time | per entity | `GameplayCommands.Upgrade` | — |
| Butchering | an explicit attack on an own cow | `CombatSystem`, `GameplayCommands.Attack` | — |
| Contested capture | the first digging player keeps the progress while digging | `CaptureSystem` | UNK-19 |
| Captured building | loses its queue and rally point | `World.ChangeOwner` | — |
| Rally default action | harvest the target node / help build the site / attack the enemy, else move | `ProductionSystem` | — |
| Gates for enemies | ~~anyone passes an open gate~~ — stream F: only the owner and the kingdoms it treats as allies (a movement bar, ADR 0102; planning still treats an open gate as open) | `World.GateBars`, `MovementSystem` | — |
| Bridge deck | land locomotors cross, ships do not; a site carries nobody | `MapState` | — |

# Stream F: GAME-12 … GAME-17, GAME-19 (UNVERIFIED)

ADRs 0100-0107. The same rules as above: **YAML** = the `world:` section or a trait in `content/engine/rules/classic.yaml`,
**code** = a rule of a sim system. From the data (not placeholders): which classes are gateways, markets, harbors,
taverns, caravans and trading ships (OBJECTS, MENU.INI, manual ch. 5); the gateway's summons (COMM UNITS producer "Gate",
MENU.INI `[magic_gateway]`); rain and snow per terrain set (`<SET>.TERRAIN.INI`); the 1:10 conversion and the loan of 1,000
then 1,200 (manual ch. 5 §7, §10); victory conditions, game types, No Fog, Explore (STRINGS.INI, manuals).

## Values

| Value | Placeholder | Where | Unknown | How to resolve |
|---|---|---|---|---|
| Sight shape | a tile disk dx² + dy² ≤ r² + r around the unit's tile; r = round((sight + half the footprint) / tile) | code (`Visibility`) | UNK-27 | fog edge captures around a lone unit |
| Sight without a table value | 2 tiles | YAML `world.defaultSightTiles` | — | captures |
| Night sight | 100% (night does not change sight) | YAML `world.nightSightPercent` | UNK-09 | sight captures at night |
| Last-seen refresh | every 4 ticks per structure (a glimpse shorter than that may be missed) | YAML `world.memoryRefreshTicks` | — | engine bound |
| Construction sites reveal | like the finished building | code (`Visibility`) | — | observation |
| Detector radius (spies, ninjas) | 5 tiles | YAML `^class.spy/ninja.Detector` | — | detection captures (stream E cloaking) |
| Stance change interval | 10 s per pair (anti flip-flop) | YAML `world.diplomacyChangeIntervalMs` | — | engine bound (owner decision welcome) |
| Tribute | once per second, ≤ 100,000, no fee | YAML `world.tributeIntervalMs`, `maxTributeAmount` | — | tribute dialog captures (fee?) |
| Stockpile cap | 10⁹ (overflow guard of world-system incomes) | YAML `world.stockpileCap` | — | engine bound |
| Teams | start as mutual allies sharing vision and victory | code (`Diplomacy.ApplyTeams`) | — | lobby captures |
| Shared resources | the payer's stockpile first, then allies in slot order | code (`SharedStockpile`) | — | observation |
| Shared unit control | movement, attack and trade orders only | code (`CommandSystem`, `GameplayCommands.Attack`) | — | observation |
| Trade load | caravan 100, galleon 200, hulk 150, xebec 100 | YAML `^class.*.Trader.capacity` | UNK-28 | trade captures |
| Trade profit | 1% of the load per tile between the docks (straight line), none below 8 tiles, at most 300% | YAML `world.trade*` | UNK-28 | caravan income vs. distance captures |
| Trade dock time | 2 s | YAML `world.tradeDockMs` | UNK-28 | captures |
| Unaffordable load | the trader leaves empty | code (`TradeSystem`) | — | observation |
| Gambling | odds 2, 3, 5, 10; win chance (1 − 10%) / odds; stake ≤ 10,000 | YAML `world.gamble*` | UNK-28 | gamble dialog captures |
| Loans | no interest; the raise to the next limit comes 120 s after a repayment; only whole multiples convert | YAML `world.loan*` | UNK-28 | loan captures |
| Claim reach | 16 px edge to edge, checked every 0.5 s | YAML `world.claimReachPx`, `claimIntervalMs` | — | gateway captures |
| Claiming from another kingdom | not possible: only Nature-owned gateways and cows are claimed | code (`NatureSystem`) | — | observation |
| Wild cows | claimed by any kingdom's unit touching them; they wander | YAML `^class.cow` | UNK-17 | observation |
| Bears | wander 6 tiles, guard 10 tiles from home | YAML `world.wanderRadiusTiles`, `guardRadiusTiles` | — | observation |
| Birds and eagles | air, SPEED 5/6, 10 HP, never targets | YAML `comm.bird`, `comm.eagle` | — | observation |
| Day length | 12 min; the match starts at 10:00; night 19:00-06:00 | YAML `world.day*`, `night*` | UNK-32 | clock globe captures (not in the first session: day/night was off) |
| Weather | 3-minute periods, 25% with weather | YAML `world.weather*` | UNK-09 | captures |
| Score | gathered/10 + killed value/10 + built value/10 + 100 per technology | YAML `world.score*` | UNK-29 | F4 captures |
| Defeat | Standard: no unit and no building (walls, gates, bridges, fields excluded); Destroy Castles: no castle | code (`VictorySystem`) | — | end-of-game captures |
| Time limit tie | the team with the lower slot | code (`VictorySystem`) | — | — |
| Deathmatch | every kingdom starts on the High resources | YAML `world.deathmatchStartingResources` | — | lobby captures |
| Cheat resources | hmdvaleva 2,000, hmpetleva 5,000 of each | YAML `world.cheat*` | UNK-30 | testing the codes (names confirmed in the executable, [list](original-switches-and-cheats.md); amounts not measured) |
| Cheat semantics | hmgod: no damage to own units and buildings; hmbuildozer: instant; hmnotech: no requirements; hmnext: win | code (`KingdomCommands.Cheat`) | UNK-30 | testing the codes |
| Tavern offers refresh | every 2 minutes, one set per kingdom (seeded) | YAML `world.tavernRefreshMs` | UNK-28 | tavern menu captures over time |
| Tavern offers | 3 mercenary groups and 1 hero at a time | YAML `world.mercenaryOffers`, `heroOffers` | UNK-28 | tavern menu captures |
| Mercenary group size | 3-5 soldiers ("small groups", manual ch. 6) | YAML `world.mercenaryGroupMin/Max` | UNK-28 | tavern menu captures |
| Mercenary and hero types | military land units of the kingdom's civilization that a building trains | code (`World.MercenaryPool`) | UNK-28 | tavern menu captures (all civilizations) |
| Mercenary price | 150% of the soldiers' training cost; heroes 600% of their type's | YAML `world.mercenaryCostPercent`, `heroCostPercent` | UNK-28 | price captures |
| Mercenary level | 1; heroes at the hero level (12) | YAML `world.mercenaryLevel`, `combat.heroLevel` | UNK-23 | level of hired units |
| Fame | +1 soldier, +2 levels, 75% of the price | YAML `world.fame*` | UNK-23/28 | captures with and without Fame |
| Hired units' level cap | the tavern level stands even above the kingdom's cap | code (`TavernOffers.Train`) | UNK-23 | observation |
| Hire refused | a hire needs population room for the whole group | code (`TavernOffers.Hire`) | UNK-20 | observation |

## Rules in code

| Rule | Placeholder | System | Unknown |
|---|---|---|---|
| Allied victory | a team = kingdoms that are mutual allies and both offer shared victory | `VictorySystem` | — |
| Defeated kingdoms | their commands are refused, their entities stay | `CommandSystem`, `VictorySystem` | — |
| Explicit attacks | follow the stance like automatic ones (allies never; neutrals' military only) | `GameplayCommands.Attack` | — |
| Gateways and wild cows | never combat targets while Nature owns them | `World.MayAttack` | — |

## Backlog (owner decisions on PR #23)

* **King of the Hill, Capture the Flag** (lobby strings `g_type_1`, `g_type_2`): out of M3 scope; TDD GAME-17 names
  Standard and Deathmatch only. No code.
* ~~**Tavern mercenaries and heroes:** a follow-up once streams E and F have both landed~~ — done: ADR 0108 (values in
  the table above). The legacy AI scripts never hire (they only build taverns), so the AI does not use `Hire`.
* **Per-kingdom gate routing** (REQ-PATH-01 per-player portals): backlog; the movement bar of ADR 0102 is enough for M3.
* **Confirmed defaults:** lobby teams start with shared victory; the 10 s stance-change interval stays (engine bound,
  UNVERIFIED).
# Stream E: GAME-07 … GAME-11 (combat, experience, holders, items, magic; ADRs 0090-0096)

From the data (not placeholders): holder capacities, `[accept]` and `[damage]` shares (`max_obj_in_holder`: castle,
outpost 50; galleon 24, hulk 16, xebec 8; carpet, caravan 8 — the data wins over the manual's "eight units" per ship);
inventory sizes (soldiers 8); `min_attack_range` (catapults 4, cannon tower 2, rocket tower 3); `max_damage` (towers);
item classes and who carries them (`[enter]`); the spells on each caster's command card; the technology of each spell
per civilization (`TECHTREE [orders]`); summons' mana and resource costs (`m` suffix); the crusade anchor's cost and
the revealers' `fog_range`; hero names (`HEROES.INI`). From the manual: weapons add attack, armor defence; +1 attack and
+½ defence per experience level; Learning, Heroism, Leadership, Honor, Body Building, Philosophy, Mastery, Medicine,
Inquisition do what their texts say (not how much); fire rain hits own units; whirlpools destroy ships. From the
strategy guide: no experience before Learning; each level costs more; garrisons heal "quickly" and take a little damage
when the building is hit; catapult stones can be stepped away from.

## Values

| Value | Placeholder | Where | Unknown | How to resolve |
|---|---|---|---|---|
| Damage classes | ranged arrow, melee sword; cannon: catapults, heavy catapults, cannon and rocket towers, galleons, hulks | YAML `Armament.damage` | UNK-22 | holder damage captures |
| Projectile per shooter | arrow (archers, longbows, horse archers, wood/guard/keep towers, xebecs, fishing boats), bolt (ballista), stone (catapults), cannon (cannon tower, galleon, hulk), rocket arrow, magic (mage, priest), flame ball (dragon) | YAML `^class.*.Armament.projectile` | UNK-22 | captures |
| Projectile speeds | arrow 400, bolt 480, stone 200, cannon 320, rocket 360, magic/bolts 320, flame ball 280 px/s | YAML `combat.projectiles` | UNK-22 | frame-by-frame captures |
| Homing | arrows, bolts, rockets, magic home; stones and cannon balls land where the target stood | YAML | UNK-22 | dodge captures |
| Hit radius of ground shots | stone 24 px, cannon 20 px, others 16 px | YAML | UNK-22 | captures |
| Splash | stone 32 px 50%, cannon 24 px 50%, rocket arrow and flame ball 16 px 50%; no friendly fire | YAML | UNK-22 | splash captures |
| Damage roll | a hit rolls in [attack, `max_damage`] (⚠ HYPOTHESIS: what `max_damage` means) | code (`CombatSystem`) | UNK-08 | tower damage grid |
| Missing RATE | priests (euro, arab) 2, Jihad warriors 3, armored chariots 3 | YAML | UNK-07 | attack timing captures |
| Carpet speed / radius | SPEED 5, 12 px (no SPEED in the table) | YAML `^class.carpet` | UNK-12, UNK-11 | captures |
| Experience per damage | 100% (1 point per point of damage dealt to an enemy) | YAML `combat.experiencePerDamagePercent` | UNK-23 | XP captures |
| Experience per kill | 0 | YAML `combat.experiencePerKill` | UNK-23 | same |
| Level curve | level n at 100 × n × (n + 1) / 2 | YAML `combat.levelBaseXp` | UNK-23 | same |
| Level cap | 0 without Learning; Learning +4, Heroism +1,000 (stream A values) | YAML `combat.baseLevelCap`, `te_*` | UNK-23 | same |
| Defence per level | ½, truncated (1 per 2 levels) | YAML `combat.defenceHalvesPerLevel` | UNK-08 | same |
| Hero level | 12 (TDD) | YAML `combat.heroLevel` | — | — |
| Leadership | 50% of a hero's gain to own units within 4 tiles | YAML `combat.leadership*` | UNK-23 | captures |
| Honor | +25% attack of heroes against heroes | YAML `combat.honorAttackPercent` | UNK-23 | captures |
| Dummies | teach up to level 3; take no damage | YAML `combat.dummyMaxLevel` | UNK-23 | captures |
| Hero names when the list is used up | "<first name> 2", "… 3" | code (`ExperienceSystem`) | — | observation |
| Holder healing | castle, outpost 120 HP per minute per unit | YAML `^class.castle/outpost.Holder.healPerMinute` | UNK-24 | garrison captures |
| Exit radius | 3 tiles | YAML `combat.exitRadiusTiles` | — | observation |
| Item defaults | weapon +3, armor +2, potion 100 HP, plusplus +1 level (⚠ HYPOTHESIS), fire-rain book 3 charges / 10 s, soul (murder, once), gold item 100 | YAML `comm.item*` | UNK-25 | editor defaults, captures |
| Crafting | 50 gold per power point, 2 s per point, at most 20 | YAML `combat.craft*` | UNK-25 | workshop captures |
| Spell magnitudes | per spell (amount, mana, range, cooldown, cast time, duration, radius, pulse) | YAML `spell_*`, `raisedead`, `cloak`, `murder` | UNK-26 | spell captures |
| Spell damage | no defence, no bonus | code (`MagicSystem`) | UNK-26 | captures |
| Summon mana above the pool | costs at most the caster's whole pool (golem 600m, dragon 800m vs `max_manna` 100; ⚠ HYPOTHESIS) | code (`MagicSystem.ManaCost`) | UNK-26 | summon captures |
| Medicine | +4 tiles of spell range for priests | YAML `te_medicine` | UNK-26 | captures |
| Inquisition | +50% attack of priests (exorcism) | YAML `te_inquisition` | UNK-26 | captures |
| Crusade | 5 crusaders per crusade, 60 s cooldown after the last died (⚠ HYPOTHESIS: how crusaders are raised) | YAML `combat.crusade*` | UNK-26 | crusade captures |
| Bribe price | 200% of the unit's cost, in gold | YAML `combat.bribeCostPercent` | UNK-26 | bribe captures |
| Magic vision | the revealer lasts 30 s | YAML `comm.fog_revealer.Lifetime` | UNK-26 | captures |

## Rules in code

| Rule | Placeholder | System | Unknown |
|---|---|---|---|
| Minimum range | no swing inside it; explicit targets wait (no backing off) | `CombatSystem` | UNK-22 |
| Attack ground | full hit within the hit radius of the point, splash share beyond | `CombatSystem` | UNK-22 |
| Stand ground / hold fire | no chasing / no fight started and no retaliation | `CombatSystem` | — |
| Line of sight, height, formations | none | — | — |
| Holder share split | evenly; the first units by index take the remainder | `CombatSystem` | UNK-24 |
| Holder destroyed | units take free tiles; none free (at sea): they die | `World` | — |
| Holder captured | the previous owner's units leave | `World` | — |
| Boarding | room and acceptance re-checked on arrival; the late ones give up | `ErrandSystem` | — |
| Pick-up race | the first unit to arrive (errand order) gets the item | `ErrandSystem` | — |
| Casting | pay on effect; one cooldown per caster; an interrupted cast is free | `MagicSystem` | UNK-26 |
| Confusion | the unit takes everyone as an enemy and everyone takes it as one | `CombatSystem` | UNK-26 |
| Cloak | ends when the unit attacks or casts | `CombatSystem`, `MagicSystem` | — |
| Murder, souls | heroes and `KillImmune` types are immune | `MagicSystem` | UNK-26 |
| Conversion | hit points in proportion; level, items and name carry over; a new order refunds it, death does not (the price is lost) | `CombatMagicCommands`, `World` | — |
| Bribe | not peasants, crusaders or heroes | `CombatMagicCommands` | — |
| Not implemented | `item_berserk`, Tournaments, ships entering harbors, spies' impersonation (visibility stream) | — | — |
