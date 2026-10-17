# 0007. Keep the sim integer-only and let Lua cross the boundary as validated integers (spike S2)

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M0, spike S2)
* **Related:** TDD §8.2, §8.7, §8.8, §17.2, §22.2, §25 (M0 S2), DEC-03, DEC-04, DEC-13, DEC-14, REQ-SIM-04,
  REQ-SIM-12, REQ-SCR-04, REQ-SCR-05, RISK-03, ADR 0005, ADR 0008

## Context

Spike S2 has to show that fixed-point Core math plus Lua-CSharp produce the same hash stream on x64 and ARM64. It
also has to find where Lua's IEEE-754 doubles could break that.

**Harness** (`spikes/S2-determinism/`, outside `Tzar2.sln`, built by `spikes/Tzar2.Spikes.sln`):

* `Tzar2.Spikes.S2`: a toy lockstep sim. 4,096 units and 8 players run for 2,000 ticks. Units steer with
  `Trig.Atan2/Sin/Cos`, measure distance with `IntMath.ISqrt`, draw from `RngStreams` (MapGen, Ai(p), Combat, Scripts)
  and fight with basis-point chances. Each player also runs a Lua script every tick in the S4 sandbox (ADR 0008),
  with a budget of 20,000 instructions.
  The script uses string-keyed tables with inserts and deletes and feeds the `pairs()` order into its result. It
  also calls `table.sort` with a Lua comparator, `bit32`, `string.format`/`sub`/`byte`, an `__index` metamethod,
  float `+ - * /` floored before return, `fx.*` and `math.random`. Its rally point and aggression values flow back
  into the sim, and it sometimes runs away on purpose so the budget aborts it: 162 aborts per run.
  After each tick the harness writes an xxHash64 of the raw unit array, the per-player arrays, the script status and
  charged instructions, and all RNG states.
* Ten probe categories (A–J) serialize every value exactly, with numbers as IEEE bit patterns. Most run in an
  unsandboxed Lua-CSharp state with the full standard library, to show what the sandbox must remove.
* `run-matrix.sh` builds and runs every configuration; `compare.py` diffs tick streams and probes.
  The x64 runs use Rosetta 2 with an x64 .NET 8.0.32/10.0.13 runtime in `~/.dotnet-x64` (dotnet-install.sh
  `--architecture x64`, no sudo). The IL is the same; only the JIT/AOT code generator and the native libraries
  (libm) differ.

**Measured on 2026-10-17, MacBook (Apple Silicon), macOS 26.5.2, .NET 8.0.32 / 10.0.13, LuaCSharp 0.5.7.** Raw
table: [`spikes/S2-determinism/results-macos-arm64-2026-10-17.md`](../../spikes/S2-determinism/results-macos-arm64-2026-10-17.md).

| configuration | final hash (2,000 ticks) | tick stream | A ieee | B libm | C casts | D NaN | E fmt | F depth | G pairs | H tostring | I math.random | J sandbox |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| arm64 net10 Release (reference) | `A47B60C0B29054F5` | — | — | — | — | — | — | — | — | — | — | — |
| arm64 net10 Release, 2nd process | `A47B60C0B29054F5` | identical | = | = | = | = | = | ≠ 1 | = | = | ≠ 5 | = |
| arm64 net10 Debug | `A47B60C0B29054F5` | identical | = | = | = | = | = | ≠ 1 | = | = | ≠ 5 | = |
| arm64 net8 Release | `A47B60C0B29054F5` | identical | = | = | ≠ 5 | ≠ 2 | = | ≠ 1 | = | = | ≠ 5 | = |
| arm64 net8 Debug | `A47B60C0B29054F5` | identical | = | = | ≠ 5 | ≠ 2 | = | ≠ 1 | = | = | ≠ 5 | = |
| arm64 net10 NativeAOT | `A47B60C0B29054F5` | identical | = | = | ≠ 17 | = | = | ≠ 1 | = | ≠ 4 | ≠ 5 | = |
| x64 (Rosetta) net10 Release | `A47B60C0B29054F5` | identical | = | **≠ 477** | = | ≠ 8 | = | ≠ 1 | = | = | ≠ 5 | = |
| x64 (Rosetta) net10 Debug | `A47B60C0B29054F5` | identical | = | **≠ 477** | = | ≠ 8 | = | ≠ 1 | = | = | ≠ 5 | = |
| x64 (Rosetta) net8 Release | `A47B60C0B29054F5` | identical | = | **≠ 477** | **≠ 52** | ≠ 10 | = | ≠ 1 | = | = | ≠ 5 | = |
| x64 (Rosetta) net8 Debug | `A47B60C0B29054F5` | identical | = | **≠ 477** | **≠ 52** | ≠ 10 | = | ≠ 1 | = | = | ≠ 5 | = |
| x64 (Rosetta) net10 NativeAOT | `A47B60C0B29054F5` | identical | = | **≠ 477** | ≠ 17 | ≠ 8 | = | ≠ 1 | = | ≠ 4 | ≠ 5 | = |

