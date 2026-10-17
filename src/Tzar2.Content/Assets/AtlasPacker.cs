using System;
using System.Collections.Generic;

namespace Tzar2.Content.Assets;

/// <summary>Where a rectangle was placed: texture-array layer and top-left corner.</summary>
/// <param name="Layer">Page (array layer) index.</param>
/// <param name="X">Left.</param>
/// <param name="Y">Top.</param>
public readonly record struct AtlasPlacement(int Layer, int X, int Y);

/// <summary>Result of <see cref="AtlasPacker.Pack"/>.</summary>
/// <param name="Placements">One placement per input rectangle, in input order (empty rectangles get layer −1).</param>
/// <param name="Layers">Number of pages used.</param>
/// <param name="UsedPixels">Sum of the input areas.</param>
public sealed record AtlasPackResult(IReadOnlyList<AtlasPlacement> Placements, int Layers, long UsedPixels)
{
    /// <summary>Fraction of the allocated page area covered by rectangles, in per mille (integer).</summary>
    public int OccupancyPerMille(int pageSize) => Layers == 0 ? 0 : (int)(UsedPixels * 1000 / ((long)Layers * pageSize * pageSize));
}

/// <summary>
/// Deterministic shelf packer for trimmed sprite cells into square <c>Texture2DArray</c> pages (TDD §13.3,
/// ADR 0013). Rectangles are sorted by height, then width (descending), then input index, and placed on shelves left
/// to right; a new shelf opens below, a new page when the page is full. Simple and stable; within a few per cent of
/// skyline packers for sprite-cell size distributions.
/// </summary>
public static class AtlasPacker
{
    /// <summary>Packs <paramref name="sizes"/> into pages of <paramref name="pageSize"/>² with <paramref name="padding"/> pixels between rectangles.</summary>
    /// <exception cref="ArgumentException">A rectangle does not fit on an empty page.</exception>
    public static AtlasPackResult Pack(IReadOnlyList<(int Width, int Height)> sizes, int pageSize, int padding = 0)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(padding);
        int[] order = new int[sizes.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            int c = sizes[b].Height.CompareTo(sizes[a].Height);
            if (c == 0)
            {
                c = sizes[b].Width.CompareTo(sizes[a].Width);
            }

            return c == 0 ? a.CompareTo(b) : c;
        });

        var placements = new AtlasPlacement[sizes.Count];
        int layer = -1, shelfY = 0, shelfHeight = 0, x = 0;
        long used = 0;
        foreach (int i in order)
        {
            (int w, int h) = sizes[i];
            if (w <= 0 || h <= 0)
            {
                placements[i] = new AtlasPlacement(-1, 0, 0);
                continue;
            }

            if (w > pageSize || h > pageSize)
            {
                throw new ArgumentException($"Rectangle {i} ({w}×{h}) does not fit a {pageSize}² page.", nameof(sizes));
            }

            used += (long)w * h;
            if (layer < 0)
            {
                layer = 0;
            }

            if (x + w > pageSize)
            {
                shelfY += shelfHeight + padding;
                x = 0;
                shelfHeight = 0;
            }

            if (shelfY + h > pageSize)
            {
                layer++;
                shelfY = 0;
                x = 0;
                shelfHeight = 0;
            }

            placements[i] = new AtlasPlacement(layer, x, shelfY);
            x += w + padding;
            shelfHeight = Math.Max(shelfHeight, h);
        }

        return new AtlasPackResult(placements, layer + 1, used);
    }
}
