# 0213. The AI reads the world through read-only views

* **Status:** Accepted
* **Date:** 2026-10-23
* **Deciders:** agent (maintainer), code review of work queue item 16 (coordinator's plan, item b)
* **Related:** REQ-AI-03; ADR 0051 (the AI is just another player); ADR 0212;
  [code review 2026-10](../reviews/2026-10-code-review.md) (module boundaries 3)

## Context

The AI may change the match only through commands (ADR 0051). `DependencyRulesTests` denies its IL calls to the world's
structural mutators (`Spawn`, `Kill`, `ChangeOwner` and the rest), but the world's state was reachable as writable
spans:

* `World.Players` is a `Span<PlayerState>`;
* `World.Kingdoms` is a `Span<KingdomState>`;
* `ComponentStore<T>.Components` is a `Span<T>`.

The AI only read through them (`world.Players[slot].Flags`, `ref readonly` locals). Still, nothing stopped a write such
as `world.Players[slot].PopulationLimit = …` from compiling, and it would have desynced nobody, because the AI runs
inside the tick on every peer. Such a write would be invisible to review, and it would break the rule that the AI plays
by commands.

## Decision

* The world offers read-only views next to the writable spans:
  * `World.PlayersView` (`ReadOnlySpan<PlayerState>`);
  * `World.KingdomsView` (`ReadOnlySpan<KingdomState>`);
  * `View` on every component store (`ReadOnlySpan<T>`; on cold stores it equals `Components`).
* The writable spans stay for the systems.
* The AI reads only through the views (58 sites in 11 files changed).
* `AiReadOnlyTests` scans the AI's IL. A reference to `World.get_Players`, `World.get_Kingdoms` or
  `ComponentStore<T>.get_Components` fails the test, including references through generic instantiations.

## Alternatives considered

* **Make the spans read-only for everyone, with writers going through methods:** that would touch every system and hot
  loop of the sim, and the hot paths need `ref` access. A view for the readers costs nothing.
* **A separate read-only world interface for the AI:** cleaner for a fork that replaces the AI, but large. The views
  are a step towards it. The interface can follow if the AI moves out of process.

## Consequences

* A write by the AI to player, kingdom or component state no longer compiles through these properties, and the test
  catches the getters if anyone goes back to them.
* Reference-typed fields inside a read-only struct are still writable (`PlayerState.Resources` is an array). The
  mutator deny-list and review cover those.
* No behaviour change: the AI's command stream and the hashes are the same (CI expect-hash `DB23BFA467B22625`).
