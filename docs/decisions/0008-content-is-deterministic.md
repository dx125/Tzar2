# 0008. Mark Tzar2.Content as deterministic

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M0 hardening review)
* **Related:** TDD §7.2, §8.8, §11, REQ-SIM-04, REQ-SIM-05, ADR 0004, ADR 0005

## Context

`Tzar2.Content` contains the YAML rules loader and the **ruleset compiler**. Every lockstep peer compiles the ruleset
from the same packages, and the compiled `Ruleset` is the input of the simulation. If the compiler parsed `"1.5"`
through `double`, or emitted entries in `Dictionary` order, two peers could build different rulesets from identical
files and desync before the first tick. ADR 0004 left Content without the `Tzar2Deterministic` opt-in. The assembly
is still empty in M0, so opting in now costs nothing.

## Decision

* `Tzar2.Content` sets `<Tzar2Deterministic>true</Tzar2Deterministic>`: T2D001-T2D005 apply (no floating point,
  no randomized hashing or unordered iteration, no clock or random sources, no runtime code generation).
* It does **not** set `Tzar2NoIO`: it implements the VFS and reads packages.
* Decimal literals in rules (`speed: 1.5`) are read as YAML scalars (strings) and converted with
  `Tzar2.Core.Numerics.Fixed.Parse`/`TryParse`, which is integer-only (ADR 0005). Integer fields use `int.Parse`/
  `long.Parse` with `CultureInfo.InvariantCulture`. YamlDotNet must not be asked to deserialize into `float`/`double`.
* `Tzar2.Architecture.Tests` (`DeterminismOptInTests`) checks the attribute on the compiled assembly.

## Alternatives considered

* **Leave Content unmarked and only test the compiler's output hash:** catches a desync only when a test happens to
  exercise the input that triggers it.
* **Split the compiler into its own deterministic assembly:** more projects for no gain while the analyzer rules are
  cheap to follow in the loader too.

## Consequences

* Loader code that needs an ordered view of a mapping uses sorted or insertion-ordered collections.
* Tooling that truly needs floating point (e.g. image statistics for import reports) belongs in Import or the CLI.
