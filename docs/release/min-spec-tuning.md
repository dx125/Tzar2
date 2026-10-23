# Min-spec tuning plan (M7)

What M7 measures on real Windows hardware, with which settings and benches, and what the owner is asked to run. Part of
the M7 plan (ADR 0240). The targets are TDD §4.3 (S2) and §21.

## Targets

| Tier | Hardware | Resolution | Must hold |
|---|---|---|---|
| **Reference (S2)** | 6-core 2020 CPU (Ryzen 5 3600 / Core i5-10400 class), GTX 1060 6 GB class, 16 GB RAM, Windows 10/11 | 2560 × 1440 | S2: Real World 512², 8 AIs, 4,000 units, day/night and weather on: **≥ 60 FPS** and **sim tick p99 ≤ 12 ms**; RAM ≤ 3 GB, VRAM ≤ 1.5 GB; match load ≤ 10 s; first-run import ≤ 3 min (TDD §21). |
| **Minimum (proposed)** | 4-core / 8-thread 2017 CPU (Core i5-7400 / Ryzen 3 3100 class), Intel UHD 620/630 or GTX 950 class, 8 GB RAM | 1920 × 1080 | A 4-player skirmish at the default settings at ≥ 30 FPS with sim p99 ≤ 25 ms; the S2 scenario playable (≥ 30 FPS) with weather off. The published minimum is what this tier measures, not this proposal. |
| **High** | the owner's PC (RTX 4070 Ti; CPU per its `t2 bench` header) | 2560 × 1440 and 3840 × 2160 | No regression against the reference: it shows the ceiling and the scaling of the sim with cores. |

## Settings that matter

Today's settings (`user://game.cfg`, SETTINGS screen, ADR 0177): display preset (Wide / Authentic 1024 × 768),
interface scale, window mode and size, day/night, weather. The sim's worker threads are a match setting (`--workers` in
`t2 bench`; never change results).

To add in M7, each only if a measurement shows it is needed (no setting without a measured effect):

* **Renderer**: Mobile (default, ADR 0010) or Compatibility (OpenGL) as a fallback for old Intel drivers. Godot's
  `--rendering-method gl_compatibility` command-line switch tests it without code.
* **V-Sync and a frame cap** (Godot `display/window/vsync/vsync_mode`, `application/run/max_fps`): V-Sync off is what
  the benches measure; a cap saves laptops' batteries.
* **Sim workers** for the client: default `min(cores, 8)`; the minimum tier may do better with fewer.
* **Weather and day/night intensity** are already on/off; the visual options of M8 refine them.

## Benches

| Bench | What it gives | Exists |
|---|---|---|
| `t2 bench --scenario s2` | The S2 sim load headless: tick p50/p99 on the machine's CPU (ADR 0080). Nightly on Linux runners. | yes |
| `t2 bench --scenario ai --players 8 --ticks 24000` | 8 legacy AIs for 20 minutes: AI cost at scale. | yes |
| `t2 bench --scenario b4` | Mass death then traffic (ORIG-01). | yes |
| Client log `slow frame` lines | Frames over budget during real play, at most one per 5 s (ADR 0176). | yes |
| **Client render bench** `--bench-render` (ADR 0244) | The S2 scenario in the real client for 120 s after 15 s of warm-up, the camera touring the castles: FPS, frame time p50/p99, sim p50/p99, the tick's phases, RAM and VRAM peaks, load time, the S2 verdict; one `RENDER-BENCH` line per run. | yes |

## Results

### High tier: the owner's PC (2026-10-23)

AMD Ryzen 7 9800X3D (8 cores, 16 threads), NVIDIA RTX 4070 Ti (12 GB), 64 GB RAM, Windows 11, monitor 3440 × 1440.
`main` 25b60b3; the installed build 0.7.0-beta.41+22eaed1 (nightly #41). Claude ran the three requests with the PC
otherwise idle.

**Request 1, the sim on a desktop CPU** (`t2 bench`, three runs per worker count; calibration 16.6–17.6 ms, scale
1.42–1.51):

| Scenario | Workers | Tick p50 (ms) | Tick p99 (ms) |
|---|---|---|---|
| s2 | 1 | 2.96–2.99 | 4.41–4.49 |
| s2 | 4 | 1.16–1.18 | 3.94–4.82 |
| s2 | 6 | 0.99–1.02 | 3.03–3.36 |
| s2 | 8 | 0.95–0.99 | 3.16–3.69 |
| ai, 8 players, 24,000 ticks | 4 | 0.32 | 1.26 |

* S2's sim half passes with room: p99 at most 4.8 ms against the 12 ms budget.
* The sim stops scaling at 6 workers.

**Request 2, the installed build:**
* The silent per-user install worked.
* `FIRST-RUN IMPORT OK` came in **10.4 s**, against the ≤ 180 s target.
* The 10-minute skirmish and its logs are still to come (the owner).

**The render bench** (`RENDER-BENCH v1`, 4,000 units, 120 s):

| Run | Renderer | FPS | 1 % low | Frame p50 / p99 (ms) | Sim p50 / p99 (ms) | Load (s) | RAM (GB) | VRAM (GB) | S2 |
|---|---|---|---|---|---|---|---|---|---|
| 2560 × 1440 | Mobile (Vulkan) | 1,684 | 603 | 0.6 / 1.4 | 2.8 / 6.8 | 2.2 | 2.31 | 0.33 | PASS |
| 2560 × 1440 | Mobile (Vulkan) | 1,670 | 543 | 0.6 / 1.5 | 2.8 / 6.8 | 2.0 | 2.37 | 0.33 | PASS |
| 1920 × 1080 | Mobile (Vulkan) | 1,585 | 527 | 0.6 / 1.5 | 2.8 / 6.6 | 2.1 | 2.29 | 0.34 | n/a |
| fullscreen 3440 × 1440 | Mobile (Vulkan) | 1,572 | 598 | 0.6 / 1.4 | 2.9 / 6.6 | 2.0 | 2.43 | 0.34 | n/a |
| 2560 × 1440 | Compatibility (OpenGL) | 1,085 | 485 | 0.9 / 1.8 | 2.8 / 6.7 | 2.1 | 2.15 | 0.43 | PASS |

**Follow-ups:**
* In the client, the sim p99 (6.6–6.8 ms) is about twice the headless bench's. The peaks are the full-state hash (up
  to 3.3 ms; once the AI, 4.7 ms).
