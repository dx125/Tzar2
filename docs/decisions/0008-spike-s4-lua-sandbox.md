# 0008. Use LuaCSharp 0.5.7 with a count-hook instruction budget and a whitelist sandbox (spike S4)

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M0, spike S4)
* **Related:** TDD §8.5 (step 2), §17.2, §25 (M0 S4), DEC-09, DEC-13, REQ-ARCH-02, REQ-SCR-04, REQ-SCR-05,
  REQ-SCR-06, REQ-SIM-05, REQ-SIM-14, ADR 0007

## Context

DEC-13b requires Lua 5.2 through a pure C#, AOT-safe interpreter. REQ-SCR-06 requires a deterministic per-call
instruction budget. If Lua-CSharp offers no step counting, the fallback is a fork or source-level loop-counter
injection. The sandbox must remove I/O, OS, debug, loading and bytecode access.

**Package.** NuGet id **`LuaCSharp`**, version **0.5.7** (2026-09-26), **MIT** (© Yusuke Nakada, repository
`nuskey8/Lua-CSharp`, formerly AnnulusGames). Targets: net10.0, net8.0, net6.0, netstandard2.1. Its dependencies
`LuaCSharp.Annotations` and `LuaCSharp.SourceGenerator` (an analyzer) are MIT too. It is a pure C# Lua 5.2 VM and
compiler with no IL emission. The source was read at tag `v0.5.7`.

**Spike code.** `spikes/S4-lua/Tzar2.Spikes.Lua` (`LuaSandbox`, `SourcePolicy`) holds the host, and
`spikes/S4-lua/Tzar2.Spikes.S4` runs the checks: `sandbox`, `budget`, `bench`, or `all`. S2 (ADR 0007) runs the
same host in its cross-platform matrix.

**Findings (measured 2026-10-17, Apple Silicon, macOS 26.5.2):**

* **Debug hooks exist and are public.** `LuaState.SetHook(LuaFunction hook, string mask, int count)` supports
  Lua's `"c"`, `"r"` and `"l"` masks and a count hook. The VM decrements a per-thread counter before every
  instruction and calls the hook at zero. So **no fork and no source rewriting are needed** for the budget.
