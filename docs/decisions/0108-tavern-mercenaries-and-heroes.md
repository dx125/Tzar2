# 0108. Hire mercenary groups and heroes in the tavern from per-period offers derived from the seed

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (M3 stream F follow-up), product owner (follow-up after streams E and F)
* **Related:** TDD §10 GAME-08, GAME-14, UNK-23, UNK-28, ADRs 0092, 0100, 0101, 0103

## Context

Manual ch. 5: the tavern is "a place for gambling, exchanging resources, hiring heroes and mercenaries"; ch. 6
(Warriors' Guild): Mercenaries "makes the recruitment of small groups of soldiers in the taverns possible", Heroes' Quest
"makes the recruitment of heroes possible", Fame "enables the recruitment of more, and better, mercenaries who will be
cheaper and more experienced". `MENU.INI` has the submenus `[tavern_merc]` and `[tavern_hero]` but no entries: the game
fills them at run time. What is offered, at what price and how often is unknown (UNK-28; levels UNK-23). Stream E (ADR
0092) provides experience levels, the hero flag and `HEROES.INI` names.

## Decision

* **Offers** are a pure function of the match seed, the kingdom, the tavern period (`world.tavernRefreshMs`, 120 s)
  and the slot. Slots 0 … `mercenaryOffers` − 1 (3) are mercenary groups of `mercenaryGroupMin` … `mercenaryGroupMax`
  (3-5) soldiers at `mercenaryLevel` (1); the next `heroOffers` (1) slots are heroes at `combat.heroLevel`. The unit type
  is drawn from the kingdom's pool: military land units (foot or mounted) of its civilization that some building trains,
  in type-id order. Fame adds `fameGroupBonus` soldiers and `fameLevelBonus` levels and keeps `fameCostPercent` of the
  price. All numbers UNVERIFIED.
* **Price:** the type's training cost × units × `mercenaryCostPercent` (150) or `heroCostPercent` (600), per resource,
  paid at once (shared resources apply).
* **`Hire` command** (47, Arg0 = slot, entities = a tavern): needs a completed own tavern, Mercenaries (heroes: Heroes'
  Quest; `hmnotech` waives both), an offer not hired this period (`OfferTaken`), population room and an entity slot per
  unit (`NoRoom`), the price (`NotAffordable`). The units appear next to the tavern at the offer's level whatever the
  kingdom's level cap (the tavern sells trained soldiers); a hero gets the hero flag, a `HEROES.INI` name and
  `HeroPromoted`; event `Hired`.
* **State:** only which slots of which period were hired: `KingdomState.TavernTaken` (bit per slot, in the former
  padding byte) and `TavernPeriod`; range-checked on restore (no bit beyond the slots, no future period). The pools are
  derived from the ruleset.
* **AI:** none of the legacy scripts (`DATA/AI/*.AI`, `*.AIS`) hires: they only build taverns (`critical rule have
  tavern end`) and research the Warriors' Guild technologies, so the AI is not wired to `Hire` (a note for the AI
  stream if scripts that hire appear).
* **Stream E integration checked:** `World.IsHeld` and `World.IsCloaked` are stream E's; visibility's `IsConcealed`
  calls `IsCloaked`, revealers and `CanSee` skip held units, combat skips concealed and held targets. Gap fixed here: a
  detector inside a holder no longer detects cloaked units.

## Alternatives considered

* **Offers as stored state refreshed by a system:** more state to validate for no gain; the pure function needs only the
  hired bits.
* **A fixed mercenary list in the rules:** the data has none; the pool follows the kingdom's own army (a mod can still
  narrow it with the traits).

## Consequences

* The client has no tavern menu yet; `TavernOffers.Offer` and `CostOf` give it the offers and prices.
* Captures of the tavern menus (what types, how many, prices, refresh) replace the constants.
* Goldens: with `GameplayScenarioOptions.Taverns` (on in the gameplay golden, off by default so the benchmark workloads
  stay the same, ADR 0080) kingdoms 0 and 1 (of three or more) get a tavern and hire offer 0 and the hero at tick 600.
  Re-pinned: gameplay ruleset `7C807B7471809ECC`, stream `BFA7F2738E087580`, final `A1F0F5296E4CDFA5`. The CI bench hashes
  (synthetic, gameplay, AI) are unchanged (`KingdomState` keeps its size: the new fields fill its padding).
