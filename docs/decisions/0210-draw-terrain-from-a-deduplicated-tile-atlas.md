# 0210. Draw terrain from a deduplicated tile atlas

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), code review of work queue item 16
* **Related:** TDD §13.5 (terrain chunks), §21 (resources); ADR 0013 (RGB555 surfaces); UNK-10;
  [code review 2026-10](../reviews/2026-10-code-review.md)

## Context

The client composed the terrain in chunks of 16 × 16 tiles (512 × 512 px) and uploaded every animation frame of every
chunk as one RG8 layer of a `Texture2DArray`: 512 KB per chunk and frame. A chunk with water carries the least common
multiple of water's 5 frames and its shores' frame counts, so the frames add up fast.

What it cost, measured with `--resource-soak` (4 kingdoms, a huge 256² continental map, seed 7, Apple M5):

* 2,204 chunk frames, so **1.2 GB of texture memory and 1.3 GB of video memory** in the match;
* every frame held at once as managed `byte[]` while loading: a **1.37 GB managed heap** when the match starts;
* a Godot `Image` per frame, never disposed: **1.15 GB of Godot static memory** at load, a working set of 2.3 GB.

Video memory grows with the map's area: a real-world 512² map has 5,352 chunk frames, which would need 2.7 GB of video
memory for its terrain alone. That is far beyond the integrated GPUs of the M7 min-spec.

The terrain repeats a lot. Base textures tile, transitions come in a few shapes and variants, and a tile's frames
repeat its own short cycle within the chunk's long one. On the huge map, 564,224 tile frames hold only 42,584 distinct
tiles (7.5 %).

## Decision

* `Tzar2.Content.Terrain.TerrainTileAtlas` (engine-free, tested) stores every distinct 32 × 32 composed tile once.
  * Its pages are 2048² RGB5A1, 4,096 tiles each.
  * `Add` returns, per frame of a window, the slot of each of its tiles.
  * Slots are 24-bit. An atlas holds at most 256 pages (1,048,576 tiles), the smallest array-texture layer limit
    renderers guarantee.
  * `Add` may run on several threads at once: it hashes outside its lock and compares the bytes on a hash hit.
* `TerrainLayer` composes the chunks in parallel exactly as before, with the same composer and the same pixels.
  * Each chunk's frames go into the atlas as soon as they are composed, then they are dropped.
  * A chunk keeps one RGBA8 layer of slots (16 × 16 texels) per frame.
  * Every `Image` made for an upload is disposed right after it.
* `shaders/terrain_tiles.gdshader` draws a chunk. It looks up the slot of the pixel's tile and frame, then the pixel in
  the atlas, and expands it as `rgb555.gdshader` does.
* The editor's terrain painting recomposes the touched chunks. Their new tiles join the atlas, and only its changed
  pages are uploaded again (all of them when a page was added).

The pixels drawn are unchanged:

* the test rebuilds every frame of composed windows from the atlas, byte for byte;
* the autoplay's first screenshot is identical before and after;
* later screenshots differ only where units move and water animates, both of which depend on wall-clock timing.

## Alternatives considered

* **Fewer frames per chunk:** draw water and the shores as separate layers, each with its own cycle. This keeps one
  full texture per chunk and layer, so the memory still grows with the map's area, and it changes how the shores
  blend.
* **Compressed textures (BCn/ETC):** lossy, which breaks the lossless RGB555 path (ADR 0013). They are also not
  available on every renderer.
* **Compose tiles per (terrain pair, shape, variant, frame) instead of per chunk:** the tiles show base textures at
  map-position-dependent offsets, so the composer would need a second, tile-level model. Deduplicating its output
  gets the same memory and keeps the composer as it is.

## Consequences

* On the huge map, video memory falls from 1.3 GB to 0.28 GB, texture memory from 1.2 GB to 0.19 GB, the managed heap
  at match start from 1.37 GB to 0.38 GB, and Godot's static memory at load from 1.15 GB to 0.14 GB.
* On the real-world 512² map: 67,220 distinct tiles on 17 pages, and 332 MB of video memory.
* Composing is still the bulk of the load time: 2–4 s on the huge map and 12 s on the real-world map, on the loaded
  review machine. The load still runs on the main thread. That is a separate finding of the review.
* A map with more than 1,048,576 distinct tiles (256 pages) cannot be drawn; the largest map has about 67 k.
* Revisit when the terrain becomes editable per tile beyond the composer's rules, or when a chunk's own texture would
  be cheaper again (a renderer with compressed lossless formats).
