using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Tzar2.Content.Terrain;

/// <summary>
/// The distinct 32 × 32 tiles of composed terrain (ADR 0210). A map's terrain repeats: its base textures tile, its
/// transitions come in a few shapes and variants, and the frames of a chunk's animation cycle (the least common multiple
/// of water's and the shores' frame counts) repeat each tile's own shorter cycle. So every composed tile of every frame
/// is looked up here, stored once, and a chunk keeps only a slot number per tile and frame. The pixels are the composer's
/// RGB5A1 words, unchanged: the terrain is drawn from the atlas exactly as from the composed frames.
/// </summary>
/// <remarks>
/// Pages of <see cref="PageTiles"/>² tiles (<see cref="PageSide"/>² pixels, two bytes each) hold the tiles in the order
/// they were first seen. Slots are 24-bit, so an atlas holds at most <see cref="MaxTiles"/> tiles. <see cref="Add"/> may be
/// called from several threads at once (slots then depend on the order, the pixels do not); the rest may not.
/// </remarks>
public sealed class TerrainTileAtlas
{
    /// <summary>Tile side in pixels.</summary>
    public const int TileSize = TerrainComposer.TileSize;

    /// <summary>Tiles per page side.</summary>
    public const int PageTiles = 64;

    /// <summary>Page side in pixels.</summary>
    public const int PageSide = PageTiles * TileSize;

    /// <summary>Tiles per page.</summary>
    public const int TilesPerPage = PageTiles * PageTiles;

    /// <summary>Most tiles an atlas holds (24-bit slots).</summary>
    public const int MaxTiles = 1 << 24;

    // Lookup only, never iterated: hash -> first slot with that hash; collisions chain through _next.
    private readonly Dictionary<ulong, int> _byHash = [];
    private readonly List<int> _next = [];
    private readonly List<byte[]> _pages = [];
    private readonly List<bool> _dirty = [];
    private readonly object _gate = new();

    /// <summary>Distinct tiles stored.</summary>
    public int Count { get; private set; }

    /// <summary>Tiles looked up so far (diagnostics: <see cref="Count"/> / <see cref="Lookups"/> is the share stored).</summary>
    public long Lookups { get; private set; }

    /// <summary>The pages: <see cref="PageSide"/>² little-endian RGB5A1 words each.</summary>
    public IReadOnlyList<byte[]> Pages => _pages;

    /// <summary>
    /// Adds the tiles of the frames of a <paramref name="width"/> × <paramref name="height"/> pixel window (whole tiles;
    /// little-endian RGB5A1 words, as <see cref="TerrainComposer.Compose(TerrainGrid, int, int, int, TerrainImage?)"/>
    /// returns them) and returns, per frame, the slot of each tile in row order.
    /// </summary>
    /// <exception cref="InvalidOperationException">The atlas is full.</exception>
    public int[][] Add(int width, int height, IReadOnlyList<byte[]> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (width <= 0 || height <= 0 || width % TileSize != 0 || height % TileSize != 0)
        {
            throw new ArgumentException($"A window of {width} x {height} pixels is not whole tiles.", nameof(width));
        }

        int across = width / TileSize, down = height / TileSize;
        for (int f = 0; f < frames.Count; f++)
        {
            if (frames[f].Length != width * height * 2)
            {
                throw new ArgumentException($"Frame {f} has {frames[f].Length} bytes, not {width * height * 2}.", nameof(frames));
            }
        }

        // Hashes first, outside the lock: callers compose chunks in parallel and add them as they finish.
        var hashes = new ulong[frames.Count][];
        for (int f = 0; f < frames.Count; f++)
        {
            hashes[f] = new ulong[across * down];
            for (int t = 0; t < across * down; t++)
            {
                hashes[f][t] = Hash(frames[f], width, t % across, t / across);
            }
        }

        var slots = new int[frames.Count][];
        lock (_gate)
        {
            for (int f = 0; f < frames.Count; f++)
            {
                slots[f] = new int[across * down];
                for (int t = 0; t < across * down; t++)
                {
                    slots[f][t] = Slot(hashes[f][t], frames[f], width, t % across, t / across);
                }
            }
        }

        return slots;
    }

