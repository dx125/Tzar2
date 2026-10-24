using System;
using System.Collections.Generic;
using Godot;
using Tzar2.Content.Assets;
using Tzar2.Content.Terrain;
using RS = Godot.RenderingServer;

namespace Tzar2.Client.Game;

/// <summary>
/// The map's terrain as chunks of <see cref="ChunkTiles"/>² tiles (TDD §13.5), each composed losslessly in RGB555 by the
/// viewer's <see cref="TerrainComposer"/> (UNK-10 rules isolated there and in <c>TerrainPresentation</c>). The composed
/// tiles of every chunk and animation frame (chunks with water carry every frame of the water/shore cycle,
/// <c>WATERFPS</c> 7) go into one deduplicated <see cref="TerrainTileAtlas"/>; a chunk keeps a slot per tile and frame
/// and is drawn by <c>shaders/terrain_tiles.gdshader</c> (ADR 0210). Off-screen chunks are hidden. Also keeps an average
/// colour per tile for the minimap.
/// </summary>
public sealed class TerrainLayer : IDisposable
{
    /// <summary>Chunk side in tiles.</summary>
    public const int ChunkTiles = 16;

    /// <summary>Water/shore animation rate (<c>WATERFPS</c> default, bmp.md §3.3).</summary>
    public const int WaterFps = 7;

    private readonly List<(Chunk Chunk, Rect2I Pixels)> _chunks = [];
    private readonly TerrainComposer _composer;
    private readonly TerrainTileAtlas _atlas;
    private Texture2DArray? _atlasTexture;

    /// <summary>
    /// Composes and uploads the terrain of <paramref name="grid"/> (<see cref="Compose"/>, then the textures, on the calling
    /// thread).
    /// </summary>
    public TerrainLayer(Rid parent, int drawIndex, TerrainSet set, TerrainGrid grid, Func<string, ImageAsset?> load)
        : this(parent, drawIndex, Compose(set, grid, load))
    {
    }

    /// <summary>Uploads terrain composed by <see cref="Compose"/> (main thread).</summary>
    public TerrainLayer(Rid parent, int drawIndex, ComposedTerrain composed)
    {
        ArgumentNullException.ThrowIfNull(composed);
        _composer = new TerrainComposer(composed.Set, composed.Load);
        _atlas = composed.Atlas;
        Width = composed.Width;
        Height = composed.Height;
        TileColours = composed.TileColours;
        ComposeMs = composed.ComposeMs;
        IndexMs = composed.IndexMs;
        FrameTotal = composed.FrameTotal;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < composed.Chunks.Length; i++)
        {
            (int cx, int cy, int width, int height, int[][] slots, int issues) = composed.Chunks[i];
            _chunks.Add((new Chunk(parent, drawIndex), new Rect2I(cx * TerrainComposer.TileSize, cy * TerrainComposer.TileSize, 0, 0)));
            Apply(i, width, height, slots, cx, cy);
            Issues += issues;
        }

