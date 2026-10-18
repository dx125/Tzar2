using System;
using Tzar2.Core.Numerics;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Navigation;

/// <summary>
/// Hierarchical path search over a <see cref="NavLayer"/> (REQ-PATH-02): connect start and goal to their clusters'
/// portals with local Dijkstra, A* over the abstract portal graph with an octile heuristic and a total-order open list
/// (deterministic tie-breaking), refinement by following the precomputed portal trees, then line-of-sight smoothing
/// into world-unit waypoints. One instance per worker job (scratch memory); results are a pure function of the layer
/// and the inputs.
/// </summary>
public sealed class PathSearch
{
    /// <summary>How many tiles ahead smoothing looks for a visible tile.</summary>
    public const int SmoothingWindow = 40;

    private readonly LocalSearch _local = new();
    private readonly byte[] _startTree = new byte[NavLayer.ClusterTiles];
    private readonly byte[] _goalTree = new byte[NavLayer.ClusterTiles];
    private readonly int[] _goalCost = new int[NavLayer.ClusterTiles];
    private readonly int[] _startCost = new int[NavLayer.ClusterTiles];
    private readonly int[] _startPortalCost = new int[NavLayer.MaxPortals];
    private readonly int[] _goalPortalCost = new int[NavLayer.MaxPortals];
    private readonly MinHeap _open = new(1024);
    private NodeState[] _state = [];
    private int _stamp;
    private int[] _tiles = new int[256];
    private int[] _nodes = new int[64];
    private int[] _waypoints = new int[64];

    /// <summary>Search nodes expanded by the last call (budget accounting, REQ-PATH-03).</summary>
    public int Expanded { get; private set; }

    /// <summary>Cost (octile, 1,024 per straight tile) of the last tile path.</summary>
    public int Cost { get; private set; }

    /// <summary>The tile path of the last <see cref="FindTilePath"/> (start and goal included).</summary>
    public ReadOnlySpan<int> Tiles => _tiles.AsSpan(0, TileCount);

    /// <summary>Waypoints of the last <see cref="Smooth"/> as x, y pairs (the start point excluded, the target included).</summary>
    public ReadOnlySpan<int> Waypoints => _waypoints.AsSpan(0, 2 * WaypointCount);

    /// <summary>Number of waypoints.</summary>
    public int WaypointCount { get; private set; }

    private int TileCount { get; set; }

    /// <summary>Short queries (start and goal at most this many tiles apart on each axis) try a windowed A* first.</summary>
    public const int ShortRange = 48;

    /// <summary>Margin in tiles around a short query's bounding box.</summary>
    public const int WindowMargin = 16;

    private const int MaxWindow = ShortRange + (2 * WindowMargin) + 1;

    private readonly int[] _wg = new int[MaxWindow * MaxWindow];
    private readonly byte[] _wdir = new byte[MaxWindow * MaxWindow];
    private readonly int[] _wseen = new int[MaxWindow * MaxWindow];
    private readonly int[] _wclosed = new int[MaxWindow * MaxWindow];
    private int _wstamp;

    /// <summary>
    /// Finds a tile path between two walkable, connected tiles (check <see cref="NavLayer.AreConnected"/> first).
    /// </summary>
    /// <returns><c>false</c> if no path exists.</returns>
    public bool FindTilePath(NavLayer layer, int start, int goal) => Find(layer, start, goal, refine: true);

    /// <summary>
    /// Plans a path: a tile path (<see cref="IsRoute"/> false: short queries and paths within one cluster region) or,
    /// for long queries, the abstract <b>route</b> of portal nodes (<see cref="RouteNodes"/>) without refining it.
    /// A unit follows a route by descending each portal's precomputed tree (ADR 0022), so long paths cost one abstract
    /// search.
    /// </summary>
    /// <returns><c>false</c> if no path exists.</returns>
    public bool FindRoute(NavLayer layer, int start, int goal) => Find(layer, start, goal, refine: false);

    /// <summary>Whether the last <see cref="FindRoute"/> produced a route instead of a tile path.</summary>
    public bool IsRoute { get; private set; }

    /// <summary>The portal nodes of the last route, first to last.</summary>
    public ReadOnlySpan<int> RouteNodes => _nodes.AsSpan(0, IsRoute ? _nodeCount : 0);

    private int _nodeCount;
    private int _goalX;
    private int _goalY;

