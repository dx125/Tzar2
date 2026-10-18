using System;

namespace Tzar2.Sim.Navigation;

/// <summary>
/// A min-heap of (key, value) pairs ordered by key, then value: a total order, so the pop sequence is a function of the
/// pushed pairs alone, identical on every platform and run and for any heap layout (deterministic tie-breaking,
/// REQ-PATH-03). Duplicates are allowed (lazy decrease-key). A 4-ary heap over one array of entries: half the depth
/// of a binary heap and each node's children share a cache line.
/// </summary>
internal sealed class MinHeap
{
    private Entry[] _entries;

    public MinHeap(int capacity = 256) => _entries = new Entry[Math.Max(4, capacity)];

    public int Count { get; private set; }

    public void Clear() => Count = 0;

    public void Push(ulong key, int value)
    {
        Entry[] entries = _entries;
        if (Count == entries.Length)
        {
            Array.Resize(ref _entries, entries.Length * 2);
            entries = _entries;
        }

        int i = Count++;
        while (i > 0)
        {
            int parent = (i - 1) >> 2;
            ref Entry p = ref entries[parent];
            if (p.Key < key || (p.Key == key && p.Value <= value))
            {
                break;
            }

            entries[i] = p;
            i = parent;
        }

        entries[i] = new Entry(key, value);
    }

    public int Pop(out ulong key)
    {
        Entry[] entries = _entries;
        key = entries[0].Key;
        int result = entries[0].Value;
        int n = --Count;
        if (n > 0)
        {
            Entry last = entries[n];
            int i = 0;
            while (true)
            {
                int first = (i << 2) + 1;
                if (first >= n)
                {
                    break;
                }

                // The least of the up to four children.
                int best = first;
                int end = Math.Min(first + 4, n);
                for (int c = first + 1; c < end; c++)
                {
                    if (Less(entries[c], entries[best]))
                    {
                        best = c;
                    }
                }

                if (!Less(entries[best], last))
                {
                    break;
                }

                entries[i] = entries[best];
                i = best;
            }

            entries[i] = last;
        }

        return result;
    }

    private static bool Less(in Entry a, in Entry b) => a.Key < b.Key || (a.Key == b.Key && a.Value < b.Value);

    private readonly record struct Entry(ulong Key, int Value);
}

/// <summary>
/// Dijkstra inside one cluster (≤ 16×16 tiles) from a root tile, producing the per-tile cost and a parent tree
/// (direction from each tile to its parent; following it leads to the root). Used for portal trees, for connecting a
/// path's start and goal to the abstract graph, and for flow-field cluster fields.
/// </summary>
internal sealed class LocalSearch
{
    private readonly int[] _cost = new int[NavLayer.ClusterTiles];
    private readonly MinHeap _heap = new(512);

    /// <summary>Tiles settled by the last search (budget accounting).</summary>
    public int Expanded { get; private set; }

    /// <summary>Cost from the root to <paramref name="tile"/> in the last search, or -1 if unreached.</summary>
    public int CostAt(NavLayer.Cluster cluster, int tile)
    {
        int c = _cost[cluster.LocalIndex(tile, WidthOfLastLayer)];
        return c == int.MaxValue ? -1 : c;
    }

    /// <summary>The raw cost array of the last search (local index; <see cref="int.MaxValue"/> = unreached).</summary>
    public ReadOnlySpan<int> Costs => _cost;

    private int WidthOfLastLayer { get; set; }

    /// <summary>Runs Dijkstra from <paramref name="root"/> over the cluster, writing the tree.</summary>
    public void Dijkstra(NavLayer layer, NavLayer.Cluster cluster, int root, byte[] tree, int[]? costOut)
    {
        Seed(layer, tree);
        int local = cluster.LocalIndex(root, layer.Width);
        _cost[local] = 0;
        tree[local] = NavLayer.TreeRoot;
        _heap.Push(0, local);
        Run(layer, cluster, tree, costOut);
    }

    /// <summary>Starts a multi-source search: clears the arrays (call <see cref="AddSource"/>, then <see cref="Run"/>).</summary>
    public void Seed(NavLayer layer, byte[] tree)
    {
        WidthOfLastLayer = layer.Width;
        _cost.AsSpan().Fill(int.MaxValue);
        tree.AsSpan().Fill(NavLayer.TreeNone);
        _heap.Clear();
        Expanded = 0;
    }

    /// <summary>Adds a source tile with an initial cost and the tree code it gets if the source wins.</summary>
    public void AddSource(NavLayer layer, NavLayer.Cluster cluster, int tile, int cost, byte code, byte[] tree)
    {
        int local = cluster.LocalIndex(tile, layer.Width);
        if (cost < _cost[local])
        {
            _cost[local] = cost;
            tree[local] = code;
            _heap.Push((ulong)cost, local);
        }
    }

    /// <summary>Runs the search to exhaustion.</summary>
    public void Run(NavLayer layer, NavLayer.Cluster cluster, byte[] tree, int[]? costOut)
    {
        while (_heap.Count > 0)
        {
            int local = _heap.Pop(out ulong key);
            int cost = (int)key;
            if (cost != _cost[local])
            {
                continue;
            }

            Expanded++;
            int lx = local % NavLayer.ClusterSize;
            int ly = local / NavLayer.ClusterSize;
            int x = cluster.X + lx;
            int y = cluster.Y + ly;
            for (int dir = 0; dir < 8; dir++)
            {
                int nlx = lx + NavLayer.DirX[dir];
                int nly = ly + NavLayer.DirY[dir];
                if ((uint)nlx >= (uint)cluster.W || (uint)nly >= (uint)cluster.H || !layer.CanStep(x, y, dir))
                {
                    continue;
                }

                int n = (nly * NavLayer.ClusterSize) + nlx;
                int nc = cost + ((dir & 1) == 0 ? NavLayer.StraightCost : NavLayer.DiagonalCost);
                if (nc < _cost[n])
                {
                    _cost[n] = nc;
                    tree[n] = (byte)((dir + 4) & 7); // from the neighbour back to this tile
                    _heap.Push((ulong)nc, n);
                }
            }
        }

        if (costOut is not null)
        {
            _cost.CopyTo(costOut, 0);
        }
    }
}
