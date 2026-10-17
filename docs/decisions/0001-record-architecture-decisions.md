# 0001. Record architecture decisions

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M0)
* **Related:** TDD §0.1, §24, §25 (M0), G-14

## Context

TDD §0.1 says every deviation from a `SHOULD` must be recorded in `docs/decisions/` as an ADR, and M0 requires one ADR
per spike. The implementing agents change over time, so decisions and their reasons must live in the repository, next
to the code they shape.

## Decision

* Architecture decisions are recorded as numbered Markdown files `docs/decisions/NNNN-short-title.md`, using
  [`0000-template.md`](0000-template.md) (a lightweight form of Michael Nygard's ADR format).
* Numbers are assigned in order and never reused. An ADR is not edited after it is accepted, except for its status
  line and links; a changed decision gets a new ADR that supersedes the old one.
* An ADR is required for: every deviation from a TDD `SHOULD`; every spike result (S1-S6); every choice between
  reasonable alternatives that a later contributor would otherwise have to rediscover (frameworks, formats, numeric
  conventions); every ambiguity in the TDD that the implementer had to resolve.
* Questions for the product owner raised by an ADR are listed in its last section and reported with the milestone.

## Alternatives considered

* **Decisions only in commit messages or PR descriptions:** hard to find and not reviewable as a set.
* **Editing the TDD directly:** the TDD is the binding specification owned by the product owner; ADRs record how it
  was applied and are proposed back into the TDD when accepted.

## Consequences

* Each PR that makes such a decision adds or updates an ADR; reviewers can check this.
* ADRs 0002-0006 record the decisions made while building the M0 skeleton.