* RAM is 2.3 GB against a 296 MB package: within the 3 GB target, but worth a look in the code review (work queue 16).
* Normal play keeps Godot's default V-Sync; only the bench turns it off.

## Requests for the owner's PC

All from the repository checkout on the owner's PC (`git pull` first), in PowerShell. Nothing needs the original game
except the import timing. Close other programs; keep the PC on mains power. Send back the printed lines (or the files).

**Now (about 15 minutes, hands off): the sim on a real desktop CPU.**

```powershell
cd <Tzar2 checkout>
git pull
foreach ($w in 1, 4, 6, 8) {
  foreach ($run in 1, 2, 3) {
    dotnet run -c Release --project src/Tzar2.Tools.Cli -- bench --scenario s2 --workers $w |
      Select-String '^(scenario|cpu|tick ms|calibration|normalized)' | Out-File -Append "$HOME\Desktop\tzar2-bench-s2.txt"
  }
}
dotnet run -c Release --project src/Tzar2.Tools.Cli -- bench --scenario ai --players 8 --ticks 24000 --workers 4 |
  Select-String '^(scenario|cpu|tick ms|calibration|normalized)' | Out-File -Append "$HOME\Desktop\tzar2-bench-s2.txt"
```

Send `Desktop\tzar2-bench-s2.txt`. It gives the S2 sim p99 on a desktop CPU (the nightly runs on 4-vCPU cloud runners)
and how the sim scales with workers.

**When the package of this PR is green (about 10 minutes): the installed build and the first-run import.**

1. Download the artifact `tzar2-windows-<run>` of the latest green `Windows package` run
   (<https://github.com/dx125/Tzar2/actions/workflows/windows-package.yml>) and unzip it.
2. Install with `Tzar2-<version>-win-x64-setup.exe` (Windows will warn about an unsigned file: More info → Run anyway).
3. Time the import headless (the game folder as the play-tests use it):

```powershell
& "$env:LOCALAPPDATA\Programs\Tzar2\Tzar2.console.exe" --headless -- --import "E:\Games\Tzar" --smoke
```

   The last line is `FIRST-RUN IMPORT OK <package> <seconds> s` (target ≤ 180 s). Send that line.
4. Start Tzar2 from the Start menu once: it asks about crash reports (answer as you like), shows the menu with the
   imported game. Play a 4-player skirmish for 10 minutes at 2560 × 1440 fullscreen, quit normally, and send the
   newest folder under `%APPDATA%\Godot\app_userdata\Tzar2\logs\`.

**The render bench (about 20 minutes, hands off).** Needs the installed build of a `Windows package` run from
2026-10-23 or later (it has `--bench-render`; request 2 says how to download and install it) and the PC otherwise idle:
no game, browser or capture running, on mains power. The window opens and closes five times; do not touch it. All
results go into one file on the desktop.

```powershell
$exe = "$env:LOCALAPPDATA\Programs\Tzar2\Tzar2.console.exe"
$pkg = "$env:APPDATA\Godot\app_userdata\Tzar2\import\base.original.t2pkg"
$out = Join-Path ([Environment]::GetFolderPath("Desktop")) "tzar2-render-bench.txt"
if (-not (Test-Path $pkg)) {
  & $exe --headless -- --import "E:\Games\Tzar" --smoke 2>&1 | Select-String "FIRST-RUN" | ForEach-Object Line | Add-Content $out
}
foreach ($run in @(
    @("--", "--package", $pkg, "--bench-render", "--resolution", "2560x1440"),
    @("--", "--package", $pkg, "--bench-render", "--resolution", "2560x1440"),
    @("--", "--package", $pkg, "--bench-render", "--resolution", "1920x1080"),
    @("--", "--package", $pkg, "--bench-render", "--fullscreen"),
    @("--rendering-method", "gl_compatibility", "--", "--package", $pkg, "--bench-render", "--resolution", "2560x1440"))) {
  & $exe @run 2>&1 | Select-String "^RENDER-BENCH" | ForEach-Object Line | Add-Content $out
}
Get-Content $out
```

Send `tzar2-render-bench.txt` from the desktop (or paste its lines). The two 2560 × 1440 runs with the default renderer
give the S2 verdict (`S2=PASS` or `FAIL`); `--fullscreen` measures the monitor's own resolution; the last line is the
Compatibility (OpenGL) renderer. If the monitor is smaller than 2560 × 1440, the window comes out smaller, the line
says so in `res=` and `S2=n/a`.

**Minimum tier:** the owner or a beta tester with such a machine (an older laptop with Intel graphics is ideal) runs
the same three steps with the installed build. If nobody has one, a cloud Windows VM does not help (no real GPU); the
beta's crash reports then give the hardware spread (GPU and CPU are in every report).
