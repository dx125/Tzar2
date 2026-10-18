# 0096. Append the GAME-07…11 commands, events and stores; validate them on restore; cover them in the goldens

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (M3 stream E)
* **Related:** TDD §8.5, §8.6, §8.7, §21, §22.2, ORIG-11, ADRs 0016, 0019, 0039, 0045, 0090-0095

## Context

Stream E adds orders (board, unload, items, spells, stances, attack ground, conversions, bribes, crusades, crafting),
state that snapshots must carry and validate, and systems in steps 6, 9, 10, 11 and 13. A concurrent stream (GAME-12…19)
appends to the same enums and stores.

## Decision

* **Commands** (wire values 20-39, agreed with the other stream): `Board` 20, `Unload` 21, `PickItem` 22, `DropItem` 23,
  `UseItem` 24, `GiveItem` 25, `SetDefaultItem` 26, `CastSpell` 27, `SetStance` 28, `AttackGround` 29, `ConvertUnit` 30,
  `Bribe` 31, `PreachCrusade` 32, `CraftItem` 33. Rejections 15-29: `HolderFull` 15, `NotAccepted` 16, `InventoryFull`
  17, `NotEnoughMana` 18, `SpellNotReady` 19, `SpellUnknown` 20, `InvalidTarget` 21, `CrusadeActive` 22. Events 24-39:
  `ProjectileLaunched`, `LevelUp`, `HeroPromoted`, `Boarded`, `Unloaded`, `ItemPicked`, `ItemDropped`, `ItemUsed`,
  `SpellCast`, `SpellEffect`, `Summoned`, `CrusadePreached`, `Bribed`, `UnitConverted`, `ItemCrafted`, `DamageShared`.
  `Killed.Y` = the killer's owner. A new order to a unit ends its errand and spell in progress (a conversion is
  refunded). Units inside holders, and frozen or stone units, take no orders but Delete.
* **Stores** (appended after stream A's): `game.experience`, `game.held`, `game.holder`, `game.errand`,
  `game.inventory`, `game.item`, `game.projectile`, `game.area`, `game.status`, `game.caster`, `game.stance`,
  `game.craft`, `game.crusade`, `game.crusader`; `CombatMode.Ground` (attack ground). Every value is range-checked on
  restore and each store requires its trait; cross-checks: held units have no movement or occupancy, stand where their
  holder stands and belong to its owner; holder counts equal the held units; inventories and held items agree both
  ways with unique slots; projectiles and area effects have no identity and lie on the map; casters cast only spells of
  their card or of a book they carry; pending experience is 0 between ticks; a hero is at least at the hero level.
* **Systems**: `ErrandSystem` (step 6), projectiles in `CombatSystem` (step 9), `MagicSystem` (step 10, after mana
  regeneration), `ExperienceSystem` (step 11), `HolderSystem` (step 13). All iterate stores in ascending entity order;
  random rolls use the `combat` stream in hit order; no system keeps state across ticks.
* **Goldens and benchmark**: the gameplay scenario gives every base a mage, a priest and a catapult and puts weapons
  and gold at the center; the bot researches Learning first, garrisons four soldiers every 600 ticks and lets them out
  300 ticks later, casts lightning at enemies and heals friends, bombards the center (attack ground) and picks items
  up; archers, towers and siege fire projectiles. Re-pinned on top of stream F (reasons in the tests): gameplay
  ruleset `CDF88159DCB8216C`, stream `14CA89CFE290A0DE`, final `259C6906C08AB4A3`; CI gameplay bench `4F22CD50EF776506`;
  synthetic movement `B12B61103AA6CEB6` / `806340E79AAFCB5C` (layout only). Snapshot format 8, hashed layout 6, ruleset
  hash version 6 (after stream F's 6 / 5 / 5 and the legacy AI's format 7). On top of the legacy AI (ADR 0051) its
  military executor skips units inside holders and invisible units the AI's kingdom does not detect, so it never aims at
  what its player could not see; AI golden commands `D94B60AC85254137`, stream `4D4B6AC077327910`, final
  `9A285B0A5C97566B`; CI AI bench `BCF3170D175046F4` (combat and magic change the games the AIs play).

Measured (Apple M5, macOS arm64, Release, .NET 10, shared machine with other agents, ±30%):

| Scenario | Result | Budget |
|---|---|---|
| gameplay, 8 kingdoms (nightly gate), 4 / 1 workers | 4,198 entities at the end; 7,816 projectiles, 1,219 kills, 276 level-ups; p99 2.49 / 3.91 ms, max 8.2 / 20.7 ms | p99 ≤ 12 ms |
| gameplay, 8 kingdoms × 900 soldiers, 4 / 1 workers | 22,331 projectiles, 2,860 kills; p99 6.84 / 10.75 ms, max 15.5 / 44.8 ms | same |
| synthetic 4,000 units / 512², churn 2, 4 workers | p50 3.63 ms, p99 6.96 ms | same |
| allocation gate (Release, gameplay and synthetic, 1 and 4 workers) | 0 B per tick on the sim thread | 0 B |

## Consequences

* The AI (stream B) and the client (stream D) can emit and show these orders and events; the codec is unchanged.
* A 900-soldier battle with one worker gets close to the budget (projectile flight and splash scans are sequential):
  a candidate for the sim-performance stream.
