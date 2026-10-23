# 0220. Make the main campaign completable: scenario-engine fixes and a stronger solver

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** maintainer agent (work queue item 10)
* **Related:** ADRs 0025, 0130, 0134, 0135, 0146, 0188, 0190; DIP-009, SHELL-013, GAME-09, ITEM-006, WORLD-016…018;
  UNK-43, UNK-64, UNK-81

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
by cliffs (UNK-81). The owner settled UNK-81 on 2026-10-22: in the original, cliffs stop every ground unit except
ninjas, and the intended way onto `rajba`'s plateau is ninjas (original footage: https://youtu.be/CxPm7ja1ujI?t=955).

Where the original's files say nothing, ReTzar's reading of its code is taken by default (owner, 2026-10-22; ADR 0190);
captures beat it.

## Decision

**Engine (the engine was wrong or a placeholder had a better reading):**

* **Scenario kingdoms never resign.** The resign flag exists only on the random-map and multiplayer screens (1.09,
  SHELL-013) and is off for a campaign mission (ReTzar `GameRules.ai_resign`), so `GameSession` gives the AI
  `MayResign = setup.Scenario is null`.
* **A scenario's Object Orders move units whatever the state of their kingdom.** The new
  `CommandSystem.ExecuteScripted` runs the in-tick validation without the "player active and not defeated" check.
* **A unit inside a holder is in an area where its holder is.** What a holder holds takes its cell (ReTzar 0x500a50),
  and the area event tests positions only. `alex1`'s `ShipsNearHarbor` names the heroes together with hulks in a
  harbour that is almost all water.
* **`$cargo` is what is aboard or inside, a hero's items too; nothing for an object with no room** (ReTzar 0x4b8c76).
  `SomeoneInGalleon: $cargo > 0`, `HulksLoaded: $cargo >= 10`.
* **Movement: mutual allies are friends (WORLD-016).** Their units shove idle ones aside, queue behind each other and
  no longer bounce off each other like enemies. The original's steps test no owner (ReTzar); the rebuild keeps
  enemies as hard obstacles (ADR 0025). This is the `Frame.Friends` table in `MovementSystem`.
* **Movement: a unit that meets a blocked corner almost head-on slides past at the step's full length (WORLD-017).**
  A galleon wedged on a coast corner moved 1 unit per tick sideways and was pulled back by each re-plan. This is a
  rebuild artifact (the original steps tile to tile), not a rule of the original.
* **Cliffs stop every ground unit but the ninja (WORLD-018, UNK-81 settled).** The ninja gets its own locomotor,
  `climber`. It is infantry everywhere else: data and technology effects that name "foot" include it
  (`LocomotorExtensions.Family`, `IsA`). A cliff tile is open to a climber when, along a row, a column or a diagonal,
  the cliff run through it is one or two tiles long with climbable ground at both ends. ReTzar (0x4a99a0) climbs a
  first cliff cell, crosses a second, and never takes a third in a row. The original decides this per step, so here a
  climber can walk along the top of a long, thin band and there it cannot. The original's ninja also climbs walls;
  walls are not done yet. The importer gives the class `ninja` this locomotor, because no data file says it. The
  maps' tile words also show that bit 4, not bit 0, is the column inside a cliff piece (wmp.md §5 corrected).

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
  * Fetching an item, the solver prefers a unit that can walk to it: a ninja climbs to the book on `rajba`'s ledge.
  * The object an area goal is measured around (Marsim) is never attacked or cleared.
  * On retries its allied kingdom gets `hmgod`.
  * `$health < $Var` about the kingdom's own hero (an ambush must wound him) switches invulnerability off until the
    event is true.

## Alternatives considered

* **Deleting the player's own units to empty the west of the map:** rejected. The solver removes only the units it
  spawned.
* **A half-piece cliff rule** (an earlier version of this ADR, behind a switch): every cliff tile blocking except
  part of each piece. The owner's check showed that the plateau is meant for ninjas, so the switch was removed.
* **Peasants carry picked-up resource items to a storage:** ReTzar confirms it (a peasant's right click on a resource
  item is Gather, program 0x1e). It is not done here; the tribute stands in for `bigisland`'s gold (follow-up).

## Consequences

* Goldens move: movement (allies, corner slide), the trigger engine (held units, cargo, scripted orders), scenario AI
  resignation and the ninja's locomotor (ruleset hash). They are re-pinned in the same PR.
* `HumanVsAiTests` on Archipelagos plays 20 minutes (2026-10-23).
  * The landed wave has hit after 17 minutes since the original's bodies (WORLD-015, #126). Before #126, from #118, it
    never hit within 24 minutes.
  * The first hit comes at 17.2 minutes before the forests of ADR 0201 and at 18.4 on `main` with them and this ADR's
    movement (#165). This ADR does not delay it.
  * Runs of 14 minutes passed only on the Hard AI's xebec shooting the human's woodcutters. With the forests, the
    corner slide moves where the xebec parks (tile 26,37 instead of 32,41), out of range.
* Cheat assistance and the solver's attacks spare an enemy that an enabled rule (not one that makes the local kingdom
  lose) wants alive, an ObjectDestroyed event under "!", until that rule fires; attacks leave the heroes a pending goal
  names (2026-10-23, `v_02`).
* `t2 campaign run --survey` on the GOG install (2026-10-22): main campaign 19 of 19, tutorial 7 of 7 (STATUS M4).
* Follow-ups, done 2026-10-23: ninjas climb walls (WORLD-018); peasants deliver resource items where the scenario
  wants them (ECO-023, ECO-024), so `bigisland` gets its gold by Azim; the solver tributes a friendly kingdom only when
  no rule still to come gives it objects, takes a resource item with the peasant the rule names, and cheat assistance
  spares the enemies near a unit a rule waits to see wounded ("WoolinIsWounded").
