# 0014. Throw on Fixed overflow in debug builds, saturate and log in release builds

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** product owner (policy), implementation agent (M2 part A, mechanism)
* **Related:** TDD §8.2, §8.7, §19.2, REQ-SIM-04, REQ-SIM-12, ADR 0005 (consequence "decided with the ECS in M2")

## Context

ADR 0005 made the `Fixed` operators checked: an overflow throws `OverflowException`, which is deterministic (every
peer throws at the same operation of the same tick) but ends the match. It left open whether a shipped game should
abort or clamp. The product owner decided: **throw in development/debug builds** (overflow is a bug and must be found),
**saturate and log in release builds** (a player's match must not die on it). Release must stay deterministic: every
peer must clamp to the same value.

## Decision

* `Tzar2.Core.Numerics.OverflowPolicy` exposes the policy compiled into Core: `OverflowMode.Throw` or
  `OverflowMode.Saturate`. The MSBuild property **`Tzar2OverflowPolicy`** (`Directory.Build.props`) selects it:
  `Saturate` for `Configuration` `Release` and Godot's `ExportRelease`, otherwise `Throw` (Debug, Godot `Debug` and
  `ExportDebug`). `-p:Tzar2OverflowPolicy=Throw|Saturate` overrides it; any other value fails the build.
  `Directory.Build.targets` turns `Saturate` into the compile constant `TZAR2_OVERFLOW_SATURATE`.
* Policy-governed operations: `+`, `-` (binary and unary), `*` (by `Fixed` and by `long`), `/` (by `Fixed` and by
  `long`), `Abs`, `FromInt`, `FromRatio`. Under `Saturate` the result is `Fixed.MaxValue` or `Fixed.MinValue` by the
  sign of the exact result (`-MinValue` and `Abs(MinValue)` give `MaxValue`). This is a pure function of the
  operands, so it is identical on all peers.
* **Division by zero is not an overflow** and throws `DivideByZeroException` under both policies.
* Saturation is reported, not silent: `OverflowPolicy.SaturationCount` counts saturated results in the process, and
  the host may set the `OverflowPolicy.Saturated` callback (called on the saturating thread, possibly a sim worker) to
  log the operation. Both are diagnostics only and never feed back into simulation state or hashes.
* The explicit `Saturating*` helpers keep clamping in every build; they are for formulas where clamping is the
  intended rule, not a bug guard.
* `OverflowPolicy.Mode` belongs to the engine identity that a session compares in the loading barrier
  (REQ-NET-05, M6): a Debug peer and a Release peer diverge only if an overflow happens, but then they do diverge.
* Other integer helpers (`IntMath.MulBasisPoints`, `IntMath.DistanceSquared`, `Tick + int`) keep throwing: they are
  outside the owner decision, and their ranges cannot be reached on a legal map.

## Alternatives considered

* **Runtime switch (a static setting chosen at startup):** mutable static state in Core, and a host could forget to
  set it per peer. A compile-time constant cannot drift within one build.
* **Saturate everywhere:** hides overflow bugs during development, which is what the checked operators were for.
* **Throw everywhere (ADR 0005 as written):** deterministic, but a single overflow in a 2-hour match ends it for all
  players.

## Consequences

* Tests that exercise overflow check `OverflowPolicy.Mode` and assert the throw or the clamp; CI runs the suite in
  Release (saturate) and developers in Debug (throw), so both paths stay covered.
* A replay recorded by a Release build replays identically on another Release build; replaying it on a Debug build
  surfaces the overflow as an exception at its tick, which is the desired debugging aid.
* Revisit when the crash reporter (REQ-PLAT-04) exists: saturation events could be attached to its bundle.
