# Developing Tzar2

How to work on the rebuild: tools, build and test, CI, the way changes land, the rules that keep the project
correct, and the fidelity loop against the original game. Current state and next steps: [STATUS.md](STATUS.md).
The specification: [TDD.md](TDD.md). Decisions and their reasons: [decisions/README.md](decisions/README.md).
Repository layout: [README.md](../README.md#layout).

## Read first

1. [STATUS.md](STATUS.md): what is done, what is next, recent decisions.
2. [TDD.md](TDD.md) §1–§2 (summary and binding decisions DEC-01…DEC-14), then the section of the area you touch.
3. The ADRs of that area (index: [decisions/README.md](decisions/README.md)). An ADR beats the TDD where they differ;
   it says why.
4. The placeholder registers in [mechanics/](mechanics/) (`m2-`, `m3-`, `mapgen-`, `ai-placeholders.md`): every
   gameplay value that is not from the original's tables or a measurement is listed there as UNVERIFIED.

## Tools

* **.NET SDK 10.0.402**, pinned by `global.json` (resolved from the current directory, so run `dotnet` from the
  repository), plus the **.NET 8 runtime** (Godot-loaded assemblies target net8.0, headless apps net10.0; ADR 0002).
  A portable SDK works: set `DOTNET_ROOT` to it and put it first on `PATH`.
* **Godot 4.7.2 .NET** only to run or edit the client (`TZAR2_GODOT` for the client smoke tests). `dotnet build`
  builds the client without it.
* **PowerShell 7** for the capture runner (`tools/capture/run-capture.ps1`) and `tools/docs/adr-index.ps1`.
* **GitHub CLI** (`gh`) for PRs; `gh auth setup-git` lets git push with its token.

## Build and test

```sh
dotnet build Tzar2.sln                  # everything, including the Godot client
dotnet test Tzar2.sln -c Release        # all tests (the allocation gates run only in Release)
dotnet build Tzar2.Headless.slnf        # everything except the client
dotnet run --project src/Tzar2.Tools.Cli -- <command>   # the t2 tool (import, convert, campaign, fidelity, bench, soak, …)
```

A full Release build and test run takes about two minutes on a desktop. Run it before every PR; for a merge of
several PRs, run it on the combined result.

**Local-only tests** need data that never enters git (REQ-LEGAL-01) and are skipped without it:

| Variable | What | Used by |
|---|---|---|
| `TZAR2_ORIGIN` (or `TZAR2_ORIGINAL_PATH`) | the original install (clean GOG v1.10 EN) | import, reference-table, campaign and fidelity tests; `t2 fidelity analyze` |
| `TZAR2_PACKAGE` | an imported base package (`t2 import`) | `t2 campaign run` (M4 harness) |
| `TZAR2_FX_CAMPAIGNS` | folder with the FX Gold Edition campaigns (kept in `Origin/Campaign`, never committed) | editor acceptance `acc-fx` |
| `TZAR2_CORPUS` | folder of community maps and campaigns | corpus pass rate (M4 exit), `acc-fx` |
| `TZAR2_GOLDENS` | golden sprites from `tools/re/make_goldens.py` | M1 golden comparisons |
| `TZAR2_GODOT` | the Godot 4.7.2 .NET binary | client smoke tests |
| `TZAR2_SOUNDFONT` | a General MIDI SoundFont | music in the client |
| `TZAR2_FIDELITY_FACTS` | another facts folder | `Tzar2.Fidelity.Tests` |

## CI

`.github/workflows/ci.yml` runs on every PR and on `main`:

* **legal**: fails if a tracked file matches the original game (REQ-LEGAL-01); **licenses**: dependency licences
  (DEC-09).
* **Windows x64 - full build and tests**, **Headless core** on Linux and macOS.
* **Determinism**: the golden-hash tests and three `t2 bench` hash streams (sim, gameplay, AI) with pinned final
  hashes (`--expect-hash`), compared across the three OSes.
* **Replays**: a corpus recorded on Linux and verified on every OS; **capture harness** self-test on Windows;
  **relay server** Docker image.

`nightly.yml` adds long hash streams, benchmarks, the Windows Godot build and the multiplayer soak.

**Pinned hashes change on purpose when the rules or a gameplay system change.** Re-pin them in the same PR, with the
reason in the commit message and in the history comment above the constants:
`tests/Tzar2.Determinism.Tests/*GoldenHashTests.cs` print the new values in their test output; the bench hashes in
`ci.yml` come from the same `t2 bench …` command run locally. All OSes and worker counts must agree; if they do not,
it is a determinism bug, not a re-pin.

**Known flaky:** `Tzar2.Net.Tests.RelayTests` time out now and then on loaded CI runners (issue #62). If only they fail,
re-run the failed job (`gh run rerun <id> --failed`) and note it on the PR.

## How changes land

* Work on a topic branch (`fidelity-batch-b`, `m4-rock-passability`, …), open a PR against `main`, merge with
  **squash** only after CI is green. The PR description says what changed, why, and how it was tested.
* **No force-pushes** to shared branches. If a pushed branch has to be rebuilt (typically a stacked PR whose base was
  squash-merged and now conflicts), replay its own commits on `main` in a new branch (`git cherry-pick`), test, open a
  replacement PR and close the old one with a link.
* Stacked PRs conflict easily after squash merges: merge the base, then retarget the next PR to `main` and check that
  it still merges cleanly before relying on its old CI run.
* Every PR keeps [STATUS.md](STATUS.md) true: milestone state, work queue, and a log line for decisions.
* AI-assisted commits end with a `Co-Authored-By:` line naming the model.

## Rules that keep the project correct

* **Nothing from the original game in git** (REQ-LEGAL-01): no files from `Origin/` or another install, no screenshots
  or sounds of it. Commit our own specs, manifests and measured numbers only. CI enforces the file part.
* **Determinism**: the simulation uses fixed-point integers, no floating point, wall clock, unordered iteration or
  global randomness (TDD §8; the analyzers in `Tzar2.Analyzers`, ADR 0004, flag what they can). Hashes must agree across
  OSes, runtimes and worker counts.
* **Placeholders are not balance knobs.** A value that is neither in the original's tables nor measured stays in its
  register as UNVERIFIED until a capture fact replaces it; then it is marked VERIFIED with the session that proved it.
* **A decision gets an ADR** when it shapes code or data others build on, or when it picks between real alternatives.
  Use the [template](decisions/0000-template.md) and the next free number, then regenerate the index with
  `pwsh tools/docs/adr-index.ps1`.
* **Files**: UTF-8 without BOM, LF line endings (`.sln`, `.ps1`, `.cmd` keep CRLF), per `.editorconfig` and
  `.gitattributes`. Docs and code comments in English.
* Windows PowerShell 5.1 pitfalls: piping text into `git commit -F -` adds a BOM to the message (write it to a file
  first); quote `'stash@{0}'`, or PowerShell eats the braces.

## The fidelity loop

The M3 gate is the share of TDD §22.3 checks that pass (ADR 0112); the measurements come from the original game on the
operator's PC.

1. The operator runs a batch: `pwsh tools/capture/run-capture.ps1 -TzarPath <install> -Batch <A…I>`; it writes
   `~/Tzar2Capture/capture-<time>.zip` (captures stay on that PC).
2. `t2 fidelity analyze <zip> --original <install> --facts tests/fidelity/facts` writes the fact files (numbers only).
   Check the analyses' `status` and `notes`: an `inconclusive` analysis may be the analysis's fault, not the capture's
   (batch A: the construction timing needed a new health-bar reading).
3. `t2 fidelity report` lists every check as pass, fail or pending. `Tzar2.Fidelity.Tests` fails on any measured
   mismatch, so a disagreeing fact is committed **together with** the engine change that makes it pass.
4. Fix the engine or the rules, run all tests, re-pin hashes if the rules changed, mark the placeholder rows VERIFIED,
   update [fidelity-summary.md](mechanics/fidelity-summary.md) and [STATUS.md](STATUS.md), write an ADR for the
   decision, open the PR.

## Capture sessions on the operator's PC

Operator instructions: [tools/capture/README.md](../tools/capture/README.md). What it took on Windows 11 25H2 with
GOG v1.10 (ADRs 0110, 0111, 0169), in case it recurs:

* GOG's compatibility database (`goggame.sdb`, `EmulateHeap`) matches `Tzar.exe` by name and crashes it in
  `winmmbase.dll` at start-up; remove it for the session (`sdbinst -u -g {guid}`, the harness prints the command).
* The harness writes `DIRECTXDEVICE=0` (GOG ships the GDI renderer) and the device counts the game checks at start-up
  (`DirectDrawEnumerateA` + 1, waveOut + 1, DirectSound with the primary driver, midiOut + 1). Bluetooth headsets change
  those counts: keep audio devices unchanged during a batch. If the game still reports "Invalid TZAR configuration",
  run `Setup.exe` in the work copy and set the values it writes in a local, uncommitted `session.yaml`.
* cnc-ddraw runs with `devmode=true` (otherwise the first click is swallowed); `-map <name>.wmp` opens the setup screen
  and scenarios click PLAY; `hmreveal`/`hmnofog` reveal the map (the command-line flags do not); `hmnopop` before
  `hmspawn`; `hmkingdom N` changes the owner of the selected unit.
* MP2's middle has a row of Nature's wild cows (map rows 33–34) that idle soldiers attack within seconds; scenarios
  that need a soldier to stay put or walk where it is told run elsewhere (ADR 0171: the view clamped to the
  bottom-left corner, rows 51–53 by our base).
* The minimap box is 128 px, but MP2 (64 × 64 tiles) is drawn 1 px per tile in its middle (x 36–99, y 63–126); a
  click outside the drawn map is ignored and the view stays put. Check the `view` snapshot of a new scenario.
* The operator must not touch mouse or keyboard during a batch; stop with Ctrl+C in the terminal (the zip keeps what
  was captured). Do not install third-party launchers into the GOG folder: they patch `Tzar.exe` (the run then fails
  the reference check).

## Diagnostics: logs, traces, crash reports, replays

Every run of the client writes a log folder (ADR 0176): `%APPDATA%\Godotpp_userdata\Tzar2\logs\yyyyMMdd-HHmmss\`
on Windows (`user://logs/`), with `game.log`, `crash-*.txt` and `crash-replay.t2rep` after a crash. Every match's replay
is in `user://replays/` (the log says which file). Godot keeps its own `user://logs/godot.log` too.

* **Levels** per area (session, commands, events, sim, ai, triggers, net, client, content): set `TZAR2_TRACE`, or write
  `user://trace.txt`, e.g. `*=info` or `*=debug, ai=verbose, events=info`. The default is debug everywhere.
* **Reading a log:** `HH:mm:ss.fff tick LEVEL area: message`. Kingdom summaries every 20 s of game time (`summary at`),
  every command (`commands`), AI decisions (`ai: p1 economy: ...`), rejected commands (`WARN ... rejected`).
* **In code:** deterministic code writes through `world.Trace` and checks `IsEnabled` first; the host attaches the sink
  (`MatchTracer.Attach`). Never read anything back from a trace.

## Where to record what

| What | Where |
|---|---|
| Current state, next steps, process decisions | [STATUS.md](STATUS.md) |
| A design decision and its reasons | an ADR in [decisions/](decisions/) |
| Requirements, binding decisions, unknowns (UNK-*) | [TDD.md](TDD.md) and its appendices in [tdd/](tdd/) |
| Gameplay values not yet measured | the placeholder registers in [mechanics/](mechanics/) |
| What we know about the original's file formats | [formats/](formats/) |
| Measurements of the original | `tests/fidelity/facts/` (generated) and [mechanics/fidelity-summary.md](mechanics/fidelity-summary.md) |
| The original build, its manifests, what may be committed | [original-build/README.md](original-build/README.md) |
