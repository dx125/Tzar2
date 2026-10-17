using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Tools.Cli.Fidelity.Scenarios;

namespace Tzar2.Tools.Cli.Fidelity.Analysis;

internal enum CellMatchKind
{
    /// <summary>All 1,024 pixels equal a base tile.</summary>
    Exact,

    /// <summary>Most rows equal a base tile; the other pixels are covered (sprites, shadows, fog).</summary>
    Partial,

    /// <summary>The non-key pixels equal a transition tile.</summary>
    Transition,
}

/// <summary>A screen cell identified as a terrain tile; (X, Y) is its top-left pixel on the screen.</summary>
internal sealed record CellMatch(int Cx, int Cy, int X, int Y, CellMatchKind Kind, TerrainTile Tile, int EqualPixels, int ComparedPixels)
{
    /// <summary>For transitions: key pixels whose colour belongs only to the lower / only to the higher terrain of the pair.</summary>
    public (int MagentaLower, int MagentaHigher, int GreenLower, int GreenHigher) KeyEvidence { get; init; }
}

/// <summary>The terrain grid found in one capture.</summary>
internal sealed record TileMatchResult(int OffsetX, int OffsetY, int Votes, int CellsTotal, IReadOnlyList<CellMatch> Cells, int Columns, int Rows)
{
    public int Count(CellMatchKind kind) => Cells.Count(c => c.Kind == kind);
}

/// <summary>
/// Finds the 32-px terrain grid in a capture and identifies the tile under every screen cell (UNK-10). Works on
/// canonical RGB555 pixels and needs exact pixel equality, so it relies on the lossless game snapshot (or a window
/// capture whose expansion preserves the top 5 bits). Grid offset: 32-pixel row strips of the capture are looked up
/// in an index of all base-tile rows; each hit votes for an offset. Cells are then matched by a full-tile hash, by row
/// votes (partly covered cells) and finally against transition tiles on their non-key pixels.
/// </summary>
internal sealed class TileMatcher
{
    private const int S = TerrainTile.Size;
    private readonly TerrainLibrary _library;
    private readonly Dictionary<ulong, List<int>> _full = [];
    private readonly Dictionary<ulong, List<int>> _strips = [];
    private readonly TerrainTile[] _transitions;

    public TileMatcher(TerrainLibrary library)
    {
        _library = library;
        foreach (TerrainTile tile in library.Tiles)
        {
            if (tile.IsTransition)
            {
                continue;
            }

            Add(_full, Hash(tile.Pixels, 0, S * S), tile.Index);
            for (int row = 0; row < S; row++)
            {
                if (Informative(tile.Pixels.AsSpan(row * S, S)))
                {
                    Add(_strips, Hash(tile.Pixels, row * S, S), (tile.Index * S) + row);
                }
            }
        }

        _transitions = library.Tiles.Where(t => t.IsTransition).ToArray();
    }

    public TileMatchResult Match(ushort[] frame, int width, int height, ScreenRect region)
    {
        int x0 = Math.Max(0, region.X), y0 = Math.Max(0, region.Y);
        int x1 = Math.Min(width, region.Right), y1 = Math.Min(height, region.Bottom);
        int[] votes = new int[S * S];
        for (int y = y0; y < y1; y += 3)
        {
            for (int x = x0; x + S <= x1; x++)
            {
                if (_strips.TryGetValue(Hash(frame, (y * width) + x, S), out List<int>? hits))
                {
                    foreach (int hit in hits)
                    {
                        int oy = ((y - (hit % S)) % S + S) % S;
                        votes[(oy * S) + (x % S)]++;
                    }
                }
            }
        }

        int best = Array.IndexOf(votes, votes.Max());
        int offsetX = best % S, offsetY = best / S;
        var cells = new List<CellMatch>();
        int firstX = x0 + ((offsetX - x0) % S + S) % S;
        int firstY = y0 + ((offsetY - y0) % S + S) % S;
        int columns = Math.Max(0, (x1 - firstX) / S), rows = Math.Max(0, (y1 - firstY) / S);
        if (votes[best] > 0)
        {
            ushort[] cell = new ushort[S * S];
            for (int cy = 0; cy < rows; cy++)
            {
                for (int cx = 0; cx < columns; cx++)
                {
                    int px = firstX + (cx * S), py = firstY + (cy * S);
                    for (int r = 0; r < S; r++)
                    {
                        Array.Copy(frame, ((py + r) * width) + px, cell, r * S, S);
                    }

                    CellMatch? match = MatchCell(cell, cx, cy, px, py);
                    if (match is not null)
                    {
                        cells.Add(match);
                    }
                }
            }
        }

        return new TileMatchResult(offsetX, offsetY, votes[best], columns * rows, cells, columns, rows);
    }

