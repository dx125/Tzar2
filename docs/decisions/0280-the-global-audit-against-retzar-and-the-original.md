# 0280. Audit the remake against ReTzar and settle disagreements in the original's code

* **Status:** Accepted
* **Date:** 2026-11-07
* **Deciders:** maintainer (Claude), at the owner's request before the eleventh play-test
* **Related:** BLD-006, PRES-001, WAR-021, WAR-022, DIP-011, CAP-002, CAP-004, CAP-005, CAP-006, MAG-038, MAG-039,
  MAG-040, EXP-008, TRADE-004, TRADE-007, TRADE-009, TRADE-012, ITEM-020, ITEM-021, ECO-005, ECO-027, ECO-028, ECO-029;
  UNK-17, UNK-19, UNK-23, UNK-25, UNK-26, UNK-28, UNK-29,
  UNK-42; ADR 0181, ADR 0191; ReTzar
  `src/gameplay.rs`, `src/gameplay/scripting.rs`

## Context

The owner asked for a global audit: war, movement, every upgrade, the economy, artifacts, resources, construction,
neutral creatures and the other global mechanics, each compared with ReTzar. Where ReTzar and our sources disagree, the
original game is the final source of truth.

ReTzar's comments cite the original's code by address. Those addresses match the GOG `Tzar.exe` installed on the owner's
PC, so a disagreement can now be settled in the code itself. A local disassembly of the cited routine is read on the
owner's PC and nothing from it is committed (REQ-LEGAL-01). Only the facts go into the ledger, under a `community` source
with the address.

The audit went through ReTzar's reading of the original, function by function, and through the 127 `UNVERIFIED` values
in `classic.yaml`. It found these differences:

| Area | The original (ReTzar) | Tzar2 before | Batch |
| --- | --- | --- | --- |
| Fire | Pictures and a sound by health; no hit points lost (0x4200c0, 0x41efe9). | 60 HP a minute below half; buildings burnt down. | A |
| Pikemen | Strike back at once with a fifth of their attack (0x4ea0b0). | Nothing. | A |
| Peasants hit | Fight back if at least as strong; otherwise run 5 tiles, rest 3 s, go back to work (0x4ae640). | Stood and took it. | A |
| Score | 500 a building, 200 anything else, mined gold and stone; shown / 100; the end test's limits (0x504cbe, 0x442d10). | Placeholder formula (UNK-29). | A |
| Capture | Resistance lowered 5 a capture cycle, taken at 5; auras of EMIT; 10 back every 2 s (0x4dfe52, 0x41f271). | Points model (UNK-19). | B |
| Bribery | (level + 20) × 10 / 4 gold (0x4ac62f). | 200 % of the unit's cost (UNK-26). | B |
| Tavern | An offer a minute per list: 8–12 mercenaries at level 0 or a hero of level 10–30, any civilization; price × 1.6 + per level (0x4d24c0, 0x4d2030, 0x4d23b0). | Period model (UNK-28). | C |
| Dice and loans | 50/33/20/10 % paying 1/2/5/10 times; a loan repaid with 200 more, the next 200 larger a minute later (0x4a3fee, 0x4d2d6a). | Odds with a house edge; no interest. | C |
| Magic workshop | Strength from the gold paid; 250 ms a gold (books 500) (0x4a5220, 0x4743b0). | Placeholders (UNK-25). | C |
| Potions | Maximum health and mana raised for good; experience in points (0x464ac0). | Healed to the maximum; levels. | C |
| Gathering | Idle peasants copy a working one after 30 s; fields give a load after 12–18 digs; boats take up to 4 fish every 1.4 s to 50; galleon, hulk and xebec carry 1000, 500, 300; cows regrow milk as mana. | Placeholders (UNK-17, UNK-28). | D |
| Bears | Wander from where they stand and look for a target in 30 % of their scans (0x42fea0, 0x4eeea0). | Guard a home and walk back (TDD). | D |
| Leadership | Units below level 11 closer than the hero's sight (0x4de900). | Every unit within 4 tiles (UNK-23). | B |
| Mana | None regained while frozen, stone, healing or teaching, or hidden on its own mana (0x504e30). | Always regained. | B |
| The ninja's cloak | 50 mana, then 1 every 2 s while hidden (0x4eb840, 0x4ebba9). | Free, for 60 s. | B |
| Smaller | Shared resources one way; the easy campaign's damage; the idle scan's period. | Both ways; none; 200 ms. | E |

