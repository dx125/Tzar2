# 0133. Drive campaigns with an explicit flow over the scenario IR, keep progress in a JSON profile, save missions as snapshot + progress

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent M4-B
* **Related:** TDD §7.2 (`Tzar2.Game`: campaign state machine, save/load), §10 GAME-20, §17.1 REQ-SCR-01 (persistence),
  §18.5 REQ-EDT-08, §19.5 REQ-NET-10, §22 item 5, Appendix C.9, Appendix G-03 (accepted by the Q-08 default), ORIG-08,
  UNK-68, UNK-74; ADR 0092 (persistent heroes), ADR 0120, ADR 0130, ADR 0132

## Context

A campaign is a set of maps with entry maps. The order lives inside the maps: Load Map, Player Win and Player Lose name
the next map (Appendix C.9). In the original campaigns only Load Map is used, with `MAPS\<name>.wmp` (stream A). Units
flagged Persistent carry their properties to a unit with the same name on later maps. Variables written `#Name` look
campaign-scoped (UNK-74). The original crashed or hung on campaign transitions (ORIG-08). G-03 asks for campaign
progress in a separate, stable, human-readable profile that survives updates. REQ-NET-10 asks for quick saves and
campaign-progress saves. M4's exit criterion is "all original campaign missions completable".

## Decision

* **Sources:** `ICampaignSource` gives the `CampaignData` graph (ADR 0120) and loads a map's `MissionContent` (`MapData` +
  `ScenarioData`). Implementations: `InMemoryCampaignSource`, the built-in **`DemoCampaign`**, and converted legacy
  campaigns (the converter, stream A).
* **Mission setup** (`ScenarioMatch.Setup`):
  * one slot per kingdom up to the highest one used; the first active Human-or-AI kingdom is the local player and the
    others are computers running the legacy AI;
  * civilizations, victory condition, population limit and No Fog / Explore come from the scenario;
  * the seed is the hash of the map and scenario;
  * an `IMatchPreparation` runs once for a new match, before the first tick and never on restore. It applies the
    kingdom table's stockpiles and diplomacy (`ScenarioOperations.SetDiplomacy`; shared victory follows the kingdom's
    flag), then the campaign carry-over.
* **Flow** (`CampaignFlow`, an explicit state machine):
  * `Begin` unlocks the entry maps and makes the first one current; a campaign without an entry map is invalid.
  * `Finish` stops the session's sim thread and reads the local kingdom's outcome: won, lost, or "moved" for a scenario
    transition without an outcome.
  * `End` decides the next map:
    1. the scenario's requested map name, matched case-insensitively against the map names and ids;
    2. otherwise, after a win, the map's only Win/Load Map edge;
    3. otherwise the campaign is finished.
    A loss without a requested map makes the mission current again (UNK-68). `End` records the history, unlocks the
    next map, and captures the carry-over (not after a loss).
* **Carry-over** (`CampaignCarry`): at the end of a map, every alive unit whose object is flagged Persistent and named
  becomes a `PersistentHero` record (ADR 0092: level, experience, hero, potions, items), merged by name over the
  earlier records. Units of every kingdom count (UNVERIFIED, UNK-68). At the next map's setup, the record goes to the
  object flagged Persistent with the same name. `#` variables are copied out and back in by name.
* **Profile** (`PlayerProfile`, `ProfileStore`): `profile.json` in the user folder.
  * Indented, canonical UTF-8 JSON with a `version`: name, and per campaign id the hash, current map, completion,
    unlocked maps, history (map, result, ticks), heroes and carried variables.
  * Written to a temporary file, then moved into place.
  * Reading is bounded and range-checked. A newer version or a malformed file gives `InvalidDataException`; the client
    keeps the file and starts a fresh profile. Older versions are migrated on read (there is only version 1 so far).
  * The campaign hash is recorded so that a changed campaign can be reported; it does not block loading.
* **Mission saves** (`SaveGame`, `.t2sav`): magic, version, campaign id, map id, label, tick, the campaign progress at
  save time (as profile JSON), and the sim snapshot (which holds the trigger state, ADR 0130).
  * `Resume` rebuilds the same match setup from the campaign map and restores the snapshot. The hash stream continues
    exactly (tested).
  * `GameSession.CaptureSnapshotNow` asks the running sim thread to take the snapshot between two ticks.
  * The client binds F2/F3 to a quick save slot for campaign missions. The save and load dialogs, and skirmish and
    multiplayer saves, remain M6.
* **Client:** the main menu gains CAMPAIGN, which opens the `S_CAMPAIGN` screen. It lists the campaigns with the maps'
  state (done / open / locked) and offers PLAY (the current map or a chosen open one), RESTART and BACK. A scenario's
  `MapRequested`, or the end of the match, finishes the mission through `CampaignFlow`. The client then saves the
  profile and starts the next map, or returns to the campaign screen. `--campaign [play]` opens the screen or starts
  the current demo mission directly.
* **Headless runner** (`CampaignAutoplay`, `t2 campaign run`): plays every mission of a campaign in a synchronously
  stepped session. A scripted solver:
  * answers conversations;
  * walks the units a scenario names into the areas its Object In Area events wait for;
  * attack-moves the army toward the nearest enemy;
  * with cheat assistance (on by default), reinforces with debug spawns after 5 minutes and kills the enemies' units
    and buildings after 10.
  The report lists, per mission: completed or not, result, ticks, the strongest assistance used (play / reinforced /
  cleared / timeout), the next map, and the scenario faults; `--report` writes it as JSON. Loading and converting the
  original campaigns from `TZAR2_ORIGIN` is the harness of the exit criterion.

## Alternatives considered

* **Progress inside the save games only:** an update that breaks snapshots would lose the campaign; G-03 asks for the
  separate profile.
* **Carry over every named unit (what `PersistentHeroes.Capture` collects):** the editor's flag is "Persistent", so only
  flagged objects carry.
* **Solve missions with `hmnext`:** it skips the scenario's own transitions (Load Map), so the campaign graph would not
  be exercised.

## Consequences

* The demo campaign gives the campaign screen, the tests and the CLI a campaign without original files.
* The exit criterion's "completable" means "the solver reaches the next map". The report says how much cheating that
  took, so missions that need a human (or a better solver) are visible.
