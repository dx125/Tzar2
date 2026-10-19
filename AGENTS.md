# Working on Tzar2 (for AI agents and new developers)

A deterministic C# + Godot 4 rebuild of *TZAR: The Burden of the Crown* (2000). Start here:

1. **[docs/STATUS.md](docs/STATUS.md)**: what is done, what is in progress, the work queue, and the log of decisions.
2. **[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)**: tools, build and test, CI, how changes land, the fidelity loop.
3. **[docs/TDD.md](docs/TDD.md)**: the specification; **[docs/decisions/README.md](docs/decisions/README.md)**: the
   ADRs, which record why the code is the way it is and override the TDD where they differ.

Non-negotiable:

* Never commit anything from the original game (`Origin/` or any install): no files, screenshots or sounds. Specs,
  manifests and measured numbers only (REQ-LEGAL-01; CI checks).
* The simulation is deterministic: fixed-point integers, no floats, wall clock, unordered iteration or global
  randomness. Golden and bench hashes change only on purpose, re-pinned in the same PR with the reason.
* Gameplay values that are neither in the original's tables nor measured are UNVERIFIED placeholders in
  `docs/mechanics/*-placeholders.md`; replace them with capture facts, do not tune them.
* A decision others will build on gets an ADR (template `docs/decisions/0000-template.md`, then
  `pwsh tools/docs/adr-index.ps1`).
* Branch, PR, green CI, squash merge; no force-pushes to shared branches.
* Before you finish: `dotnet build Tzar2.sln` and `dotnet test Tzar2.sln -c Release` pass, and **docs/STATUS.md is
  updated** (milestones, work queue, a log line for any decision).

Things only the product owner can do (on the Windows PC with the original game): capture batches, sessions in the
original editor, local-only tests that need the original files. Ask for them with exact commands and say how long
they take; the operator must not touch the PC during a capture.
