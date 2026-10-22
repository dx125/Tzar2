# 0190. Take the rules ReTzar read from the original's code, never ReTzar's code

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** product owner (ReTzar as a reference, 2026-10-22), agent (maintainer)
* **Related:** DEC-09 (licenses); ADRs 0169 (TIME), 0170 (loads), 0172 (walking speed), 0095 (crusades); ledger
  WORLD-012, MAG-016, MAG-017, TECH-006, TECH-010, TECH-023, TECH-024, TECH-036; UNK-12, UNK-21, UNK-26, UNK-28

## Context

[ReTzar](https://github.com/ColCarlo/ReTzar) is a fan remake of Tzar in Rust and Bevy. Its code names the addresses in
the original's executable it was read from (for example `0x49891b`, the length of a step). The owner pointed to it for
questions the original game alone cannot settle.

ReTzar is GPL-3.0. Tzar2 is MIT, and DEC-09 lets GPL code be read as a reference but never copied or linked.

Several of our placeholders are rules ReTzar has read:

* **Walking speed.** We measured 18 classes; the others walked at SPEED × 10 px/s, known to be wrong (ADR 0172).
* **Crusades.** A crusade raised 5 crusaders (a hypothesis), then waited 60 s.
* **Gathering technologies.** They sped up gathering, though the help says they raise loads.
* **Trade Taxes and Banking.** Each added 10 % to trade income.
* **Ship cannon levels.** Each added 5 attack, though the help says the cannons fire more often.

## Decision

* **ReTzar is a source of facts, not of code.** We read it; we write our own code from the rule it states. The ledger
  names it as a `community` source with the address it gives. A ReTzar rule replaces a placeholder only where it agrees
  with what we have measured, or says what code of the original it read.
* **Walking speed follows the stride.**
  * The original steps one pixel every `(32000 / move_len) >> 5` of its milliseconds. `move_len` is the stride of the
    picture the unit walks with (IMAGES.INI).
  * Its millisecond lasts 1.25 of ours (TIME, ADR 0169).
  * The rule gives every measured class its measured speed, within 1 % (stride 50: 40 px/s, 85: 72.7, 56: 47, 80: 66.7,
    32: 25.8).
  * The importer gives each unit its walk sheet's stride (`Mobile.stride`). A measured `pixelsPerSecond` still comes
    first, and SPEED × 10 is left only for what has no stride.
  * New speeds: horse archers, horse samurai, ninjas, janissaries, heavy cavalry and chariots 72.7 px/s; catapults and
    ballistas 32; galleons 40; hulks and boats 53; xebecs 66.7; caravans 61.5; carpets 100.
* **Crusades gather.**
  * A kingdom starts with 16 crusaders gathered.
  * Each finished cathedral gathers one more every 12 s in the first 15 minutes, every 8 s until 20 minutes, every 4 s
    until 30 minutes, then every second.
  * The ceiling is 16 plus 4 for each full crusade, at most 36.
  * A crusade (one at a time) takes all the gathered crusaders, for the anchor's price. They arrive one every 500 ms.
  * When they have all died, the anchor goes and the kingdom starts again from 4.

  This is the 1.09 note's "a crusade preached earlier brings fewer crusaders" (MAG-017). The kingdom's state keeps the
  count and the full crusades, in what was padding.
* **Gathering technologies raise loads.** A peasant carries 10, plus 2 for the "plus" technology of the resource it
  gathers (Rotating Fields, Saw, Stone Mining, Gold Mining), plus 4 of Irrigation for food. The bonus goes by the node;
  cows keep their 50 (ADR 0170).
* **Trade Taxes and Banking** add 2 and 3 to the base 40 of the trade profit: 5 % and 7.5 % more profit (7 % here,
  whole percents).
* **Ship cannon levels** shorten the firing period from 3,000 to 2,400, 1,900, 1,500, 1,200 and 800. This is a new stat,
  `fireRate`, in percent of the weapon's own rate.

## Alternatives considered

* **Copy ReTzar's code or port it line by line.** DEC-09 forbids it, and its model (cells, opcodes) is not ours.
* **Wait for captures.** A capture can check a ReTzar rule, but each costs the owner a run. Where the rule matches our
  measurements, as the stride does for all 18 classes, a capture adds little.

## Consequences

* More placeholders go away. The ones still left: ore flotation (ReTzar: mines lose half as much), the trade profit's
  distance rule, the soul gem's bound unit and technologies' other magnitudes (UNK-21). ReTzar reads all of them; each
  is its own change.
* The reference data hash changes: the imported tables now carry the strides.
* Every golden, the trigger mission and the CI benches are re-pinned.