These already matched and were left alone:

* combat damage, bonuses, honour and dummies;
* targeting, the call for help and the towers' and ships' fire;
* movement and the search for a path;
* experience, levels and heroes (ADR 0191);
* the technologies' effects (ADR 0191);
* the spells (ADRs 0261 to 0279);
* trade profits, the crusade, Jewelry's market, cows' meat and grazing;
* construction, repair and refunds.

## Decision

Fix the differences in five batches, A to E. A batch's rules and tests land with its change. The original's code wins
over the manual where the two disagree. The fire is the first such case: the manual says a burning building burns down,
and the code takes nothing.

Batch A (this ADR's first change):

* **Fire.** A building burns by its health with the original's steps: one small fire at 50 % and below, two below 36 %,
  two and a big one below 16 %. The fire takes nothing. `burnThresholdPercent` and `burnDamagePerMinute` are gone; the
  frame carries the fire level, and the client draws the original's fires.
* **Pikemen.** A pikeman left standing by a hand-to-hand blow of another kind strikes back at once with 20 % of its
  attack (`combat.counterBlowClasses`, `counterBlowPercent`).
* **Peasants hit.** A peasant hit while idle or gathering compares strength: attack + defence + range / 5 + hit points /
  20, twice for a tower. At least as strong, it fights back. Otherwise it runs 5 tiles straight away (the new
  `CombatMode.Flee`), rests 3 s and goes back to its work (`combat.fleeClasses`, `fleeTiles`, `fleeRestMs`).
* **Score.** A kingdom's raw score (`KingdomState.Score`, in the place of the old kill value) grows by 500 for a building
  destroyed, 200 for anything else killed, and each load of gold or stone as it leaves its mine. It is shown as
  (raw + 50) / 100. The score and time limits follow the original's end test.

Batch B:

* **Capture.** A building has a resistance target: its RESISTANCE (raised by Fanaticism or Buddhism) plus the EMIT of the
  finished buildings whose footprint, grown by their sight, meets it. The EMIT's own value counts from an ally, its
  other value from the rest.
  * Each 1196 ms cycle a hostile peasant takes 5, and the building is taken at the cycle that finds no more than 5.
  * A friendly peasant gives 5 back until the building is whole, and the capturer goes on doing so after the capture.
  * Every 2 s the resistance moves 10 toward the target, so one peasant alone never takes a building at full
    resistance.
  * A peasant may capture an enemy's buildings, the watch, guard, stone and cannon towers of any non-ally, and its own or
    an ally's lowered buildings.
  * `CaptureState` now holds the current resistance while it is away from the target. The points, the reset, the
    16-tile fade and the minimum are gone.
* **Bribery.** A bribe costs (level + 20) × 10 / 4 gold.
* **Leadership.** It reaches units below level 11 closer than the hero's sight (`leadershipBelowLevel`).
* **Mana.** None comes back while frozen, stone, healing or teaching with the mana for it, or hiding on its own mana.
* **The ninja's cloak.** It costs 50 and lasts 2 s, renewed for 1 mana every 2 s while the ninja has it (the new spell
  field `drain`, `StatusEffects.CloakDrain`).

Batch C:

