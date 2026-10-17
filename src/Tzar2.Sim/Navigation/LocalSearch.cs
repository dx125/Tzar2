using System;

namespace Tzar2.Sim.Navigation;

/// <summary>
/// A binary min-heap of (key, value) pairs ordered by key, then value: a total order, so pops are identical on every
/// platform and run (deterministic tie-breaking, REQ-PATH-03). Duplicates are allowed (lazy decrease-key).
/// </summary>
internal sealed class MinHeap
{
    private ulong[] _keys;
    private int[] _values;

    public MinHeap(int capacity = 256)
    {
        _keys = new ulong[capacity];
        _values = new int[capacity];
    }

    public int Count { get; private set; }

    public void Clear() => Count = 0;

    public void Push(ulong key, int value)
    {
        if (Count == _keys.Length)
        {
            Array.Resize(ref _keys, _keys.Length * 2);
            Array.Resize(ref _values, _values.Length * 2);
        }

        int i = Count++;
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (!Less(key, value, _keys[parent], _values[parent]))
            {
                break;
            }

            _keys[i] = _keys[parent];
            _values[i] = _values[parent];
            i = parent;
        }

        _keys[i] = key;
        _values[i] = value;
    }

    public int Pop(out ulong key)
    {
        key = _keys[0];
        int result = _values[0];
        int n = --Count;
        if (n > 0)
        {
            ulong k = _keys[n];
            int v = _values[n];
            int i = 0;
            while (true)
            {
                int child = (2 * i) + 1;
                if (child >= n)
                {
                    break;
                }

                if (child + 1 < n && Less(_keys[child + 1], _values[child + 1], _keys[child], _values[child]))
                {
                    child++;
                }

                if (!Less(_keys[child], _values[child], k, v))
                {
                    break;
                }

                _keys[i] = _keys[child];
                _values[i] = _values[child];
                i = child;
            }

            _keys[i] = k;
            _values[i] = v;
        }

        return result;
    }

    private static bool Less(ulong ka, int va, ulong kb, int vb) => ka < kb || (ka == kb && va < vb);
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
