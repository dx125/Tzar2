# 0010. Draw world sprites as canvas MultiMeshes in CPU radix-sorted painter's order from a Texture2DArray

* **Status:** Accepted for the rendering path, sorting and texture layout. The renderer **backend for Windows is
  provisional** until `run-bench.ps1` has been run on the Windows desktop reference (see Consequences).
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M0, spike S1)
* **Related:** DEC-07, DEC-10, REQ-GFX-01, REQ-GFX-03, REQ-GFX-04, REQ-GFX-06, REQ-GFX-07, TDD §13.3, §13.4, §21, §25 (M0
  S1), spike [`spikes/S1-renderer`](../../spikes/S1-renderer/README.md), raw data
  [`results/macos-arm64-m5`](../../spikes/S1-renderer/results/macos-arm64-m5/results.csv), S6 (sprite memory)

## Context

M0 spike S1 asks whether Godot 4.7.2 .NET can draw 10,000 moving, y-sorted, pixel-exact sprites from a texture array
with per-instance player colour and shadows (Windows >= 60 FPS on the desktop reference), and asks us to choose the
renderer backend, the sorting approach and the texture layout by measurement (§13.4).

**Prototype** (`spikes/S1-renderer`, standalone Godot project, procedurally generated placeholder art only):

* 256 frames of 48x48 px (8 kinds x 8 directions x 4 animation frames), RGB555-quantised RGBA8 colour plane + R8
  player-colour mask plane (REQ-GFX-03 layout (a)), 8 placeholder ramps x 8 shades, applied in the fragment shader
  through `texelFetch` (REQ-GFX-04 mechanism). All sprites move and animate every frame.
* **cpu-radix:** `RenderingServer` canvas items with one `MultiMesh` for objects (Transform2D + 4 floats custom data:
  frame, player, flags), one for shadows, one ground rect: 3 draw calls. Every frame: stable 2-pass LSD radix sort by
  snapped foot line (ties by entity id), buffer fill in sorted order, one `MultimeshSetBuffer(ReadOnlySpan<float>)`.
* **depth:** the same quads as a 3D `MultiMesh` under an orthographic camera, z = foot line, depth test + alpha
  scissor, no CPU sort.
* Texture layout **array** (`Texture2DArray`, one frame per layer) vs **atlas** (one 768x768 2D atlas, cell computed in
  the shader from the frame index).
* Shadows: the frame silhouette flattened/sheared as one darkening MultiMesh (**direct**), or (2D only) opaque
  silhouettes into a `SubViewport` mask composited once (**mask**, the §13.3 design; overlaps do not double-darken).
* World scale per REQ-GFX-07: integer `max(1, floor(h / 600))`, i.e. **x1 at 1920x1080 and x2 at 2560x1440**. All
  sprites are on screen, so 1440p (x2, 1280x720 logical) has 4x the fill per sprite and is the heavy case.
* `--verify` renders 80 overlapping sprites (unique foot lines, ids in an order unrelated to y) at scales x1/x2/x3 and
  compares the **whole frame pixel by pixel** with a CPU reference composited in painter's order.

**Machine:** MacBook, Apple M5 (CPU and GPU), macOS 26.5, built-in display, windowed, V-Sync off, Godot 4.7.2 .NET.
No Windows machine was available. Other agents were building on the same machine during the runs, so single numbers
carry some noise; trends were consistent across runs. Frame time = wall time between frames; FPS = 1000 / mean frame
time. Godot's viewport GPU timer reads 0 on Metal, so no separate GPU time is reported. Many light configurations hit
a ~500-650 FPS ceiling of the frame loop itself; only differences below that are meaningful.

### Results: FPS / p99 frame time (ms), shadows = direct