    private bool Find(NavLayer layer, int start, int goal, bool refine)
    {
        ArgumentNullException.ThrowIfNull(layer);
        Expanded = 0;
        TileCount = 0;
        Cost = 0;
        IsRoute = false;
        if (!layer.IsWalkable(start) || !layer.IsWalkable(goal))
        {
            return false;
        }

        if (start == goal)
        {
            AddTile(start);
            return true;
        }

        int width0 = layer.Width;
        if (Math.Abs((start % width0) - (goal % width0)) <= ShortRange && Math.Abs((start / width0) - (goal / width0)) <= ShortRange
            && WindowSearch(layer, start, goal))
        {
            return true;
        }

        TileCount = 0;
        int cs = layer.ClusterOf(start);
        int cg = layer.ClusterOf(goal);
        NavLayer.Cluster startCluster = layer.ClusterAt(cs);
        NavLayer.Cluster goalCluster = layer.ClusterAt(cg);
        if (cs == cg && layer.RegionOf(start) == layer.RegionOf(goal))
        {
            // Within one region of one cluster: the start tree already holds the path.
            _local.Dijkstra(layer, startCluster, start, _startTree, _startCost);
            Expanded += _local.Expanded;
            Cost = _startCost[startCluster.LocalIndex(goal, layer.Width)];
            AppendTreeTrace(layer, startCluster, goal, _startTree, reverse: true);
            return true;
        }

        if (refine)
        {
            // Refinement traces the start and goal trees, so search them.
            _local.Dijkstra(layer, startCluster, start, _startTree, _startCost);
            Expanded += _local.Expanded;
            PortalCostsFromSearch(layer, startCluster, _startCost, _startPortalCost);
            _local.Dijkstra(layer, goalCluster, goal, _goalTree, _goalCost);
            Expanded += _local.Expanded;
            PortalCostsFromSearch(layer, goalCluster, _goalCost, _goalPortalCost);
        }
        else
        {
            // A route needs only the start's and goal's costs to their clusters' portals: read them off the portals'
            // precomputed trees, and charge what the two cluster searches would expand (ADR 0080).
            Expanded += PortalCostsFromTrees(layer, startCluster, start, _startPortalCost);
            Expanded += PortalCostsFromTrees(layer, goalCluster, goal, _goalPortalCost);
        }

        if (!AbstractSearch(layer, start, goal, cs, startCluster, cg, goalCluster, out int virtualCost))
        {
            return false;
        }

        Cost = virtualCost;
        CollectNodes(layer);
        if (!refine)
        {
            IsRoute = true;
            return true;
        }

        Refine(layer, startCluster, goalCluster);
        return true;
    }

    /// <summary>
    /// Smooths the last tile path into waypoints from (<paramref name="startX"/>, <paramref name="startY"/>) to
    /// (<paramref name="targetX"/>, <paramref name="targetY"/>), keeping a corridor of <paramref name="margin"/> world
    /// units clear on both sides of every segment.
    /// </summary>
    public void Smooth(NavLayer layer, int startX, int startY, int targetX, int targetY, int margin)
    {
        ArgumentNullException.ThrowIfNull(layer);
        WaypointCount = 0;
        int n = TileCount - 1;
        int ax = startX;
        int ay = startY;
        int i = 0;
        while (true)
        {
            int steps = 0;
            if (i >= n || CorridorClear(layer, ax, ay, targetX, targetY, margin, out steps))
            {
                Expanded += steps / 8;
                AddWaypoint(targetX, targetY);
                return;
            }

            Expanded += steps / 8;
            int best = i + 1;
            int limit = Math.Min(n, i + SmoothingWindow);
            for (int k = i + 2; k <= limit; k++)
            {
                bool clear = CorridorClear(layer, ax, ay, layer.CenterX(_tiles[k]), layer.CenterY(_tiles[k]), margin, out steps);
                Expanded += steps / 8;
                if (!clear)
                {
                    break;
                }

                best = k;
            }

            if (best >= n)
            {
                AddWaypoint(targetX, targetY);
                return;
            }

            ax = layer.CenterX(_tiles[best]);
            ay = layer.CenterY(_tiles[best]);
            AddWaypoint(ax, ay);
            i = best;
        }
    }

    /// <summary>
    /// Whether a unit center can travel the segment with <paramref name="margin"/> units of clearance on both sides:
    /// the center line and two parallel lines must cross walkable tiles only, without cutting corners.
    /// </summary>
    public static bool CorridorClear(NavLayer layer, int x0, int y0, int x1, int y1, int margin, out int steps)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (!LineWalkable(layer, x0, y0, x1, y1, out steps))
        {
            return false;
        }

