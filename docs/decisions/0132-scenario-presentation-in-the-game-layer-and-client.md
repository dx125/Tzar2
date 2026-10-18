# 0132. Resolve scenario presentation in the game layer and show it with the original dialog art

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent M4-B
* **Related:** TDD §7.2 (ADR 0073), §15 REQ-UI-02, §17.1 REQ-SCR-03, RE-09, UNK-15; ADR 0070, ADR 0072, ADR 0130, ADR 0131

## Context

The trigger engine emits presentation events that carry action and phrase indices (ADR 0131). The client may read
only Game, Content, Import and the contract namespaces `Tzar2.Core.Ids`, `Tzar2.Sim.Commands` and `Tzar2.Sim.Events`
(ADR 0073), so it cannot see the trigger engine or the scenario state. Single player pauses during conversations and
story screens (REQ-SCR-03). The original dialogs are bitmaps in `IMAGES/SCREENS`: `D_SAY` (conversation), `D_QUESTS`
(quest log), `D_MESSAGE`/`D_MSGWND` (messages) and `S_SCRPLAY` (story screen). Their exact layouts are not measured
yet (RE-09).

## Decision

* **`MatchSetup.Scenario`** (a `ScenarioData`) and `TriggerOptions` make a session a scenario match. Responders default
  to the human kingdoms, or to everyone if none is human. The scenario hash and the options fingerprint join the match
  fingerprint, so peers with another scenario are refused. `FixedMapSource` carries a converted or authored map.
  `GameSession` attaches the trigger engine after the AI, in the same way, and `Restore` re-attaches it from the
  snapshot.
* **`Tzar2.Game.Scenario.ScenarioPresenter`** (sim thread, after every tick) turns the tick's scenario events into
  immutable **`ScenarioNotice`** records. Each record has the kind, title, text, user image/sound/music paths, duration,
  auto-close, a camera point, and whether the local kingdom is targeted, with every text taken from the scenario. The
  presenter also keeps an immutable **`ScenarioView`**: the quest log (titles and texts in the order the quests were
  added) and the conversation waiting for a choice (serial, speaker title or name, phrase texts). It rebuilds the view
  only when either changes. `GameFrame.Scenario` publishes the view with each frame, and `GameSession.DrainNotices`
  hands the notices to the main thread.
* **Replies:** `GameSession.ReplyToConversation(serial, position)` submits a `ScenarioReply` command.
* **Pausing (single player only):**
  * While a conversation waits, `GameSession.HoldsForScenario` stops the sim clock until a reply is queued; the tick
    that carries the reply then runs.
  * The client pauses the session while a story screen is open.
  * Multiplayer sessions never hold.
* **Client** (`GameScreen.Scenario.cs`, `ScenarioScreens.cs`):
  * Quests added/removed, chat lines and said phrases appear in the HUD message area.
  * Show Message opens `D_MESSAGE`; story screens open `S_SCRPLAY`, with the text scrolling over the action's scroll
    time and closing by itself when Auto Close is set. Both are queued, one at a time.
  * A waiting conversation opens `D_SAY` with the speaker and the candidates. A click or keys 1–9 answer; Enter or Space
    answers when there is a single candidate; Escape does not dismiss it.
  * F8 opens the quest log (`D_QUESTS`).
  * Lock View centres the camera and ignores scrolling for its time (until Unlock View); Scroll View centres the
    camera.
  * `MapRequested` is raised for the campaign layer (ADR 0133).
* **Layout:** positions inside the dialogs are first estimates (UNVERIFIED until RE-09 measures the original dialogs).
  Labels that the original bakes into its bitmaps are not drawn again (UNK-15). Custom user images are looked up as
  imported map assets; missing ones are simply not drawn.

## Alternatives considered

* **Let the client read the trigger state:** it violates ADR 0073, and it would read state that the sim thread owns.
* **Put texts in the sim events:** sim events are fixed-size integers (ADR 0131).

## Consequences

* Headless hosts (tests, the campaign runner) use the same presenter and replies without Godot.
* When RE-09 delivers measured dialog layouts, only `ScenarioScreens.cs` changes.