| Resolution (scale) | Backend | Sort | Tex | 1k | 5k | 10k | 20k |
|---|---|---|---|---|---|---|---|
| 1920x1080 (x1) | Compatibility / native GL 4.1 | cpu-radix | array | 667 / 3.5 | 244 / 6.4 | 168 / 9.4 | 98 / 13.0 |
| 1920x1080 (x1) | Compatibility / ANGLE on Metal | cpu-radix | array | 499 / 3.7 | 497 / 3.9 | 506 / 3.6 | 491 / 3.7 |
| 1920x1080 (x1) | Mobile / Metal | cpu-radix | array | 656 / 2.9 | 550 / 3.1 | 543 / 3.3 | 473 / 3.1 |
| 1920x1080 (x1) | Forward+ / Metal | cpu-radix | array | 612 / 2.9 | 579 / 3.4 | 565 / 3.3 | 484 / 3.1 |
| 1920x1080 (x1) | Mobile / Metal | cpu-radix | atlas | 562 / 3.3 | 539 / 3.5 | 531 / 3.3 | 355 / 6.0 |
| 1920x1080 (x1) | Mobile / Metal | depth | array | 594 / 3.0 | 558 / 3.4 | 544 / 3.4 | 519 / 3.2 |
| 2560x1440 (x2) | Compatibility / native GL 4.1 | cpu-radix | array | 550 / 4.7 | 237 / 6.5 | **133 / 10.6** | 83 / 14.6 |
| 2560x1440 (x2) | Compatibility / native GL 4.1 | cpu-radix | atlas | 530 / 4.7 | 233 / 6.7 | 122 / 11.7 | 76 / 16.0 |
| 2560x1440 (x2) | Compatibility / native GL 4.1 | depth | array | 566 / 4.2 | 426 / 4.7 | 310 / 4.7 | 197 / 5.9 |
| 2560x1440 (x2) | Compatibility / ANGLE on Metal | cpu-radix | array | 532 / 3.4 | 492 / 3.5 | 364 / 7.5 | 191 / 11.1 |
| 2560x1440 (x2) | Compatibility / ANGLE on Metal | cpu-radix | atlas | 484 / 3.3 | 393 / 14.1 | 223 / 16.9 | 132 / 15.5 |
| 2560x1440 (x2) | Compatibility / ANGLE on Metal | depth | array | 542 / 3.3 | 528 / 3.3 | 522 / 3.4 | 441 / 4.0 |
| 2560x1440 (x2) | Mobile / Metal | cpu-radix | array | 538 / 3.3 | 513 / 3.3 | **322 / 8.2** | 177 / 6.6 |
| 2560x1440 (x2) | Mobile / Metal | cpu-radix | atlas | 530 / 3.3 | 437 / 3.3 | 231 / 6.9 | 123 / 9.5 |
| 2560x1440 (x2) | Mobile / Metal | depth | array | 518 / 3.9 | 541 / 3.9 | 476 / 3.2 | 299 / 8.4 |
| 2560x1440 (x2) | Mobile / Metal | depth | atlas | 511 / 6.1 | 487 / 4.0 | 371 / 4.5 | 242 / 8.0 |
| 2560x1440 (x2) | Forward+ / Metal | cpu-radix | array | 624 / 4.5 | 500 / 3.5 | **335 / 8.2** | 181 / 7.0 |
| 2560x1440 (x2) | Forward+ / Metal | cpu-radix | atlas | 560 / 6.3 | 407 / 7.5 | 224 / 8.1 | 123 / 9.5 |
| 2560x1440 (x2) | Forward+ / Metal | depth | array | 531 / 3.5 | 547 / 3.3 | 446 / 4.1 | 301 / 8.1 |

The full matrix (every backend x sort x layout x resolution) is in the CSV.

### Results: CPU time of the per-frame steps (Forward+/Metal, cpu-radix, array; mean ms, step p99 in brackets)

