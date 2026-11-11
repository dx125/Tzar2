# 0290. The editor's helpers: playability check, mission goal wizard, trigger history and "where used"

* **Status:** Accepted
* **Date:** 2026-11-11
* **Deciders:** maintainer (Claude), the owner's choice of the editor review's section 3 (2026-11-11)
* **Related:** TDD §18.2, §18.3; ADR 0133, ADR 0153, ADR 0220, ADR 0285; STATUS item 26.8

## Context

The editor review's section 3 proposed helpers for making new campaigns and scenarios. The playtest's rewind came first
(ADR 0285). The owner chose four more, in this order:

* **A playability check.** The campaign acceptance's scripted bot (ADR 0133, ADR 0220) already plays the original
  campaigns headless and says where a mission stalls. A map maker could use the same bot on their own map or campaign.
* **A mission goal wizard.** Common goals take several events, actions and rules each: destroy the enemies, bring a hero
  somewhere, hold out, gather, keep a hero alive.
* **A trigger history.** The trigger debugger logged what fired, but the tester could not go back to that moment.
* **"Where used".** The rule editor renames names everywhere and replaces object references, but it could not say where
  a name or an object is used.

## Decision

* **Playability check.** `PlayabilityCheck` (in `Tzar2.Game`, engine-free) wraps `CampaignAutoplay`.
  * `Prepare` builds every map on the editor's thread, so the map can be edited while the bot plays.
  * `Run` plays on a worker thread. It stops on request: `AutoplayOptions.Cancellation` and `Progress` are new.
  * It reports each attempt in words: completed by play alone or with which help, lost, or out of time. It also gives
    where the bot got to: units, the rules that fired, the variables, its goal.
  * A campaign is played in survey mode, so every map is reported.
  * The editor's Play menu and the campaign window open it.
* **Mission goal wizard.** `MissionGoals` (in `Tzar2.Content.Editing`) turns a goal into ordinary, numbered,
  legacy-safe elements: a start rule with the quest (and the timer), an event or a condition, and a rule that wins or
  loses and takes the quest away.
  * Goals name objects by document id; the scenario numbers them when it is built.
  * There are five kinds: destroy the enemies, bring an object to an area, hold out, gather a resource, keep an object
    alive.
  * The Triggers menu opens it, with the selected object and the map's named areas.
  * The tests have the acceptance's bot play each goal.
* **Trigger history.** `TriggerDebugger.History` lists the rules that fired and the actions that ran, with their ticks.
  A rewind drops what came after it. In the debugger window, a double-click rewinds the playtest to the point before
  that moment.
* **Where used.** `TriggerUses` lists:
  * every expression that names an event, a timer or a variable, matched as the rename matches;
  * the actions that stop a timer;
  * every event, action, phrase or sound that refers to an object.
  
  The rule editor shows the list; a double-click locates the place.

## Alternatives considered

* **A new bot for the check:** the acceptance's bot already copes with the original campaigns; one bot means one set of
  fixes.
* **Goal "macros" kept as goals in the map:** they would need their own format, editor and runtime; ordinary rules stay
  editable and run everywhere the triggers run.
* **A history in the replay instead of the debugger:** the debugger already observes every firing without changing the
  match.

## Consequences

* A map maker can see if a map can be won, and where its scripts stall, without playing it through.
* A first mission needs no knowledge of the rule editor; the goals stay editable rules.
* Going back to the moment a rule fired takes one double-click in a debugged playtest.
