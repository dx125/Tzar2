using System;
using Tzar2.Core.Numerics;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Navigation;

/// <summary>
/// Arrival slots for a group move (REQ-PATH-05, fixes ORIG-02; ADR 0024): free occupancy cells around the goal found by
/// a breadth-first flood over reachable cells, on a lattice spaced for the group's largest unit, then assigned to the
/// units one by one — in order of ascending distance to the goal, ties by entity index — each taking the free slot
/// nearest to its desired point. Small, compact groups keep their formation: a unit's desired point is the goal plus
/// its offset from the group's centroid; spread-out groups gather around the goal.
/// </summary>
internal sealed class ArrivalPlanner
{
    /// <summary>Most cells the flood visits.</summary>
    public const int MaxFloodCells = 65_536;

    private int[] _queue = new int[1024];
    private int[] _visitedStamp = [];
    private int[] _memberCount = [];
    private int _stamp;
    private int[] _slots = new int[256];
    private bool[] _taken = new bool[256];
    private int[] _order = new int[64];
    private long[] _orderKey = new long[64];

    /// <summary>Work done by the last call (budget accounting).</summary>
    public int Cost { get; private set; }

    /// <summary>
    /// Assigns slots. <paramref name="x"/>/<paramref name="y"/> are the members' positions, <paramref name="ids"/> their
    /// entity indices (tie-breaks); the targets are written to <paramref name="targetX"/>/<paramref name="targetY"/>.
    /// </summary>
    /// <returns>The radius (world units) of the occupied slot area, for settling.</returns>
    public int Plan(
        MapState map,
        NavLayer layer,
        int component,
        int goalX,
        int goalY,
        int maxRadius,
        ReadOnlySpan<int> x,
        ReadOnlySpan<int> y,
        ReadOnlySpan<int> ids,
        Span<int> targetX,
        Span<int> targetY)
    {
        int n = x.Length;
        Cost = 0;
        int unit = map.UnitsPerCell;
        int spacing = Math.Max(1, ((2 * maxRadius * 9 / 8) + unit - 1) / unit);
        int cells = map.CellWidth * map.CellHeight;
        if (_visitedStamp.Length != cells)
        {
            _visitedStamp = new int[cells];
            _memberCount = new int[cells];
            _stamp = 0;
        }

        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_visitedStamp);
            _stamp = 1;
        }

        // Members do not block their own slots.
        for (int i = 0; i < n; i++)
        {
            _memberCount[map.CellIndexAt(x[i], y[i])]++;
        }

        // Formation: compact groups keep their shape.
        long sx = 0;
        long sy = 0;
        for (int i = 0; i < n; i++)
        {
            sx += x[i];
            sy += y[i];
        }

        int cx = (int)(sx / n);
        int cy = (int)(sy / n);
        long spread = 0;
        for (int i = 0; i < n; i++)
        {
            spread = Math.Max(spread, IntMath.DistanceSquared(x[i], y[i], cx, cy));
        }

        spread = IntMath.ISqrt(spread);
        long compact = (long)spacing * unit * (IntMath.ISqrt(n) + 1) / 2;
        bool formation = spread <= Math.Max(2 * compact, 8L * MapState.UnitsPerTile);
        long reach = (formation ? spread : compact) + (2L * spacing * unit) + MapState.UnitsPerTile;
        int reachCells = (int)Math.Min(int.MaxValue / 4, (reach / unit) + 1);
        int wanted = Math.Max(n * 3, n + 16);

        // Flood from the goal cell over reachable cells, collecting free lattice cells.
        int gcx = Math.Clamp(goalX / unit, 0, map.CellWidth - 1);
        int gcy = Math.Clamp(goalY / unit, 0, map.CellHeight - 1);
        int goalCell = (gcy * map.CellWidth) + gcx;
        int slots = 0;
        int head = 0;
        int tail = 0;
        Push(goalCell, ref tail);
        _visitedStamp[goalCell] = _stamp;
        ReadOnlySpan<int> occupancy = map.Occupancy;
        while (head < tail && tail < MaxFloodCells)
        {
            int cell = _queue[head++];
            int ccx = cell % map.CellWidth;
            int ccy = cell / map.CellWidth;
            if (Math.Max(Math.Abs(ccx - gcx), Math.Abs(ccy - gcy)) > reachCells && slots >= wanted)
            {
                break;
            }

            if ((((ccx - gcx) % spacing) + spacing) % spacing == 0 && (((ccy - gcy) % spacing) + spacing) % spacing == 0
                && occupancy[cell] - _memberCount[cell] <= 0
                && layer.IsWalkablePoint((ccx * unit) + (unit / 2), (ccy * unit) + (unit / 2)))
            {
                if (slots == _slots.Length)
                {
                    Array.Resize(ref _slots, slots * 2);
                }

                _slots[slots++] = cell;
            }

            for (int d = 0; d < 4; d++)
            {
                int nx = ccx + (d == 0 ? 1 : d == 2 ? -1 : 0);
                int ny = ccy + (d == 1 ? 1 : d == 3 ? -1 : 0);
                if ((uint)nx >= (uint)map.CellWidth || (uint)ny >= (uint)map.CellHeight)
                {
                    continue;
                }

                int next = (ny * map.CellWidth) + nx;
                if (_visitedStamp[next] == _stamp)
                {
                    continue;
                }

                _visitedStamp[next] = _stamp;
                int px = (nx * unit) + (unit / 2);
                int py = (ny * unit) + (unit / 2);
                if (layer.IsWalkablePoint(px, py) && layer.ComponentOf(layer.TileOf(px, py)) == component)
                {
                    Push(next, ref tail);
                }
            }
        }

        Cost += tail;
        for (int i = 0; i < n; i++)
        {
            _memberCount[map.CellIndexAt(x[i], y[i])]--;
        }

        // Assignment order: ascending distance of the desired point to the goal, then entity index.
        if (_order.Length < n)
        {
            _order = new int[n];
            _orderKey = new long[n];
        }

        for (int i = 0; i < n; i++)
        {
            int dx = formation ? goalX + (x[i] - cx) : goalX;
            int dy = formation ? goalY + (y[i] - cy) : goalY;
            targetX[i] = dx;
            targetY[i] = dy;
            _order[i] = i;

            // Unique composite key: distance² (≤ 2^42 on the largest map) above the 20-bit entity index.
            long d2 = Math.Min(IntMath.DistanceSquared(dx, dy, goalX, goalY), (1L << 42) - 1);
            _orderKey[i] = (d2 << 20) | (uint)ids[i];
        }

        Array.Sort(_orderKey, _order, 0, n);
        ReadOnlySpan<int> order = _order.AsSpan(0, n);
        if (_taken.Length < slots)
        {
            _taken = new bool[_slots.Length];
        }

        Array.Clear(_taken, 0, slots);
        long farthest = 0;
        foreach (int i in order)
        {
            int best = -1;
            long bestD = long.MaxValue;
            for (int s = 0; s < slots; s++)
            {
                if (_taken[s])
                {
                    continue;
                }

                int scell = _slots[s];
                long d = IntMath.DistanceSquared((scell % map.CellWidth * unit) + (unit / 2), (scell / map.CellWidth * unit) + (unit / 2), targetX[i], targetY[i]);
                if (d < bestD)
                {
                    bestD = d;
                    best = s;
                }
            }

            Cost += slots / 16;
            if (best < 0)
            {
                // No free slot left: head for the goal and settle wherever the crowd allows (never orbit).
                targetX[i] = goalX;
                targetY[i] = goalY;
                continue;
            }

            _taken[best] = true;
            int cell = _slots[best];
            targetX[i] = (cell % map.CellWidth * unit) + (unit / 2);
            targetY[i] = (cell / map.CellWidth * unit) + (unit / 2);
            farthest = Math.Max(farthest, IntMath.DistanceSquared(targetX[i], targetY[i], goalX, goalY));
        }

        return (int)Math.Min(int.MaxValue / 2, IntMath.ISqrt(farthest));
    }

    private void Push(int cell, ref int tail)
    {
        if (tail == _queue.Length)
        {
            Array.Resize(ref _queue, _queue.Length * 2);
        }

        _queue[tail++] = cell;
    }
}
