# 0271. Let saves outlive the version that wrote them

* **Status:** Accepted
* **Date:** 2026-10-28
* **Deciders:** agent (Claude), product owner (who reported a save that no longer loaded)
* **Related:** ADR 0019 (snapshots are validated input), ADR 0133 (save games), REQ-SIM-13, SHELL-014

## Context

A save game (`.t2sav`) holds a sim snapshot. The snapshot was read only by the very version that wrote it:
* the same format version;
* the same ruleset content hash;
* the same full-state hash.

The ruleset hash changes with nearly every gameplay change. The snapshot format changed twice in four days: 22 added the land units' cell steps (ADR 0270), 23 the tavern exchange. So every save stopped loading within a day or two.

The client also failed silently. A custom map's save that would not restore opened the list of maps. The owner's saves of 2026-10-25 (format 21) failed this way after #255.

Replays, rejoins and desync reports need the exact state: a lockstep peer must hash the same as the others. A player continuing a save needs only a sound state.

## Decision

* **Two tolerances.** `SnapshotTolerance.Exact` (the default) keeps the old checks for replays, rejoins and desync tools. `SnapshotTolerance.SaveGame`, which `SaveGame`'s resumes use, reads a player's save leniently.
* **Older formats are upgraded**, back to `WorldSnapshot.OldestSaveVersion` (21):
  * a store a format did not have yet (`core.cellstep` before 22) stays empty;
  * a kingdom's record shorter in an older format (16 bytes before 23) is padded with zeros;
  * each later format change adds its step here and keeps the old fixture loading.
* **Other rules are accepted when their tables match.** From format 24 the snapshot carries `Ruleset.TablesHash`. It hashes what a state refers to by index:
  * the keys of terrains, entity types (with their footprints), resources, technologies, upgrades, spells and civilizations, in order.

  A save made under other rules with the same tables goes on under the current rules; other tables are refused. Older formats carry no tables hash and are taken as they come.
* **What is still checked.** The state is always validated (ADR 0019). The stored full-state hash is checked only when the format and the rules are the same.
* **The player is told.** A save that does not load says why, and the player goes back to the load screen. A save that continued under other rules says so in the game's message lines (`GameSession.RestoredExactly`).
* **A fixture guards the path.** `tests/Tzar2.Sim.Tests/Fixtures/save-format21.t2snap` was taken by the format 21 code on a synthetic map. `SaveCompatibilityTests` restores it, plays on, and checks the rules and tables cases.

## Alternatives considered

* **Saves as a command log, replayed under the new version.** Every version change alters the outcome. The replayed match would not be the one saved, and a long match takes long to replay.
* **No compatibility, a clear message only.** The owner plays builds that change daily; every save would be lost.
* **A migration per ruleset change.** It is not feasible: rules change almost every day. Tables change rarely, and the tables hash catches it when they do.

## Consequences

* Each snapshot format change must add its upgrade step and keep `SaveCompatibilityTests` passing. To drop old saves on purpose, raise `OldestSaveVersion` in the same PR, with the reason.
* Adding, removing or reordering an entity type, technology, upgrade, spell, resource, terrain or civilization makes older saves refused, with a message. Appending new ones keeps the old indices, but still changes the hash: refused too. An index remap would be the follow-up if this happens often.
* A save continued under other rules plays on with the current balance. Its outcome differs from the version that wrote it, which is what the player expects of a newer build.
