# 0278. Give mages and priests the original's casting, targets and pictures, and let the Attack order strike anyone

* **Status:** Accepted
* **Date:** 2026-11-06
* **Deciders:** maintainer (Claude), at the owner's request after the tenth play-test
* **Related:** MAG-027, MAG-030, MAG-031, MAG-032, MAG-033, WAR-020, PRES-019, NAT-008; ADR 0263, ADR 0266, ADR 0267,
  ADR 0274, ADR 0275; ReTzar `src/gameplay.rs`, `src/frontend/effects.rs`

## Context

The owner's tenth play-test (2026-11-06, the session log read locally only) found these faults with the European mage:

1. It did not attack allied troops or buildings when ordered to. The log has `NotAllied` and `InvalidTarget` refusals.
2. A lightning cast on a bird struck, but with no animation.
3. The freeze looked wrong and did the wrong thing. Its casts on the ground and on an own priest were refused
   (`InvalidTarget`).
4. Bats came one at a time, where the original spends all the mana.
5. The stone golem:
   * the mage showed no gathering of mana;
   * the golem did not come by itself at 600 mana (`NotEnoughMana`), and the spell had to be cast again;
   * the mage's mana cap went from 100 to 600.

The owner asked for a review of every skill of every mage and priest of every civilization: animations, effects and
cursors, and attacking allies.

ReTzar reads the original's spell code (0x4723e0 to 0x473bf0) as follows.

| | The original | Tzar2 before |
| --- | --- | --- |
| Short of mana | The order stays: the caster walks into range and stands. Its most mana is raised to the cost, with the `pump` picture, and it looks again every 2 s. It casts once the mana is there (0x4e1c30, 0x4dff44). | The order was refused. The pool was raised, but `pump` was never loaded, so it never showed. |
| Summons | The mana is spent in multiples of the creature's cost, one creature per multiple (0x472409). | One creature a cast. |
| Lightning, flame ball, heal, invisibility, stone, hypnosis, teaching | Any unit or object of any kingdom; the heal and teaching only their kinds. | Attack spells on enemies only, the others on own units only. |
| Freeze | Cast at a point. A ball flies there at 700 px/s and freezes every unit within 3 tiles, own ones and the caster too, with 40 damage short of death (0x432590). | Cast on an enemy unit. |
| Confusion, blessing | At a point: everyone but the caster within 2; those not hostile within 4. | On a unit. |
| Heal | Goes on while its unit is wounded (0x4e00a0). | One cast an order. |
| Attack order | Takes any object; only the right click checks the relations (0x435500). | Refused allies and own units. |
| Pictures | Lightning bolts (700 ms). Freeze and flame balls are trails of puffs every 45 ms. `healing`, `bless`, `curse`, `hypnotise`, `convert_exp` play over the unit taken, `turn_to_stone` where it strikes, stars for invisibility. Summons come out of `spawn`, skeletons out of `skeleton_rise`. | None of these. A ball showed one puff at its head. |

The ranges, mana costs and amounts (ADRs 0260, 0261) already match ReTzar's tables. The spell cursor is `cs_spell`, as
ReTzar's. The mana cap raised to the golem's 600 is the original's own (MAG-027), not a fault.

## Decision

* **Waiting for mana (MAG-030).** A `CastSpell` short of mana is accepted. In range, the caster stands and looks again
  every `MagicSystem.ManaWaitMs` (2000 ms): it raises its pool (`ManaRaised`, the pump) when below the cost, and casts
  once it can. A cast that finds its mana gone at the end waits again.
* **Summons (MAG-031).** The number of multiples is the caster's mana divided by the cost. Tzar2 lowers it until the
  kingdom can pay and house that many, then pays that many costs.
* **Targets (MAG-032), as data:**
  * `affects: all` for the lightning, the flame ball, the heal, invisibility, stone, hypnosis and teaching;
  * `target: ground` for the freeze, confusion and the blessing.
* **Spell balls at a point.** A spell projectile may fly to a point. Its landing applies the spell there
  (`HitRecord.X`, `Y`). The freeze takes its caster too.
* **The heal (MAG-033)** goes on in the casting phase while its unit is wounded.
* **The Attack order (WAR-020).** It takes any unit or building but the rescue kingdom's (NAT-008), ambient life,
  resources and indestructible objects, and the attacker itself. An explicit fight keeps any targetable target. The
  card's Attack mode sends it on own targets too.
* **The client (PRES-019):**
  * `SpellPictures` names the pictures; `EffectImages.All` loads them and `pump`.
  * `GameScreen` plays them on `SpellEffect`, `Summoned` and `ManaRaised`, and draws a lightning on `SpellCast`.
  * `WorldRenderer` draws the bolts as three jagged lines and the balls' trails as puffs.

## Alternatives considered

* **Keep refusing a spell short of mana.** The golem and the dragon, priced above any pool, then need a second order.
* **Allow explicit attacks on the rescue kingdom too.** The original's prisoners wait to be freed, and Tzar2's
  missions count on them. Tzar2 keeps them out of any fight until a capture shows otherwise.

## Consequences

* A mage can freeze its own troops, and lightning can strike an own building, as in the original.
* The AI casts no spells, so its play is unchanged. The scripted gameplay scenario casts the heal, which now goes on, so
  its golden is re-pinned.
* The bolts are Tzar2's own drawing: the original draws them by code, and this reading does not take the shape of its
  lines.
