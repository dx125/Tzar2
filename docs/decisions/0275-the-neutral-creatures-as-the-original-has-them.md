# 0275. Give the neutral creatures the original's rules: prisoners freed, gateways taken by presence, cows only poached

* **Status:** Accepted
* **Date:** 2026-11-04
* **Deciders:** maintainer (Claude), at the owner's request after the ninth play-test
* **Related:** NAT-001, NAT-002, NAT-008, UNK-66; ADR 0060, ADR 0104 (amended), ADR 0134, ADR 0271; ReTzar
  `src/gameplay.rs`, `src/random_map.rs`

## Context

On Conquer the World the owner's knights were ordered to attack some yellow peasants, and every attack was refused
(`NotAllied`). In the original these peasants are grey and neutral, and a unit takes them by coming close. The owner
asked for a review of all of the neutral creatures' rules.

A probe of the original's 33 maps (local only) lists the owners of what belongs to no kingdom:

* **Owner 9, the rescue pseudo-kingdom:** 101 peasants, 21 cows, 15 magic gateways, houses, a castle, barracks, towers
  and storages.
* **Owner 10, Nature:** bears, birds, eagles, bridges, walls, towers, dummies, fields, a smithy, 12 cows, and dwarves,
  orcs, ghosts and skeletons.
* **Owner 0:** only fish and resources.

ReTzar reads the original's code as follows.

| | The original | Tzar2 before |
| --- | --- | --- |
| Owner 9's units | Wait to be freed (0x44092d). Once a second each joins the kingdom of the first fighter on a tile beside it (0x502e18). Kingdoms barred by `@kingdomcanrescue` free nobody. | A passive kingdom, neutral both ways, in a kingdom's colour; nobody could free its units (UNK-66) |
| Relations of owner 9 | Nobody attacks it (R[x][9] = 0x40); it fights the kingdoms (R[9][x] = 0x10) | Neutral both ways |
| Owner 10 (Nature) | Fought by everyone, and it fights the players | The same, except Nature's working buildings and units: they went to the passive kingdom above, neutral |
| Magic gateways | Every second, the fighters in its footprint grown by one take it when they are all of one kingdom, from whoever held it (0x420fd0). Nothing hurts it (0x4b6740); its owner cannot delete it (0x421190). | Nature's only, by the nearest military unit within 16 px; a kingdom's gateway could be hurt and deleted |
| Cows of nobody's or another kingdom's | Every two seconds the peasants beside it take it when one of them knows Cow Poaching and none is of its kingdom or an ally (0x4ec944). Touching does nothing. Nature's cows are fought as Nature is. | Any unit's touch took a wild cow; nobody fought one |
| Random maps | No cows at all (0x4b1560) | Four Nature cows near each castle, more scattered |
| Bears, birds | Fight near home; never attacked | The same (NAT-003, NAT-002) |

The right click is a separate question. The original's right click does not attack what its relations spare; an
explicit Attack order may target anything. Tzar2's right click attacked any other kingdom's unit, and the sim
refused the attack.

## Decision

* **The rescue kingdom** has `PlayerFlags.Rescue`, set by the match preparation for the legacy owner 9.
  * `World.MayAttack` spares it for every other owner.
  * Its units look for no target; its towers do.
  * Its stance is Enemy toward every kingdom. The kingdoms are Neutral toward it, and `SetDiplomacy` refuses either
    direction.
  * The AI's census leaves it out.
* **Prisoners are freed** (`NatureSystem.Rescue`, every second). A unit of the rescue kingdom joins the kingdom of the
  first fighter standing within one tile of it, with the units it holds.
  * A fighter is a military unit of a kingdom that may rescue: in the match, not defeated, not the rescue kingdom, and
    without `PlayerFlags.NoRescue`.
  * `@kingdomcanrescue(player, on)` sets or clears `NoRescue`.
* **Nature's working buildings and units** go to a second passive kingdom, at war with everyone as Nature is. With no
  slot left, they join the rescue kingdom as before.
* **Magic gateways** (`ClaimOnTouch: { by: military }`) are taken by presence from any owner, checked every
  `claimIntervalMs`, now 1000 ms. The new trait `Indestructible` (no damage, no Delete) is set on the gateway.
* **Cows lose `ClaimOnTouch`.** `NatureSystem.Poach` applies the original's Cow Poaching every two seconds. Tzar2's own
  Capture order on a cow stays. Random maps place no cows (`MapGenCatalog.Classic`).
* **The client.**
  * The rescue kingdom draws without colours.
  * A right click on a prisoner, or on a magic gateway not yours, walks up to it (`EntityInfo.TakenByComing`).

## Alternatives considered

* **Keep the rescue kingdom neutral both ways.** Its peasants could then not be freed, and soldiers could fight its
  armed units. That is what the owner met in play.
* **An explicit Attack on anyone, as the original allows.** This is a separate rule of orders (ReTzar 0x435500) with
  its own risks: friendly fire by mistake, and the AI. Left out; walking up frees prisoners.
* **Keep a few cows on random maps for the start.** The original has none. A kingdom breeds them at its farm.

## Consequences

* **Campaign maps** with owner 9 now free their prisoners as the original does, and their gateways can be taken.
* **Wild cows** are rarer (only authored maps), and soldiers kill them. The economy of random maps no longer has cows
  at the start.
* **The hashes change** (the ruleset's claims, cows and gateways; the random maps' content).