        UploadAtlas();
        UploadMs = watch.ElapsedMilliseconds;
    }

    /// <summary>
    /// Composes the terrain of <paramref name="grid"/> into a tile atlas and the chunks' slots: CPU only, so it may run
    /// off the main thread (match load, code review 2026-10). The chunks are composed in parallel (one composer per
    /// thread; <paramref name="load"/> must be thread-safe); each chunk's frames go into the atlas as soon as they are
    /// composed, so only the chunks in flight hold their frames. Slots depend on the order chunks arrive, the pixels
    /// drawn do not.
    /// </summary>
    public static ComposedTerrain Compose(TerrainSet set, TerrainGrid grid, Func<string, ImageAsset?> load)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(load);
        int width = grid.Width, height = grid.Height;
        var colours = new Color[width * height];
        var origins = new List<(int X, int Y)>();
        for (int cy = 0; cy < height; cy += ChunkTiles)
        {
            for (int cx = 0; cx < width; cx += ChunkTiles)
            {
                origins.Add((cx, cy));
            }
        }

        var atlas = new TerrainTileAtlas();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var chunks = new (int X, int Y, int Width, int Height, int[][] Slots, int Issues)[origins.Count];
        int frameTotal = 0;
        long indexTicks = 0;
        System.Threading.Tasks.Parallel.For(
            0,
            origins.Count,
            () => new TerrainComposer(set, load),
            (i, _, composer) =>
            {
                (int cx, int cy) = origins[i];
                (TerrainImage first, List<byte[]> frames) = ComposeChunk(composer, grid, width, height, cx, cy);
                Average(first, colours, width, cx, cy, Math.Min(ChunkTiles, width - cx), Math.Min(ChunkTiles, height - cy));
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                int[][] slots = atlas.Add(first.Width, first.Height, frames);
                System.Threading.Interlocked.Add(ref indexTicks, System.Diagnostics.Stopwatch.GetTimestamp() - start);
                System.Threading.Interlocked.Add(ref frameTotal, frames.Count);
                chunks[i] = (cx, cy, first.Width, first.Height, slots, first.Issues.Count);
                return composer;
            },
            _ => { });
        return new ComposedTerrain(set, load, width, height, atlas, chunks, colours)
        {
            ComposeMs = watch.ElapsedMilliseconds,
            IndexMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(0, indexTicks).TotalMilliseconds,
            FrameTotal = frameTotal,
        };
    }

    /// <summary>Time spent composing chunk pixels (diagnostics).</summary>
    public long ComposeMs { get; private set; }

    /// <summary>Time spent looking chunk tiles up in the atlas, summed over the threads (diagnostics).</summary>
    public long IndexMs { get; private set; }

    /// <summary>Time spent uploading textures (diagnostics).</summary>
    public long UploadMs { get; private set; }

    /// <summary>Animation frames composed over all chunks (diagnostics).</summary>
    public int FrameTotal { get; private set; }

    /// <summary>Distinct tiles in the atlas (diagnostics).</summary>
    public int AtlasTiles => _atlas.Count;

    /// <summary>Atlas pages (diagnostics: <see cref="TerrainTileAtlas.PageSide"/>² × 2 bytes each in video memory).</summary>
    public int AtlasPages => _atlas.Pages.Count;

    /// <summary>Tiles across.</summary>
    public int Width { get; }

    /// <summary>Tiles down.</summary>
    public int Height { get; }

    /// <summary>Tiles drawn without a known rule (UNK-10).</summary>
    public int Issues { get; }

    /// <summary>Average colour per tile (minimap).</summary>
    public Color[] TileColours { get; }

    /// <summary>
    /// Recomposes the chunks that overlap tiles (<paramref name="left"/>, <paramref name="top"/>)–(<paramref name="right"/>,
    /// <paramref name="bottom"/>) from <paramref name="grid"/> (the editor's terrain painting; same size as the original grid).
    /// </summary>
    public void Recompose(TerrainGrid grid, int left, int top, int right, int bottom)
    {
        ArgumentNullException.ThrowIfNull(grid);
        int perRow = (Width + ChunkTiles - 1) / ChunkTiles;
        for (int cy = Math.Max(0, (top - 1) / ChunkTiles); cy <= Math.Min((Height - 1) / ChunkTiles, (bottom + 1) / ChunkTiles); cy++)
        {
            for (int cx = Math.Max(0, (left - 1) / ChunkTiles); cx <= Math.Min((Width - 1) / ChunkTiles, (right + 1) / ChunkTiles); cx++)
            {
                (TerrainImage first, List<byte[]> frames) = ComposeChunk(_composer, grid, Width, Height, cx * ChunkTiles, cy * ChunkTiles);
                Store((cy * perRow) + cx, first, frames, cx * ChunkTiles, cy * ChunkTiles);
            }
        }

        UploadAtlas();
    }

    /// <summary>Applies the camera and the animation clock; hides chunks outside <paramref name="visible"/> (world pixels).</summary>
    public void Update(Transform2D view, Rect2I visible, long timeMs)
    {
        int frame = (int)(timeMs * WaterFps / 1000);
        foreach ((Chunk chunk, Rect2I pixels) in _chunks)
        {
            bool show = pixels.Intersects(visible);
            chunk.SetVisible(show);
            if (show)
            {
                chunk.SetView(view);
                chunk.SetFrame(frame);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach ((Chunk chunk, _) in _chunks)
        {
            chunk.Dispose();
        }

        _chunks.Clear();
        _atlasTexture?.Dispose();
        _atlasTexture = null;
    }

    // Every animation frame of the chunk whose top-left tile is (cx, cy); later frames redraw only the tiles that move.
    private static (TerrainImage First, List<byte[]> Frames) ComposeChunk(TerrainComposer composer, TerrainGrid grid, int width, int height, int cx, int cy)
    {
        int w = Math.Min(ChunkTiles, width - cx), h = Math.Min(ChunkTiles, height - cy);
        var sub = new TerrainGrid(w, h, 0);
        for (int y = 0; y <= h; y++)
        {
            for (int x = 0; x <= w; x++)
            {
                sub[x, y] = grid[cx + x, cy + y];
            }
        }

        int frames = composer.FrameCount(sub);
        var images = new List<byte[]>(frames);
        TerrainImage first = composer.Compose(sub, 0, cx, cy);
        images.Add(first.Pixels);
        for (int f = 1; f < frames; f++)
        {
            images.Add(composer.Compose(sub, f, cx, cy, first).Pixels);
        }

        return (first, images);
    }

    // The chunk's tiles into the atlas, its slots to the GPU (the editor's recomposition).
    private void Store(int chunk, TerrainImage first, List<byte[]> frames, int cx, int cy)
    {
        int[][] slots = _atlas.Add(first.Width, first.Height, frames);

        Apply(chunk, first.Width, first.Height, slots, cx, cy);
        Average(first, TileColours, Width, cx, cy, Math.Min(ChunkTiles, Width - cx), Math.Min(ChunkTiles, Height - cy));
    }

    // The chunk's slots to the GPU.
    private void Apply(int chunk, int width, int height, int[][] slots, int cx, int cy)
    {
        Chunk surface = _chunks[chunk].Chunk;
        var origin = new Vector2I(cx * TerrainComposer.TileSize, cy * TerrainComposer.TileSize);
        surface.Set(width, height, slots, origin);
        _chunks[chunk] = (surface, new Rect2I(origin, width, height));
    }

    // Uploads the atlas pages that changed (all of them, in a new texture, when a page was added).
    private void UploadAtlas()
    {
        int[] dirty = _atlas.TakeDirtyPages();
        if (_atlasTexture is null || _atlasTexture.GetLayers() != _atlas.Pages.Count)
        {
            var images = new Godot.Collections.Array<Image>();
            foreach (byte[] page in _atlas.Pages)
            {
                images.Add(Image.CreateFromData(TerrainTileAtlas.PageSide, TerrainTileAtlas.PageSide, false, Image.Format.Rg8, page));
            }

            var texture = new Texture2DArray();
            Error error = texture.CreateFromImages(images);
            foreach (Image image in images)
            {
                image.Dispose();
            }

            if (error != Error.Ok)
            {
                throw new InvalidOperationException($"Terrain atlas upload failed: {error}.");
            }

            foreach ((Chunk chunk, _) in _chunks)
            {
                chunk.SetAtlas(texture);
            }

            _atlasTexture?.Dispose();
            _atlasTexture = texture;
        }
        else
        {
            foreach (int page in dirty)
            {
                using Image image = Image.CreateFromData(TerrainTileAtlas.PageSide, TerrainTileAtlas.PageSide, false, Image.Format.Rg8, _atlas.Pages[page]);
                _atlasTexture.UpdateLayer(image, page);
            }
        }
    }

    private static void Average(TerrainImage image, Color[] colours, int mapWidth, int cx, int cy, int w, int h)
    {
        int t = TerrainComposer.TileSize;
        for (int ty = 0; ty < h; ty++)
        {
            for (int tx = 0; tx < w; tx++)
            {
                long r = 0, g = 0, b = 0;
                for (int py = 0; py < t; py += 4)
                {
                    for (int px = 0; px < t; px += 4)
                    {
                        int o = 2 * ((((ty * t) + py) * image.Width) + (tx * t) + px);
                        int word = image.Pixels[o] | (image.Pixels[o + 1] << 8);
                        r += Expand((word >> 10) & 31);
                        g += Expand((word >> 5) & 31);
                        b += Expand(word & 31);
                    }
                }

                const int n = 64;
                colours[((cy + ty) * mapWidth) + cx + tx] = Color.Color8((byte)(r / n), (byte)(g / n), (byte)(b / n));
            }
        }
    }

    private static int Expand(int c5) => (c5 << 3) | (c5 >> 2);

    /// <summary>Terrain composed off the main thread, ready for <see cref="TerrainLayer(Rid, int, ComposedTerrain)"/>.</summary>
    public sealed class ComposedTerrain
    {
        internal ComposedTerrain(TerrainSet set, Func<string, ImageAsset?> load, int width, int height, TerrainTileAtlas atlas, (int X, int Y, int Width, int Height, int[][] Slots, int Issues)[] chunks, Color[] tileColours)
        {
            Set = set;
            Load = load;
            Width = width;
            Height = height;
            Atlas = atlas;
            Chunks = chunks;
            TileColours = tileColours;
        }

        internal TerrainSet Set { get; }

        internal Func<string, ImageAsset?> Load { get; }

        internal int Width { get; }

        internal int Height { get; }

        internal TerrainTileAtlas Atlas { get; }

        internal (int X, int Y, int Width, int Height, int[][] Slots, int Issues)[] Chunks { get; }

        internal Color[] TileColours { get; }

        internal long ComposeMs { get; init; }

        internal long IndexMs { get; init; }

        internal int FrameTotal { get; init; }
    }

    // One chunk: a canvas item drawn from the atlas through the chunk's slots (one RGBA8 layer of 24-bit slots per frame).
    private sealed class Chunk : IDisposable
    {
        // Shader parameter names made once: a string argument would make a new StringName on every call.
        private static readonly StringName TilesParam = "tiles";
        private static readonly StringName SizeParam = "size";
        private static readonly StringName FrameParam = "frame";
        private static readonly StringName AtlasParam = "atlas";

        private static ImageTexture? s_white;
        private readonly Rid _item;
        private readonly ShaderMaterial _material;
        private Texture2DArray? _tiles;
        private int _frameCount = 1;
        private int _frame = -1;

        public Chunk(Rid parent, int drawIndex)
        {
            _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/terrain_tiles.gdshader") };
            _item = RS.CanvasItemCreate();
            RS.CanvasItemSetParent(_item, parent);
            RS.CanvasItemSetDrawIndex(_item, drawIndex);
            RS.CanvasItemSetMaterial(_item, _material.GetRid());
            RS.CanvasItemSetDefaultTextureFilter(_item, RS.CanvasItemTextureFilter.Nearest);
        }

        public void Set(int width, int height, int[][] slots, Vector2I position)
        {
            int across = width / TerrainTileAtlas.TileSize, down = height / TerrainTileAtlas.TileSize;
            var images = new Godot.Collections.Array<Image>();
            byte[] bytes = new byte[across * down * 4];
            foreach (int[] frame in slots)
            {
                for (int i = 0; i < frame.Length; i++)
                {
                    bytes[4 * i] = (byte)frame[i];
                    bytes[(4 * i) + 1] = (byte)(frame[i] >> 8);
                    bytes[(4 * i) + 2] = (byte)(frame[i] >> 16);
                }

                images.Add(Image.CreateFromData(across, down, false, Image.Format.Rgba8, bytes));
            }

            var tiles = new Texture2DArray();
            Error error = tiles.CreateFromImages(images);
            foreach (Image image in images)
            {
                image.Dispose();
            }

            if (error != Error.Ok)
            {
                throw new InvalidOperationException($"Terrain chunk upload failed: {error}.");
            }

            _tiles?.Dispose();
            _tiles = tiles;
            _frameCount = slots.Length;
            _material.SetShaderParameter(TilesParam, _tiles);
            _material.SetShaderParameter(SizeParam, new Vector2(width, height));
            _material.SetShaderParameter(FrameParam, 0);
            _frame = 0;
            if (s_white is null)
            {
                using Image white = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
                s_white = ImageTexture.CreateFromImage(white);
            }

            RS.CanvasItemClear(_item);
            RS.CanvasItemAddTextureRect(_item, new Rect2(position, new Vector2(width, height)), s_white.GetRid());
        }

        public void SetAtlas(Texture2DArray atlas) => _material.SetShaderParameter(AtlasParam, atlas);

        // Called for every visible chunk on every drawn frame; the water's frame changes a few times a second, so the
        // parameter is only sent when it does (the code review: per-frame calls made a StringName and a native call each).
        public void SetFrame(int frame)
        {
            int f = ((frame % _frameCount) + _frameCount) % _frameCount;
            if (f != _frame)
            {
                _frame = f;
                _material.SetShaderParameter(FrameParam, f);
            }
        }

        public void SetView(Transform2D view) => RS.CanvasItemSetTransform(_item, view);

        public void SetVisible(bool value) => RS.CanvasItemSetVisible(_item, value);

        public void Dispose()
        {
            _tiles?.Dispose();
            _tiles = null;
            RS.FreeRid(_item);
            _material.Dispose();
        }
    }
}