"=" means every value matches the reference; "≠ n" gives the number of differing values. The probes found:

* **The sim plus the sandboxed Lua scripts produced the same 2,000 tick hashes in all 11 configurations**: 2 archs
  × 2 runtimes × Debug/Release, plus a second process and NativeAOT on both archs. That covers the 162 budget
  aborts too. This includes float `+ - * /` inside Lua, as long as the result is floored before it leaves Lua.
* **A (IEEE `+ - * / %`, `sqrt`, `floor`, `fmod`, subnormals, ±inf, −0): identical everywhere** (27,010 values).
  These operations are correctly rounded on both ISAs, and no FMA contraction showed up in any code generator.
* **B (libm: `sin cos tan asin acos atan atan2 exp log sinh cosh tanh pow ^`): 477 of 15,000 results differ by
  1 ulp between x64 and ARM64.** That is on the same OS; Windows' CRT would add further differences. `math.pow` and
  the `^` operator both call `Math.Pow`, and so does the compiler when it folds constants like `2^0.5`.
* **C (double→int casts inside the stdlib):** the library casts arguments with plain `(int)`/`(long)`. On .NET 8,
  x64 and ARM64 handle NaN and out-of-range values differently (x64 gives `0x80000000`, ARM64 saturates). As a
  result, `string.sub('abcdefgh', 1e20)` returns `"abcdefgh"` on x64 and `""` on ARM64, `bit32.lshift(1, 1e20)`
  throws on one and returns 0 on the other, and so on (52 values). .NET 9+ made x64 conversions saturating, so net10
  x64 = net10 ARM64. The other differences in C are only text: .NET exception messages *with stack traces* leak into
  Lua error strings through `pcall`, and they differ between JIT and AOT (17) and between runtime versions (5).
* **D (NaN bits): the sign of a generated NaN differs.** x64 produces `0xFFF8…` and ARM64 `0x7FF8…`, and
  `math.fmod(1, 0)` even differs between net8 and net10 on ARM64. Lua-side behaviour (`x ~= x`, `tostring`) is the
  same everywhere, but any host code that hashes or stores the bits of a Lua number would diverge.
* **E (formatting/parsing: `tostring`, `%g/%e/%f/%.17g`, `tonumber`): identical everywhere.** .NET's formatting
  is managed code, the same on every platform. Note: `tostring(0.1 + 0.2)` gives `0.30000000000000004`, not the
  5.2 reference `0.3`. That is a compatibility quirk, not a determinism problem.
* **F (recursion limits):** pure Lua recursion stops at a deterministic LuaStack limit (depth 524,277). Deep
  `__index` recursion stops at depth 174,758 and deep parser nesting at 191 levels, both the same everywhere.
  **`pcall` recursion stops at a native-stack-dependent depth**, and it changes even between two processes of the
  same configuration (2,973–5,251). The S4 sandbox replaces this limit with a deterministic one.
* **G (`pairs()` order with string/number/boolean/table keys, inserts and deletes, plus `table.sort`): identical
  everywhere.** Lua-CSharp's `LuaValueDictionary` iterates its entries array in insertion order with free-list
  reuse, so the order does not depend on the hash codes. That matters because string hash codes are randomized per
  process in .NET and table keys hash by object identity.
* **H (`tostring({})`, `tostring(print)`, `string.format('%s', {})`) prints `RuntimeHelpers.GetHashCode`.** The
  value happened to be stable across JIT processes here but differs under NativeAOT, so it is unsafe.
