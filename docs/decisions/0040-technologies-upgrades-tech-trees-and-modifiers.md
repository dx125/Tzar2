# 0040. Import technologies, upgrades and per-civ trees from the tables; apply effects as per-player modifiers

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** product owner (data source, placeholders), implementation agent (M3 stream A)
* **Related:** TDD §8.5 step 4, §10 GAME-05/06, §11.2, REQ-SIM-09, REQ-DATA-05/06, UNK-21, ADRs 0031-0033, 0037,
  docs/mechanics/m3-placeholders.md

## Context

GAME-06: 87 technologies and 12 upgrades with per-civ trees and dependencies (`TECHNOLOGIES.INI`, `UPGRADES.INI`,
`TECHTREE <civ>.INI`). The tables hold names, research times and costs; the trees hold prerequisites per civilization
(`[depend]` technology or unit → technology, `[required]` → building, `[orders]` order → technology). Which building
researches what is not in a table of its own: it is the building's command card — `OBJECTS/<class>.INI [civ] menu` →
`MENU.INI [menu]` (keys or bare lines; `SUBMENU` orders nest) → `ORDERS.INI [order] ACTION = TECHNOLOGY`/`UPGRADE x`.
The *effects* are hard-coded in the original (§3.3); the manual (ch. 6) says only what each technology does. Owner
decision: values from the tables; anything only measurable in the running game is an `UNVERIFIED` placeholder.

## Decision

* **Import (`LegacyImporter`).** Every row of every unit and building table (`units: all`, `buildings: all`; classes in
  both tables — towers — are buildings armed from the unit row), every technology and upgrade, the three trees, the
  command cards. A tree's `[depend]` entry for a class replaces the table's technology (the tables name some that do not
  exist, e.g. `te_domestication` for cows where the tree says `te_livestock`); a tree's `[required]` building replaces a
  table building that is not imported (Asian/Arabian churches "need a cathedral"). Unknown names compile into
  *unsatisfiable* requirements with a diagnostic, never silently dropped. Common units are trained in every
  civilization's building of the producer class. `UPGRADE <class>` converts into that class of the entity's own
  civilization (or the common one); `UPGRADE <itself>` is a level kept by the entity (ship cannons). The upgrade key is
  the order's `CLASSNAME` (`[to_swall]` buys `to_wall_stone`).
* **Rules.** Technologies and upgrades are entries like entity types (ADR 0031: templates, patches, `+=`), recognised
  by a `Technology` or `Upgrade` trait, with their own dense ids (sorted keys). Traits: `Legacy {class, name}`, `Cost`,
  `BuildTime {ms}`, `Requires`, `TechTree {<civ>: {technologies, buildings, orders}}`, `Technology {effects}`,
  `Upgrade {into, after, effects}`. Entities gain `Researcher {technologies, upgrades}` (the command card) and per-civ
  `TechTree` requirements. A template's `-Trait:` now passes on to entries inheriting it after a parent that adds the
  trait (fields and walls do not burn although `^Building` does).
* **Effects are modifiers.** `{ stat, add, percent, <filter> }` with selectors (`classes`, BONUS.INI `groups`,
  `types`; union) and conditions (`damage`, `category`, `locomotor`, `civ`; intersection) resolved to sorted type ids at
  compile time. `value = (base + Σadd) × (10,000 + Σbp) / 10,000`, truncated, ≥ 0. Stats (append-only enum): attack,
  defence, maxHealth, resistance, sight, range, gatherRate (types = the *node* types), carryCapacity, buildRate, cost,
  populationProvided, manaRegen, manaMax, and stats kept for later systems: holderCapacity (GAME-09), experienceGain,
  experienceCap, startingLevel, healthPerLevel (GAME-08), abilityPower, abilityRange (GAME-08/11), income (GAME-14).
  Every magnitude in `classic.yaml` is `UNVERIFIED (UNK-21)`; pure unlocks have no effect (their trees do it).
* **Sim state.** `PlayerState` gains a 256-bit `Technologies` set and `Civilization` (index into
  `Ruleset.Civilizations`: the first own object's civilization at match start, else the first non-common one;
  `World.SetCivilization` for the lobby). `PlayerStats` (derived, not hashed) holds per player × stat × type the summed
  adds and basis points, rebuilt when a research completes and after every restore. Systems read effective values:
  combat (attack, defence, resistance, range, sight), gathering (rate per node type, capacity), construction and repair
  (build rate), costs (training, construction; the cost modifier paid is stored with the queue slot or the site, so a refund returns
  what was paid), population (houses), maximum hit points
  (existing entities keep the damage they took), mana.
* **Requirements per civilization.** A requirement set applies when its civilization is the *actor's*: the builder,
  the producer or the researching building — its type's civilization, or its owner's for common types (towers, walls,
  ships). So a captured Asian blacksmith researches by the Asian tree.
* **Queues.** Research and upgrades use the production queue (ADR 0037): items below `0x4000` are unit types,
  `0x4000 | id` upgrades, `0x8000 | id` technologies (so at most 16,384 entity types). `Research` (command 12) queues
  in the first listed building that researches it and whose tree allows it; a technology is researched once per player
  and never queued twice anywhere (the 1.10 lagged-MP fix). `Upgrade` (13) queues per listed entity; one conversion at a
  time, levels in order (`after`). Both are paid when queued; `Cancel` refunds `researchRefundPercent` (UNVERIFIED
  100%). Completion: the technology bit, the modifier tables and the hit points; a conversion changes the type in place
  (the ruleset requires the same footprint size and the same stateful traits — gate, fire, capture, decay, mana,
  lifetime, bridge, resource, repair, workers — so every store stays valid; blocked tiles, armament, queue and levels
  follow; queued items the new type cannot hold are refunded what was paid); a level sets a bit in `UpgradeLevels`, whose effects apply to that entity only.
* **Order gates** (`TECHTREE [orders]`: spells, loans, bribes...) compile into `Ruleset.OrderGates`;
  `Ruleset.OrderTechnology(order, civ)` looks one up by binary search for the systems of GAME-11/14.

## Alternatives considered

* **Re-typed effect tables per technology in code:** no data path for mods, and the same unknown magnitudes.
* **A tech tree per player instead of per actor:** common buildings then need the owner's civilization anyway, and
  captured buildings would research by the wrong tree.
* **A separate research component:** the existing queue already has timing, cancel and refunds.

## Consequences

* With the reference build, 86 of 87 technologies have a research site (`te_ironplus` is on no command card) and 11
  of 12 upgrades (`to_convertres` is on none); `OriginRulesetTests` checks this.
* GAME-08/09/11/14 read their stats from `World.Stats` and the order gates from the ruleset; they add stats by
  appending to `ModifierStat`.
* Replacing a magnitude is a YAML change plus its capture fact (`t2 fidelity`, UNK-21).
