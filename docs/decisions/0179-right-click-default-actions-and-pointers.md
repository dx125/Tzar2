# 0179. Give each unit its default action on a right click, and show it with the original's pointers

* **Status:** Accepted
* **Date:** 2026-10-20
* **Deciders:** agent (maintainer); product owner (third play-test, 2026-10-20)
* **Related:** TDD Appendix E.6, §13.8; UNK-40; GAME-04, GAME-09, GAME-10; ADR 0071 (item 9: cursors)

## Context

The owner's third play-test found three problems:

* The hand pointer showed whenever units were selected and the pointer was over any object. Even cows got the
  harvest-looking hand over a cow.
* A herd right-clicked on one of its own cows walked on in formation instead of gathering round it.
* No order was confirmed on screen. The original plays a red cross where units are sent, and marks the object they
  are sent to.

The right-click resolver knew only these default actions:

* attack;
* harvest;
* help build;
* repair;
* move.

The pointer had its own, separate copy of these rules.

What the original does, from the manual (ch. 5 § 2.3), right-clicking:

* a peasant on an enemy building captures it;
* units on a vehicle board it;
* a unit on an item (with the hand pointer) picks it up.

In IMAGES.INI:

* The software-cursor entries `cs_*` give every pointer its hotspot: `cs_arrow` = `s_def` at 2, 0; `cs_move` 15, 10;
  `cs_pick` = the hand at 5, 9; `cs_guard` 10, 12; the others 0, 0. This resolves UNK-40 for the cursors that have
  entries.
* `cursor` (`anims\cursor`, ten frames of a red cross that turns and shrinks, hotspot 24, 24) is the order mark.

## Decision

1. `OrderResolver.ActionOf(unit, target)` decides one `DefaultAction` per selected own mobile unit:

   | Clicked object | Default action |
   |---|---|
   | Item | pick up (units with an inventory); others walk there |
   | Enemy unit or building, Nature's creatures, another kingdom's fields and cows | capture: peasants on a completed building they may capture now (its technology researched); attack: armed units; others walk there |
   | Resource | harvest by the harvesters that gather that resource; own cows of a herd: follow |
   | Own construction site | help build (builders of any civilization) |
   | Own damaged object | repair (builders) |
   | Own completed holder that takes the unit | board |
   | Own unit | follow; the clicked unit itself does nothing |
   | Anything else, or the ground | move |

   `RightClick` groups the selection by action into one command each, moves last.
2. The pointer over an object is `OrderResolver.Hover`: the strongest action of the selection (attack > capture >
   harvest > build > repair > pick up > board > follow > move). Pointers:

   | Action | Pointer |
   |---|---|
   | attack | sword (`s_attack`) |
   | capture, pick up | hand (`s_hand`) |
   | harvest | saw over trees, pickaxe over rocks, `s_harvest` over fields, cows and fish |
   | build, repair | hammer (`s_repair`) |
   | board, follow | `s_move` |
   | move, or nothing | the default pointer (`s_def`, or the system's) |

   Every original pointer clicks with its `cs_*` hotspot; one without an entry clicks with its centre.
3. A world click that sends orders is confirmed:
   * on the ground, the original's red cross plays at the clicked point;
   * on an object, its selection corners blink three times in 720 ms.

   Both run on game time and wait with the pause.

## Alternatives considered

* **Keep a separate pointer table.** It drifted from the click rules (the cow case), so one function decides both.
* **Right-click on own units = move to the point.** The owner expects a herd to gather at the clicked cow. Follow
  does that and keeps up with a moving unit.
* **Show `s_move` over open ground.** The original's move pointer belongs to the card's Move order. Over the
  ground the default pointer stays.

## Consequences

* New EntityInfo flags: `CanCapture`, `IsCapturable`, `CanPickItems`, `IsItem`. Boarding and per-resource
  harvesting need the ruleset (`rules` argument).
* The target's blink is ours. The original's look for a confirmed target is not captured; revisit it with a capture.
* Delivering a load by right-clicking a drop-off is not a default action yet: the sim has no deliver command.
