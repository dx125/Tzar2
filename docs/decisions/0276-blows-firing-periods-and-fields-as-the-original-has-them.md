# 0276. Deal blows, time towers and ships, and keep fields as the original does

* **Status:** Accepted
* **Date:** 2026-11-06
* **Deciders:** maintainer (Claude), at the owner's request after the tenth play-test
* **Related:** WAR-001, WAR-002, WAR-019, ECO-008, ECO-012, BLD-029, UNK-07, UNK-08, UNK-17; ADR 0038 (damage
  superseded), ADR 0042 (field decay superseded), ADR 0045 (resistanceScale withdrawn), ADR 0274; ReTzar
  `src/gameplay.rs`

## Context

The owner's tenth play-test (2026-11-06, the save Game 6, read locally only) found four faults in this batch:

1. **Merchants' Guild and market.** Neither could be built: "not available to this kingdom".
2. **Farmers off the middle.** Peasants now and then dug away from the middle of their fields.
3. **Damage of towers and ships.** Gun towers hurt ships far too much, as catapults would, and ships' cannons hurt
   buildings too much.
4. **Fields.** Fields left without peasants did not wither.

The causes:

* **The guild.** ORDERS.INI builds it with `BUILD TCGuild`, the only class the original names in capitals. Tzar2 looked
  types up case by case, so it found no guild. The market requires the guild (`[required] market = tcguild`), so it
  was barred as well.
* **The farmers.** A probe resumed Game 6 and sampled the farmers 1,547 times. All of them stood one tile off the
  middle: on the edge of the field next to the farm they brought food to. Tzar2 let a farmer that had stopped anywhere
  on its field dig there.
* **Blows.** ReTzar reads the original's damage routine (0x504900) as follows:
  * A blow deals A² / (A + D), at least 1.
  * A is the attack grown by its single BONUS.INI entry; when several entries match, the last one holds (0x499ff1).
  * Magic ignores the defence: mages, dragons, the Messiah and priests that shoot.
  * A holder takes its share of every blow from its `[damage]` table, whether anyone is inside or not; the units
    inside share the rest.
  * Buildings have no defence. Their RESISTANCE is how hard they are to capture (CAP-002), not how hard they are to
    hurt.

  Tzar2 had placeholders here:
  * It dealt attack × bonus − defence.
  * It cut a building's damage by 1000 / (1000 + RESISTANCE).
  * It summed bonuses. The castle is named twice in `buildings_no_towers_walls`, so ships and catapults got their
    bonus against it twice.
  * A holder took its share only with units inside.
* **Firing periods.** Towers and warships fire on a fixed period with no attack animation (0x4dba30, 0x4e8f00):

  | Shooter | Period |
  | --- | --- |
  | Arrow towers | 1000 ms |
  | Cannon and rocket towers | 3000 ms |
  | Galleon and hulk | 3000 ms, shortened by the cannon levels to 2400, 1900, 1500, 1200 and 800 ms |
  | Xebec | 1300 ms |

  Tzar2 timed them by 6000 / RATE (UNK-07), so ships fired every 1.5 s and cannon towers every 2 s.
* **Fields.** In the original (0x44d1e0, 0x44cc70, 0x44c880):
  * A field nobody has worked for 60 s loses 10 of its 100 hit points every 5 s and goes at 0.
  * Short of full health, it shows `field_grow` at row health × 4 / 100.
  * Each dig of its farmer, one e_peon_dig cycle of 1196 ms, gives it 10 back, and it bears food only once whole.

  Tzar2 lost 30 hit points a minute, about four minutes to go. It drew the field ripe to the end and never grew it
  back.

## Decision

* **Class names.** `PresentationCatalog.TypeOfClass` lower-cases the class.
* **Damage** (`CombatSystem.Damage`, WAR-001):
  * The attack is grown by its bonus, then A² / (A + D), at least `minimumDamage`.
  * A ranged attacker of `combat.magicAttackers` ignores the defence.
  * `gameplay.resistanceScale` is gone.
  * A holder's taken share applies to every blow; the distributed share goes to the units inside when there are any.
* **Bonuses** (`LegacyBonuses`): the last entry for a pair holds.
* **Firing periods.** The towers and warships get explicit `cooldownMs` and `hitDelayMs: 0` (WAR-019). The cannon
  levels' fire rates (TECH-036) already give 2400 to 800 ms over a 3000 ms base.
* **Fields:**
  * Trait `Decay { afterMs: 60000, everyMs: 5000, health: 10, growMs: 1196 }`.
  * A farmer at a field short of health spends its gathering time growing it, `health` every `growMs`, and starts its
    load only once the field is whole.
  * The client draws `field_grow` by health.
* **The farmer's place** (`Interaction.WorkerReaches`): the middle tile, or where the farmer stopped on the field when
  another unit stands on the middle.

## Alternatives considered

* **Keep the damage placeholders and only retime the shooters.** Ships against castles would still double their bonus,
  and buildings would keep an armour the original does not give them.
* **Keep RESISTANCE as a damage reduction.** The manual's word is ambiguous. The original's code uses RESISTANCE only
  for capture and religion (0x4d4064, the capture clock).

## Consequences

* Buildings take more from every blow, the castle up to twice as much, since nothing cuts their damage now. Units with
  defence take a little more (footman against footman: 4 instead of 3).
* Two shooter classes slow down a lot. Cannon ships against a building lose about half their damage per minute, and
  cannon towers against ships two thirds. Arrow towers shoot more often.
* A neglected field goes in 110 s instead of about 4 minutes, and shows it withering.
* Hashes:
  * The gameplay, AI and sim goldens and benches are re-pinned.
  * The AI's random-map test now waits nine minutes for the first blow on Island instead of eight.
* The captured duels (attack-timing facts) all have defence 0, so they cannot tell the two formulas apart. A capture
  with an armoured defender would.
