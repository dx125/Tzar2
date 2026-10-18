# 0095. Cast spells in range and pay on effect; lasting effects, summons, area spells; crusades, conversions, bribes

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (M3 stream E)
* **Related:** TDD §8.5 step 10, §10 GAME-11, §5.1 (crusade anchor, invisible units, Raise Dead on buildings),
  UNK-26, ADRs 0040 (order gates), 0041 (mana), 0090, 0091

## Context

Appendix E.4 lists the spells per civilization; the command cards say which caster has which (euro mage: lightning,
freeze, invisibility, bat, stone golem; asian mage: confusion ("berserk"), turn to stone, whirlpool, flame bolt, dragon;
arab mage: carpet, jinn, rain of fire, magic vision, raise dead; priests heal and bless, the asian priest converts mana
into experience (Mastery); ninjas cloak and murder). `TECHTREE [orders]` names the technology of each. Mana pools and
regeneration exist (ADR 0041); summons' mana costs are the `m` suffix of the unit tables. Magnitudes, ranges,
durations and area shapes: none (UNK-26). Crusade: anchor ≤ 4 tiles from the cathedral, a cooldown after the crusaders
die (1.09); Jihad: peasants become Jihad warriors; bribery (not crusaders or peasants).

## Decision

* **Casting** (command 27; `game.caster`): the caster walks into range (spell range + `abilityRange` tiles — Medicine;
  range 0 = touch), waits the cast time, then pays and applies: mana (`ManaCost`; summons add the summoned type's mana,
  ⚠ HYPOTHESIS capped at the caster's whole pool, since golems cost 600m and dragons 800m against `max_manna = 100`),
  a book's charge and cooldown, a summon's resources and population. Validation at the order (technology gate per
  civilization, mana, cooldown, target) and again at the effect; an interrupted cast costs nothing. One cooldown per
  caster. Casting (and attacking) ends invisibility.
* **Effects**: damage (amount, no defence: UNVERIFIED), heal, bless (+% attack), curse (−% attack and defence), freeze
  (no moving or acting), stone (no acting, no damage), cloak (not a target for other kingdoms; visibility consults
  `World.IsCloaked`), confusion (hostile to all), summon (next to the caster), raise dead (the oldest corpses within the
  radius rise; the corpse decals are consumed: `DecalIndex.TakeCorpse`), reveal (a revealer entity with a lifetime:
  magic vision), experience (Mastery), area damage in pulses (rain of fire hits own units too, manual; whirlpools only
  ships), kill (murder, souls; not heroes). Spells with a projectile (freeze bolt, flame bolt) carry the effect to the
  target (ADR 0091). Lasting effects live in `game.status` (end ticks) and the store entry goes when all ended.
* **Exorcism** is the priests' attack (order `exorcism` = attack) with the BONUS.INI bonuses; Inquisition raises it
  (+50% attack of priests, UNVERIFIED).
* **Crusades** (command 32, ⚠ HYPOTHESIS UNK-26): a cathedral with the crusade technology places an anchor (the
  `CRUSADE_ANCHOR.INI` cost) within 4 tiles; the anchor raises `crusadeSize` crusaders (UNVERIFIED 5), one per tick;
  once all its crusaders are dead (also inside holders — 1.10 fix) the anchor stays for `crusadeCooldownMs` (UNVERIFIED
  60 s) and then goes; one anchor per kingdom at a time.
* **Conversions** (command 30): a unit of a `ConvertsFrom` source turns into the target type (Jihad warriors from
  peasants, priests from monks) for the target's cost and after its build time, if the order's technology and the
  target's requirements hold; level, experience, items and name carry over, hit points in proportion (UNVERIFIED); a
  new order cancels it with a refund.
* **Bribes** (command 31): a peasant with Bribery walks to another kingdom's unit (not peasants, crusaders or heroes)
  and, on arrival, pays `bribeCostPercent` (UNVERIFIED 200%) of its cost in gold; the unit changes sides.

## Alternatives considered

* **Paying at the order:** a cancelled or interrupted cast would cost mana for nothing, or, refunded, invite spam.
* **Summons over the pool cost:** they could never be cast with the original pools; the cap keeps them castable with a
  full pool until captures say how the original does it.

## Consequences

* Spies' impersonation and detection belong to visibility (GAME-12, the other stream) — not implemented here.
* Spell spam is bounded by mana, cooldowns, charges and the one-cast-at-a-time caster state.
