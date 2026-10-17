using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Rng;

namespace Tzar2.Sim.Scenarios;

/// <summary>
/// Synthetic test maps for the pathfinding regression tests and benchmarks (ORIG-01/02/03, TDD §21 B3/B4), built on
/// the terrain of <see cref="SyntheticScenario.CreateRuleset"/>. No original data is involved (REQ-LEGAL-03).
/// </summary>
public static class ScenarioMaps
{
    /// <summary>
    /// Builds a map from rows of characters, one per tile: <c>.</c> grass, <c>#</c> cliff (impassable except for air),
    /// <c>~</c> water (ships inside a lake; impassable for land units). A corner is water only if every tile touching
    /// it is water, so land tiles next to water stay fully passable.
    /// </summary>
    /// <exception cref="ArgumentException">The rows are empty, ragged or use an unknown character.</exception>
    public static MapData FromAscii(string name, params string[] rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Length == 0 || rows[0].Length == 0)
        {
            throw new ArgumentException("A map needs at least one row.", nameof(rows));
        }

        int w = rows[0].Length;
        int h = rows.Length;
        byte[] water = new byte[w * h];
        byte[] cliffs = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            if (rows[y].Length != w)
            {
                throw new ArgumentException($"Row {y} has {rows[y].Length} tiles, expected {w}.", nameof(rows));
            }

            for (int x = 0; x < w; x++)
            {
                switch (rows[y][x])
                {
                    case '.': break;
                    case '#': cliffs[(y * w) + x] = 1; break;
                    case '~': water[(y * w) + x] = 1; break;
                    default: throw new ArgumentException($"Unknown map character '{rows[y][x]}'.", nameof(rows));
                }
            }
        }

        return Build(name, w, h, water, cliffs);
    }

    /// <summary>
    /// Builds a map from per-tile flags (row-major W×H): <paramref name="water"/> non-zero = water tile,
    /// <paramref name="cliffs"/> non-zero = cliff tile.
    /// </summary>
    public static MapData Build(string name, int width, int height, ReadOnlySpan<byte> water, ReadOnlySpan<byte> cliffs)
    {
        int stride = width + 1;
        byte[] corners = new byte[stride * (height + 1)];
        for (int cy = 0; cy <= height; cy++)
        {
            for (int cx = 0; cx <= width; cx++)
            {
                bool allWater = true;
                for (int ty = cy - 1; ty <= cy && allWater; ty++)
                {
                    for (int tx = cx - 1; tx <= cx; tx++)
                    {
                        if ((uint)tx < (uint)width && (uint)ty < (uint)height && water[(ty * width) + tx] == 0)
                        {
                            allWater = false;
                            break;
                        }
                    }
                }

                corners[(cy * stride) + cx] = allWater ? SyntheticScenario.Water : SyntheticScenario.Grass;
            }
        }

        return new MapData(name, width, height, corners, [], cliffs);
    }

    /// <summary>
    /// A perfect maze (recursive backtracker, deterministic from <paramref name="seed"/>) of cliff walls on a
    /// <paramref name="size"/>² map: maze cells are <paramref name="corridor"/> tiles wide, walls
    /// <paramref name="wall"/> tiles thick. Every corridor tile is reachable from every other one, usually along a long,
    /// winding route (TDD §9.2 acceptance 6, §21 B3).
    /// </summary>
    /// <returns>The map, plus the tile centers of the first and the last maze cell (opposite corners).</returns>
    public static (MapData Map, (int X, int Y) First, (int X, int Y) Last) Maze(int size, int corridor, int wall, ulong seed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(corridor, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(wall, 1);
        int pitch = corridor + wall;
        int cells = (size - wall) / pitch;
        ArgumentOutOfRangeException.ThrowIfLessThan(cells, 2, nameof(size));
        byte[] cliffs = new byte[size * size];
        cliffs.AsSpan().Fill(1);

        void Carve(int x0, int y0, int w, int h)
        {
            for (int y = y0; y < y0 + h; y++)
            {
                cliffs.AsSpan((y * size) + x0, w).Clear();
            }
        }

        var rng = Xoshiro256StarStar.FromSeed(seed);
        bool[] visited = new bool[cells * cells];
        int[] stack = new int[cells * cells];
        int top = 0;
        stack[top++] = 0;
        visited[0] = true;
        Carve(wall, wall, corridor, corridor);
        Span<int> options = stackalloc int[4];
        while (top > 0)
        {
            int c = stack[top - 1];
            int cx = c % cells;
            int cy = c / cells;
            int n = 0;
            if (cy > 0 && !visited[c - cells]) { options[n++] = 0; }
            if (cx < cells - 1 && !visited[c + 1]) { options[n++] = 1; }
            if (cy < cells - 1 && !visited[c + cells]) { options[n++] = 2; }
            if (cx > 0 && !visited[c - 1]) { options[n++] = 3; }
            if (n == 0)
            {
                top--;
                continue;
            }

            int dir = options[rng.NextInt(n)];
            int nx = cx + (dir == 1 ? 1 : dir == 3 ? -1 : 0);
            int ny = cy + (dir == 2 ? 1 : dir == 0 ? -1 : 0);
            int next = (ny * cells) + nx;
            visited[next] = true;
            stack[top++] = next;
            int ox = wall + (nx * pitch);
            int oy = wall + (ny * pitch);
            Carve(ox, oy, corridor, corridor);

            // The wall between the two cells.
            int bx = wall + (Math.Min(cx, nx) * pitch);
            int by = wall + (Math.Min(cy, ny) * pitch);
            if (nx != cx)
            {
                Carve(bx + corridor, by, wall, corridor);
            }
            else
            {
                Carve(bx, by + corridor, corridor, wall);
            }
        }

        MapData map = new($"maze-{size}", size, size, new byte[(size + 1) * (size + 1)], [], cliffs);
        int half = corridor / 2;
        int last = wall + ((cells - 1) * pitch) + half;
        return (map, (wall + half, wall + half), (last, last));
    }
}
