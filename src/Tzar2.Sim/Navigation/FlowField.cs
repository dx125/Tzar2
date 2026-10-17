using System;
using System.Collections.Generic;

namespace Tzar2.Sim.Navigation;

/// <summary>
/// A shared flow field toward one goal tile on one layer (REQ-PATH-04, ADR 0023): an abstract Dijkstra from the goal
/// over the portal graph gives every portal its cost-to-goal and next hop; per cluster, a local field (built on demand
/// when a unit is in the cluster) is a multi-source Dijkstra from the cluster's exit portals (and the goal tile in the
/// goal cluster), storing the direction toward the goal per tile. Units descend the field instead of each planning a
/// path, so a large group costs one search.
/// </summary>
/// <remarks>
/// Every value is a pure function of the layer and the goal tile; the field is a cache (not state) and is rebuilt when
/// the layer's version changes. Its budget cost (<see cref="Cost"/>) is the number of nodes a fresh build settles,
/// independent of cache history (REQ-PATH-03).
/// </remarks>
public sealed class FlowField
{
    private int[] _nodeCost = [];
    private int[] _nodeNext = [];
    private LocalField?[] _local = [];
    private int[] _localUsed = [];
    private int _localUsedCount;
    private readonly Stack<LocalField> _pool;
    private LocalField? _spare;

    internal FlowField(Stack<LocalField> sharedPool) => _pool = sharedPool;

    /// <summary>Layer index.</summary>
    public int Layer { get; private set; }

    /// <summary>Goal tile.</summary>
    public int GoalTile { get; private set; }

    /// <summary>The navigation version the field was built for.</summary>
    public int Version { get; private set; } = -1;

    /// <summary>Search nodes a fresh build settles (budget cost).</summary>
    public int Cost { get; private set; }

    internal long LastUse { get; set; }

    internal static long Key(int layer, int goalTile) => ((long)layer << 32) | (uint)goalTile;

    /// <summary>Whether the local field of a cluster exists.</summary>
    public bool HasLocal(int cluster) => _local[cluster] is not null;

    /// <summary>
    /// The direction code (0-7) to step from <paramref name="tile"/> toward the goal, <c>0xFE</c> at the goal, or
    /// <c>0xFF</c> if the tile is not covered (no local field yet, or not connected).
    /// </summary>
    public byte DirectionAt(NavLayer layer, int tile)
    {
        ArgumentNullException.ThrowIfNull(layer);
        int c = layer.ClusterOf(tile);
        LocalField? local = _local[c];
        return local is null ? NavLayer.TreeNone : local.Dir[layer.ClusterAt(c).LocalIndex(tile, layer.Width)];
    }

    /// <summary>Cost-to-goal of a covered tile, or <see cref="int.MaxValue"/>.</summary>
    public int CostAt(NavLayer layer, int tile)
    {
        ArgumentNullException.ThrowIfNull(layer);
        int c = layer.ClusterOf(tile);
        LocalField? local = _local[c];
        return local is null ? int.MaxValue : local.Cost[layer.ClusterAt(c).LocalIndex(tile, layer.Width)];
    }

    /// <summary>Releases the old contents and reserves the goal cluster's field (sequential code, before <see cref="Build"/>).</summary>
    internal void PrepareBuild()
    {
        Release();
        _spare ??= Take();
    }

