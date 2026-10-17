# 0002. Target net8.0 for Godot-loaded assemblies, net10.0 for headless apps, pin SDK 10.0.402

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M0)
* **Related:** DEC-01, DEC-02, TDD §24 (.NET), REQ-ARCH-02

## Context

* TDD §24: use the .NET version required by the pinned Godot release (at least .NET 8 LTS) for everything Godot
  loads; Server and CLI MAY target a newer LTS.
* The pinned engine is Godot 4.7.2 .NET (DEC-01). Its `Godot.NET.Sdk` 4.7.2 and `GodotSharp` 4.7.2 packages ship
  only `lib/net8.0` (checked in the NuGet packages bundled with the editor). So net8.0 is the TFM for Godot.
* The .NET 10 SDK (10.0.402) builds net8.0 projects and is the current LTS SDK. .NET 8 reaches end of support on
  2026-11-10, i.e. weeks after M0.
* Godot's editor rewrites a client `.csproj` whose `TargetFramework` is not a literal (observed: it replaced
  `$(Tzar2LibraryTargetFramework)` with `net8.0` and left a `.csproj.old` backup).

## Decision

* `global.json` pins SDK **10.0.402** (`rollForward: latestFeature`).
* All libraries that the client loads (`Core`, `Formats`, `Content`, `Import`, `Sim`, `Scripting`, `AI`, `Net`,
  `Replay`, `Game`) and their tests target **net8.0**, via the property `Tzar2LibraryTargetFramework` in
  `Directory.Build.props`. `Tzar2.Client.csproj` states `net8.0` literally (see context).
* The headless apps `Tzar2.Tools.Cli` (`t2`) and `Tzar2.Server` target **net10.0** (`Tzar2AppTargetFramework`), as do
  their tests and `tools/Tzar2.Tools.LutGen`. `Tzar2.Determinism.Tests` multi-targets net8.0 and net10.0 so the golden
  hash is checked on both runtimes.
* The analyzer targets netstandard2.0 (required for Roslyn components) and references Microsoft.CodeAnalysis 4.8.0 so
  it loads in any compiler since .NET 8.0.1xx.
* The Godot engine version is pinned by `Sdk="Godot.NET.Sdk/4.7.2"` in the client project and `GODOT_VERSION` in the
  nightly workflow.
* One classic `Tzar2.sln` (Godot tooling expects `.sln`) contains every project; `Tzar2.Headless.slnf` is the same
  solution without the Godot client, used by the Linux/macOS CI jobs. The solution's `Release` configuration builds the
  client in Godot's `ExportRelease` configuration (the Godot SDK has no `Release`).

## Alternatives considered

* **Everything on net8.0:** simplest, but the CLI and server would ship on a runtime that leaves support in weeks.
* **Everything on net10.0:** not possible for the client side: a net10.0 library needs a net10.0 host, and Godot 4.7
  hosts net8.0 assemblies.
* **`.slnx`:** supported by the .NET 10 SDK, but Godot 4.7 and some IDEs still look for `.sln`.

## Consequences

* Libraries cannot use .NET 9/10-only APIs. If a later Godot release moves to net10.0, bumping
  `Tzar2LibraryTargetFramework` and the client literal is a one-line change each (via a new ADR, DEC-01).
* CI installs both the 10.0 SDK (from `global.json`) and the 8.0 runtime.

## Questions for the product owner

* .NET 8 support ends on 2026-11-10 while Godot 4.7.x still targets it. Do we accept shipping the Windows 1.0 client
  on .NET 8 (as every Godot 4.7 C# game does), or should we plan a Godot upgrade once a net10.0-based release exists?
