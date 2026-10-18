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

    /// <summary>Composes and uploads the terrain of <paramref name="grid"/>.</summary>
    public TerrainLayer(Rid parent, int drawIndex, TerrainSet set, TerrainGrid grid, Func<string, ImageAsset?> load)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(grid);
        var composer = new TerrainComposer(set, load);
        Width = grid.Width;
        Height = grid.Height;
        TileColours = new Color[Width * Height];
        for (int cy = 0; cy < Height; cy += ChunkTiles)
        {
            for (int cx = 0; cx < Width; cx += ChunkTiles)
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
                    images.Add(composer.Compose(sub, f, cx, cy).Pixels);
                }

                Issues += first.Issues.Count;
                var surface = new Rgb555Surface(parent, drawIndex);
                var origin = new Vector2I(cx * TerrainComposer.TileSize, cy * TerrainComposer.TileSize);
                surface.Set(first.Width, first.Height, images, origin, showKeys: false);
                _chunks.Add((surface, new Rect2I(origin, first.Width, first.Height)));
                Average(first, cx, cy, w, h);
            }
        }
    }

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
