# 0178. Keep the original scripts for strategy and give the AI its own tactics, scaled by difficulty

* **Status:** Accepted
* **Date:** 2026-10-20
* **Deciders:** product owner (first play-test; decision "scripts + new tactics"), maintainer
* **Related:** REQ-AI-02, REQ-AI-04, GAME-07, UNK-35, UNK-36; ADR 0052 (executors), ADR 0144 (naval executor);
  docs/mechanics/ai-placeholders.md

## Context

In the first play-test the owner saw the AI ignore danger: towers shot its soldiers while they stood still, and enemy
buildings went up next to its town unanswered. The original scripts (`random.ais` and others) only say what to build
and train; the war AI was hard-coded in the original's executable, and ADR 0052 filled the gap with the most plausible
behaviour: defence against soldiers only, waves at a fixed size to the nearest building, idle wave soldiers attack the
nearest object, retreat below a quarter of the launch size. Nothing in it looked at towers or weighed a fight.

Two causes, one in the sim and one in the AI:

* **Sim.** A unit hit while idle fights back (ADR 0038), but not one on a move-advance: such a unit only acquires what
  its sight shows. A stone tower hits at 13 tiles, a footman sees 6, so a wave walking past a tower was shot down
  without an answer. The chase leash (twice the sight) also dropped a tower that outranges it.
* **AI.** The military executor never considered towers or buildings, and nothing compared strengths: a wave left at
  10 soldiers whatever stood at its target, and gatherers kept walking to a mine a tower covered.

Asked how far to depart from the original, the owner chose to keep the scripts for strategy and to write new tactics,
scaled by difficulty (2026-10-20). The original's AI "was not great either"; there is nothing to measure for this.

## Decision

* **Sim (GAME-07).** A unit on a move-advance without a target that is hit takes the attacker as its target and
  resumes its way after the fight (as when it acquires something in sight). A plain move still ignores fire. A unit
  may always chase what can hit it from where it stands: the leash is at least the target's own reach.
* **Combat power** (`AiThreats.PowerOf`): √(current health × average attack per second) of an armed entity, 0 without
  an armament; a group's power is the sum (Lanchester's square law: comparing these sums compares the sides'
  strengths). Defence, damage classes and bonuses are left out: the tactics only need to tell a hopeless fight from a
  fair one. Measured on the classic ruleset: footman 25, peasant 6, stone tower 119.
* **Threats** (`AiThreats`, built once per executor run on first use): the completed armed buildings of every kingdom
  the AI may fight (enemy or neutral) with their reach (range plus half the footprint), and their soldiers on the map.
  `PowerNear(x, y, r)` sums the soldiers within r and the towers whose reach comes within r.
* **Tactics per difficulty** (`AiOptions.Tactics`, part of the options fingerprint):

  | | Easy | Normal | Hard |
  |---|---|---|---|
  | Defence also answers enemy towers whose reach comes into the town, and enemy buildings and sites in it | yes | yes | yes |
  | Power sent against a threat at home, % of the enemy power there (0 = every free soldier goes) | 0 | 100 | 150 |
  | A wave starts with at least this % of the power around its target (0 = no check; a wave of the largest size starts anyway) | 0 | 100 | 150 |
  | A wave goes home below this % of the enemy power around it (0 = only the size rule of ADR 0052) | 0 | 50 | 70 |
  | Idle wave soldiers attack by danger: enemy soldiers, towers that reach the wave, other towers, workers; then the nearest object | no | yes | yes |
  | The wave aims at the enemy building with the least power around it, not the nearest | no | no | yes |
  | Gatherers leave and avoid resource nodes a hostile tower reaches (with a tile to spare) | no | yes | yes |

  The threat radius is 12 tiles. Soldiers sent against soldiers go even when they are too few (the town must be
  defended); against a building they wait, the army stays home and grows (no wave leaves while the town is threatened,
  as before). Buildings are attacked with an explicit attack order: a move-advance into a building's tiles ends at
  the nearest reachable point and acquires only what is in sight.
* These are Tzar2's own rules, chosen by the owner, not placeholders of the original: they are listed in
  docs/mechanics/ai-placeholders.md as T1–T7 and may be tuned for play. The scripts and everything ADR 0052 lists stay
  as they were; difficulty still changes what the scripts see, and now also the tactics.

## Alternatives considered

* **Measure the original's war AI (RE-07):** it is hard-coded, and the owner does not want its weaknesses back.
* **A full influence map with paths around towers:** costs a grid per AI per run and a path cost hook in the sim; the
  point checks above answer the play-test findings. Revisit if waves still walk into tower fields.
* **Same tactics on every difficulty:** the owner asked for scaling; Easy keeps the old, unweighed behaviour so a new
  player can win.

## Consequences

* The AI's commands change: the `ai` bench hash and the AI golden streams are re-pinned with this ADR; the move-advance
  change also re-pins the gameplay hashes where a unit on a move-advance is hit.
* Threats cost one pass over the hostile buildings and soldiers per AI run (every 10 ticks), only when a tactic asks;
  choosing the weakest building is buildings × threats, only while a Hard wave is due.
* Follow-ups: build placement that keeps out of tower reach; healing and repair after a retreat; ranged units kept
  behind melee; walls and towers of the AI's own (ADR 0052's list). The owner's second play-test decides the next
  round of tuning.