* **I:** unseeded `math.random` (System.Random) differs per process; seeded `math.randomseed(42)` matches.
* **J (sandbox: budget abort points and charged instructions for 4 budgets, re-entry limit, `fx.*`, engine RNG):
  identical everywhere.**

## Decision

1. **The DEC-04 numeric model stands.** Core's Q47.16 `Fixed`, LUT trig, integer `Atan2`/`ISqrt` and xoshiro256**
   showed no divergence across arch, runtime, configuration or AOT. No change to Core.
2. **Lua stays double-based internally, but only deterministic operations are reachable, and the boundary is
   integer-only** (refines REQ-SCR-04/05):
   * Allowed: `+ - * / %`, comparisons, `math.floor/ceil/abs/min/max/fmod`, `math.huge`, strings, tables, `bit32`
     (with validated arguments).
   * Removed: every libm function, `math.pow`, `math.random()` without arguments, `math.randomseed`,
     `math.sqrt` (deterministic, but it invites float logic; use `fx.isqrt`).
   * The `^` operator is rejected at load time by a source scan (`SourcePolicy`), because it calls `Math.Pow` at run
     time and in constant folding. Integer powers are written as loops or with `fx.*` helpers.
   * `math.random(m[, n])` draws from the match's `Scripts` RNG stream (REQ-SIM-06), integers only.
   * Integer arguments of library functions are validated (exact integer, in int32 range; ±2^32 for `bit32`
     values) before they reach Lua-CSharp's casts.
   * `tostring`/`string.format("%s")` of tables, functions and threads are rejected (a `__tostring`
     metamethod is allowed), and `print` is captured.
   * **Host boundary rule:** a Lua number becomes an engine value only if it is an exact integer within the
     target range (int32 for ints, ±(2^53−1) for Fixed raw values). Anything else, including NaN, ±inf and
     fractions, is a script error, the same on every peer. Engine values cross into Lua as integers or Fixed raw
     integers below 2^53. **Host code never hashes or stores the bit pattern of a Lua double.**
3. **Lua-CSharp error strings are not sim state.** They may contain .NET stack traces and runtime-specific messages.
   Scripts must not branch on error text, and the host must not hash or replicate it. The production host converts
   non-Lua exceptions from library wrappers into fixed messages.
4. **Release/Debug, net8/net10, JIT/NativeAOT are not determinism dimensions for Core or sandboxed Lua.** The
   game (Godot, net8) and the relay/CLI (net10) may mix freely.
5. The S2 harness stays as an M0 artefact. M2's determinism CI (TDD §22.2) replaces it with replay corpora, and its
   probe list becomes the Lua part of that suite.

## Alternatives considered

* **Patch Lua-CSharp to an integer number type (Lua 5.3-style integers):** the clean long-term answer for scripts
  that need 64-bit integers, but it means a fork of the VM and the parser. Not needed: doubles hold every integer
  below 2^53 exactly, and the measured problems are all at the edges (libm, casts, NaN bits, error text), which the
  sandbox closes.
* **Expose correctly rounded managed libm (software sin/cos/exp):** possible, but the TDD already routes
  trigonometry through `fx.*`, and float transcendental math in gameplay scripts would bring float-order
  sensitivity back into the sim.
* **Run x64 only on real hardware:** Rosetta 2 reproduces x86 semantics (it produced x86's negative default NaN and
  x86 cast behaviour), so it is good enough for the spike. It does not replace Windows x64 runs; see consequences.

## Consequences

* TDD REQ-SCR-04's math subset is confirmed and extended with: no `^`, validated integer arguments, no reference
  `tostring`, engine-RNG `math.random`, and the integer-only boundary rule above. REQ-SCR-05's claim ("Lua
  arithmetic on doubles is IEEE-754 and deterministic for `+ - * /` on .NET x64/ARM64") is confirmed by
  measurement.
* Rosetta is not Windows: Windows' CRT libm and the Windows x64 JIT are not covered. Before M2 closes, the S2
  harness (or its replacement) must run in CI on `windows-latest` x64 and `ubuntu-latest` x64 next to macOS arm64.
  `run-matrix.sh` already works without Rosetta (`DOTNET_X64` absent).
* Lua-CSharp upgrades are determinism-relevant. Re-run `run-matrix.sh` (≈1 min) and the S4 probe on every package
  bump.
* Revisit if Lua-CSharp gains native integers, or if a mod API needs floats in sim-affecting logic.
