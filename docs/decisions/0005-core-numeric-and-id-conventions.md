# 0005. Core numeric, angle, RNG and ID conventions

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M0)
* **Related:** TDD §8.2, §8.3, §8.4, REQ-SIM-04, REQ-SIM-06, REQ-SIM-07, REQ-SIM-12, UNK-08, RE-02

## Context

TDD §8 fixes the representations (Q47.16 `Fixed` in a `long`, 16-bit binary angles, a 1,024-entry sine LUT, integer
`Atan2`/`ISqrt`, xoshiro256** with named streams, 32-bit index+generation entity handles, xxHash64 state hashes) but
leaves rounding, overflow behaviour, orientation and bit splits open.

## Decision

* **`Fixed` (Q47.16):** `*`, `/`, `FromRatio`, `ToInt` truncate toward zero (the TDD default until UNK-08 says
  otherwise); `Floor*`, `Ceiling*`, `Round*` (half away from zero) are explicit. Operators are **checked** and throw
  `OverflowException`/`DivideByZeroException`; `Saturating*` helpers clamp. `ToString` is exact and invariant (16
  fractional digits suffice); `Parse` is integer-only and truncates.
* **Angles:** `ushort`, 65,536 units per turn, wrapping. 0 = +X (east); angles grow toward +Y, and world Y points down
  the map (south), so positive angles turn clockwise on screen. `ToDirection8()` rounds to the nearest 45-degree
  facing, 0 = east, counting in the direction of increasing angle; mapping facings to sprite columns is left to RE-02.
* **Trig tables:** `Sin` = 1,024 entries over the full turn in Q16, linearly interpolated (64 angle units per entry);
  `Cos(a) = Sin(a + 90°)`. `Atan2` reduces to the first octant and interpolates a 1,025-entry `atan(i/1024)` table in
  angle units. Accuracy: within ±2 raw units (tested for all 65,536 angles and random vectors). Tables are produced
  by `tools/Tzar2.Tools.LutGen` and verified by xxHash64 in `TrigTests`.
* **RNG:** xoshiro256** 1.0, seeded by four SplitMix64 outputs (reference procedure; tested against Vigna's C code).
  Stream (kind, index) of a match starts from `FromSeed(Mix(matchSeed ^ Mix(kind << 32 | index)))` with
  `Mix` = the SplitMix64 finalizer. Streams: `Combat`, `MapGen`, `Scripts`, `Misc`, and `Ai(player)`. Bounded integers
  use Lemire's unbiased method; chances use basis points.
* **State hashing:** xxHash64 (System.IO.Hashing) over little-endian encodings. Fixed-size values are appended
  without framing; variable-length data (`Add(ReadOnlySpan<byte>)`, `AddSpan<T>`) is preceded by its byte length as
  a 64-bit little-endian integer, so different splits of the same bytes hash differently (amended in the M0
  hardening review; the golden hash in `Tzar2.Determinism.Tests` changed with it). `AddSpan<T>` hashes raw memory of
  unmanaged structs, which must therefore have no padding.
* **`EntityId`:** 20-bit index (1,048,575 slots) + 12-bit generation (1..4,095, wrapping to 1); the all-zero value is
  `None`. Ordered by index, then generation.
* **`Tick`:** non-negative `int` (at 20 Hz, over three years of game time); `Tick.DefaultLengthMilliseconds = 50`.
* **Binary I/O:** `BinaryBufferWriter`/`BinaryBufferReader`, little-endian, LEB128/zigzag varints, length-prefixed
  strict UTF-8 strings with an explicit size bound; malformed input throws `InvalidDataException`. Varints must be
  canonical (shortest form): redundant trailing zero groups are rejected, so every value has exactly one encoding.

## Alternatives considered

* **Unchecked (wrapping) `Fixed` operators:** faster and also deterministic, but silent wrap-around hides bugs.
* **Quarter-wave sine table:** more precise for the same size, but the TDD names a 1,024-entry table; full-circle
  interpolation already meets the accuracy target.
* **24/8 index/generation split:** more entities, but a slot's generation would repeat after 256 reuses.

## Consequences

* An overflow inside a tick throws on every peer at the same tick. Whether the match then aborts with a repro bundle
  or the sim clamps is decided with the ECS in M2.

## Questions for the product owner

* Is 1,048,575 simultaneous entities (units, buildings, trees, projectiles, items, corpses) enough for 1,024² maps with
  mods? If not, `EntityId` would have to grow to 64 bits before M2 snapshots are frozen.
