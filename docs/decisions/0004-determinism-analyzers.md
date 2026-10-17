# 0004. Enforce determinism with an opt-in Roslyn analyzer (rules T2D001-T2D006)

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M0)
* **Related:** TDD §8.2, §8.8, REQ-SIM-04, REQ-SIM-05, REQ-SIM-06, REQ-ARCH-01, REQ-ARCH-02

## Context

TDD §8.8 reserves analyzer rules `T2D001-T2D020` and applies them to assemblies marked
`[assembly: Tzar2Deterministic]`. REQ-ARCH-01 additionally forbids I/O in Sim, Scripting and AI, but not in Core or
Replay (Replay records files).

## Decision

* Two MSBuild properties, handled in the root `Directory.Build.targets`:
  * `<Tzar2Deterministic>true</Tzar2Deterministic>` adds `[assembly: Tzar2.Core.Tzar2Deterministic]` and the analyzer.
    Set in **Core, Sim, Scripting, AI, Replay**.
  * `<Tzar2NoIO>true</Tzar2NoIO>` adds `[assembly: Tzar2.Core.Tzar2NoIO]`. Set in **Sim, Scripting, AI**.
  The analyzer keys on the attributes, so the attribute alone (e.g. in a mod assembly) is enough.
  `Tzar2.Architecture.Tests` checks that these projects keep their opt-in.
* Rules, all `Error`:

  | ID | Rule | Reported for |
  |---|---|---|
  | T2D001 | Floating point | `float`, `double`, `decimal`, `Half`, `NFloat`, `MathF`, `System.Numerics` vector/matrix/quaternion/complex types in declarations, literals, conversions, invocations, member access and `typeof`; one diagnostic per outermost floating expression |
  | T2D002 | Random source | `System.Random`, `RandomNumberGenerator`, `Guid.NewGuid` |
  | T2D003 | Wall clock | `DateTime.Now/UtcNow/Today`, `DateTimeOffset.Now/UtcNow`, `Environment.TickCount(64)`, `Stopwatch`, `TimeProvider` |
  | T2D004 | Unordered iteration | `foreach` over `Dictionary` (and its key/value collections), `HashSet`, `IDictionary`, `IReadOnlyDictionary`, `ISet`, `IReadOnlySet`, concurrent, immutable and frozen hash collections, `Hashtable`, and types deriving from them |
  | T2D005 | Runtime code generation | anything in `System.Reflection.Emit`; `LambdaExpression.Compile` / `Expression<T>.Compile` |
  | T2D006 | I/O (only with `Tzar2NoIO`) | `File`, `Directory`, `FileInfo`, `DirectoryInfo`, `FileSystemInfo`, `DriveInfo`, `FileSystemWatcher`, `RandomAccess`; constructing any `Stream`, `StreamReader/Writer`, `BinaryReader/Writer`; anything in `System.Net` |

* Generated code is analyzed too.
* Culture-sensitive string operations are covered for now by the built-in CA1304/CA1305/CA1307/CA1309/CA1310 rules,
  raised to warnings (= errors under `TreatWarningsAsErrors`) in `.editorconfig`.

## Alternatives considered

* **BannedApiAnalyzers (`BannedSymbols.txt`):** good for member bans, but cannot express "floating-point expression",
  "foreach over an unordered collection" or "constructing any Stream subtype".
* **Opting in by project name in MSBuild only:** works for our projects but not for C# trait modules from mods
  (DEC-13c); the attribute does.

## Consequences

* Not yet covered (IDs T2D007+ reserved): static mutable state, `async`/`await`, `Parallel.*` with unordered merges,
  `string.GetHashCode()`, and LINQ or `ToList()` over unordered collections. These come with M2, when the ECS gives
  them concrete shape. (Floating `System.Math` APIs such as `Math.Sqrt(int)` are already caught by T2D001 through
  their return type.)
* Core is deterministic but may do I/O; `Fixed` and `Trig` therefore never use floating point, and the trig tables are
  generated offline by `tools/Tzar2.Tools.LutGen` and hash-checked in `Tzar2.Core.Tests`.
