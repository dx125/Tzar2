# 0220. Make the main campaign completable: scenario-engine fixes and a stronger solver

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** maintainer agent (work queue item 10)
* **Related:** ADRs 0025, 0130, 0134, 0135, 0146, 0188; DIP-009, SHELL-013, GAME-09, ITEM-006; UNK-43, UNK-64, UNK-81

## Context

The M4 exit criterion is that every original campaign mission is completable (ADR 0134). On `main` 7545fa8 the survey
reported 17 of 19. That number was inflated. Since DIP-009 (#128) every computer kingdom of a campaign map ran
`RESIGN.AI`, so kingdoms with no town resigned within seconds:

* `saa3` was "won" at tick 21, and `alex1`, `benas`, `vsaa` and `rajba` were "won" by resignations plus clearing.
* The kingdom that only holds a campaign's heroes resigned too. Its heroes then ignored the scenario's own orders,
  because a defeated owner's commands are rejected. `hmgod` for them was rejected as well. That broke `bigisland` and
  `v_01`.

With resignation fixed, the honest count was 15 of 19: `alex1`, `bigisland`, `vsaa` and `rajba` failed. They fail
deep inside long scripted chains with ferries, deliveries, wounds and evacuations. The `rajba` mage plateau is sealed
by cliffs (UNK-81).

## Decision

**Engine (the engine was wrong or a placeholder had a better reading):**

* **Scenario kingdoms never resign.** The resign flag exists only on the random-map and multiplayer screens (1.09,
  SHELL-013), so `GameSession` gives the AI `MayResign = setup.Scenario is null`. UNVERIFIED.
* **A scenario's Object Orders move units whatever the state of their kingdom.** The new
  `CommandSystem.ExecuteScripted` runs the in-tick validation without the "player active and not defeated" check.
* **A unit inside a holder is in an area where its holder is.** Positions follow the holder (GAME-09). `alex1`'s
  `ShipsNearHarbor` names the heroes together with hulks in a harbour that is almost all water. UNVERIFIED.
* **`$cargo` of a holder is the number of objects it holds.** For a worker it stays the carried load. Evidence:
  `SomeoneInGalleon: $cargo > 0` and `HulksLoaded: $cargo >= 10`. UNK-64 stays UNVERIFIED.
* **Movement: mutual allies are friends.** Their units shove idle ones aside, queue behind each other and no longer
  bounce off each other like enemies (ADR 0025 meant enemies). This is the `Frame.Friends` table in `MovementSystem`.
* **Movement: a unit that meets a blocked corner almost head-on slides past at the step's full length.** A galleon
  wedged on a coast corner moved 1 unit per tick sideways and was pulled back by each re-plan.
* **UNK-81 candidate, off by default:** `LegacyConversionOptions.Unk81HalfCliffs` (`t2 campaign run
  --unk81-half-cliffs`). It opens the left column of vertical cliff runs (piece shape 6). This is a measurement aid
  only and needs the owner's in-game check. It is the simplest single-shape rule that opens the `rajba` plateau along a
  long, corridor-like route. The cliff atlases (`18.BMP`, `19.BMP`) put most of a run's rock in one row or column of its
  2 × 2 piece, which supports some half-piece rule, but not which half. The maps' tile words also show that bit 4, not
  bit 0, is the column inside a piece (wmp.md §5 corrected).

**Solver (`CampaignAutoplay`; it still only reads trigger state and issues allowed commands):**

* **Rules that wait on someone else are deferred.** A rule whose condition holds a fixed false part is skipped: today
  that is only an enemy kingdom's stockpile. So is a rule whose events the solver once made true without the rule
  firing. Later rules come first, so units no longer walk back and forth between two goals.
* **Tribute.** For `@gold(k) >= n` about a kingdom that is not an enemy, the solver sends the shortfall as a tribute.
  `Tribute` is a player order and joins the allowed list. The local stock is topped up by the resource cheat when short.
* **Negated events it can act on:**
  * `!GoldNotTaken`: a unit picks the named item up.
  * `!ForceNotToTheEast` (no unit of mine at `$x < n`): every counted unit goes over the line, walking or by ferry, and
    sailing when only water lies beyond. The solver removes the reinforcements it spawned itself (it undoes its own
    assistance); the scenario's units all go.
* **Ferries:**
  * The units and the carrier chosen first keep the job: typed goals, item deliveries and pickups.
  * Ships are never walkers.
  * A ship must have water near both the units and the goal: no lake ships. A ship that refuses three unload orders
    from where it lies is dropped.
  * The solver lands on the ground inside a goal rectangle, the rim included, not at its watery centre.
  * Loaded or docking ships leave the army.
  * The army steps back from the shore when it hems the boarders in.
  * Cargo loading never takes the units a goal names.
  * Unload orders wait while a docking is under way.
  * Units sail over a coordinate line at sea (`$x <= 5`: leave by the west edge).
* **Protection:**
  * The object an area goal is measured around (Marsim) is never attacked or cleared.
  * On retries its allied kingdom gets `hmgod`.
  * `$health < $Var` about the kingdom's own hero (an ambush must wound him) switches invulnerability off until the
    event is true.

## Alternatives considered

* **Peasants carry picked-up resource items to a storage** (`bigisland`'s quest: "let him take it, then return him
  to the village's storage"). This needs a carried load that survives a change of owner and a "default" order that
  delivers. That is three speculative engine changes, so the tribute stands in. The question is open below.
* **Deleting the player's own units to empty the west of the map:** rejected. The solver removes only the units it
  spawned.
* **A per-variant cliff orientation rule:** the variant does not encode the side (variant 1 is used for both west and
  east walls in `rajba`), so a geometric guess would be no better founded.

## Consequences

* Goldens move: movement (allies, corner slide), the trigger engine (held units, cargo, scripted orders) and scenario
  AI resignation. They are re-pinned in the same PR.
* `t2 campaign run --survey` on the GOG install (2026-10-22): main campaign 18 of 19 by default and 19 of 19 with
  `--unk81-half-cliffs`; tutorial 7 of 7 (STATUS M4).
* Revisit when the owner settles UNK-81 (screenshot below), and when captures show how area events and `$cargo` treat
  units aboard.

## Questions for the product owner

* **UNK-81, rajba:** in the original, open `rajba` and walk Sartor's army into the north-west cliff rings toward the
  mage tower (about tile 25, 20). Take a screenshot where a unit stands on the left half of a vertical cliff run,
  e.g. tile 6, 22 (the west wall) or 12, 24, or the message that the ground is blocked. A unit standing on those tiles
  confirms the switch's rule. Units that cannot enter, or reach the mages another way, settle it the other way.
* Does a peasant who picks up a gold item carry it to a storage, as `bigisland`'s quest text says?
