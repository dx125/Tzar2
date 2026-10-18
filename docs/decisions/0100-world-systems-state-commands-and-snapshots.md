# 0100. Keep the world systems in their own state, stores, commands and rules section

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 stream F)
* **Related:** TDD §8.5, §8.6, §8.7, §10 GAME-12…17, GAME-19, §11.2, REQ-SIM-11…13, ADRs 0016, 0019, 0045, 0101-0107

## Context

Stream F adds fog and visibility, diplomacy, trade and tavern, gateways and Nature, the day clock and weather, victory
and scoring, and cheats. Stream E (combat, heroes, holders, items, magic) works on the same files at the same time, so
the additions must be easy to merge, and every new piece of state must be in snapshots, hashes and the restore
validation (ADR 0019).

## Decision

* **Rules.** A ruleset-wide `world:` section (field-by-field layering like `gameplay:`) compiles into
  `Ruleset.World` (`WorldSettings`); entity types get `EntityTraits.World` (`WorldTraits`) from the traits `Trader`,
  `TradeDock`, `Tavern`, `ClaimOnTouch`, `Animal` and `Detector`, plus the derived `IsMilitary` (a unit with a weapon that
  is neither a worker nor a trader). `Ruleset` and `EntityCompiler` are `partial`; the additions live in
  `Ruleset.World.cs`, `EntityCompiler.World.cs` and `WorldReader.cs`. Ruleset hash version 5.
* **State.** `World` is `partial`; `World.Kingdoms.cs` holds `Diplomacy`, `Visibility`, the per-kingdom
  `KingdomState` array (statistics, loan, tribute timer, cheat switches, outcome tick) and two hot stores appended last:
  `world.trade` (`TradeRoute`) and `world.home` (`NatureHome`). `PlayerFlags` gain `Victorious` and `Resigned`. The day
  clock and the weather are pure functions of the tick and the match seed (no state).
* **Config.** `SimulationConfig` gains No Fog, Explore, the victory condition (default `NotSpecified`: a sandbox; the
  skirmish lobby sets Standard), game type, score and time limits, day/night and weather switches, teams (4 bits per
  slot) and `Ranked` (never with cheats). All are in the snapshot header.
* **Pipeline.** Step 5 `TradeSystem`, step 6 `NatureSystem`, step 14 `VisibilitySystem`, step 15 `ClockSystem`
  (events only), step 16 `VictorySystem`.
* **Commands** (wire values appended; stream E uses 20-39): `SetDiplomacy` 40, `Tribute` 41, `Resign` 42, `Trade` 43,
  `Convert` 44, `Gamble` 45, `Loan` 46, `Cheat` 0xF010. Rejections `DiplomacyBusy` 30, `TributeBusy` 31, `NotAllied` 32,
  `LoanOutstanding` 33, `NoLoan` 34. Events 40-52 (diplomacy, tribute, trade, conversion, gambling, loans, claims, day
  phase, weather, defeat, victory, cheats). `Killed` events now carry the killer's owner in Y.
* **Snapshots and hashes.** Format 6: after the path queue, the kingdom states (raw), diplomacy (raw) and visibility
  (explored bitsets as runs of equal words, the sight stamps, temporary reveals, last-seen records); counts and the
  combined bitsets are rebuilt from the stamps. The tick hash adds the kingdom states and diplomacy; the full hash adds
  visibility (capacity-independent). Every value is range-checked on restore (stances, offers, change ticks, kingdom
  fields, cheat flags only when cheats are allowed, stamps naming slots below the high-water mark, records of other
  kingdoms' structures, trade routes on traders between two different docks, homes on Nature's roaming units).
  Layout version 5.
* **Hooks into shared code** (one line each, so stream E's restructuring stays mergeable): `CombatSystem` asks
  `World.MayAttack` in `IsEnemyTarget` and `Acquire` and skips concealed candidates; `Hit` honours `hmgod`; `ResearchRules`
  pays through the shared stockpile and honours `hmnotech`; `ProductionSystem` honours `hmbuildozer`; `MovementSystem`
  refreshes and applies gate bars; `StructureSystem` opens gates for allies; `GameplayCommands.ClearTasks` ends trade
  routes; `World.IsHeld` and `World.IsConcealed` are placeholders that stream E wires to its holders and cloaking.

## Alternatives considered

* **Fields in `gameplay:` and `GameplaySettings`:** fewer files, but every stream edits the same record and reader.
* **The extension blocks of PR #20 (`WorldStateExtensions`):** meant for controllers outside Tzar2.Sim (AI, scripts);
  world systems are part of the sim's own validated state.

## Consequences

* Streams E and F meet in a handful of lines; the enum ranges are agreed.
* The client and AI read `World.Visibility`, `World.Diplomacy` and `World.Kingdoms`; they issue the new commands.