    private CellMatch? MatchCell(ushort[] cell, int cx, int cy, int px, int py)
    {
        if (_full.TryGetValue(Hash(cell, 0, S * S), out List<int>? exact))
        {
            foreach (int index in exact)
            {
                if (cell.AsSpan().SequenceEqual(_library.Tiles[index].Pixels))
                {
                    return new CellMatch(cx, cy, px, py, CellMatchKind.Exact, _library.Tiles[index], S * S, S * S);
                }
            }
        }

        var rowVotes = new Dictionary<int, int>();
        for (int r = 0; r < S; r++)
        {
            if (_strips.TryGetValue(Hash(cell, r * S, S), out List<int>? hits))
            {
                foreach (int hit in hits)
                {
                    if (hit % S == r)
                    {
                        rowVotes[hit / S] = rowVotes.GetValueOrDefault(hit / S) + 1;
                    }
                }
            }
        }

        if (rowVotes.Count > 0)
        {
            // Several tiles can share rows; the one with the most equal pixels wins.
            TerrainTile? bestTile = null;
            int bestEqual = -1;
            foreach ((int index, int count) in rowVotes)
            {
                if (count < 3)
                {
                    continue;
                }

                int equal = Equal(cell, _library.Tiles[index].Pixels);
                if (equal > bestEqual)
                {
                    (bestTile, bestEqual) = (_library.Tiles[index], equal);
                }
            }

            if (bestTile is not null && bestEqual >= S * 4)
            {
                return new CellMatch(cx, cy, px, py, CellMatchKind.Partial, bestTile, bestEqual, S * S);
            }
        }

        return MatchTransition(cell, cx, cy, px, py);
    }

    private CellMatch? MatchTransition(ushort[] cell, int cx, int cy, int px, int py)
    {
        TerrainTile? bestTile = null;
        int bestEqual = 0, bestCompared = 0;
        foreach (TerrainTile t in _transitions)
        {
            int compared = 0, equal = 0;
            ushort[] p = t.Pixels;
            for (int i = 0; i < p.Length; i++)
            {
                if (p[i] is TerrainTile.Green or TerrainTile.Magenta)
                {
                    continue;
                }

                compared++;
                if (p[i] == cell[i])
                {
                    equal++;
                }
            }

            if (compared >= 64 && equal * 10 >= compared * 9 && equal > bestEqual)
            {
                (bestTile, bestEqual, bestCompared) = (t, equal, compared);
            }
        }

        if (bestTile is null)
        {
            return null;
        }

        return new CellMatch(cx, cy, px, py, CellMatchKind.Transition, bestTile, bestEqual, bestCompared) { KeyEvidence = KeyEvidence(cell, bestTile) };
    }

    private (int, int, int, int) KeyEvidence(ushort[] cell, TerrainTile tile)
    {
        // The pair's two terrains (e.g. code "12": 1 lower, 2 higher). Several pairs may share a file; use the first.
        int lower = tile.Codes[0][0] - '0', higher = tile.Codes[0][1] - '0';
        HashSet<ushort> lowSet = _library.ColoursByTerrain.GetValueOrDefault(lower) ?? [];
        HashSet<ushort> highSet = _library.ColoursByTerrain.GetValueOrDefault(higher) ?? [];
        int ml = 0, mh = 0, gl = 0, gh = 0;
        for (int i = 0; i < cell.Length; i++)
        {
            ushort key = tile.Pixels[i];
            if (key is not (TerrainTile.Green or TerrainTile.Magenta))
            {
                continue;
            }

            bool inLow = lowSet.Contains(cell[i]), inHigh = highSet.Contains(cell[i]);
            if (inLow == inHigh)
            {
                continue;
            }

            if (key == TerrainTile.Magenta)
            {
                ml += inLow ? 1 : 0;
                mh += inHigh ? 1 : 0;
            }
            else
            {
                gl += inLow ? 1 : 0;
                gh += inHigh ? 1 : 0;
            }
        }

        return (ml, mh, gl, gh);
    }

    private static int Equal(ushort[] a, ushort[] b)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i++)
        {
            n += a[i] == b[i] ? 1 : 0;
        }

        return n;
    }

    private static bool Informative(ReadOnlySpan<ushort> row)
    {
        ushort a = row[0];
        int distinct = 1;
        ushort b = a;
        foreach (ushort v in row)
        {
            if (v != a && (distinct == 1 || v != b))
            {
                if (distinct == 1)
                {
                    b = v;
                }

                distinct++;
                if (distinct >= 3)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ulong Hash(ushort[] data, int start, int count)
    {
        ulong h = 0xCBF29CE484222325;
        for (int i = start; i < start + count; i++)
        {
            h = (h ^ data[i]) * 0x100000001B3;
        }

        return h;
    }

    private static void Add(Dictionary<ulong, List<int>> map, ulong key, int value)
    {
        if (!map.TryGetValue(key, out List<int>? list))
        {
            map[key] = list = [];
        }

        list.Add(value);
    }
}
