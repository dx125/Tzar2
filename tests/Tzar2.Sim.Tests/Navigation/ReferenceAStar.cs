using System;
using System.Collections.Generic;
using Tzar2.Sim.Navigation;

namespace Tzar2.Sim.Tests.Navigation;

/// <summary>Plain full-grid A* with the same move rules and costs as the navigation layers: the optimality oracle.</summary>
internal static class ReferenceAStar
{
    /// <summary>The optimal octile cost from <paramref name="start"/> to <paramref name="goal"/>, or -1.</summary>
    public static int Cost(NavLayer layer, int start, int goal, out int expanded)
    {
        expanded = 0;
        int w = layer.Width;
        int n = w * layer.Height;
        if (!layer.IsWalkable(start) || !layer.IsWalkable(goal))
        {
            return -1;
        }

        int[] g = new int[n];
        Array.Fill(g, int.MaxValue);
        bool[] closed = new bool[n];
        var open = new PriorityQueue<int, (long F, int Tile)>();
        g[start] = 0;
        open.Enqueue(start, (PathSearch.Octile(w, start, goal), start));
        ReadOnlySpan<int> dx = [1, 1, 0, -1, -1, -1, 0, 1];
        ReadOnlySpan<int> dy = [0, 1, 1, 1, 0, -1, -1, -1];
        while (open.Count > 0)
        {
            int t = open.Dequeue();
            if (closed[t])
            {
                continue;
            }

            closed[t] = true;
            expanded++;
            if (t == goal)
            {
                return g[t];
            }

            int x = t % w;
            int y = t / w;
            for (int d = 0; d < 8; d++)
            {
                int nx = x + dx[d];
                int ny = y + dy[d];
                if (!layer.IsWalkable(nx, ny) || ((d & 1) == 1 && (!layer.IsWalkable(nx, y) || !layer.IsWalkable(x, ny))))
                {
                    continue;
                }

                int m = (ny * w) + nx;
                int ng = g[t] + ((d & 1) == 0 ? NavLayer.StraightCost : NavLayer.DiagonalCost);
                if (ng < g[m])
                {
                    g[m] = ng;
                    open.Enqueue(m, (ng + PathSearch.Octile(w, m, goal), m));
                }
            }
        }

        return -1;
    }
}
