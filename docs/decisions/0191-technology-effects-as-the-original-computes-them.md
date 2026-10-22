# 0191. Give technologies the effects the original's research handlers compute

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), under ADR 0190
* **Related:** ADR 0190 (ReTzar as a source of facts); ADR 0040 (technology effects); ledger TECH-003 to TECH-032;
  UNK-21, UNK-23, UNK-26

## Context

Every technology's strength was a placeholder (UNK-21): percent bonuses chosen to look plausible. ReTzar reads the
original's research handlers (`0x4d33d0`) and the getters they feed (`0x4de7d0` weapons, `0x4de880` armour). Most
of them are whole points of attack or defence, or plain percentages.

## Decision

The classic ruleset takes those effects (ReTzar's addresses are in the YAML comments and in the ledger):

| Technology | Effect |
|---|---|
| Feudalism, Slavery | a peasant costs 80 % |
| Horsemanship | horse archers and heavy cavalry cost 75 % |
| Meditation | the Asian monk costs 75 % |
| Architecture | a building's TIME is 70 % (a 43 % faster rate) |
| Great Wall | stone towers, walls and gates get 30 % more hit points |
| Fanaticism, Buddhism | buildings get half again their resistance |
| Temper Iron, Scimitar | +1 attack to hand-to-hand units |
| Crossbow, Chemistry | +1 attack to bows, ballistas, xebecs and arrow towers |
| Engineering | +5 attack to catapults, cannon ships and cannon and rocket towers |
| Kung Fu | +1 attack to peasants |
| Scale Armor, Chain Mail | +1 and +2 defence to the units on foot |
| Riding Hordes | +2 attack and +1 defence to horse archers and heavy cavalry |
| Inquisition | +5 attack to priests |
| Gunpowder | +10 attack to cannon towers |
| Concentration | mana back 2 a second, 4 more |
| Warfare, Tactics, Sun Tzu | a level needs 70, 50 and 35 % of the experience |
| Philosophy | monks and priests start at level 2 |
| Honor | hero against hero, 50 % more |
| Leadership | the hero's experience goes in full to the units round it |

Warfare, Tactics and Sun Tzu *set* the need in the original, and are researched in that order. Our modifiers add up,
so they are gains that sum to the same thing: +43 %, then +57 % (+100 % in all), then +86 % (+186 %).

## Alternatives considered

* **Keep the placeholders until each is captured.** A capture per technology is many runs. The handlers give exact
  numbers, and the captures we have (combat pairs, ADR 0169) agree with whole points.
* **Model the original's experience counter** (a counter emptied at each level that needs `speed × level / 100`,
  a cap of Learning +4 per academy +10). That would change GAME-08 itself, so it is left for its own change. The
  gains above give the same relative speeds.

## Consequences

* Not done yet: Supply Storage and Seafood raise the population *limit* by a quarter each in the original; Tzar2 still
  gives houses more room (TECH-022, partial). Ore flotation, Tournaments, Jewelry and Fame are separate changes.
* The ruleset hash changes; the goldens and benches are re-pinned.
