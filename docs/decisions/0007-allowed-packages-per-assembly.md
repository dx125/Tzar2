# 0007. Allow NuGet packages per assembly; System.IO.Hashing in Core

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M0 hardening review)
* **Related:** TDD §7.2, §8.5, §24, DEC-09, REQ-ARCH-01, REQ-ARCH-02, ADR 0004

## Context

TDD §7.2 gives each assembly a "May reference" list that mixes Tzar2 projects and third-party libraries
(Content → YamlDotNet, Scripting → Lua-CSharp, Net → LiteNetLib) and says Core uses the "BCL only". M0 checked only
project references, so any package could be added to any assembly without a test failing. Core already uses
`System.IO.Hashing` for xxHash64 (TDD §8.5 state hashes), which ships as a NuGet package, not in the shared framework.

DEC-09 requires permissive runtime licenses. `tools/ci/allowed-licenses.json` also allowed MS-PL, which no package in
the solution (runtime, tools or tests; 64 packages at the time of writing) uses.

## Decision

* `Tzar2.Architecture.Tests` holds an allowed-package map per source project and checks it three ways: the
  `PackageReference`s in each `.csproj`, no `PackageReference` in the shared `Directory.Build.*` files, and the direct
  dependencies NuGet actually restored (`obj/project.assets.json`, SDK-implicit `autoReferenced` packages excepted).
  Compiled assemblies may reference only allowed Tzar2 assemblies, the framework, and assemblies of their allowed
  packages.

  | Project | Allowed packages |
  |---|---|
  | Core | `System.IO.Hashing` |
  | Content | `YamlDotNet`, `Vecc.YamlDotNet.Analyzers.StaticGenerator` (YamlDotNet static generator, REQ-ARCH-02) |
  | Scripting | `LuaCSharp` (Lua-CSharp, REQ-SCR-04), once added |
  | Net | `LiteNetLib` (DEC-08) |
  | Client | the Godot SDK packages (`GodotSharp`, `GodotSharpEditor`, `Godot.SourceGenerators`) |
  | Analyzers | `Microsoft.CodeAnalysis.CSharp`, `Microsoft.CodeAnalysis.Analyzers` |
  | Formats, Import, Sim, AI, Replay, Game, Server, Tools.Cli | none |
  | `tests/*` | free (licenses are still checked) |

* **Core's "BCL only" admits `System.IO.Hashing`.** It is an out-of-band part of the .NET BCL (dotnet/runtime,
  Microsoft, MIT), pure managed, AOT/trim-safe, and its xxHash64 is specified bit for bit, so it does not weaken
  determinism. No other package may be added to Core without a new ADR.
* MS-PL is removed from `tools/ci/allowed-licenses.json`; the allowed set is MIT, Apache-2.0, BSD-2/3-Clause, Zlib and
  MPL-2.0. If a test-only dependency ever needs another license, it is added with an ADR and restricted to test projects.
* `Tzar2.Architecture.Tests` targets the app framework (net10.0) so it can load and check the Server and `t2` assemblies too.

## Alternatives considered

* **Copy xxHash64 into Core:** removes the exception but duplicates a maintained, tested BCL implementation.
* **Check only `.csproj` XML:** misses packages added by imported props/targets; the restore output covers that.

## Consequences

* Adding a package to a source project means updating `DependencyRulesTests.AllowedPackages`, which makes the change
  visible in review and ties it to TDD §7.2.
* When Lua-CSharp, YamlDotNet or LiteNetLib are added, verify the NuGet ID and assembly name in the map.