    // Safe to run on a worker: touches only this field (after PrepareBuild).
    internal void Build(NavLayer layer, int goalTile, LocalSearch search, MinHeap heap)
    {
        Layer = layer.Index;
        GoalTile = goalTile;
        Version = -1;
        // Indexed by compact node id (the layer's CSR graph), so a field takes 8 bytes per portal.
        int capacity = layer.GraphNodeCount;
        if (_nodeCost.Length < capacity)
        {
            _nodeCost = new int[Math.Max(capacity, _nodeCost.Length * 2)];
            _nodeNext = new int[_nodeCost.Length];
        }

        if (_local.Length != layer.ClusterCount)
        {
            _local = new LocalField?[layer.ClusterCount];
            _localUsed = new int[layer.ClusterCount];
        }

        _nodeCost.AsSpan(0, capacity).Fill(int.MaxValue);
        int[] compactOf = layer.GraphCompactOf;
        int[] adjStart = layer.GraphAdjStart;
        int[] adjNode = layer.GraphAdjNode;
        int[] adjCost = layer.GraphAdjCost;
        int width = layer.Width;
        int cg = layer.ClusterOf(goalTile);
        NavLayer.Cluster goalCluster = layer.ClusterAt(cg);
        LocalField goalField = _spare ?? new LocalField();
        _spare = null;
        search.Dijkstra(layer, goalCluster, goalTile, goalField.Dir, goalField.Cost);
        int settled = search.Expanded;
        _local[cg] = goalField;
        _localUsed[_localUsedCount++] = cg;

        heap.Clear();
        byte goalRegion = layer.RegionOf(goalTile);
        for (int i = 0; i < goalCluster.PortalCount; i++)
        {
            int c = goalField.Cost[goalCluster.LocalIndex(goalCluster.PortalTile[i], width)];
            if (goalCluster.PortalRegion[i] == goalRegion && c != int.MaxValue)
            {
                int node = compactOf[(cg * NavLayer.MaxPortals) + i];
                _nodeCost[node] = c;
                _nodeNext[node] = -2;
                heap.Push((ulong)c, node);
            }
        }

        while (heap.Count > 0)
        {
            int node = heap.Pop(out ulong key);
            if ((int)key != _nodeCost[node])
            {
                continue;
            }

            settled++;
            int cost = (int)key;
            for (int e = adjStart[node]; e < adjStart[node + 1]; e++)
            {
                Relax(heap, adjNode[e], cost + adjCost[e], node);
            }
        }

        // The goal cluster's final field also flows out through its exits (other regions of the cluster).
        FillLocal(layer, cg, goalField, search, goalTile);
        Cost = settled;
        Version = -1;
    }

    internal void MarkBuilt(int version) => Version = version;

    internal void Assign(int layer, int goalTile)
    {
        Release();
        Layer = layer;
        GoalTile = goalTile;
        Version = -1;
    }

    /// <summary>Builds the local field of a cluster if it is missing.</summary>
    internal void EnsureLocal(NavLayer layer, int cluster, LocalSearch search)
    {
        if (_local[cluster] is not null)
        {
            return;
        }

        LocalField field = Take();
        FillLocal(layer, cluster, field, search, -1);
        _local[cluster] = field;
        _localUsed[_localUsedCount++] = cluster;
    }

    private void FillLocal(NavLayer layer, int cluster, LocalField field, LocalSearch search, int goalTile)
    {
        NavLayer.Cluster c = layer.ClusterAt(cluster);
        int width = layer.Width;
        search.Seed(layer, field.Dir);
        if (goalTile >= 0)
        {
            search.AddSource(layer, c, goalTile, 0, NavLayer.TreeRoot, field.Dir);
        }

        int[] compactOf = layer.GraphCompactOf;
        for (int i = 0; i < c.PortalCount; i++)
        {
            int node = compactOf[(cluster * NavLayer.MaxPortals) + i];
            int partner = c.PortalPartner[i];
            if (_nodeCost[node] == int.MaxValue || partner < 0 || _nodeNext[node] != compactOf[partner])
            {
                continue;
            }

            // An exit: the next hop crosses into the neighbouring cluster.
            int tile = c.PortalTile[i];
            int across = layer.PortalTile(partner);
            int dx = (across % width) - (tile % width);
            int dy = (across / width) - (tile / width);
            byte code = (byte)(dx == 1 ? 0 : dx == -1 ? 4 : dy == 1 ? 2 : 6);
            search.AddSource(layer, c, tile, _nodeCost[node], code, field.Dir);
        }

        search.Run(layer, c, field.Dir, field.Cost);
    }

    // Returns the local fields to the cache's shared pool (sequential code only).
    internal void Release()
    {
        for (int i = 0; i < _localUsedCount; i++)
        {
            _pool.Push(_local[_localUsed[i]]!);
            _local[_localUsed[i]] = null;
        }

        _localUsedCount = 0;
        Version = -1;
    }

    private LocalField Take() => _pool.Count > 0 ? _pool.Pop() : new LocalField();

    private void Relax(MinHeap heap, int node, int cost, int from)
    {
        if (cost < _nodeCost[node])
        {
            _nodeCost[node] = cost;
            _nodeNext[node] = from;
            heap.Push((ulong)cost, node);
        }
    }

    /// <summary>A cluster's part of the field.</summary>
    internal sealed class LocalField
    {
        public int[] Cost { get; } = new int[NavLayer.ClusterTiles];

        public byte[] Dir { get; } = new byte[NavLayer.ClusterTiles];
    }
}

