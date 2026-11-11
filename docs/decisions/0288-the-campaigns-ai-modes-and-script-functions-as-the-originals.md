# 0288. A campaign's computers run the AI its map gives them, and the script functions act as the original's

* **Status:** Accepted
* **Date:** 2026-11-10
* **Deciders:** maintainer (Claude), the editor review (batch D, the UNVERIFIED readings)
* **Related:** SCN-009 to SCN-014; UNK-50, UNK-53, UNK-56, UNK-64; ADR 0130, ADR 0183, ADR 0280, ADR 0287; STATUS
  item 26.6

## Context

The trigger engine had readings marked UNVERIFIED. ReTzar reads the original's code for them, so each was compared
with it. A count on the owner's install showed which ones the original's maps use.

* **The AI of a campaign's computer kingdoms.** In the original a kingdom gets a computer player only when its
  kingdom table sets an AI mode: the economy, war, or both. `@setai` changes the mode during play (ReTzar ai.rs:
  `State::start`, the AI command 0x404460). Tzar2 ran the whole AI for every computer kingdom.
  * The main campaign leaves the AI off for most of its story kingdoms: in saa4, five of seven. Others run only one
    half.
  * In Tzar2 those kingdoms built, gathered and attacked from the first second.
* **Functions the campaign calls that Tzar2 did not have:** `@setai` (3 times), `@god` (2), `@settime` (2) and
  `@build` (1).
* **Player Lose.** The original defeats the listed kingdoms and makes every other one a winner, which ends the game.
  Tzar2 only defeated the listed ones (UNK-50).
* **Explore Area of 0 ms.** The original shows the area for 4 seconds; Tzar2 for one tick (UNK-56).
* **Small results.**
  * `@rnd` with reversed bounds gives the first; Tzar2 swapped them.
  * `@str` and `@kingdomcanrescue` give 1.
  * `@getdiplomacy` gives the relation word; Tzar2 gave 0, 1 or 2.

## Decision

* **The AI by halves (SCN-009).**
  * A map played as it is, that is a campaign mission or a playtest, sets the new player flag `AiHalves` at the
    start on each computer kingdom whose table does not give both halves. The halves it does give go in
    `AssistEconomy` and `AssistWar`, the flags of AI assistance (ADR 0183).
  * The legacy AI runs only those halves, or nothing.
  * `@setai(kingdom, mode)` sets them: 0 none, 1 economy, 2 war; 3 clears `AiHalves`, the whole AI.
  * A human kingdom's own computer, its AI assistance, is left alone.
  * A custom map's setup chose computer opponents, so they play whole, as before.
* **`@god`, `@build`, `@settime` (SCN-011, SCN-012).**
  * `@god(1)` puts the current object in a list in the trigger state; combat and magic do no damage to it.
  * `@build()` gives the current site all its building, and the production step finishes it as any site.
  * `@settime(hour)` keeps a shift of the day clock in the trigger state, which `World.MinuteAt` adds through
    `IScenarioController.DayClockShiftMinutes`.
  * The trigger state is format 4; a state without these is still written as before, byte for byte.
* **Player Lose (SCN-010)** makes the other kingdoms still in the match winners through Player Win's path; the rescue
  kingdom wins nothing.
* **Explore Area (SCN-014)**:
  * 4 seconds for 0 ms;
  * the radius is (width + height) / 4 tiles when that is 1 to 31, else 8.
* **Small results (SCN-013)** as ReTzar reads them.
* **Left as they are:**
  * Change Diplomacy's two directions: the original merges both kingdoms' wishes, and what the converter keeps of
    them must be checked first (UNK-55).
  * The order mapping (UNK-57).
  * Overflow and the `&` and `|` operators of expressions: the original wraps and has bitwise operators; no
    campaign expression is known to need them.
  * `$program`, `@count`, `@inprogress` and `@setnoattacktime`: no original map calls them.

## Alternatives considered

* **Leaving the whole AI on and letting the scripts' @setai only add:** the story kingdoms would still build armies
  the original's do not have, and the missions would play otherwise.
* **Creating a computer player when @setai names a kingdom without one:** Tzar2 attaches the AI at the start (its
  state is in the snapshots), and every campaign kingdom that is not human already has one, with its halves off.

## Consequences

* The campaign's story kingdoms stay idle until their scripts wake them, and a war-only kingdom no longer gathers.
  The campaign acceptance plays the missions with these kingdoms.
* Scripts that make a hero immortal, finish a building or move the clock to night now do so.
* A mission that loses a listed enemy kingdom ends in victory for the others, as the original.
