# 0268. The ninja's murder is one blow of fifteen times its attack

* **Status:** Accepted
* **Date:** 2026-10-23
* **Deciders:** Claude (maintainer), under ADR 0190 (ReTzar as a source of the original's rules)
* **Related:** ADR 0095 (spells), ADR 0250 (the Messiah's immunity); ledger MAG-014, MAG-028, PRES-005, WAR-010

## Context

Tzar2's murder was a spell that killed its target outright, sparing heroes, for 50 mana. As a kill it counted as
magic, so the Messiah was immune to it. Both disagree with the original:

* **ReTzar reads the Murder op (0x4e076a).** The ninja shows itself, spends all its mana and strikes once with
  fifteen times its attack, through the target's defence like any blow. The order (0x437250) takes any visible unit
  but the ninja itself. A ninja with 60 mana or more that is ordered to attack a unit murders it with its first blow
  (0x4eb950). The picture is `image_murder`, or the attack's for a class without one (0x4e197d).
* **The strategy guide**, mission 18: "8-10 cloaked Ninja set to murder him". The Messiah takes murders, and one does
  not kill him.

## Decision

* **A new spell effect `murder`:**
  * the caster strikes the target once, at its attack times the spell's `amount` (15 in classic), through
    `CombatSystem.Apply`: defence, BONUS.INI and Honor as for any blow;
  * the caster's mana then drops to 0;
  * casting already reveals the caster.

  It is not magic, so the Messiah's immunity does not apply. Heroes are no longer spared. Classic's `murder` uses it
  with `affects: all`; its mana, 50, stays UNVERIFIED. The `kill` effect stays for other rulesets.
* **`combat.murderBlowMana`** (60 in classic, 0 by default): a unit that can murder, with at least that much mana,
  ordered to attack a unit without Shift, murders it (`CombatMagicCommands.TryMurderBlow`).
* **The frame** marks a murder in progress as `UnitActivity.Murder`. The client draws `image_murder`, falling back to
  the attack picture (PRES-005).
* The ruleset hash moves. The match changes where ninjas fight.

## Consequences

* A murder is lethal to most units: the ninja's attack times fifteen is far above their hit points. It is not lethal
  to the Messiah, to knights with defence, or to heroes with many hit points.
* The guide's way through mission 18 works in Tzar2.
