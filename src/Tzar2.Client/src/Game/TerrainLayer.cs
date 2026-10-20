using System;
using System.Collections.Generic;
using Godot;
using Tzar2.Client.Rendering;
using Tzar2.Content.Assets;
using Tzar2.Content.Terrain;

namespace Tzar2.Client.Game;

/// <summary>
/// The map's terrain as chunks of <see cref="ChunkTiles"/>² tiles (TDD §13.5), each composed losslessly in RGB555 by the
/// viewer's <see cref="TerrainComposer"/> (UNK-10 rules isolated there and in <c>TerrainPresentation</c>) and drawn by an
/// <see cref="Rgb555Surface"/>; chunks with water carry every frame of the water/shore cycle (<c>WATERFPS</c> 7).
/// Off-screen chunks are hidden. Also keeps an average colour per tile for the minimap.
/// </summary>
public sealed class TerrainLayer : IDisposable
{
    /// <summary>Chunk side in tiles.</summary>
    public const int ChunkTiles = 16;

    /// <summary>Water/shore animation rate (<c>WATERFPS</c> default, bmp.md §3.3).</summary>
    public const int WaterFps = 7;

    private readonly List<(Rgb555Surface Surface, Rect2I Pixels)> _chunks = [];
    private readonly TerrainComposer _composer;

    /// <summary>
    /// Composes and uploads the terrain of <paramref name="grid"/>. The chunks are composed in parallel (one composer per
    /// thread; <paramref name="load"/> must be thread-safe), then uploaded in order on the calling thread.
    /// </summary>
    public TerrainLayer(Rid parent, int drawIndex, TerrainSet set, TerrainGrid grid, Func<string, ImageAsset?> load)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(grid);
        _composer = new TerrainComposer(set, load);
        Width = grid.Width;
        Height = grid.Height;
        TileColours = new Color[Width * Height];
        var origins = new List<(int X, int Y)>();
        for (int cy = 0; cy < Height; cy += ChunkTiles)
        {
            for (int cx = 0; cx < Width; cx += ChunkTiles)
            {
                origins.Add((cx, cy));
                _chunks.Add((new Rgb555Surface(parent, drawIndex), new Rect2I(cx * TerrainComposer.TileSize, cy * TerrainComposer.TileSize, 0, 0)));
            }
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var composed = new (TerrainImage First, List<byte[]> Frames)[origins.Count];
        System.Threading.Tasks.Parallel.For(
            0,
            origins.Count,
            () => new TerrainComposer(set, load),
            (i, _, composer) =>
            {
                composed[i] = ComposeChunk(composer, grid, origins[i].X, origins[i].Y);
                return composer;
            },
            _ => { });
        ComposeMs = watch.ElapsedMilliseconds;
        for (int i = 0; i < origins.Count; i++)
        {
            Issues += Upload(i, composed[i].First, composed[i].Frames, origins[i].X, origins[i].Y);
        }
    }

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
                Compose((cy * perRow) + cx, grid, cx * ChunkTiles, cy * ChunkTiles);
            }
        }
    }

    private int Compose(int chunk, TerrainGrid grid, int cx, int cy)
    {
        (TerrainImage first, List<byte[]> frames) = ComposeChunk(_composer, grid, cx, cy);
        return Upload(chunk, first, frames, cx, cy);
    }

    // Every animation frame of the chunk whose top-left tile is (cx, cy); later frames redraw only the tiles that move.
    private (TerrainImage First, List<byte[]> Frames) ComposeChunk(TerrainComposer composer, TerrainGrid grid, int cx, int cy)
    {
        int w = Math.Min(ChunkTiles, Width - cx), h = Math.Min(ChunkTiles, Height - cy);
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

    private int Upload(int chunk, TerrainImage first, List<byte[]> frames, int cx, int cy)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        FrameTotal += frames.Count;
        Rgb555Surface surface = _chunks[chunk].Surface;
        var origin = new Vector2I(cx * TerrainComposer.TileSize, cy * TerrainComposer.TileSize);
        surface.Set(first.Width, first.Height, frames, origin, showKeys: false);
        _chunks[chunk] = (surface, new Rect2I(origin, first.Width, first.Height));
        Average(first, cx, cy, Math.Min(ChunkTiles, Width - cx), Math.Min(ChunkTiles, Height - cy));
        UploadMs += watch.ElapsedMilliseconds;
        return first.Issues.Count;
    }

    /// <summary>Time spent composing chunk pixels (diagnostics).</summary>
    public long ComposeMs { get; private set; }

    /// <summary>Time spent uploading chunk textures (diagnostics).</summary>
    public long UploadMs { get; private set; }

    /// <summary>Animation frames composed over all chunks (diagnostics).</summary>
    public int FrameTotal { get; private set; }

    /// <summary>Tiles across.</summary>
    public int Width { get; }

    /// <summary>Tiles down.</summary>
    public int Height { get; }

    /// <summary>Tiles drawn without a known rule (UNK-10).</summary>
    public int Issues { get; }

    /// <summary>Average colour per tile (minimap).</summary>
    public Color[] TileColours { get; }

    /// <summary>Applies the camera and the animation clock; hides chunks outside <paramref name="visible"/> (world pixels).</summary>
    public void Update(Transform2D view, Rect2I visible, long timeMs)
    {
        int frame = (int)(timeMs * WaterFps / 1000);
        foreach ((Rgb555Surface surface, Rect2I pixels) in _chunks)
        {
            bool show = pixels.Intersects(visible);
            surface.SetVisible(show);
            if (show)
            {
                surface.SetView(view);
                surface.SetFrame(frame);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach ((Rgb555Surface surface, _) in _chunks)
        {
            surface.Dispose();
        }

        _chunks.Clear();
    }

    private void Average(TerrainImage image, int cx, int cy, int w, int h)
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
                TileColours[((cy + ty) * Width) + cx + tx] = Color.Color8((byte)(r / n), (byte)(g / n), (byte)(b / n));
            }
        }
    }

    private static int Expand(int c5) => (c5 << 3) | (c5 >> 2);
}