        long dx = (long)x1 - x0;
        long dy = (long)y1 - y0;
        long length = IntMath.ISqrt((dx * dx) + (dy * dy));
        if (margin <= 0 || length == 0)
        {
            return true;
        }

        int px = (int)(-dy * margin / length);
        int py = (int)(dx * margin / length);
        bool left = LineWalkable(layer, x0 + px, y0 + py, x1 + px, y1 + py, out int s1);
        steps += s1;
        if (!left)
        {
            return false;
        }

        bool right = LineWalkable(layer, x0 - px, y0 - py, x1 - px, y1 - py, out int s2);
        steps += s2;
        return right;
    }

    /// <summary>
    /// Whether the segment between two unit-center points crosses walkable anchor tiles only (supercover traversal;
    /// at an exact corner crossing both side tiles must be walkable).
    /// </summary>
    public static bool LineWalkable(NavLayer layer, int x0, int y0, int x1, int y1, out int steps)
    {
        ArgumentNullException.ThrowIfNull(layer);
        steps = 0;
        const int T = MapState.UnitsPerTile;
        long ax = (long)x0 - layer.PointOffset;
        long ay = (long)y0 - layer.PointOffset;
        long bx = (long)x1 - layer.PointOffset;
        long by = (long)y1 - layer.PointOffset;
        if (ax < 0 || ay < 0 || bx < 0 || by < 0)
        {
            return false;
        }

        int tx = (int)(ax / T);
        int ty = (int)(ay / T);
        int ex = (int)(bx / T);
        int ey = (int)(by / T);
        if (!layer.IsWalkable(tx, ty) || !layer.IsWalkable(ex, ey))
        {
            return false;
        }

        long dx = bx - ax;
        long dy = by - ay;
        int stepX = Math.Sign(dx);
        int stepY = Math.Sign(dy);
        long adx = Math.Abs(dx);
        long ady = Math.Abs(dy);
        long boundaryX = stepX > 0 ? ((long)tx + 1) * T : (long)tx * T;
        long boundaryY = stepY > 0 ? ((long)ty + 1) * T : (long)ty * T;
        int guard = Math.Abs(ex - tx) + Math.Abs(ey - ty) + 2;
        while (tx != ex || ty != ey)
        {
            if (++steps > guard)
            {
                return false; // unreachable: the traversal takes at most |dx| + |dy| tile steps
            }

            long distX = Math.Abs(boundaryX - ax);
            long distY = Math.Abs(boundaryY - ay);
            long cx = stepX == 0 ? long.MaxValue : distX * ady;
            long cy = stepY == 0 ? long.MaxValue : distY * adx;
            if (cx < cy)
            {
                tx += stepX;
                boundaryX += stepX * (long)T;
            }
            else if (cy < cx)
            {
                ty += stepY;
                boundaryY += stepY * (long)T;
            }
            else
            {
                if (!layer.IsWalkable(tx + stepX, ty) || !layer.IsWalkable(tx, ty + stepY))
                {
                    return false;
                }

                tx += stepX;
                ty += stepY;
                boundaryX += stepX * (long)T;
                boundaryY += stepY * (long)T;
            }

            if (!layer.IsWalkable(tx, ty))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a stored route plan (portal node ids, then their tiles; see <c>MotionMode.Route</c>) is still valid on
    /// <paramref name="layer"/> from route index <paramref name="from"/> on: every node exists and still names the same
    /// portal tile, and consecutive nodes are still joined (partners, or connected within their cluster). A pure function
    /// of the plan and the layer, so a change elsewhere on the map never invalidates the plan.
    /// </summary>
    public static bool RouteValid(NavLayer layer, ReadOnlySpan<int> plan, int from)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (plan.IsEmpty || (plan.Length & 1) != 0)
        {
            return false;
        }

        int len = plan.Length / 2;
        for (int j = Math.Max(0, from); j < len; j++)
        {
            int node = plan[j];
            if ((uint)node >= (uint)layer.NodeCapacity)
            {
                return false;
            }

            NavLayer.Cluster cluster = layer.ClusterAt(node / NavLayer.MaxPortals);
            int index = node % NavLayer.MaxPortals;
            if (index >= cluster.PortalCount || cluster.PortalTile[index] != plan[len + j])
            {
                return false;
            }

            if (j + 1 < len)
            {
                int next = plan[j + 1];
                bool joined = cluster.PortalPartner[index] == next
                    || (next / NavLayer.MaxPortals == node / NavLayer.MaxPortals && next % NavLayer.MaxPortals < cluster.PortalCount
                        && cluster.IntraCost[(index * NavLayer.MaxPortals) + (next % NavLayer.MaxPortals)] >= 0);
                if (!joined)
                {
                    return false;
                }
            }
        }

        return true;
    }

    // Per portal of the cluster: the cost from a cluster search's result (int.MaxValue = unreached).
    private static void PortalCostsFromSearch(NavLayer layer, NavLayer.Cluster cluster, int[] costs, int[] portalCost)
    {
        for (int i = 0; i < cluster.PortalCount; i++)
        {
            portalCost[i] = costs[cluster.LocalIndex(cluster.PortalTile[i], layer.Width)];
        }
    }

    // Per portal of the cluster in the tile's region: the cost between the tile and the portal, read by walking the
    // portal's tree from the tile (a shortest-path tree, and steps are symmetric, so this is exactly what a cluster
    // search from the tile finds). Other portals get int.MaxValue. Returns the tile's region size: the tiles a cluster
    // search from the tile settles, i.e. its Expanded count.
    private static int PortalCostsFromTrees(NavLayer layer, NavLayer.Cluster cluster, int tile, int[] portalCost)
    {
        int width = layer.Width;
        byte region = layer.RegionOf(tile);
        int start = cluster.LocalIndex(tile, width);
        for (int i = 0; i < cluster.PortalCount; i++)
        {
            if (cluster.PortalRegion[i] != region)
            {
                portalCost[i] = int.MaxValue;
                continue;
            }

            byte[] tree = cluster.Trees[i];
            int local = start;
            int cost = 0;
            portalCost[i] = int.MaxValue;

            // A tree path visits each cluster tile at most once; the bound only guards against a corrupt tree.
            for (int steps = 0; steps <= NavLayer.ClusterTiles; steps++)
            {
                byte code = tree[local];
                if (code >= NavLayer.TreeRoot)
                {
                    portalCost[i] = code == NavLayer.TreeRoot ? cost : int.MaxValue;
                    break;
                }

                cost += (code & 1) == 0 ? NavLayer.StraightCost : NavLayer.DiagonalCost;
                local += NavLayer.DirX[code] + (NavLayer.DirY[code] * NavLayer.ClusterSize);
            }
        }

        int settled = 0;
        for (int y = cluster.Y; y < cluster.Y + cluster.H; y++)
        {
            int row = y * width;
            for (int x = cluster.X; x < cluster.X + cluster.W; x++)
            {
                settled += layer.RegionOf(row + x) == region ? 1 : 0;
            }
        }

        return settled;
    }

    /// <summary>Octile distance between two tiles in cost units.</summary>
    public static int Octile(int width, int a, int b)
    {
        int dx = Math.Abs((a % width) - (b % width));
        int dy = Math.Abs((a / width) - (b / width));
        return (NavLayer.StraightCost * Math.Max(dx, dy)) + ((NavLayer.DiagonalCost - NavLayer.StraightCost) * Math.Min(dx, dy));
    }

    // Plain A* inside the bounding box of start and goal plus a margin: optimal for most short queries.
    private bool WindowSearch(NavLayer layer, int start, int goal)
    {
        int width = layer.Width;
        int sx = start % width;
        int sy = start / width;
        int gx = goal % width;
        int gy = goal / width;
        int x0 = Math.Max(0, Math.Min(sx, gx) - WindowMargin);
        int y0 = Math.Max(0, Math.Min(sy, gy) - WindowMargin);
        int x1 = Math.Min(width - 1, Math.Max(sx, gx) + WindowMargin);
        int y1 = Math.Min(layer.Height - 1, Math.Max(sy, gy) + WindowMargin);
        int ww = x1 - x0 + 1;
        int wh = y1 - y0 + 1;
        if (++_wstamp == int.MaxValue)
        {
            Array.Clear(_wseen);
            Array.Clear(_wclosed);
            _wstamp = 1;
        }

        _open.Clear();
        int ls = ((sy - y0) * ww) + (sx - x0);
        int lg = ((gy - y0) * ww) + (gx - x0);
        _wseen[ls] = _wstamp;
        _wg[ls] = 0;
        _wdir[ls] = NavLayer.TreeRoot;
        _open.Push((ulong)(uint)Octile(width, start, goal) << 32, ls);
        while (_open.Count > 0)
        {
            int local = _open.Pop(out _);
            if (_wclosed[local] == _wstamp)
            {
                continue;
            }

            _wclosed[local] = _wstamp;
            Expanded++;
            if (local == lg)
            {
                Cost = _wg[local];

                // Walk back along the parent directions, then reverse.
                int t = local;
                while (true)
                {
                    AddTile(((y0 + (t / ww)) * width) + x0 + (t % ww));
                    byte code = _wdir[t];
                    if (code == NavLayer.TreeRoot)
                    {
                        break;
                    }

                    t += NavLayer.DirX[code] + (NavLayer.DirY[code] * ww);
                }

                Array.Reverse(_tiles, 0, TileCount);
                return true;
            }

            int lx = local % ww;
            int ly = local / ww;
            int g = _wg[local];
            for (int dir = 0; dir < 8; dir++)
            {
                int nlx = lx + NavLayer.DirX[dir];
                int nly = ly + NavLayer.DirY[dir];
                if ((uint)nlx >= (uint)ww || (uint)nly >= (uint)wh || !layer.CanStep(x0 + lx, y0 + ly, dir))
                {
                    continue;
                }

                int n = (nly * ww) + nlx;
                if (_wclosed[n] == _wstamp)
                {
                    continue;
                }

                int ng = g + ((dir & 1) == 0 ? NavLayer.StraightCost : NavLayer.DiagonalCost);
                if (_wseen[n] == _wstamp && ng >= _wg[n])
                {
                    continue;
                }

                _wseen[n] = _wstamp;
                _wg[n] = ng;
                _wdir[n] = (byte)((dir + 4) & 7);
                int h = Octile(width, ((y0 + nly) * width) + x0 + nlx, goal);
                _open.Push(((ulong)(uint)(ng + h) << 32) | (uint)h, n);
            }
        }

        return false;
    }

    // A* over the layer's compact portal graph (CSR); compact ids preserve the node-id order, so ties break the same.
    private bool AbstractSearch(NavLayer layer, int start, int goal, int cs, NavLayer.Cluster startCluster, int cg, NavLayer.Cluster goalCluster, out int cost)
    {
        cost = 0;
        int n = layer.GraphNodeCount;
        int capacity = n + 1;
        if (_state.Length < capacity)
        {
            _state = new NodeState[Math.Max(capacity, _state.Length * 2)];
            _stamp = 0;
        }

        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_state);
            _stamp = 1;
        }

        NodeState[] state = _state;
        int stamp = _stamp;

        int virtualGoal = n;
        int width = layer.Width;
        _goalX = goal % width;
        _goalY = goal / width;
        _open.Clear();
        int[] compactOf = layer.GraphCompactOf;
        int[] adjStart = layer.GraphAdjStart;
        int[] adjNode = layer.GraphAdjNode;
        int[] adjCost = layer.GraphAdjCost;
        int[] adjPos = layer.GraphAdjPos;
        int[] nodeCluster = layer.GraphCluster;
        byte[] nodeRegion = layer.GraphRegion;
        int[] nodeId = layer.GraphNodeId;
        byte startRegion = layer.RegionOf(start);
        for (int i = 0; i < startCluster.PortalCount; i++)
        {
            int c = _startPortalCost[i];
            if (startCluster.PortalRegion[i] == startRegion && c != int.MaxValue)
            {
                Relax(compactOf[(cs * NavLayer.MaxPortals) + i], c, -1, startCluster.PortalX[i], startCluster.PortalY[i]);
            }
        }

        byte goalRegion = layer.RegionOf(goal);
        while (_open.Count > 0)
        {
            int node = _open.Pop(out _);
            ref NodeState current = ref state[node];
            if (current.Closed == stamp)
            {
                continue;
            }

            current.Closed = stamp;
            Expanded++;
            if (node == virtualGoal)
            {
                cost = current.G;
                return true;
            }

            int g = current.G;
            if (nodeCluster[node] == cg && nodeRegion[node] == goalRegion)
            {
                int toGoal = _goalPortalCost[nodeId[node] % NavLayer.MaxPortals];
                if (toGoal != int.MaxValue)
                {
                    Relax(virtualGoal, g + toGoal, node, _goalX, _goalY);
                }
            }

            for (int e = adjStart[node]; e < adjStart[node + 1]; e++)
            {
                int target = adjNode[e];
                if (state[target].Closed != stamp)
                {
                    int pos = adjPos[e];
                    Relax(target, g + adjCost[e], node, pos & 0xFFFF, pos >> 16);
                }
            }
        }

        return false;
    }

    // The heuristic (octile distance from the node's tile to the goal) is computed only for improving relaxations.
    private void Relax(int node, int g, int parent, int x, int y)
    {
        ref NodeState s = ref _state[node];
        if (s.Seen == _stamp && g >= s.G)
        {
            return;
        }

        int dx = Math.Abs(x - _goalX);
        int dy = Math.Abs(y - _goalY);
        int h = dx > dy
            ? (NavLayer.StraightCost * dx) + ((NavLayer.DiagonalCost - NavLayer.StraightCost) * dy)
            : (NavLayer.StraightCost * dy) + ((NavLayer.DiagonalCost - NavLayer.StraightCost) * dx);
        s.Seen = _stamp;
        s.G = g;
        s.Parent = parent;
        _open.Push(((ulong)(uint)(g + h) << 32) | (uint)h, node);
    }

    // Abstract nodes of the found path, first to last.
    private void CollectNodes(NavLayer layer)
    {
        int count = 0;
        int[] nodeId = layer.GraphNodeId;
        for (int n = _state[layer.GraphNodeCount].Parent; n >= 0; n = _state[n].Parent)
        {
            if (count == _nodes.Length)
            {
                Array.Resize(ref _nodes, _nodes.Length * 2);
            }

            _nodes[count++] = nodeId[n];
        }

        Array.Reverse(_nodes, 0, count);
        _nodeCount = count;
    }

    private void Refine(NavLayer layer, NavLayer.Cluster startCluster, NavLayer.Cluster goalCluster)
    {
        int count = _nodeCount;

        // Start → first portal: the start tree leads from the portal back to the start, so reverse it.
        AppendTreeTrace(layer, startCluster, layer.PortalTile(_nodes[0]), _startTree, reverse: true);
        for (int k = 0; k + 1 < count; k++)
        {
            int a = _nodes[k];
            int b = _nodes[k + 1];
            if (layer.PortalPartner(a) == b)
            {
                AddTile(layer.PortalTile(b));
            }
            else
            {
                NavLayer.Cluster c = layer.ClusterAt(a / NavLayer.MaxPortals);
                AppendTreeTraceFrom(layer, c, layer.PortalTile(a), c.Trees[b % NavLayer.MaxPortals]);
            }
        }

        // Last portal → goal along the goal tree.
        AppendTreeTraceFrom(layer, goalCluster, layer.PortalTile(_nodes[count - 1]), _goalTree);
    }

    // Appends the tiles after `from` up to the tree root.
    private void AppendTreeTraceFrom(NavLayer layer, NavLayer.Cluster cluster, int from, byte[] tree)
    {
        int width = layer.Width;
        int tile = from;
        while (true)
        {
            byte code = tree[cluster.LocalIndex(tile, width)];
            if (code >= NavLayer.TreeRoot)
            {
                return;
            }

            tile += NavLayer.DirX[code] + (NavLayer.DirY[code] * width);
            AddTile(tile);
        }
    }

    // Appends the path root → `to` (reverse = true; the tree runs from `to` to the root).
    private void AppendTreeTrace(NavLayer layer, NavLayer.Cluster cluster, int to, byte[] tree, bool reverse)
    {
        int begin = TileCount;
        AddTile(to);
        AppendTreeTraceFrom(layer, cluster, to, tree);
        if (reverse)
        {
            Array.Reverse(_tiles, begin, TileCount - begin);
        }

        // Do not repeat the tile the previous segment ended on.
        if (begin > 0 && _tiles[begin - 1] == _tiles[begin])
        {
            Array.Copy(_tiles, begin + 1, _tiles, begin, TileCount - begin - 1);
            TileCount--;
        }
    }

    private void AddTile(int tile)
    {
        if (TileCount == _tiles.Length)
        {
            Array.Resize(ref _tiles, _tiles.Length * 2);
        }

        _tiles[TileCount++] = tile;
    }

    private void AddWaypoint(int x, int y)
    {
        if (2 * (WaypointCount + 1) > _waypoints.Length)
        {
            Array.Resize(ref _waypoints, _waypoints.Length * 2);
        }

        _waypoints[2 * WaypointCount] = x;
        _waypoints[(2 * WaypointCount) + 1] = y;
        WaypointCount++;
    }

    // Search state of one abstract node (one cache line holds four).
    private struct NodeState
    {
        public int Seen;
        public int Closed;
        public int G;
        public int Parent;
    }
}
