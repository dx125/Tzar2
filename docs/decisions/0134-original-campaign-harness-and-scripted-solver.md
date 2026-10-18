# 0134. Measure "all original campaign missions completable" with a scripted, escalating, cheat-assisted solver

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent M4-B
* **Related:** TDD §22 item 5, §25 (M4 exit criterion), REQ-LEGAL-01, UNK-66, UNK-69; ADR 0121 (converter), ADR 0130,
  ADR 0133

## Context

M4's exit criterion is "all original campaign missions completable". The original campaigns exist only on the user's
machine; the converter (ADR 0121) turns them into `MapData` + `ScenarioData` on the fly. Nobody can play 26 missions
by hand on every change, and TDD §22 asks for "campaign completion bots (recorded command scripts)". Recorded scripts
would need the original game to record them and would be original-derived data that cannot be committed.

## Decision

* **Harness:** `t2 campaign run <wcm|wmp|folder> --package <base.t2pkg>` (and `OriginalCampaignHarnessTests`, skipped
  unless `TZAR2_ORIGIN` and `TZAR2_PACKAGE` are set) converts the campaigns against the imported classic ruleset,
  plays them with `CampaignAutoplay`, and prints one line per mission: completed or not, result, ticks, the strongest
  assistance used, the next map, the scenario faults. `--survey` continues past a failed map along the campaign graph so
  a report covers every map; `--only`/`--from` play one map or start later; `--god` protects from the first attempt.
  Nothing original is written to the repository; reports stay local.
* **Solver** (generic, reads only the scenario IR and the trigger state):
  * answers conversations (first candidate);
  * **guards**: Object In Area events of rules that make the local kingdom lose, about other kingdoms' named objects,
    send the army against the nearest such threat;
  * **pursues** the first rule still enabled (never one that makes the local kingdom lose) whose condition waits for an
    event it can make true: named own (or commandable) units walk into an area, one by one; "so many of my units" sends
    the nearest ones; units board holders; the army attacks named objects that must be destroyed or whose health an
    expression watches; "at most N objects of these kingdoms" sends the army at them;
  * otherwise attack-moves to the nearest enemy.
* **Cheat assistance** (on by default, reported per mission): a threat within 25 tiles of a guarded area is removed
  (`DebugKill`, "cleared"); reinforcements spawn by the army after 5 minutes ("reinforced"); after 10 minutes the
  enemies an "at most N" event waits for are removed ("cleared"); on a retry of a lost mission the kingdoms whose units
  must survive get `hmgod` ("god"). A mission counts as completable when the solver reaches the scenario's next map
  (or its win); the report says how much cheating that took.
* **Match setup for real maps** (in `ScenarioMatch`): owner-less objects that work for a kingdom (the sim gives Nature
  no workers, producers or drop-offs) and the Rescue pseudo-kingdom's objects go to one more, **passive** kingdom
  (`ControllerKind.Passive`: no AI, no human), neutral to everyone; a free slot is used, else an inactive kingdom that
  owns nothing (UNK-66). A pair's "Command Units" flag also adds the other side's offer, because the sim's consented
  sharing needs both (UNK-69).

## Alternatives considered

* **`hmnext` for every mission:** skips the scenario's own transitions, so the campaign graph and the triggers would
  not be exercised.
* **Recorded human inputs:** needs the original running and produces original-derived data.

## Consequences

* "Completable with assistance" proves that the triggers, conversations, transitions and carry-over of each map work
  end to end in the rebuild; missions completed by "play" also show that the AI, combat and movement suffice.
* Missions the solver cannot finish name their blocker in the report (the rule it waits on); they are either solver
  work (a new goal kind) or engine work (a mechanic the rebuild lacks, e.g. rescuing, `$isselected`).
