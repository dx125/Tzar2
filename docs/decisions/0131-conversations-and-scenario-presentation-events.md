# 0131. Run conversations in the sim and send scenario presentation to the client as events

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent M4-B
* **Related:** TDD §7.2 (ADR 0073), §15 REQ-UI-02, §17.1 (REQ-SCR-01/02/03), Appendix C.7/C.8, ORIG-13, UNK-60;
  ADR 0120, ADR 0130

## Context

A conversation is a phrase graph: initial phrases, conditions that decide which phrases are offered, result
expressions that run when a phrase is said, and follow-ups; when several phrases are candidates the player chooses
(Appendix C.8). Results change variables, so conversations are sim state: every peer must see the same choice at the
same tick. Quests, messages, story screens, chat lines and camera actions only show something (REQ-SCR-03). The
client may read only Game, Content, Import, `Tzar2.Core.Ids`, `Tzar2.Sim.Commands` and `Tzar2.Sim.Events` (ADR 0073),
and sim events carry integers, not texts.

## Decision

* **Conversation runtime** (part of `LegacyTriggerSystem`, state in `triggers.legacy`): the Conversation action starts
  a session (serial number, step count). It offers the conversation's initial phrases whose condition holds (an empty
  condition always holds) and emits `ConversationOffered` (X = conversation, Y = serial, Arg = number of candidates).
  A player in `TriggerOptions.ResponderMask` (the human players) answers with the command
  `ScenarioReply(Arg0 = serial, Arg1 = candidate position)`. The command step validates it: a session runs, the serial
  matches, the position exists, no reply is pending, and the player is a responder. Otherwise the reply is
  `NoScenarioPrompt`; only the first valid reply of a tick counts. In step 2 the chosen phrase is said (`PhraseSaid`),
  its result runs with the phrase's source object as the current object, and its follow-ups whose condition holds are
  offered. No candidates → `ConversationEnded`. A single candidate is still offered, so the player reads it and clicks
  on (UNK-60).
* **Loop guard** (ORIG-13, REQ-SCR-02): a session is cut after `MaxConversationSteps` (default 256) phrases, with
  `ConversationEnded` Arg = 1 and a `ScenarioFault`. A Conversation action while another runs queues it (at most 64).
* **Pausing** is not sim state. The game layer pauses a single-player session while a conversation waits or a story
  screen is shown, and still runs the tick that carries the reply. In multiplayer nothing pauses (REQ-SCR-03).
* **Presentation events** (values 60–79 of `SimEventKind`), Player = 0xFF, X = targeted kingdoms (bit = slot), Arg =
  the action index: `QuestAdded`, `QuestRemoved`, `ScenarioMessage`, `StoryScreen`, `ScenarioChat`, `ViewLocked`
  (X, Y = world point, Arg = ms), `ViewUnlocked`, `ViewScrolled` (X, Y = target point), `ConversationOffered`,
  `PhraseSaid`, `ConversationEnded`, `MapRequested` (Y = 0 Load Map, 1 win, 2 lose), `ScenarioFault`. The game layer
  resolves texts, images and sounds from the scenario by index and gives the client display records. The active quest
  list and the offered phrases are also readable from the engine's state between ticks, so a client that joins or
  loads mid-mission shows them without the events.

## Alternatives considered

* **Conversations purely in the client:** results would run outside the sim and desync peers.
* **Texts in sim events:** events are fixed-size unmanaged structs; texts belong to the scenario, which every peer has.

## Consequences

* A headless driver (tests, the campaign runner) plays conversations with plain commands.
* Replays carry the replies as ordinary commands.
