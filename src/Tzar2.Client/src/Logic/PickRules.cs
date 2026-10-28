using System;

namespace Tzar2.Client.Logic;

/// <summary>What a candidate of a click is to the pick.</summary>
public enum PickKind
{
    /// <summary>A unit, or anything else without a footprint (an item): found by the middle of its picture.</summary>
    Unit = 0,

    /// <summary>A structure or a mine: found by the cells its footprint takes.</summary>
    Structure = 1,

    /// <summary>A tree: found last, by its cell.</summary>
    Tree = 2,
}

/// <summary>Something drawn that a click may pick.</summary>
/// <param name="Index">The caller's index of it (the frame's entity).</param>
/// <param name="Kind">How the pick finds it.</param>
/// <param name="TileX">The tile a unit stands on (not read for structures and trees).</param>
/// <param name="TileY">See <paramref name="TileX"/>.</param>
/// <param name="MidX">The middle of its drawn picture, in world pixels (units).</param>
/// <param name="MidY">See <paramref name="MidX"/>.</param>
/// <param name="Newest">Higher for what came later: of several on the clicked cell, the newest is picked.</param>
/// <param name="Covers">For a structure or tree: whether it takes the clicked cell (its footprint, without the cells its
/// picture leaves free).</param>
public readonly record struct PickCandidate(int Index, PickKind Kind, int TileX, int TileY, int MidX, int MidY, long Newest, bool Covers);

/// <summary>
/// What a click (or the target of an order) picks, as the original finds it (CTRL-001; ReTzar's reading, 0x4e4280,
/// 0x44e900, 0x44e400): the unit on the cells round the clicked one whose picture's middle is nearest the point, when it is
/// under <see cref="ClickReach"/> pixels each way; else the newest object on the clicked cell, a unit standing on it or a
/// structure taking it; else a tree there. Never by where pictures overlap: a building under a tree's crown and a unit in
/// front of another are found as the original finds them (the sixth play-test).
/// </summary>
public static class PickRules
{
    /// <summary>The original's distance from a click to a unit's picture middle under which the unit is taken (0x44ea10).</summary>
    public const int ClickReach = 30;

    /// <summary>The drag (pixels) after which a held left button draws its box (0x4f4998).</summary>
    public const int BoxShown = 8;

    /// <summary>The drag (pixels) after which the box selects instead of a click (0x4f4ef8; the sixth play-test: a click in a group often took several).</summary>
    public const int BoxSelects = 16;

    /// <summary>The picked candidate's <see cref="PickCandidate.Index"/> for a click at world pixel (<paramref name="x"/>, <paramref name="y"/>), or −1.</summary>
    public static int Pointed(ReadOnlySpan<PickCandidate> candidates, int x, int y)
    {
        int cx = (int)Math.Floor(x / 32.0), cy = (int)Math.Floor(y / 32.0);

        // The units round the cell, by the middle of their pictures; ties to the one further left, then up, then the newest.
        int best = -1;
        int bestGap = int.MaxValue;
        for (int i = 0; i < candidates.Length; i++)
        {
            PickCandidate c = candidates[i];
            if (c.Kind != PickKind.Unit || Math.Abs(c.TileX - cx) > 1 || Math.Abs(c.TileY - cy) > 1)
            {
                continue;
            }

            int gap = Math.Max(Math.Abs(c.MidX - x), Math.Abs(c.MidY - y));
            if (gap < bestGap || (gap == bestGap && Before(c, candidates[best])))
            {
                (best, bestGap) = (i, gap);
            }
        }

        if (best >= 0 && bestGap < ClickReach)
        {
            return candidates[best].Index;
        }

        // The newest on the cell: a unit standing there or a structure taking it; then a tree.
        int found = Newest(candidates, c => (c.Kind == PickKind.Unit && c.TileX == cx && c.TileY == cy) || (c.Kind == PickKind.Structure && c.Covers));
        return found >= 0 ? found : Newest(candidates, c => c.Kind == PickKind.Tree && c.Covers);
    }

    private static bool Before(in PickCandidate a, in PickCandidate b)
        => a.TileX != b.TileX ? a.TileX < b.TileX : a.TileY != b.TileY ? a.TileY < b.TileY : a.Newest > b.Newest;

    private static int Newest(ReadOnlySpan<PickCandidate> candidates, Func<PickCandidate, bool> takes)
    {
        int best = -1;
        for (int i = 0; i < candidates.Length; i++)
        {
            if (takes(candidates[i]) && (best < 0 || candidates[i].Newest > candidates[best].Newest))
            {
                best = i;
            }
        }

        return best < 0 ? -1 : candidates[best].Index;
    }
}