/// <summary>
/// Cache of <see cref="FlowField"/>s keyed by (layer, goal tile) (REQ-PATH-04; §8.9 "flow-field caches with obstacle
/// versioning"). Not state: a field's contents are a pure function of its key and the layer, so whether it is cached
/// never changes results, only work. A field that a unit used in this or the previous tick is never evicted, so live
/// groups do not thrash; above <see cref="Capacity"/> the least recently used idle field is recycled.
/// </summary>
public sealed class FlowFieldCache
{
    /// <summary>Fields kept before the least recently used idle one is recycled.</summary>
    public const int Capacity = 64;

    // Lookup only, never iterated (T2D004); _all is the ordered list used for eviction.
    private readonly Dictionary<long, FlowField> _byKey = [];
    private readonly List<FlowField> _all = [];
    private readonly Stack<FlowField> _freeFields = new();
    private readonly Stack<FlowField.LocalField> _localPool = new();
    private readonly LocalSearch _search = new();
    private readonly MinHeap _heap = new(4096);

    /// <summary>Fields built so far (diagnostics).</summary>
    public long Builds { get; private set; }

    /// <summary>Fields in the cache.</summary>
    public int Count => _all.Count;

    /// <summary>Field objects created so far (diagnostics: steady state should stop allocating).</summary>
    public int FieldsAllocated { get; private set; }

    /// <summary>The field for (layer, goal) if it is cached and up to date, else <c>null</c>.</summary>
    public FlowField? Find(NavLayer layer, int goalTile, int version)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return _byKey.TryGetValue(FlowField.Key(layer.Index, goalTile), out FlowField? f) && f.Version == version ? f : null;
    }

    /// <summary>The up-to-date field for (layer, goal), built here if needed (sequential code only).</summary>
    public FlowField Get(NavLayer layer, int goalTile, int version, long tick)
    {
        FlowField field = Reserve(layer, goalTile, tick);
        if (field.Version != version)
        {
            field.PrepareBuild();
            field.Build(layer, goalTile, _search, _heap);
            field.MarkBuilt(version);
            Builds++;
        }

        return field;
    }

    /// <summary>The cached or a recycled field object for (layer, goal), marked used; not built (sequential code only).</summary>
    public FlowField Reserve(NavLayer layer, int goalTile, long tick)
    {
        ArgumentNullException.ThrowIfNull(layer);
        long key = FlowField.Key(layer.Index, goalTile);
        if (!_byKey.TryGetValue(key, out FlowField? field))
        {
            field = Allocate(tick);
            field.Assign(layer.Index, goalTile);
            _byKey[key] = field;
            _all.Add(field);
        }

        field.LastUse = tick;
        return field;
    }

    /// <summary>Prepares a reserved field for <see cref="BuildReserved"/> (sequential code, before the fork).</summary>
    internal static void PrepareReserved(FlowField field) => field.PrepareBuild();

    /// <summary>Builds a reserved field with caller-owned scratch (fork/join jobs: one field per job at a time).</summary>
    internal void BuildReserved(FlowField field, NavLayer layer, int version, LocalSearch search, MinHeap heap)
    {
        field.Build(layer, field.GoalTile, search, heap);
        field.MarkBuilt(version);
    }

    /// <summary>Counts parallel builds done through <see cref="BuildReserved"/> (sequential code only).</summary>
    internal void CountBuilds(int n) => Builds += n;

    /// <summary>Ensures the local field of <paramref name="cluster"/> (sequential code only).</summary>
    public void EnsureLocal(FlowField field, NavLayer layer, int cluster)
    {
        ArgumentNullException.ThrowIfNull(field);
        field.EnsureLocal(layer, cluster, _search);
    }

    private FlowField Allocate(long tick)
    {
        if (_all.Count >= Capacity)
        {
            int victim = -1;
            for (int i = 0; i < _all.Count; i++)
            {
                FlowField f = _all[i];
                if (f.LastUse < tick - 1 && (victim < 0 || f.LastUse < _all[victim].LastUse))
                {
                    victim = i;
                }
            }

            if (victim >= 0)
            {
                FlowField old = _all[victim];
                _all.RemoveAt(victim);
                _byKey.Remove(FlowField.Key(old.Layer, old.GoalTile));
                old.Release();
                _freeFields.Push(old);
            }
        }

        if (_freeFields.Count > 0)
        {
            return _freeFields.Pop();
        }

        FieldsAllocated++;
        return new FlowField(_localPool);
    }
}