    /// <summary>The pages changed since the last call, in ascending order; clears the list.</summary>
    public int[] TakeDirtyPages()
    {
        var dirty = new List<int>();
        for (int i = 0; i < _dirty.Count; i++)
        {
            if (_dirty[i])
            {
                dirty.Add(i);
                _dirty[i] = false;
            }
        }

        return [.. dirty];
    }

    /// <summary>Copies the pixels of slot <paramref name="slot"/> into <paramref name="destination"/> (row order, 2 bytes per pixel).</summary>
    public void CopyTile(int slot, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, Count);
        byte[] page = _pages[slot / TilesPerPage];
        int local = slot % TilesPerPage;
        int x = (local % PageTiles) * TileSize, y = (local / PageTiles) * TileSize;
        for (int row = 0; row < TileSize; row++)
        {
            page.AsSpan((((y + row) * PageSide) + x) * 2, TileSize * 2).CopyTo(destination.Slice(row * TileSize * 2, TileSize * 2));
        }
    }

    // The slot of the tile at (tx, ty) of a frame <width> pixels wide, stored if it is new (under the lock).
    private int Slot(ulong hash, byte[] pixels, int width, int tx, int ty)
    {
        Lookups++;
        int first = _byHash.TryGetValue(hash, out int found) ? found : -1;
        for (int s = first; s >= 0; s = _next[s])
        {
            if (Same(s, pixels, width, tx, ty))
            {
                return s;
            }
        }

        if (Count >= MaxTiles)
        {
            throw new InvalidOperationException($"The terrain has more than {MaxTiles} distinct tiles.");
        }

        int slot = Count++;
        _next.Add(first);
        _byHash[hash] = slot;
        int pageIndex = slot / TilesPerPage;
        if (pageIndex == _pages.Count)
        {
            _pages.Add(new byte[PageSide * PageSide * 2]);
            _dirty.Add(false);
        }

        byte[] page = _pages[pageIndex];
        (int x, int y) = PageOrigin(slot);
        for (int row = 0; row < TileSize; row++)
        {
            Buffer.BlockCopy(pixels, ((((ty * TileSize) + row) * width) + (tx * TileSize)) * 2, page, (((y + row) * PageSide) + x) * 2, TileSize * 2);
        }

        _dirty[pageIndex] = true;
        return slot;
    }

    private bool Same(int slot, byte[] pixels, int width, int tx, int ty)
    {
        byte[] page = _pages[slot / TilesPerPage];
        (int x, int y) = PageOrigin(slot);
        for (int row = 0; row < TileSize; row++)
        {
            ReadOnlySpan<byte> stored = page.AsSpan((((y + row) * PageSide) + x) * 2, TileSize * 2);
            if (!stored.SequenceEqual(pixels.AsSpan(((((ty * TileSize) + row) * width) + (tx * TileSize)) * 2, TileSize * 2)))
            {
                return false;
            }
        }

        return true;
    }

    private static (int X, int Y) PageOrigin(int slot)
    {
        int local = slot % TilesPerPage;
        return ((local % PageTiles) * TileSize, (local / PageTiles) * TileSize);
    }

    // FNV-1a over the tile's rows as 64-bit words (a lookup key only; equal hashes are compared byte for byte).
    private static ulong Hash(byte[] pixels, int width, int tx, int ty)
    {
        ulong h = 14695981039346656037UL;
        for (int row = 0; row < TileSize; row++)
        {
            ReadOnlySpan<byte> bytes = pixels.AsSpan(((((ty * TileSize) + row) * width) + (tx * TileSize)) * 2, TileSize * 2);
            foreach (ulong word in MemoryMarshal.Cast<byte, ulong>(bytes))
            {
                h = (h ^ word) * 1099511628211UL;
                h ^= h >> 29;
            }
        }

        return h;
    }
}