* **Upstream bug: a hook that throws disables all later hooks on that thread.** `ExecutePerInstructionHook` sets
  `LuaState.IsInHook = true` around the count hook without `try/finally`, so after the first throw-based abort
  the next runaway call never stops. The S4 probe demonstrates this ("2nd runaway call ... did not stop within
  2 s"). **Workaround:** the hook does not throw. It cancels the call's `CancellationTokenSource` and returns
  normally. The VM checks the token right after the hook and throws `LuaCanceledException`.
* **Scripts cannot swallow the abort:** `pcall` rethrows `OperationCanceledException`, and `xpcall` rethrows when
  the token is cancelled. Verified for `while true`, a `pcall` loop, an `xpcall` loop, `error()` loops, coroutine
  bodies, `coroutine.wrap` + `pcall`, `table.sort` comparators, `__index` metamethods, `gsub` callbacks and
  recursion. The state stays usable after an abort, including 6 aborts in a row.
* **Coroutines do not inherit the hook** (`LuaState.CreateCoroutine` copies nothing), so a coroutine body was not
  budgeted. The sandbox's `coroutine.create`/`wrap` arm the hook on every new thread, and all threads charge
  one shared per-call budget.
* **The abort point is exactly deterministic.** Budget 100,000 with hook interval 1,000: the spin loop always stops
  after 25,249 iterations with 101,000 charged. That held over 50 fresh states and across arm64/x64 (Rosetta),
  net8/net10, Debug/Release, JIT/NativeAOT (S2 probe J, 4 budgets).
* **Overhead of the count hook** (benchmark: 274,162 instructions per call; median of 7 × 20 calls; ns per VM
  instruction):

  | hook interval | net10 JIT ms/call | overhead | net10 NativeAOT ms/call | overhead |
  |---|---|---|---|---|
  | none (no budget) | 1.725 | — | 1.962 | — |
  | 1 | 6.860 | +298% | 7.295 | +272% |
  | 10 | 2.221 | +29% | 2.539 | +29% |
  | 100 | 1.762 | +2% | 2.003 | +2% |
  | **1,000** | 1.783 | **+3% (noise)** | 1.946 | **−1% (noise)** |
  | 10,000 | 1.723 | 0% | 1.970 | 0% |

  The VM runs about 6–7 ns per instruction (≈150 M instructions/s) on this machine. A 20,000-instruction
  `onTick` costs about 0.13 ms. net8 numbers were noisy (tiered JIT) but the same order.
* **NativeAOT works.** `dotnet publish -c Release -f net10.0 -r osx-arm64 -p:Tzar2Aot=true` gives a 2.5 MB Mach-O
  arm64 executable with zero trim/AOT warnings under `TreatWarningsAsErrors`. All sandbox, budget and benchmark
  checks pass in it, and the cross-compiled `osx-x64` AOT build passes under Rosetta too (S2). Xcode command-line
  tools were present (Apple clang 21). Use `-p:Tzar2Aot=true`, not `-p:PublishAot=true`: the latter flows as a
  global property into the netstandard2.0 analyzer project and fails with NETSDK1207.
* **`pairs()` order is deterministic** (insertion order with free-list reuse, independent of hash codes; ADR 0007
  probe G). Iteration order still depends on the insert/delete history, which is part of the deterministic script
  state.
* **C#-stack re-entry depth is not deterministic upstream.** `pcall`, `xpcall`, `sort`, `gsub`, `resume` and
  `tostring` re-enter the VM on the C# stack, and Lua-CSharp stops that recursion with
  `RuntimeHelpers.TryEnsureSufficientExecutionStack`. The limit was 2,973–5,251 levels depending on
  platform/runtime/process. The sandbox counts re-entries itself and stops at a deterministic **128**. Pure Lua
  recursion (LuaStack limit) and `__index` recursion stopped at the same depth everywhere.
* **Performance trap:** the length operator `#t` is a linear scan (`LuaTable.ArrayLength`), so `t[#t + 1] = v` is
  quadratic: 20,000 appends take 133 ms instead of 0.6 ms. `table.insert(t, v)` uses the same scan.
* **No memory limit.** Lua-CSharp allocates on the .NET heap and has no allocator hook, so a deterministic memory
  cap is impossible without patching. The budget bounds growth *per call*, but doubling a string (`s = s .. s`)
  reaches gigabytes in about 30 instructions, and tables kept in globals can grow across ticks.

## Decision

1. **Use `LuaCSharp` 0.5.7** (added to `Directory.Packages.props`, Runtime group) as the DEC-13b runtime. The
   production host goes into `src/Tzar2.Scripting` and is modelled on `spikes/S4-lua/Tzar2.Spikes.Lua/LuaSandbox.cs`.
2. **Budget (REQ-SCR-06): count hook, interval 1,000 instructions, abort by cancellation, never by throwing.**
   * Each call re-arms the main thread's hook with `SetHook`, which resets the per-call residue. Every hook firing
     charges 1,000 against the budget. The call aborts at the first firing where charged > budget, so enforcement
     granularity is 1,000 instructions and the outcome is exact and deterministic.
   * Coroutines created through the sandbox arm the same hook and charge the same budget.
   * The abort maps to `onScriptError` with status `BudgetExceeded` (TDD REQ-SCR-06).
   * Instructions inside C# library functions are not counted, so those functions are bounded by argument limits
     instead (point 3).
3. **Sandbox = whitelist plus wrappers** (each item is checked by `Tzar2.Spikes.S4 sandbox`):
   * Libraries opened: basic, string, table, math, bit32, coroutine. Never opened: `io`, `os`, `debug`,
     `package`/`require`.
   * Removed from basic: `dofile`, `loadfile`, `load`, `loadstring`, `collectgarbage`. `print` is captured.
   * The string metatable is locked (`__metatable = false`) and `string.dump` is removed. The host loads text
     chunks only, so a binary chunk is a syntax error.
   * `math` is replaced by `abs ceil floor max min fmod huge` plus an engine-RNG `random(m[, n])`.
   * Integer arguments of `string.byte/char/sub/find/match/gmatch/gsub/rep`, `table.insert/remove/concat/unpack`,
     `select` and all of `bit32` are validated (ADR 0007).
   * `string.rep` output is capped at 64 KiB. `string.format` accepts only plain values. `tostring` of reference
     values needs `__tostring`.
   * Re-entrant functions are wrapped and share a deterministic nesting limit of 128.
   * The `^` operator is rejected at load (`SourcePolicy`).
   * An `fx` table provides `sin cos atan2 isqrt mul div idiv one` over Core's fixed-point math.
4. **The host calls Lua synchronously.** Lua-CSharp's API is `ValueTask`-based, but no sandboxed function is
   asynchronous. The host checks `IsCompleted` and treats a pending task as a host bug, so sim code stays free of
   `async/await` (REQ-SIM-05).
5. **Memory:** for 1.0, rely on the per-call budget, the length caps and a script-visible size quota on the
   persistent `state` table (REQ-SIM-14), checked deterministically by the host between calls. A non-deterministic
   process-level guard (e.g. allocated-bytes watchdog) may only end the match as a crash, never act as a sim rule. A
   deterministic concat/table-growth cap needs a small upstream patch (below).
6. **Engine and package upkeep:** the IsInHook bug, the linear `#t` and a string-length cap in `Concat` are filed
   upstream or carried as a minimal patch. The patch option is a vendored fork, which DEC-09 allows under MIT. That
   decision is deferred until the Lua API work in M4/M8 shows whether the issues bite.

## Alternatives considered

* **Interpreter fork with a built-in step counter** (TDD fallback 1): not needed, since the public count hook
  works and costs ~0% at interval 1,000.
* **Source-to-source loop-counter injection** (TDD fallback 2): it misses metamethods, comparators and callbacks
  re-entered from C#, which the hook covers, and it needs a parser of our own.
* **Wall-clock cancellation** (`CancellationToken` with a timeout): non-deterministic; it would desync lockstep
  peers.
* **MoonSharp / NLua / KeraLua:** MoonSharp is Lua 5.2 in C# but unmaintained and allocation-heavy, and its
  dynamic-code paths are a NativeAOT risk. NLua/KeraLua wrap native Lua (P/Invoke, per-platform binaries; native
  code is harder to sandbox and to keep identical across platforms).

## Consequences

* REQ-SCR-06 is answered: Lua-CSharp supports count hooks, and the budget is deterministic with negligible
  overhead. Upgrading the package means re-running `Tzar2.Spikes.S4 all` and `spikes/S2-determinism/run-matrix.sh`.
  The IsInHook workaround and the coroutine hook arming must survive any upgrade. The probe "a hook that throws
  disables later hooks" turns red once upstream fixes the bug, and that is the signal to revisit point 2.
* The Lua API documentation must tell modders: no `^`, integer arguments, `t[n] = v` with an explicit counter
  instead of `t[#t + 1]` in hot loops, no float results across the boundary, `tostring` only on plain values.
* Open items for M4/M8: exact budget numbers per hook type (`onTick` vs events), the `state` size quota, and what
  `onScriptError` does on repeated aborts (disable the script?).

## Questions for the product owner

* Budget sizing is a gameplay/modding decision: is **~20,000 instructions per script per tick** (~0.1 ms on this
  machine) an acceptable default for map scripts, with per-map overrides up to a hard engine maximum?
