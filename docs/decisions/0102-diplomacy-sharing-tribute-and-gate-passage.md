# 0102. Asymmetric stances, consented sharing, bounded tribute, and gates that bar non-allies

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 stream F)
* **Related:** TDD §9.1, §9.2 REQ-PATH-01, acceptance test 5, §10 GAME-13, ADRs 0021, 0043, 0100, 0101; m3-placeholders.md
  "Gates for enemies"

## Context

Manual ch. 4 §1.3 and ch. 5 §8: each kingdom chooses Ally, Enemy or Neutral towards each other one; stances may differ
("when B's units attack A's, A's units will not attack back"); neutral military units attack only military units in
their sight; shared vision, shared resources and shared unit control are switched per kingdom and glow when the other
kingdom switched them too; allied victory; tribute of gold, food, stone or wood to anyone. A lockstep game must also
resist spam (stance flip-flops, tribute floods) and overflows. Gates stood open for everyone (ADR 0043 follow-up).

## Decision

* **Stances** are per ordered pair; Nature is everyone's enemy. `MayAttack(attacker, target)`: enemies anything,
  neutrals only military units, allies nothing. It gates automatic acquisition, keeping a target, explicit attack orders
  (`NotAllied`) and captures (only enemies' buildings). A unit hit by an ally it treats as an ally does not strike back.
* **Sharing** (`Sharing` flags: vision, resources, unit control, victory) takes effect only between mutual allies who
  both offer it. Vision feeds the combined visibility (ADR 0101). Resources: a cost the payer's stockpile does not cover
  is taken from the sharing allies in slot order, all or nothing. Unit control: movement and attack orders (and trade
  routes) for the ally's units; deleting, cancelling, training and other economic orders stay with the owner (their
  costs and refunds belong to the owner). Victory: the team wins together (ADR 0106).
* **Rate limits.** One stance or sharing change per ordered pair per `world.diplomacyChangeIntervalMs` (10 s), else
  `DiplomacyBusy`; identical "changes" are refused. One tribute per kingdom per `world.tributeIntervalMs` (1 s), at most
  `world.maxTributeAmount` (100,000) of one resource, only from the sender's own stockpile, never to itself or a defeated
  kingdom; the receiver's stockpile saturates at `world.stockpileCap` (10⁹).
* **Lobby teams** (`SimulationConfig.Teams`) start as mutual allies sharing vision and victory.
* **Gates.** Navigation keeps one passability per locomotor; an open gate's passage tiles stay passable for planning.
  Movement (step 8) refreshes a per-tile table of open passages and their owners and refuses a step onto a passage tile
  of a gate whose owner does not treat the mover's kingdom as an ally (a unit already on the passage may leave).
  Automatic gates open for allies' units too. Enemies therefore stop at an open gate instead of slipping through.

## Alternatives considered

* **Per-kingdom navigation layers or portal costs (REQ-PATH-01 "portals with per-player passability"):** correct
  planning around foreign gates, but 8× the layer memory or HPA* changes in code stream E and PR #22 are reworking; the
  movement bar is the smallest change that keeps enemies out.
* **Sharing on one side's offer:** lets a kingdom read or spend another's without consent.

## Consequences

* An enemy whose only route leads through an open foreign gate walks to it and stops (blocked, then gives up after its
  re-plans) rather than routing around a closed wall it cannot know about. Per-kingdom portals remain a pathfinding
  follow-up if captures show the original re-routes.
* AI diplomacy behaviour (TDD GAME-13 unknown) is the AI stream's.
