# 0266. Casters wait only for mana; units learn spells from books

* **Status:** Accepted
* **Date:** 2026-10-23
* **Deciders:** Claude (maintainer), under ADR 0190 (ReTzar as a source of the original's rules)
* **Related:** ADR 0095 (spells), ADR 0260 (ranges and mana), ADR 0265 (books' own pools); ledger MAG-025, ITEM-019;
  UNK-26

## Context

Two of ReTzar's readings of the original's code disagree with Tzar2.

* **No wait between spells.** Every spell in Tzar2 had a `cooldownMs` (1.5–5 s), and ADR 0260 left them UNVERIFIED.
  ReTzar reads the cast (0x4e1c30): mana is the only thing that holds a caster back. When the caster is short of mana,
  it stands idle and looks again in 2 s. A heal is cast again at once while the target is still wounded. The cooldown
  also held back the holder of a book after each cast (ADR 0265's open question).
* **Spells learned from books.** ReTzar reads the book (0x4640f0) and the inventory (0x4a1c58):
  * A book used with no target (the inventory's `item_use_instant`) teaches its spell to a holder that has a mana pool
    and does not know the spell yet. The book is used up, whatever its charges.
  * Spells learned this way join that unit's own card when it is selected alone (0x4e3fb0).
  * Ordering such a spell still asks the order's research and the civilization's tree. Only the menu check is skipped.

  Tzar2 had no way to learn a spell. Ctrl + click on a book cast it on its holder.

## Decision

1. **No spell has a cooldown in the classic ruleset.** The `Spell.cooldownMs` field stays in the model, default 0, so a
   fork can give spells a wait. Mana, the cast time (still UNVERIFIED) and a book's own pool are what pace casting.
2. **A unit can learn a spell from a book** (ITEM-019):
   * A new store `game.learned` holds `LearnedSpells`, a 64-bit mask over the ruleset's spells. Only units that learned
     something have an entry. A ruleset's first 64 spells can be learned, which is more than the original's 22.
   * `UseItem` with `Arg1` 1 on a book uses it with no target. The holder learns the spell if it has a mana pool, does
     not cast the spell by nature and has not learned it yet. Otherwise the command is refused. The book is destroyed,
     and `ItemUsed` carries the spell in `Y`.
   * `MagicSystem.CanCast` and the world's validation accept a learned spell like one of the type's own. The order's
     technology gate still applies.
   * The frame carries `LearnedSpells`. A lone selected own unit's card adds the button of each learned spell its menu
     lacks, through `ICardSource.OrderButton` (a new member, `null` by default).
   * In the inventory, Ctrl + click on a book learns its spell. A plain click casts it, as before.
3. **Layout:** the new store moves the hashed layout to 18 and the snapshot to 20, so older saves are refused. Every
   golden, the trigger mission and the CI bench hashes are re-pinned. The match changes too, because casters no longer
   wait between spells.

## Consequences

* Mages, priests and books cast as often as their mana allows. Pacing in play now comes from mana regeneration (one
  point a second, UNVERIFIED, UNK-26) and the cast times.
* The AI casts more often where it has the mana.
* Open questions:
  * The computer's units in the original learn a one-charge book they can learn when they pick it up, and use a
    many-charge summoning book on themselves (ReTzar 0x464280). Tzar2's AI does neither yet.
  * Whether learned spells follow a hero to the next campaign map is not read. Persistent heroes do not carry them.
* **Not done here** (its own change): a caster short of mana should accept the order and wait for the mana, looking
  again every 2 s. A caster whose maximum is below the cost should have its maximum raised to the cost (0x4e1c30, the
  `pump` picture). Tzar2 still refuses the order.

## Alternatives considered

* **Keep the cooldowns as Tzar2's own pacing:** rejected. Rules stay faithful (the owner's rule), and the only source
  that reads the original says there is none.
* **A per-unit spell list of any length:** components are fixed-size data. A mask is enough for every ruleset with up
  to 64 spells. A fork that needs more widens the mask; that is a layout change like this one.
