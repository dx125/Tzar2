# 0163. Apply the drop policy in the sim: standby AIs for every human kingdom and a SetController command

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (M6)
* **Related:** REQ-NET-07, ORIG-06, REQ-AI-03, ADR 0051 (AI in the lockstep), ADR 0160, ADR 0162

## Context

REQ-NET-07: after the drop timeout a dropped kingdom "is taken over by AI (lobby option), set neutral, or defeated. A
rage-quit never desyncs the others." The AI is a sim system (ADR 0051): it must make the same decisions on every peer,
and its state lives in the snapshot. Attaching an AI in the middle of a match would change the pipeline after the first
tick, which `LegacyAiSystem.Attach` forbids, and a rejoining peer restoring a snapshot must end up with the same
pipeline as everyone else.

## Decision

* **Standby AIs**: in a multiplayer match every human kingdom gets an AI from the start (`AiPlayerSettings.Standby`).
  A standby AI does nothing until its kingdom carries the new `PlayerFlags.AiControlled`. The program hashes of the
  standby AIs are part of the match fingerprint (`MatchSetup.AiPrograms`), so peers agree on them at the barrier.
  Single-player setups are unchanged (no standby AIs, same fingerprints, same golden hashes).
* **`CommandKind.SetController` (48)**: `Arg0 = 1` sets `AiControlled`, `0` clears it. The game issues it, stamped
  with the kingdom, from the relay's control events in the closed tick: `Takeover(AiTakeover)` → 1, `Returned` → 0. Peers'
  own batches can never carry it: the lockstep driver drops it before the sim (otherwise a player could hand their
  kingdom to the AI, or someone else's).
* **Neutral**: `SetController(0)` plus `SetDiplomacy(neutral)` from the kingdom towards every other kingdom (subject to
  the normal diplomacy rate limit). Nobody commands it; it stops being hostile on its side.
* **Defeat**: `Resign`, stamped with the kingdom.
* A taken-over player who rejoins gets the kingdom back with `Returned` in the next closed tick, on every peer at once;
  the AI's state stays in the snapshot, so a second drop continues where it stopped.
* A resumed multiplayer save (ADR 0166) clears `AiControlled` of every kingdom that has its human in the new lobby in
  closure tick 0; kingdoms whose players are missing are computer kingdoms in the new lobby (the AI runs regardless of
  the flag).

## Alternatives considered

* **Attach the AI when the takeover happens:** a pipeline change mid-match; a rejoining peer would need to know which
  AIs to attach before restoring, and the snapshot format would have to say so.
* **Let the relay simulate or send AI commands:** the relay never simulates (DEC-08).
* **Only defeat dropped players:** simplest, but the lobby option of REQ-NET-07 asks for AI takeover and neutral too.

## Consequences

* Each human kingdom carries an idle AI state in the snapshot (a few KB).
* The AI that takes over starts its script from its current state with whatever the kingdom has; it was not built for
  mid-game entry, so its first decisions may be odd (UNVERIFIED against the original, which had no takeover).
* `ValidateRestoredState` accepts the new flag.
