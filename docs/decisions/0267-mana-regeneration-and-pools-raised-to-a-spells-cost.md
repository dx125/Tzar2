# 0267. Mana regrows 2 a second (Nature 1); a pool below a spell's cost is raised to it

* **Status:** Accepted
* **Date:** 2026-10-23
* **Deciders:** Claude (maintainer), under ADR 0190 (ReTzar as a source of the original's rules)
* **Related:** ADR 0095 (spells), ADR 0191 (Concentration), ADR 0260 (spell mana), ADR 0266 (no spell cooldowns);
  ledger MAG-001, MAG-025, MAG-026, MAG-027; UNK-26

## Context

With spells no longer waiting (ADR 0266), mana is what paces casting. Two of its rules were placeholders:

* **Regeneration.** Every caster regained 60 mana a minute (1 a second, UNVERIFIED). Concentration added 200 %
  (ADR 0191, after ReTzar's "2 a second, +4"). ReTzar reads the original's timer (0x502ea2): once every 1000 ms, a
  kingdom's caster regains 2, a neutral one 1, and each Concentration adds 4 for the kingdom.
* **Summons above the pool.** The tables price summons above any caster's pool. A mage has 100 mana; the bat costs
  25, the jinn 300, the carpet 500, the golem 600 and the dragon 800. Tzar2 assumed a summon costs at most the whole
  pool (a HYPOTHESIS, UNK-26). ReTzar reads the cast (0x4e1c30): a caster ordered to cast what it cannot afford has
  its maximum raised to the cost when it is lower, and plays the `pump` picture. It then has to fill that far.

## Decision

1. **`gameplay.manaRegenPerMinute` is 120**, 2 a second. Concentration's +200 % then gives 6 a second, the original's
   2 + 4.
2. **`combat.natureManaRegenPercent: 50`**: Nature's casters regain half their type's rate, 1 a second. The new setting
   defaults to 100 for other rulesets.
3. **`ManaState.RaisedMax`**:
   * When a cast is refused for lack of mana and the spell's cost is above the caster's most mana, the most mana
     becomes the cost (`MagicSystem.RaiseMaxToCost`).
   * `World.MaxMana` is the one place that answers a caster's most mana: the pool's maximum with the owner's
     technologies, or `RaisedMax` when that is more. Regeneration, potions, the frame and validation use it.
4. **A summon costs its creature's whole mana.** The cap at the caster's pool goes.
5. The card shows a summon's mana with its creature's.
6. **Layout:** `ManaState` grows, so the hashed layout goes to 19 and the snapshot to 21, and every hash is re-pinned.

## Consequences

* Casters regain mana twice as fast as before.
* Summons take long:
  * a mage's first golem needs 600 mana, 300 s at 2 a second (100 s with Concentration);
  * a dragon needs 400 s (about 133 s with Concentration);
  * books still cast for free (ADR 0265).
* The `pump` picture over the caster is not drawn yet.
* **Still different from the original** (MAG-025): a caster short of mana is refused at the order. The original
  accepts the order, walks into range and only then stands idle, looking again in 2 s. The refusal keeps the card's
  feedback immediate.

## Alternatives considered

* **Keep the cap at the pool:** rejected. It was a guess. ReTzar reads how the original makes these summons
  possible, and its numbers agree with the tables (costs above every pool).
