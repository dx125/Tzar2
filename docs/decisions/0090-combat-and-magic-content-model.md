# 0090. Model combat, experience, holders, items and magic as traits, a `combat:` section and spell entries

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** product owner (data source, placeholders), implementation agent (M3 stream E)
* **Related:** TDD §8.4, §10 GAME-07…11, §11.2, REQ-SIM-09, REQ-DATA-04/05/06, ADRs 0031-0033, 0040, 0041,
  docs/mechanics/m3-placeholders.md (stream E)

## Context

GAME-07…11 need data the M2/M3-A model lacks: projectiles, minimum ranges, holder capacities and damage shares, item
inventories, items, spells per caster, experience, conversions, crafting, crusades. The original keeps most of it in
the object INIs (`OBJECTS/*.INI`: `max_obj_in_holder`, `[accept]`, `[damage]`, `[enter]`, `min_attack_range`,
`max_damage`, `max_manna`, `menu`), the command cards (`MENU.INI` → `ORDERS.INI`: `ACTION = spell spell_x`), the
technology trees (`TECHTREE [orders]`: which technology unlocks a spell per civilization) and `HEROES.INI`; spell and
item magnitudes, experience numbers and projectile speeds are in no table (UNK-22…26). Another stream (m3f-world,
GAME-12…19) edits the same shared files concurrently.

## Decision

* **Traits** (EntityTraits is `partial`; `EntityTraits.Combat.cs`): `Holder {capacity, accepts, damage, healPerMinute}`,
  `Inventory {slots}`, `Item {kind, holders, power, resource, spell, charges, cooldownMs, needsMana, level, attack,
  defence, health, mana}`, `Spellcaster {spells}`, `Experience`, `Briber`, `BribeImmune`, `CrusadePreacher`,
  `TrainingDummy`, `KillImmune`, `ConvertsFrom {classes, order}`, `ItemCrafter {items}`; `Armament` gains `minRange`
  (tiles), `maxAttack` and `projectile`. Unit filters (`accepts`, `holders`, `classes`) are resolved after every type
  is compiled: a keyword of `combat.unitKeywords` (`allunits`, `earthunits`, `infantryunits`, `mountedunits` → the
  locomotors they mean), a class (of the holder's civilization or the common one) or an entity key.
* **Section `combat:`** (merged field by field like `gameplay:`): experience curve and cap, hero level, Leadership,
  Honor, dummies, holder exits, crafting prices, bribes, crusades, hero names (`legacy` = `HEROES.INI`), unit keywords
  and the projectiles (`{pixelsPerSecond, homing, hitRadiusPx, splashRadiusPx, splashPercent, friendlyFire}`).
  Compiled into `Ruleset.Combat` (`CombatSettings`) and `Ruleset.Projectiles`.
* **Spells are rules entries with a `Spell` trait** (own ids: sorted keys, like technologies), keyed by the order of the
  original (`spell_lightning`, `raisedead`, `cloak`, `murder`), declared in the YAML (`new: true`): the data only names
  them. Which caster has which spell comes from its command card (orders with `ACTION = spell x`, `raisedead`,
  `cloak`, `murder`); a carded spell the rules do not define is left out with a diagnostic, so a modded install never
  fails to compile. Which technology unlocks it per civilization is `Ruleset.OrderTechnology` (ADR 0040).
* **Import** (`LegacyImporter.Combat.cs`): an object with a non-empty `[accept]` and a `max_obj_in_holder` is a
  holder with that capacity and its `[damage]` shares (`00 = 90,10`: % taken, % spread over the units inside; default
  100,0); a unit with `max_obj_in_holder` and no `[accept]` has an inventory of that many slots (soldiers: 8). The data
  wins over the manual where they differ (galleon 24, hulk 16, xebec 8 places; the manual says ships "load up to eight
  units"; source priority §0.3). `legacy.items` maps the item classes to kinds (their `[enter]` lists say who may
  carry them); `legacy.objects` imports objects outside the tables with their `cost` and `fog_range` (crusade anchor,
  magic vision's revealer). Table rows with an ATTACK but no RATE (priests, Jihad warriors, armored chariots) get an
  armament only when the rules give a rate (UNVERIFIED placeholders), instead of a broken one.
* **Separation from the concurrent stream:** the shared classes are `partial` (`Ruleset`, `EntityTraits`,
  `EntityCompiler`, `LegacyImporter`, `World`, `GameplayScenario`); the shared files get one trailing constructor
  parameter (`combat:`), one hash call, one section name and single-line hooks. Wire ranges were agreed with m3f-world:
  commands 20-39, events 24-39, rejections 15-29 (theirs: 40-59, 40-59, 30-39); ADRs 0090-0099 (theirs 0100-0109).

## Alternatives considered

* **Spells imported from `ORDERS.INI` as declared entries:** a test that replaces ORDERS.INI (and any modded install
  without some order) made every YAML patch of a missing spell fail the whole ruleset.
* **One `Holder` trait for units and items:** soldiers would "hold" items with the same capacity field; two traits keep
  the item rules (`[enter]` of the item) apart from the unit rules (`[accept]` of the holder).
* **Magnitudes re-typed from community sources:** no table holds them; clearly marked placeholders are safer
  (owner decision for M2/M3).

## Consequences

* `Ruleset.HashVersion` 5; `legacy.referenceDataHash` re-pinned (the imported layer gained holders, inventories,
  ranges and armaments without RATE).
* Every magnitude is an `UNVERIFIED` YAML value listed in docs/mechanics/m3-placeholders.md (stream E).
