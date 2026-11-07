# 0280. Audit the remake against ReTzar and settle disagreements in the original's code

* **Status:** Accepted
* **Date:** 2026-11-07
* **Deciders:** maintainer (Claude), at the owner's request before the eleventh play-test
* **Related:** BLD-006, PRES-001, WAR-021, WAR-022, DIP-011; UNK-29, UNK-42; ADR 0181, ADR 0191; ReTzar
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
| Gathering | Idle peasants copy a working one after 30 s; fields give a load after 12–18 digs; boats take up to 4 fish every 1.4 s to 50; galleon, hulk and xebec carry 1000, 500, 300; cows regrow milk as mana. | Placeholders (UNK-17, UNK-28). | D |
| Bears | Wander from where they stand and look for a target in 30 % of their scans (0x42fea0, 0x4eeea0). | Guard a home and walk back (TDD). | D |
| Smaller | Leadership within the hero's sight; no mana regained while frozen, stone, healing or hidden; shared resources one way. | Placeholders, or both ways. | E |

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

## Consequences

* Buildings no longer burn down by themselves. An attacked town keeps its damaged buildings until they are struck again.
* Raiders who hit peasants scatter them, and the peasants come back.
* The score and its limits differ from before. The F4 scoreboard shows the original's numbers.
* The original also scores a held ruin's `ruinpoints` and capture-the-flag flags. Tzar2 has neither object (DIP-011's
  note).
* The gameplay golden's ruleset hash changes, and so does its stream wherever peasants are hit or buildings burn.
