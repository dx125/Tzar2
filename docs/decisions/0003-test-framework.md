# 0003. Use xUnit v2 with FsCheck, a direct Roslyn harness for analyzer tests, and BenchmarkDotNet

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M0)
* **Related:** TDD §22, §24 (dependencies), DEC-09, REQ-LEGAL-03

## Context

TDD §22 asks for unit and property tests per assembly (FsCheck named), a determinism suite and BenchmarkDotNet
benchmarks. All tools must be permissively licensed (DEC-09) and fixtures synthetic (REQ-LEGAL-03).

## Decision

* **xUnit 2.9.3** (Apache-2.0) with `xunit.runner.visualstudio` and `Microsoft.NET.Test.Sdk`, run through `dotnet test`.
  Shared package references live in `tests/Directory.Build.props`, versions in `Directory.Packages.props`.
* **FsCheck 3.4 + FsCheck.Xunit** (BSD-3-Clause) for property tests (`[Property]`), with arbitrary-precision
  `BigInteger` reference models for fixed-point arithmetic.
* **One test project per source assembly** (`tests/<Assembly>.Tests`), plus `Tzar2.Architecture.Tests` (dependency
  rules of TDD §7.2), `Tzar2.Determinism.Tests` (golden hash, both runtimes) and `Tzar2.Benchmarks`
  (BenchmarkDotNet 0.15, MIT; an executable, not run by `dotnet test`).
* **Analyzer tests** compile snippets with `CSharpCompilation` against the test host's framework assemblies and run
  the analyzer through `CompilationWithAnalyzers` (see `tests/Tzar2.Analyzers.Tests/AnalyzerHarness.cs`).
* Every test fixture is generated in the test (temporary directories, synthetic manifests). The only repository data a
  test reads is the reference manifest (metadata, not game files).

## Alternatives considered

* **xunit.v3:** the newer line, but the FsCheck integration and runner story for v3 is younger; v2 is stable and the
  migration later is mechanical.
* **NUnit / MSTest:** fine, but xUnit was requested and is the most common choice in .NET OSS.
* **Microsoft.CodeAnalysis.Analyzer.Testing:** heavier, downloads reference-assembly packages at test time, and its
  xUnit-specific packages are deprecated. The direct harness is ~60 lines and offline.

## Consequences

* Moving to xunit.v3 later is a separate ADR.
* The CI license check covers test-only packages too, which is stricter than DEC-09 requires.
