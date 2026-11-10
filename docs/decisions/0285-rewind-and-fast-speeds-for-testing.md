# 0285. Rewind and fast speeds for testing

* **Status:** Accepted
* **Date:** 2026-11-10
* **Deciders:** maintainer (Claude), at the owner's request (the editor review, section 3, taken first)
* **Related:** TDD §18.3 "time controls"; ADR 0153 (playtest and the trigger debugger), ADR 0168 (the replay viewer's
  seek), ADR 0133 (saves), ADR 0271 (save compatibility)

## Context

The owner tests maps and campaigns by playing them. A test that fails late, such as a lost battle at minute 20 or a
trigger that did not fire, used to mean starting again or keeping saves by hand. The playtest's time controls were the
game's speed keys (25-300 %) and the pause.

The engine already has what a rewind needs:

* the simulation is deterministic;
* a snapshot restores a match exactly (saves, rejoins, the replay viewer's keyframes).

## Decision

* **Rewind points.** `MatchRewind` (in `Tzar2.Game`, engine-free and tested) attaches to a single-player session before
  it starts. Its current state is the first point. After that it takes a snapshot every 10 s of game time, on the sim
  thread between two ticks (`GameSession.AfterTick`).
  * Every point of the latest 3 minutes is kept.
  * Older points are kept one per minute.
  * While the points take more than the budget (256 MB), the older points' grid doubles.
  * The first point always stays.
* **Going back.** A rewind stops the session and restores the chosen point into a new one: the same setup, speed,
  pause, fog and view. The points after it are dropped, because that future no longer happens. The match then plays
  exactly as before from that point (a test compares every tick's hash) until the player's orders differ.
  * Back goes to the previous point, at least 2 s ago, so pressing it again goes further back.
  * Back a minute goes to the start when the match is younger.
  * Dragging the time line goes to any point.
* **Fast speeds.** A session with the testing tools offers 500, 1000 and 2000 % after the original's 25-300 %. The
  ticks are the same; only the clock runs faster.
* **In the client:**
  * A rewind swaps the session under the game screen. The sprites, the terrain and the camera stay; a new screen
    would load for seconds on a large map. The per-match client state starts again from the restored frame: the
    sounds, the scenario's dialogs, the outcome and the map's sound sources.
  * The rewind bar at the top of the screen has: back a minute, back (also Ctrl+Backspace; the original binds
    Backspace alone), the time line, pause, the speed and FINISH.
  * A match that ends waits for FINISH, so the tester can go back instead.
  * The trigger debugger follows the rewind. Its breakpoints stay, its log keeps what happened before the point, and
    its rules' last results are unknown again.
* **Where it is on:**
  * always in the editor's playtest;
  * in a campaign played from the editor (a campaign playtest or a converted campaign);
  * in any single-player match when the setting "Rewind (testing)" is on (off by default).
  
  Never in multiplayer.

## Alternatives considered

* **Replaying from the nearest point with the recorded orders (an exact seek, as the replay viewer):** going back to
  any tick, not only to a point, would need the session's command log. Points every 10 s are close enough to test with.
* **A new game screen per rewind (as the replay viewer):** simple, but a large map takes 2-12 s to load.
* **Quick saves by hand:** they already exist, but they need the tester to save before the moment that goes wrong.

## Consequences

* Testing a late moment of a map, a trigger chain or a campaign mission no longer means playing it again from the
  start.
* A rewind costs a snapshot every 10 s of game time on the sim thread (milliseconds on large maps) and up to 256 MB of
  memory.
* After a rewind, the match's replay and statistics cover only the new branch (the restored session does not record).
  The editor's playtest does not keep a replay anyway.
* Automated tests do not get faster from this: the headless runs (the campaign acceptance, the goldens) already run as
  fast as the CPU allows.
