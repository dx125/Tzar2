# 0260. Give spells the original's ranges and mana

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), under ADR 0190; coordinator (take the spell ranges and mana, 2026-10-22)
* **Related:** ADR 0095 (spells), ADR 0190; ledger MAG-023, MAG-009; UNK-26

## Context

Every spell magnitude was a placeholder (UNK-26). ReTzar reads two of the original's dispatch routines:

* **Ranges** (`0x4721e0`, in cells).
  * fire rain 14;
  * lightning, freeze and confusion 12;
  * heal 3, or 5 with Medicine (the kingdom's healing range);
  * the golem, carpet, dragon and jinn summons 4;
  * bats and whirlpool 16;
  * magic vision 1024, i.e. anywhere;
  * stone 20;
  * flame bolt 18;
  * Mastery's teaching 1;
  * every other spell 8.
* **Mana** (`0x4723e0`).
  * fire rain 100;
  * heal 4;
  * freeze 60;
  * confusion and raise dead 70;
  * magic vision 30;
  * Mastery 10;
  * every other spell 50.
  * A summon costs its creature's mana. That is already how Tzar2 prices summons, with the spell's own mana at 0.

## Decision

* The classic ruleset takes those ranges (in tiles) and mana. A spell's range may now be up to 1,024 tiles.
* **Medicine** adds 2 tiles (heal 3 → 5), where the placeholder added 4.
* The other magnitudes stay UNVERIFIED: amounts, durations, cooldowns, cast times and radii. ReTzar reads several of
  them, which is follow-up work.

## Consequences

* MAG-023 is tested.
* Spells cost more: lightning, bless, cloak and the curse 50, fire rain 100 (more than a synthetic mage's 90). A mage
  casts fewer spells between rests. The AI casts fewer spells, and the goldens move.
* Tzar2 counts the range edge to edge. Whether the original measures it between centres or between edges is not
  settled.
