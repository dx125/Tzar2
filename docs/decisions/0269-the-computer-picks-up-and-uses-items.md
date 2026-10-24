# 0269. The computer picks up and uses items

* **Status:** Accepted
* **Date:** 2026-10-23
* **Deciders:** Claude (maintainer), under ADR 0178 (the computer's tactics) and ADR 0190 (ReTzar as a source)
* **Related:** ADRs 0186, 0265, 0266 (items, books, learning); ledger AI-005, AI-002

## Context

Tzar2's computer players never touched an item, so books, potions and weapons on a map were the human player's alone.
ReTzar reads the original's AI:
* beyond the easy level, an idle fighter picks up the nearest power item, magic item or health item within 16 cells
  (0x413e00, 0x413ee0, 0x414219), and one walking into a cell picks up those in the 3 × 3 cells round it (0x4de310);
* a computer's unit that takes an item uses it at once (0x4155c0): it drinks a potion, learns the spell of a
  one-charge book it can learn, and casts a summoning book of more charges on itself; a full summoning book is used so
  on the one-second timer (0x464280).

## Decision

A new AI executor, `ItemExecutor`, runs after the military one each think:
* every army unit of the computer that holds a potion drinks it; a one-charge book whose spell the unit can learn is
  learned (`UseItem` with `Arg1` 1, ITEM-019); a summoning book with charges left, once ready, is cast at the holder;
* beyond the easy level, every idle army unit at home with room in its inventory, not claimed by another executor,
  goes for the nearest power item, spell book or potion within 16 tiles not already sought by another unit.

The walk-in pickup of 0x4de310 is left out: idle pickup covers what lies near a town, and the walk-in rule would act
mid-attack.

Two guards, found by the original campaigns' acceptance test:
* **Not in a scenario's match** (`AiPlayerSettings.TakesItems`, false when the session has a scenario, kept in the AI
  state's settings byte). In bigisland a computer kingdom took a summoning book, and the book Razma's quest needs; the
  mission could no longer be won. A scenario's items are often its story's.
* **Only books whose charges run out are used to summon.** An endless book (ADR 0265) would summon for ever.

## Consequences

* Computer players use the map's items in skirmish, random-map and multiplayer matches; the AI goldens and the AI bench
  hash move (the settings byte).
* Their tactics stay Tzar2's own otherwise (AI-002).