| Sprites | update (sim stand-in) | radix sort | buffer fill | upload | **sort + fill + upload** |
|---|---|---|---|---|---|
| 1k | 0.02 | 0.02 | 0.02 | 0.004 | **0.04** (0.05) |
| 5k | 0.12 | 0.08 | 0.09 | 0.02 | **0.19** (0.23) |
| 10k | 0.24 | 0.17 | 0.18 | 0.04 | **0.39** (0.46) |
| 20k | 0.53 | 0.38 | 0.41 | 0.07 | **0.85** (0.99) |

The depth path saves only the sort (fill + upload 0.25 ms at 10k, 0.64 ms at 20k). Upload costs 3-4x more on
Compatibility (0.13 ms at 10k), still small. The renderer path allocates nothing per frame (the 40 B/frame reported is the
benchmark's own `await`). The sort order was checked every 60 frames in every run (`sorted_ok=True`).

### Results: shadows (2560x1440 x2, cpu-radix, array; FPS at 10k / 20k)

| Backend | off | direct | mask (SubViewport, composited once) |
|---|---|---|---|
| Compatibility / native GL | 239 / 141 | 133 / 83 | 140 / 86 |
| Compatibility / ANGLE | 477 / 269 | 364 / 191 | 324 / 180 |
| Mobile / Metal | 487 / 262 | 322 / 177 | 333 / 178 |
| Forward+ / Metal | 484 / 261 | 335 / 181 | 335 / 178 |

### Results: pixel exactness and y-order (`--verify`, 1280x720, scales x1, x2, x3)

| Backend | cpu-radix, array and atlas | depth, array and atlas |
|---|---|---|
| Compatibility / native GL | **0 mismatching pixels** | 11-110k pixels off by <= 7 per channel |
| Compatibility / ANGLE | **0** | 11-106k pixels off by <= 7 |
| Mobile / Metal | **0** | 10-97k pixels off by <= 2 |
| Forward+ / Metal | **0** | **0** |

The depth path has the right order everywhere (an order error would produce large differences; the counts scale
exactly with k², i.e. per texel), but its colours are not exact on Mobile and Compatibility: Godot's 3D pipeline goes
through a linear intermediate buffer (RGB10A2 on Mobile, RGBA8 on Compatibility), so the sRGB round trip loses
precision. Only Forward+ (RGBA16F) is exact. The 2D canvas path never leaves sRGB and is exact on every backend.

Screenshots (bench frame at 10k, verify frames) are produced by the scripts; they were inspected: correct overlap
order, hard 1:1 / 2:2 pixel blocks, dither visible, shadows under the sprites, mask shadows without double darkening.

## Decision

1. **Rendering path:** world sprites are drawn through `RenderingServer` canvas items holding `MultiMesh`es (2D),
   one MultiMesh per draw layer (§13.3 layer list), with per-instance custom data (frame/layer, player, flags). No Node
   per entity (DEC-10). Positions are snapped to whole logical pixels on the CPU before upload, the integer world
   scale is a canvas transform, sampling is nearest everywhere (REQ-GFX-01, REQ-GFX-07).
2. **Sorting:** painter's order is established on the CPU every frame with a stable LSD radix sort of the key
   `(layer, footY, subOrder, entityId)` (as specified in §13.3), and the instance buffer is written in that order. The
   depth-buffer approach is **rejected**: it is not pixel-exact on Mobile and Compatibility, it cannot express
   translucency (translucent trees, silhouettes, hit flashes) or the exact tie order, and its benefit (no sort,
   ~0.2-0.4 ms CPU, fewer shaded fragments under heavy overdraw) is not needed to reach the budget.
3. **Texture layout:** sprite frames are sampled from a **`Texture2DArray`** with the layer chosen per instance. In
   every fill-bound measurement the array was equal to or faster than a single 2D atlas (at 1440p, 10k-20k sprites:
   +39-50 % FPS on Metal, +9-63 % on Compatibility). The production layout stays as §13.3 says (frames packed into
   atlas pages that are layers of a `Texture2DArray`, instance -> layer + UV rect + pivot); S6 must re-measure the
   page-packing vs. one-frame-per-layer trade-off with the real frame sizes and memory volume.
4. **Shadows:** implement the §13.3 shadow pass as designed (silhouettes into a single-channel mask render target,
   composited once): in the spike it costs about the same as drawing shadows directly (within +-5 % on Metal and native GL, 6-11 % slower on ANGLE) and avoids double
   darkening (UNK-03 still decides the exact original blend).
5. **Backend (provisional for Windows):** target Godot's **Mobile** renderer (RenderingDevice: D3D12/Vulkan on
   Windows, Metal on macOS) as the default for the game client, with **Compatibility** as the fallback for GPUs
   without a usable RenderingDevice driver. Forward+ brings nothing for this 2D workload (same FPS as Mobile, heavier
   start-up and features we do not use). On macOS (PX) use Mobile/Metal; native OpenGL there is 2x slower than Metal
   and deprecated by Apple. This choice is confirmed or changed by the Windows run (Consequences).
