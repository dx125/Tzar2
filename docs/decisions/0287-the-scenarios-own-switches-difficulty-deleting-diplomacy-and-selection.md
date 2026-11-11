# 0287. A scenario reads the game's difficulty and the player's selection, and can forbid deleting and diplomacy

* **Status:** Accepted
* **Date:** 2026-11-10
* **Deciders:** maintainer (Claude), the editor review (batch D)
* **Related:** SCN-005, SCN-006, SCN-007, SCN-008; UNK-64; ADR 0120, ADR 0130, ADR 0133, ADR 0271; STATUS item 26.6

## Context

The trigger engine read six reserved variables as 0: `$difficulty`, `$flags`, `$program`, `$isselected`,
`$canusediplomacy` and `$candelobjects` (UNK-64). ReTzar reads the original's code for them. A count on the owner's
install shows which ones the original's maps use:

* **`$difficulty`, 6 times in the main campaign.**
  * Enemy waves come after `(6-$difficulty)` minutes, and later ones after 7 or 8 minus it.
  * A computer kingdom gets `2300 + $difficulty * 400` food.
  
  Read as 0, every difficulty played the easy waves.
* **`$candelobjects = 0`, in five missions of the main campaign.** It is the original's switch that stops the player
  from deleting objects (ReTzar: 0x5cf6b0 = !value; Delete does nothing while it is set).
* **`$isselected == 1`, in the tutorial's first lessons** ("select the peasant"). Read as 0, those lessons never go on.
* `$canusediplomacy` (0x5877a0), `$flags` and `$program` are not used by any original map.

Separately, a campaign save did not keep the campaign's difficulty: it resumed at normal.

## Decision

* **`$difficulty`** reads the game's difficulty: 0 easy, 1 normal, 2 hard. `MatchSetup.GameDifficulty` is the
  computer kingdoms' difficulty, which a campaign and a custom map set alike; without computer kingdoms it is normal.
  It goes into `TriggerOptions.Difficulty`. A write is ignored, as ReTzar does.
  * The options' fingerprint includes it only when it is not normal, so every existing snapshot keeps its
    fingerprint.
  * A campaign save keeps the difficulty: save format 4, written only when the difficulty is not normal. Loading a
    campaign save continues at that difficulty.
* **`$candelobjects` and `$canusediplomacy`** are write-only switches in the trigger state; reading them gives 0.
  * At 0, the simulation refuses the human kingdoms' Delete, or their diplomacy and tribute commands
    (`CommandRejection.ScenarioForbids`), through `IScenarioController.Forbids`. Computer kingdoms are not bound.
  * The game says so, and does not open the diplomacy and tribute windows (`ScenarioView.CanDelete`,
    `CanUseDiplomacy`).
* **`$isselected`** reads whether a human kingdom has the object selected. The selection is the interface's, not the
  simulation's, so the client tells the simulation with a `ScenarioSelect` command whenever the selection changes,
  and only when the scenario reads `$isselected` or `$flags` (`ScenarioPresenter.UsesSelection`). The command goes
  through the lockstep and the replays like any order, and changes no unit.
  * `$flags` models only bit 0 (selected) of the original object record.
  * `$program` (the object's order in the original's numbering) still reads 0, UNVERIFIED.
* **The trigger state** keeps the switches and the selections in format 3. A state without them is still written as
  format 2, byte for byte, so the hashes of every scenario that does not use them stay the same.

## Alternatives considered

* **Selection in the simulation for every match:** every selection change would travel through the lockstep and the
  replays, for the few scenarios that read it.
* **The local player's selection only, as the original's:** in multiplayer each peer would read its own and desync.
  The human kingdoms' selections, sent as commands, are the same on every peer.
* **Refusing Delete only in the client:** a command from elsewhere (a replay, another client) would still delete. The
  simulation decides, as it does for every order.

## Consequences

* The main campaign plays its normal and hard difficulties as the original: enemy waves come sooner and the computer
  kingdom gets more food. The campaign acceptance runs at normal and now sees these waves.
* The tutorial's selection lessons go on when the player selects what they ask for.
* Deleting objects is forbidden where the campaign forbids it.
