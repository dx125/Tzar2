# 0250. Curse, hypnotize, scream and resist magic as the original's code does

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), under ADR 0190
* **Related:** ADR 0095 (spells), ADR 0190 (ReTzar as a source of facts), ADR 0184 (takeover); ledger WAR-009,
  WAR-010, MAG-008; UNK-26

## Context

Three rules of the ledger were open:

* **WAR-009** (missing): the strategy guide says a ghost "may let out a banshee scream" that weakens the enemies around
  it.
* **WAR-010** (missing): the guide says the campaign's Messiah is immune to magic.
* **MAG-008** (unclear): the manual names Hypnotism, but neither the manual nor the game's texts say what it does.
  Tzar2 made it a freeze.

The curse they depend on was a placeholder. It took 25 % off attack and defence, on one unit.

ReTzar reads all of it from the original's code:

* **The curse** (`0x4e5841`): every hostile unit within the caster's sight of the point. Its attack is halved and its
  defence becomes 0 for 30 s. It skips the units the original calls machines (query `0x2719`): golems, siege engines,
  carpets, ships, ghosts, skeletons and fishing boats.
* **The banshee** (`0x4ee780`): after its blow, a ghost casts the curse for free in 71 draws of 1001.
* **Hypnosis** (`0x473887`, `0x4e59b2`, `0x4e5c4b`): effect 13 for 30 s.
  * The kingdoms that command the caster command the unit, and the others no longer do.
  * The unit follows the caster's relations.
  * When the effect ends, every kingdom's hold goes back to the diplomacy.
  * A new hypnosis restarts the timer.
* **The Messiah** (`0x4ef200`): its own handler refuses freeze, discord, hypnosis, stone and curse.

In v1.10's data, `spell_hypnotize` is on no command card. Only the unused `ORG MENU.INI` lists a `te_hypnotize`, so
only spell books cast it.

## Decision

* **The curse** is an area effect around its point: the target's position, or the caster's for the banshee. The radius
  is the caster's sight, or the spell's radius when the caster is gone. It reaches every enemy unit the spell may
  affect, except the classes in its new `targets.except` list. A cursed unit's attack drops by the spell's amount (50 %),
  and its defence becomes 0. `spell_curse` keeps its unit target on the card: aiming at a unit puts the point there.
* **The banshee** is the `Banshee` trait on the ghost. It uses `combat.bansheeSpell`, `bansheeChance` and
  `bansheeOutOf` (spell_curse, 71, 1001). The draw comes from the combat stream after each melee hit the ghost lands.
* **The Messiah** has the `MagicImmune` trait. Freeze, stone, confusion, hypnosis and the curse do not take on it.
  Damage spells, murder and soul items still work.
* **Hypnosis** is the new spell effect `hypnotize`, for 30 s.
  * The unit changes owner to the caster's kingdom (`World.ChangeOwner`). `StatusEffects` remembers the kingdom it
    came from (`HypnoOwner`) and when it ends (`HypnoUntil`).
  * When the time is up, it goes back with its orders dropped. If its kingdom has left the game, it stays.
  * Meanwhile it keeps its kingdom's colours (`FrameEntity.SeemsOwner`) and counts in that kingdom's population.
  * Mana, range, cooldown and cast time remain UNVERIFIED (UNK-26).
* `StatusEffects` grows by 8 bytes. The hashed layout goes to 13 and the snapshot format to 15.

## Alternatives considered

* **Hypnosis through a "side" separate from the owner, as in the original.** Every hostility check, command check, AI
  query and sight rule reads `Identity.Owner`; a second notion of side would have to reach all of them. Handing the
  unit over is the path the takeover of spies already uses (ADR 0184 amendment 6). The difference that remains is
  small: for 30 s the unit's attack, defence and hit points follow the caster's technologies.
* **Keep the curse on one unit.** The banshee's rule is "the enemies around it", and the original's spell is an area
  spell. One effect serves both.

## Consequences

* WAR-009 and WAR-010 are tested. MAG-008 is tested, and its note names what remains different: the technologies.
* The ghost's draw only happens when a ghost lands a blow, so matches without ghosts keep their combat stream.
* The ruleset hash changes (the curse, the banshee settings, the Messiah's trait, the synthetic Messiah). Every golden
  and the CI benches are re-pinned.
* Found while reading, not done: in the original, heal, freeze and raise dead also skip the machines (`0x2719`), and
  the Magic Workshop writes no hypnosis or soul binding books.
