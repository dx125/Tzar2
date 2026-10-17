# Spike S1: instanced sprite renderer

Question (TDD §25 M0, DEC-10, §13.3, §13.4): can Godot 4.7 .NET draw 10,000+ moving, y-sorted, pixel-exact sprites from
a texture array, with per-instance player colour and shadows, at >= 60 FPS on the desktop reference? Which renderer
backend, sorting approach and texture layout should the client use? Result and decision:
[ADR 0010](../../docs/decisions/0010-spike-s1-instanced-renderer.md).

This is a standalone Godot project (not in `Tzar2.sln`, see `spikes/README.md`). All art is generated procedurally at
start-up (`src/SpriteSheet.cs`); no original assets are used.

## What it does

* `SpriteSheet`: 256 frames (8 kinds x 8 directions x 4 animation frames) of 48x48 px, RGB555-quantised RGBA8 colour
  plane + R8 player-colour mask plane (REQ-GFX-03 layout (a)), 8 placeholder player ramps x 8 shades, a ground tile.
  Exposed both as `Texture2DArray` (one frame per layer) and as a 16x16 atlas.
* `Entities`: SoA stand-in for a render snapshot; every sprite moves and animates every frame. `Sort()` is a stable
  2-pass LSD radix sort by snapped foot line (ties by entity id), allocation-free.
* `SpriteBatch2D` (**cpu-radix**): `RenderingServer` canvas items + `MultiMesh` (Transform2D + 4 floats custom data:
  frame, player, flags). The painter's order is the instance order, written every frame from the sorted index list.
  One draw call for objects, one for shadows, one for the ground.
* `SpriteBatch3D` (**depth**): the same quads as a 3D `MultiMesh` under an orthographic camera (1 unit = 1 logical px),
  z = foot line, depth test + alpha scissor, no CPU sort.
* Shader (`shaders/sprite_common.gdshaderinc`, variants built in `src/Shaders.cs`): nearest sampling, frame index ->
  array layer or atlas cell, mask -> player ramp via `texelFetch`, alpha test at 0.5.
* Shadows: the frame silhouette flattened and sheared, drawn as one darkening MultiMesh (`direct`), or (2D only)
  rendered as opaque white into a `SubViewport` mask and composited once, so overlaps do not double-darken (`mask`,
  the TDD §13.3 design).
* Integer world scale per REQ-GFX-07 (`max(1, floor(height / 600))`: 1 at 1080p, 2 at 1440p); positions are snapped to
  whole logical pixels before upload.

## Running

Requires Godot 4.7.2 .NET and the .NET SDK (`DOTNET_ROOT` must point to it so Godot finds `hostfxr`).

```sh
# macOS / Linux: full matrix (renderers x 1920x1080, 2560x1440 x {cpu,depth} x {array,atlas} x {1k,5k,10k,20k}) + verify
GODOT=/path/to/Godot spikes/S1-renderer/run-bench.sh
QUICK=1 GODOT=... spikes/S1-renderer/run-bench.sh                                  # smoke test
BENCH_ARGS="--modes=cpu:array --counts=10000,20000 --shadows=mask" GODOT=... spikes/S1-renderer/run-bench.sh
```

```powershell
# Windows (use the *_console.exe Godot build so stdout is visible)
pwsh spikes/S1-renderer/run-bench.ps1 -Godot C:\Godot\Godot_v4.7.2-stable_mono_win64_console.exe
```

Direct invocation (the user arguments come after `--`):

```sh
godot --path spikes/S1-renderer --resolution 2560x1440 --rendering-method mobile -- \
  --bench [--size=2560x1440] [--counts=1000,5000,10000,20000] [--modes=cpu:array,cpu:atlas,depth:array,depth:atlas] \
  [--shadows=direct|mask|off] [--warmup=1.5] [--seconds=4] [--scale=N] [--out=results.csv] [--screenshot=frame.png] [--shotcount=10000]
godot --path spikes/S1-renderer -- --verify [--scales=1,2,3] [--shotdir=DIR]   # exit code 1 on any mismatch
godot --path spikes/S1-renderer -- [--count=10000] [--sort=cpu|depth] [--tex=array|atlas]   # interactive, Esc quits
```

Output lines:

* `S1RESULT ... fps= ft_mean_ms= ft_p50_ms= ft_p99_ms= update_ms= sort_ms= fill_ms= upload_ms= sortupload_ms=
  sortupload_p99_ms= sorted_ok= gc_bytes_per_frame=` per configuration (also appended to the CSV). Frame time is wall
  time between frames with V-Sync off. `gpu_ms`/`render_cpu_ms` are Godot's viewport render-time measurements; they
  read 0 or are unreliable on some drivers (Metal) and are not used for decisions. `gc_bytes_per_frame` includes the
  harness's own `await` (~40 B); the renderer path itself does not allocate.
* `S1VERIFY ... mismatches= max_channel_diff= result=PASS|FAIL`: 80 overlapping sprites (unique foot lines, ids in an
  order unrelated to y) rendered without ground/shadows at integer scales 1-3 and compared with a CPU reference
  composited in painter's order, pixel by pixel. A wrong y-order shows up as large differences; colour-pipeline
  rounding as small ones.

Curated results are in `results/<machine>/`.