* **The tavern.** Each tavern keeps two lists (`TavernStock`). A second after it stands, and then every minute (six in a
  Deathmatch), the mercenary list gains an offer with Mercenaries and the hero list one with Heroes' Quest.
  * An offer is a unit of a random civilization's barracks: 8 to 12 at level 0, or a hero of level 10 to 30. Fame adds 4
    levels to either.
  * A full list of 8 loses its oldest offer, and a hired offer leaves its list.
  * The price is cost × count × 1.6, plus 30 gold a level and 15 a level of each other resource the type costs.
  * The frame carries each tavern's offers. The card shows those of the first tavern selected and sends the hire to it.
* **The dice.** The odds 1:1, 2:1, 5:1 and 10:1 win at 50, 33, 20 and 10 %. A win keeps the stake and adds it the odds
  times. Stakes go by 50.
* **Loans.** A loan is repaid with 200 more, and the next one, 200 larger, is offered a minute after.
* **The magic workshop.** It makes an item from the gold paid, 10 or more by 10, in 250 ms a gold (a book in 500). A
  kingdom makes one item at a time.
  * Experience potions give gold / 5, mana potions the gold, and attack or defence potions gold / 2.
  * A sword, bow or shield gives gold / 4, at most 250.
  * A book has (gold / 4) / the spell's rate charges, and needs at least four rates of gold.
  * It makes no health potion and no book of soul binding or hypnosis.
  * The original then takes the gold again when it can (0x466a87); Tzar2 does not, taking that for a bug.
* **Potions.** Health and mana raise the maximum for good (`Gains`, `ManaState.RaisedMax`), and experience is points.
* The snapshot format goes to 28, for the taverns' offers and the potions' gains.

Batch D:

* **Idle peasants.** Idle 30 s, a person's peasant takes up the work of the nearest of its kingdom's peasants within twice
  its sight. That work may be chopping, mining, tending cows, building, repairing or capturing, but not digging a field
  (`idleCopyAfterMs`). The 30 s was read in `Tzar.exe` too. A computer kingdom's peasant still finds work by itself, since
  the original asks its player.
* **Fields: found, not turned on.** The original gives a load after 12 to 18 digs of 1196 ms, about half Tzar2's rate.
  The countdown is built (`ResourceNode.loadCyclesMin`, `loadCyclesMax`, `cycleMs`) but `classic.yaml` does not use it
  yet. With it, Tzar2's computer, whose pacing was set on the faster fields, sent no wave within 10 minutes in any AI test,
  and a campaign mission failed. ECO-028 stays missing until the owner decides and the AI's farming follows.
* **Fishing.** A boat carries 50, at 350 ms a fish. A patch nobody fishes grows by 5 % every 10 s up to 2000
  (`growPercent`, `growEveryMs`, `growMax`). The original's patches also drift; Tzar2's do not.
* **Ships' loads.** A galleon carries 1000 a trip, a hulk 500, a xebec 300.
* **Cows' milk.** It comes back at the kingdom's mana rate (2 a second, Concentration too), half for Nature's cows, and not
  while milked. How fast the pail fills stays as the owner's capture measured it (ADR 0170); ReTzar's 3 a 900 ms cycle
  is slower.
* **A fix found on the way.** A ship's approach point beyond the first ring round a building could be a diagonal corner
  out of reach. A boat stood there for ever, unable to deliver. Corners of far rings are now taken only when nothing
  else is free.

## Consequences

* Buildings no longer burn down by themselves. An attacked town keeps its damaged buildings until they are struck again.
* Raiders who hit peasants scatter them, and the peasants come back.
* The score and its limits differ from before. The F4 scoreboard shows the original's numbers.
* The original also scores a held ruin's `ruinpoints` and capture-the-flag flags. Tzar2 has neither object (DIP-011's
  note).
* The gameplay golden's ruleset hash changes, and so does its stream wherever peasants are hit or buildings burn.
* Capturing takes several peasants, as in the original, and a raided building's resistance comes back by itself.
* Ninjas spend their mana to stay hidden, and run out of it.