6. The spike's benchmark (`spikes/S1-renderer/run-bench.sh` / `run-bench.ps1`, `--bench`/`--verify`) is the
   reference harness for re-measuring; its `--verify` pixel comparison is the seed of the golden-image tests (§13.9).

## Alternatives considered

* **Depth-sorted 3D MultiMesh (measured):** 1.7-2.4x the FPS in the most fill-bound cases (20k at 1440p:
  300 vs 180 FPS on Metal, 197 vs 83 on native GL), but not pixel-exact on Mobile/Compatibility (max +-2 / +-7 per
  channel), no translucency, ties resolved by draw order instead of entity id. Could be revisited only as an
  opt-in "performance" path on Forward+, which is exact.
* **Single 2D atlas (measured):** slower than the array under overdraw; would also need padding/bleeding rules.
* **One Node (`Sprite2D`) per entity / Godot `y_sort_enabled`:** excluded by DEC-10; not measured.
* **Forward+ as default:** equal performance here, no benefit for 2D, larger shader/start-up cost.

## Consequences

* **The M0 exit criterion "Windows >= 60 FPS on the desktop reference" is NOT yet demonstrated.** All numbers are from
  an Apple M5 laptop. Required follow-up before the M0 sign-off of S1: run
  `pwsh spikes/S1-renderer/run-bench.ps1 -Godot <Godot 4.7.2 mono console exe>` on the Windows reference
  (6-core 2020 CPU, GTX 1060-class GPU) at 1920x1080 and 2560x1440, commit `results/windows-<gpu>/`, and update this
  ADR's backend decision with the Windows data (Mobile/D3D12 vs Mobile/Vulkan vs Compatibility). On the M5 the worst
  backend (native GL) still ran 10k sprites with shadows at 133 FPS (p99 10.6 ms) at 1440p x2 and 20k at 83 FPS, and
  the RenderingDevice backends ran 10k at 322-335 FPS, so the margin is large, but a GTX 1060 has different fill and
  driver characteristics, so this is evidence, not proof.
* CPU budget: sort + fill + upload is 0.39 ms at 10k and 0.85 ms at 20k on the M5 (p99 < 1 ms on Metal); the Windows
  run reports the same columns. Render snapshots (REQ-ARCH-03) should be SoA so the fill loop stays linear.
* The client needs a sprite-batch module that owns one MultiMesh per draw layer and grows capacity in chunks
  (`MultimeshAllocateData` per size change), and sets a custom AABB (avoids per-upload bounds computation).
* The 3D/depth path and the atlas variant stay in the spike only; they are not carried into the client.
* Revisit if: the Windows run misses 60 FPS at 10k on any planned backend; S6 shows that frames must be packed so
  tightly that per-frame layers are impossible; or translucency/silhouette passes (ORIG-17) blow the fill budget.
* Open measurement gaps: no GPU timer on Metal (frame time only); no Vulkan-on-macOS (MoltenVK) or Linux numbers;
  sprite cell fixed at 48x48 (real frames vary, S6); terrain chunks, fog and light passes not included.
