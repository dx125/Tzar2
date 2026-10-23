# 0244. Measure S2 in the real client with a render bench that prints one line

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** maintainer (Claude), coordinator
* **Related:** TDD §4.3 S2, §21; ADRs 0010, 0080, 0176, 0183, 0240 (M7 deliverable D6)

## Context

S2 has two halves: the S2 scenario (Real World 512², 8 AIs, 4,000 units, day/night and weather) must render at
≥ 60 FPS at 2560 × 1440, and its sim tick p99 must stay ≤ 12 ms on a 2020 6-core CPU. The nightly `t2 bench --scenario
s2` measures a synthetic proxy of the sim half on cloud runners (ADR 0080). Nothing measured the render half since
spike S1 (ADR 0010), and nothing measured the real game's sim, where the legacy AI, combat, the trigger engine and the
renderer's frame building run together. The owner's PC is the only Windows machine with a real GPU; the result must
come back as one line he can paste.

## Decision

1. **`--bench-render`** in the client (`GameApp.RenderBench.cs`) plays the S2 scenario of `RenderBench`
   (Tzar2.Game, engine-free and tested):
   * a 512 × 512 random continental map (the Real World size; the original's Real World map is not ours to ship), grass;
   * eight kingdoms, all run by the Hard AI (the viewer's own kingdom through AI assistance, ADR 0183);
   * armies spawned on free tiles around each castle up to 4,000 mobile units at match start;
   * day/night and weather on, no fog, so every unit is drawn;
   * a fixed seed, so every run plays the same match.
2. **The measurement:** V-Sync and the frame cap off; the window at `--resolution` (default 2560x1440) or exclusive
   `--fullscreen`; 15 s of warm-up, then `--bench-seconds` (default 120) while the camera tours the castles (8 s a leg).
   Frame times are wall time between process frames; sim tick times come from the session's own clock around each tick
   (`GameSession.TickTimes`, a ring the sim thread fills without allocating; never part of the match).
3. **The output:** one line,
   `RENDER-BENCH v1 S2=<PASS|FAIL|n/a> res=… fps=… low1=… frame_p50=… frame_p99=… sim_p50=… sim_p99=… ticks=…
   frames=… units=… load=…s ram=…GB vram=…GB renderer=… gpu="…" cpu="…" version=…`, printed, written to the run's log
   and appended to `user://bench/render-bench.txt`. `phases=name:mean/slow,…` splits the tick into its top-level
   sections (`PhaseTimes`, fed through the sim's `ITickProfiler`): each one's mean and its mean over the slowest 1 % of
   ticks (what the p99 is made of), largest first, `(outside)` for tick time outside every section, `rest` for the
   smaller ones. `S2=PASS` needs 2560 × 1440, a real renderer, ≥ 60 FPS and
   sim p99 ≤ 12 ms; other resolutions and headless runs say `n/a`. Exit code 0 when frames were measured.
4. **Headless** (`--headless`) runs the same match on the dummy renderer: a check that the bench works anywhere (CI,
   the Mac), with `renderer=dummy/headless` and `S2=n/a`.

## Alternatives considered

* **Godot's own profiler / an external tool (PresentMon):** numbers the owner would have to read off a screen or a CSV;
  one line is what can come back reliably.
* **The original's Real World map:** it is the owner's data; a converted map would make the bench depend on the
  import and on the converter, and could not run in CI. The random 512² map has the same size and kingdoms.
* **Units through the AI's own production:** 4,000 units would take an hour of play and differ per run; spawned armies
  give the load at once and the same every time.

## Consequences

* First runs on a MacBook (Apple M5, the imported GOG package, client assemblies built Release, 1920 × 1080 windowed,
  60 s, 2026-10-22), on a machine shared with other agents (load average 30–97 for the first, 4–6 for the second):
  120 FPS, frame p99 15.8–18.3 ms; sim p50 3.1–4.1 ms, **p99 12.9–14.8 ms**; load 3.5–3.8 s; VRAM 3.1 GB (Metal's
  figure, before the terrain atlas of PR #138 and the freed pages of PR #161). The phases of the quieter run:
  `full_hash:0.3/5.4,visibility:1.2/3.6,movement:1.5/2.4,ai:0.6/2.0`: the slowest ticks are the ones that also compute
  the full-state hash. The owner's Windows numbers decide; a Debug build is several times slower (sim p99 82 ms), so
  measure only packaged (ExportRelease) builds.
* **Re-run on `main` fb8aa49 (2026-10-23)**, after the terrain atlas (#138), the freed pages and stopped shadow
  viewports (#161) and loading on a worker thread (#172); same MacBook, Release assemblies, 60 s per run, six runs
  while other agents' builds kept the load average between 5 and 41:

  | Run | Load avg | Resolution | FPS | 1 % low | Frame p99 | Sim p50 | Sim p99 | Load | RAM | VRAM | S2 |
  |---|---|---|---|---|---|---|---|---|---|---|---|
  | 1 | 44 → 23 | 1920 × 1080 | 239.2 | 81.8 | 10.0 ms | 2.9 ms | 41.6 ms | 2.7 s | 0.82 GB | 0.38 GB | n/a |
  | 2 | 6 → 41 | 1920 × 1080 | 239.0 | 72.4 | 10.9 ms | 5.9 ms | 109.8 ms | 3.0 s | 1.54 GB | 0.38 GB | n/a |
  | 3 | 41 → 35 | 2560 × 1440 | 238.8 | 70.7 | 13.3 ms | 3.3 ms | 10.6 ms | 7.3 s | 1.10 GB | 0.38 GB | PASS |
  | 4 | 30 → 15 | 2560 × 1440 | 239.5 | 76.4 | 11.5 ms | 2.6 ms | 18.8 ms | 2.4 s | 1.39 GB | 0.38 GB | FAIL |
  | 5 | 15 → 11 | 2560 × 1440 | 239.1 | 75.4 | 11.4 ms | 2.8 ms | 10.8 ms | 2.4 s | 1.21 GB | 0.38 GB | PASS |
  | 6 | 11 → 10 | 1920 × 1080 | 238.7 | 71.0 | 12.7 ms | 2.6 ms | 11.0 ms | 2.4 s | 1.49 GB | 0.38 GB | n/a |

  * **Render:** about 239 FPS in every run at both resolutions. This looks like a ceiling of the Mac's frame loop,
    not of the GPU. The 1 % low is 71–82 FPS and frame p99 10–13 ms.
  * **Memory and load:** VRAM fell from 3.10 to **0.38 GB**, under TDD §21's 1.5 GB. Load time is 2.3–3.0 s, with
    one run at 7.3 s.
  * **Sim:** p50 is 2.6–3.3 ms. p99 is 10.6–11.0 ms in the quieter runs and 16–110 ms when other processes take the
    cores.
  * **Phases:** the section that dominates the slowest ticks changes from run to run (movement, the AI, visibility,
    the full hash). That points to preemption by other processes, not to one costly section.
  * **Verdict:** a dedicated PC is needed for the sim half of S2; the owner's Windows run decides.
* The owner's runs come with exact commands (docs/STATUS.md work queue item 13 and the min-spec plan).
* Follow-ups: a per-system breakdown of the real game's tick (the `t2 bench` sections inside the client), and the
  bench as an optional nightly job once a runner with a GPU exists.
