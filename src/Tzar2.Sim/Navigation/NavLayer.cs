using System;
using Tzar2.Content.Compiled;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Navigation;

/// <summary>
/// The navigation data of one movement class (a set of equivalent locomotors and a clearance; TDD §9.2, ADR 0021):
/// walkability with clearance, HPA* clusters with regions and portals, intra-cluster portal costs with their path
/// trees, and connected components (REQ-PATH-01/02).
/// </summary>
/// <remarks>
/// <para><b>Walkable</b>: tile (x, y) is walkable for clearance c if all tiles of the c×c square whose top-left is
/// (x, y) are passable for the layer's locomotor and free of blockers. A unit's center stands at the square's center,
/// so <see cref="PointOffset"/> = (c − 1) × 1,024 units converts centers to anchor tiles.</para>
/// <para><b>Moves</b> are 8-connected without corner cutting: a diagonal step needs both orthogonal neighbours
/// walkable. Costs are integer octile: <see cref="StraightCost"/> = 1,024, <see cref="DiagonalCost"/> = 1,448.</para>
/// <para><b>Clusters</b> are 16×16 tiles. A cluster's walkable tiles split into <b>regions</b> (connected within the
/// cluster). Every maximal run of walkable tile pairs across a cluster border is an <b>entrance</b> with one
/// transition (runs shorter than 6 tiles) or two (at the run's ends), as in HPA*; each transition is a pair of
/// <b>portal</b> nodes, one per side. A portal's node id is <c>cluster × 32 + index</c>. Within a cluster, each
/// portal keeps a Dijkstra tree over the cluster (direction to the parent per tile), which gives the costs to the
/// other portals of its region and refines abstract edges without searching.</para>
/// <para><b>Components</b> (REQ-PATH-01) are labelled over the region graph (regions joined by portal pairs) with
/// union-find and canonical numbering, so "is the goal reachable?" is two array reads. The whole structure is a pure
/// function of the walkability grid: an incremental update gives the same result as a rebuild (tested), which is
/// why navigation is not part of snapshots.</para>
/// </remarks>
public sealed class NavLayer
{
    /// <summary>Cluster side in tiles (REQ-PATH-02).</summary>
    public const int ClusterSize = 16;

    /// <summary>Most portals per cluster side.</summary>
    public const int MaxPortalsPerSide = 8;

    /// <summary>Most portals per cluster.</summary>
    public const int MaxPortals = 4 * MaxPortalsPerSide;

    /// <summary>Cost of a straight step.</summary>
    public const int StraightCost = 1024;

    /// <summary>Cost of a diagonal step (1,024 × √2, rounded).</summary>
    public const int DiagonalCost = 1448;

    /// <summary>"No region".</summary>
    public const byte NoRegion = 0xFF;

    /// <summary>Tree code: the tile is the root.</summary>
    internal const byte TreeRoot = 0xFE;

    /// <summary>Tree code: not reached.</summary>
    internal const byte TreeNone = 0xFF;

    internal const int ClusterTiles = ClusterSize * ClusterSize;

    /// <summary>Direction codes 0-7: E, SE, S, SW, W, NW, N, NE (clockwise from east, y grows south).</summary>
    internal static ReadOnlySpan<sbyte> DirX => [1, 1, 0, -1, -1, -1, 0, 1];

    internal static ReadOnlySpan<sbyte> DirY => [0, 1, 1, 1, 0, -1, -1, -1];

    private readonly MapState _map;
    private readonly byte[] _walkable;
    private readonly byte[] _tileRegion;
    private readonly Cluster[] _clusters;
    private readonly int[] _regionComponent;
    private readonly int[] _unionParent;
    private readonly bool[] _dirtyCluster;
    private readonly int[] _dirtyList;
    private readonly LocalSearch _search = new();
    private int _dirtyCount;

    internal NavLayer(int index, MapState map, LocomotorMask locomotors, Locomotor representative, int clearance)
    {
        Index = index;
        _map = map;
        Locomotors = locomotors;
        Representative = representative;
        Clearance = clearance;
        Width = map.Width;
        Height = map.Height;
        ClustersX = (Width + ClusterSize - 1) / ClusterSize;
        ClustersY = (Height + ClusterSize - 1) / ClusterSize;
        PointOffset = (clearance - 1) * (MapState.UnitsPerTile / 2);
        _walkable = new byte[Width * Height];
        _tileRegion = new byte[Width * Height];
        _clusters = new Cluster[ClustersX * ClustersY];
        for (int i = 0; i < _clusters.Length; i++)
        {
            int cx = i % ClustersX;
            int cy = i / ClustersX;
            _clusters[i] = new Cluster(cx * ClusterSize, cy * ClusterSize, Math.Min(ClusterSize, Width - (cx * ClusterSize)), Math.Min(ClusterSize, Height - (cy * ClusterSize)));
        }

        _regionComponent = new int[_clusters.Length * 256];
        _unionParent = new int[_clusters.Length * 256];
        _dirtyCluster = new bool[_clusters.Length];
        _dirtyList = new int[_clusters.Length];
        RebuildAll();
    }

    /// <summary>Position in <see cref="NavigationGrid.Layers"/>.</summary>
    public int Index { get; }

    /// <summary>The locomotors that use this layer.</summary>
    public LocomotorMask Locomotors { get; }

    /// <summary>The locomotor whose passability bit defines the layer.</summary>
    public Locomotor Representative { get; }

    /// <summary>Clearance in tiles.</summary>
    public int Clearance { get; }

    /// <summary>Map width in tiles.</summary>
    public int Width { get; }

    /// <summary>Map height in tiles.</summary>
    public int Height { get; }

    /// <summary>Clusters per row.</summary>
    public int ClustersX { get; }

    /// <summary>Cluster rows.</summary>
    public int ClustersY { get; }

    /// <summary>Number of clusters.</summary>
    public int ClusterCount => _clusters.Length;

    /// <summary>Size of the abstract node id space (<see cref="ClusterCount"/> × <see cref="MaxPortals"/>).</summary>
    public int NodeCapacity => _clusters.Length * MaxPortals;

    /// <summary>Number of connected components.</summary>
    public int ComponentCount { get; private set; }

    /// <summary>Portals (abstract nodes) in use.</summary>
    public int PortalCount { get; private set; }

    /// <summary>World units subtracted from a unit center before converting it to its anchor tile.</summary>
    public int PointOffset { get; }

    /// <summary>The walkability grid (1 = walkable), row-major.</summary>
    public ReadOnlySpan<byte> Walkable => _walkable;

    /// <summary>Whether tile (<paramref name="x"/>, <paramref name="y"/>) is on the map and walkable.</summary>
    public bool IsWalkable(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height && _walkable[(y * Width) + x] != 0;

    /// <summary>Whether tile index <paramref name="tile"/> is walkable.</summary>
    public bool IsWalkable(int tile) => _walkable[tile] != 0;

    /// <summary>Anchor tile column of a unit center X (clamped to the map).</summary>
    public int TileX(int x) => Math.Clamp((x - PointOffset) / MapState.UnitsPerTile, 0, Width - 1);

    /// <summary>Anchor tile row of a unit center Y (clamped to the map).</summary>
    public int TileY(int y) => Math.Clamp((y - PointOffset) / MapState.UnitsPerTile, 0, Height - 1);

    /// <summary>Anchor tile index of a unit center.</summary>
    public int TileOf(int x, int y) => (TileY(y) * Width) + TileX(x);

    /// <summary>The unit-center point of a tile (inverse of <see cref="TileOf"/>).</summary>
    public int CenterX(int tile) => ((tile % Width) * MapState.UnitsPerTile) + (Clearance * (MapState.UnitsPerTile / 2));

    /// <summary>The unit-center point of a tile.</summary>
    public int CenterY(int tile) => ((tile / Width) * MapState.UnitsPerTile) + (Clearance * (MapState.UnitsPerTile / 2));

    /// <summary>Whether a unit center point is on a walkable anchor tile.</summary>
    public bool IsWalkablePoint(int x, int y)
    {
        int ax = x - PointOffset;
        int ay = y - PointOffset;
        return ax >= 0 && ay >= 0 && IsWalkable(ax / MapState.UnitsPerTile, ay / MapState.UnitsPerTile);
    }

    /// <summary>Connected component of a tile, or -1 if it is not walkable.</summary>
    public int ComponentOf(int tile)
    {
        byte region = _tileRegion[tile];
        return region == NoRegion ? -1 : _regionComponent[(ClusterOf(tile) * 256) + region];
    }

    /// <summary>Whether two tiles are walkable and connected (REQ-PATH-01: O(1)).</summary>
    public bool AreConnected(int a, int b)
    {
        int ca = ComponentOf(a);
        return ca >= 0 && ca == ComponentOf(b);
    }

    /// <summary>The cluster of a tile.</summary>
    public int ClusterOf(int tile) => ((tile / Width / ClusterSize) * ClustersX) + (tile % Width / ClusterSize);

    /// <summary>Region of a tile within its cluster, or <see cref="NoRegion"/>.</summary>
    public byte RegionOf(int tile) => _tileRegion[tile];

    // ----- cluster data access for the searches -----------------------------------------------------------------

    internal Cluster ClusterAt(int cluster) => _clusters[cluster];

    internal int ComponentOfRegion(int cluster, byte region) => _regionComponent[(cluster * 256) + region];

    internal int PortalTile(int node) => _clusters[node / MaxPortals].PortalTile[node % MaxPortals];

    internal int PortalPartner(int node) => _clusters[node / MaxPortals].PortalPartner[node % MaxPortals];

    // ----- updates ---------------------------------------------------------------------------------------------

    /// <summary>Recomputes walkability around a tile whose passability changed and marks the clusters dirty.</summary>
    internal void TileChanged(int tx, int ty)
    {
        for (int y = Math.Max(0, ty - Clearance + 1); y <= ty; y++)
        {
            for (int x = Math.Max(0, tx - Clearance + 1); x <= tx; x++)
            {
                byte walkable = ComputeWalkable(x, y) ? (byte)1 : (byte)0;
                int i = (y * Width) + x;
                if (_walkable[i] != walkable)
                {
                    _walkable[i] = walkable;
                    MarkDirty(ClusterOf(i));
                }
            }
        }
    }

    /// <summary>Rebuilds every dirty cluster, its neighbours' portals and the components.</summary>
    internal void ApplyChanges()
    {
        if (_dirtyCount == 0)
        {
            return;
        }

        // Regions change only inside dirty clusters; portals and intra costs also in their 4-neighbours.
        int dirty = _dirtyCount;
        for (int k = 0; k < dirty; k++)
        {
            BuildRegions(_dirtyList[k]);
        }

        for (int k = 0; k < dirty; k++)
        {
            int c = _dirtyList[k];
            int cx = c % ClustersX;
            int cy = c / ClustersX;
            if (cx > 0) { MarkDirty(c - 1); }
            if (cx < ClustersX - 1) { MarkDirty(c + 1); }
            if (cy > 0) { MarkDirty(c - ClustersX); }
            if (cy < ClustersY - 1) { MarkDirty(c + ClustersX); }
        }

        // Ascending order keeps the rebuild independent of the order in which tiles changed.
        Array.Sort(_dirtyList, 0, _dirtyCount);
        for (int k = 0; k < _dirtyCount; k++)
        {
            BuildPortals(_dirtyList[k]);
        }

        for (int k = 0; k < _dirtyCount; k++)
        {
            int c = _dirtyList[k];
            LinkPartners(c);
            int cx = c % ClustersX;
            int cy = c / ClustersX;
            if (cx > 0) { LinkPartners(c - 1); }
            if (cx < ClustersX - 1) { LinkPartners(c + 1); }
            if (cy > 0) { LinkPartners(c - ClustersX); }
            if (cy < ClustersY - 1) { LinkPartners(c + ClustersX); }
        }

        for (int k = 0; k < _dirtyCount; k++)
        {
            BuildIntra(_dirtyList[k]);
            _dirtyCluster[_dirtyList[k]] = false;
        }

        _dirtyCount = 0;
        BuildComponents();
    }

    /// <summary>Recomputes everything from the map (construction; also the reference for incremental tests).</summary>
    internal void RebuildAll()
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                _walkable[(y * Width) + x] = ComputeWalkable(x, y) ? (byte)1 : (byte)0;
            }
        }

        for (int c = 0; c < _clusters.Length; c++)
        {
            BuildRegions(c);
        }

        for (int c = 0; c < _clusters.Length; c++)
        {
            BuildPortals(c);
        }

        for (int c = 0; c < _clusters.Length; c++)
        {
            LinkPartners(c);
        }

        for (int c = 0; c < _clusters.Length; c++)
        {
            BuildIntra(c);
            _dirtyCluster[c] = false;
        }

        _dirtyCount = 0;
        BuildComponents();
    }

    /// <summary>A digest of the derived data (regions, portals, costs, components) for equivalence tests.</summary>
    internal ulong Digest()
    {
        var hasher = new Tzar2.Core.Hashing.StateHasher();
        hasher.AddSpan<byte>(_walkable).AddSpan<byte>(_tileRegion).Add(ComponentCount);
        for (int ci = 0; ci < _clusters.Length; ci++)
        {
            Cluster c = _clusters[ci];
            hasher.Add(c.RegionCount).Add(c.PortalCount);
            hasher.AddSpan<int>(c.PortalTile.AsSpan(0, c.PortalCount)).AddSpan<int>(c.PortalPartner.AsSpan(0, c.PortalCount));
            for (int i = 0; i < c.PortalCount; i++)
            {
                hasher.AddSpan<int>(c.IntraCost.AsSpan(i * MaxPortals, c.PortalCount));
            }

            for (int r = 0; r < c.RegionCount; r++)
            {
                hasher.Add(_regionComponent[(ci * 256) + r]);
            }
        }

        return hasher.GetCurrentHash();
    }

    private void MarkDirty(int cluster)
    {
        if (!_dirtyCluster[cluster])
        {
            _dirtyCluster[cluster] = true;
            _dirtyList[_dirtyCount++] = cluster;
        }
    }

    private bool ComputeWalkable(int x, int y)
    {
        if (x + Clearance > Width || y + Clearance > Height)
        {
            return false;
        }

        for (int dy = 0; dy < Clearance; dy++)
        {
            for (int dx = 0; dx < Clearance; dx++)
            {
                if (!_map.IsPassable(Representative, x + dx, y + dy))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Whether a step from tile (x, y) in direction <paramref name="dir"/> is allowed (no corner cutting).</summary>
    internal bool CanStep(int x, int y, int dir)
    {
        int nx = x + DirX[dir];
        int ny = y + DirY[dir];
        if (!IsWalkable(nx, ny))
        {
            return false;
        }

        return (dir & 1) == 0 || (IsWalkable(nx, y) && IsWalkable(x, ny));
    }

    private void BuildRegions(int c)
    {
        Cluster cluster = _clusters[c];
        Span<int> queue = stackalloc int[ClusterTiles];
        for (int ly = 0; ly < cluster.H; ly++)
        {
            _tileRegion.AsSpan(((cluster.Y + ly) * Width) + cluster.X, cluster.W).Fill(NoRegion);
        }

        int regions = 0;
        for (int ly = 0; ly < cluster.H; ly++)
        {
            for (int lx = 0; lx < cluster.W; lx++)
            {
                int tile = ((cluster.Y + ly) * Width) + cluster.X + lx;
                if (_walkable[tile] == 0 || _tileRegion[tile] != NoRegion)
                {
                    continue;
                }

                byte region = (byte)regions++;
                int head = 0;
                int tail = 0;
                _tileRegion[tile] = region;
                queue[tail++] = tile;
                while (head < tail)
                {
                    int t = queue[head++];
                    int x = t % Width;
                    int y = t / Width;
                    for (int dir = 0; dir < 8; dir++)
                    {
                        int nx = x + DirX[dir];
                        int ny = y + DirY[dir];
                        if (nx < cluster.X || ny < cluster.Y || nx >= cluster.X + cluster.W || ny >= cluster.Y + cluster.H || !CanStep(x, y, dir))
                        {
                            continue;
                        }

                        int n = (ny * Width) + nx;
                        if (_tileRegion[n] == NoRegion)
                        {
                            _tileRegion[n] = region;
                            queue[tail++] = n;
                        }
                    }
                }
            }
        }

        cluster.RegionCount = regions;
    }

    private void BuildPortals(int c)
    {
        Cluster cluster = _clusters[c];
        PortalCount -= cluster.PortalCount;
        cluster.PortalCount = 0;

        // North, east, south, west: fixed order, runs in ascending coordinate.
        if (cluster.Y > 0)
        {
            AddSide(cluster, cluster.X, cluster.Y, 1, 0, 0, -1, cluster.W);
        }

        if (cluster.X + cluster.W < Width)
        {
            AddSide(cluster, cluster.X + cluster.W - 1, cluster.Y, 0, 1, 1, 0, cluster.H);
        }

        if (cluster.Y + cluster.H < Height)
        {
            AddSide(cluster, cluster.X, cluster.Y + cluster.H - 1, 1, 0, 0, 1, cluster.W);
        }

        if (cluster.X > 0)
        {
            AddSide(cluster, cluster.X, cluster.Y, 0, 1, -1, 0, cluster.H);
        }

        PortalCount += cluster.PortalCount;
    }

    // Walks one border: (x0, y0) + k·(sx, sy) are this cluster's edge tiles, (ox, oy) points across the border.
    private void AddSide(Cluster cluster, int x0, int y0, int sx, int sy, int ox, int oy, int length)
    {
        int runStart = -1;
        for (int k = 0; k <= length; k++)
        {
            bool open = k < length && IsWalkable(x0 + (k * sx), y0 + (k * sy)) && IsWalkable(x0 + (k * sx) + ox, y0 + (k * sy) + oy);
            if (open && runStart < 0)
            {
                runStart = k;
            }
            else if (!open && runStart >= 0)
            {
                int runLength = k - runStart;
                if (runLength < 6)
                {
                    AddPortal(cluster, x0, y0, sx, sy, ox, oy, runStart + ((runLength - 1) / 2));
                }
                else
                {
                    AddPortal(cluster, x0, y0, sx, sy, ox, oy, runStart);
                    AddPortal(cluster, x0, y0, sx, sy, ox, oy, k - 1);
                }

                runStart = -1;
            }
        }
    }

    private void AddPortal(Cluster cluster, int x0, int y0, int sx, int sy, int ox, int oy, int k)
    {
        int x = x0 + (k * sx);
        int y = y0 + (k * sy);
        int i = cluster.PortalCount++;
        cluster.PortalTile[i] = (y * Width) + x;
        cluster.PortalRegion[i] = _tileRegion[(y * Width) + x];
        cluster.PortalOutside[i] = ((y + oy) * Width) + x + ox;
        cluster.PortalPartner[i] = -1;
    }

    private void LinkPartners(int c)
    {
        Cluster cluster = _clusters[c];
        for (int i = 0; i < cluster.PortalCount; i++)
        {
            int outside = cluster.PortalOutside[i];
            int oc = ClusterOf(outside);
            Cluster other = _clusters[oc];
            int partner = -1;
            for (int j = 0; j < other.PortalCount; j++)
            {
                if (other.PortalTile[j] == outside && other.PortalOutside[j] == cluster.PortalTile[i])
                {
                    partner = (oc * MaxPortals) + j;
                    break;
                }
            }

            cluster.PortalPartner[i] = partner;
        }
    }

    private void BuildIntra(int c)
    {
        Cluster cluster = _clusters[c];
        int n = cluster.PortalCount;
        cluster.EnsureTrees(n);
        for (int i = 0; i < n; i++)
        {
            byte[] tree = cluster.Trees[i];
            _search.Dijkstra(this, cluster, cluster.PortalTile[i], tree, null);
            for (int j = 0; j < n; j++)
            {
                cluster.IntraCost[(i * MaxPortals) + j] = i == j || cluster.PortalRegion[i] != cluster.PortalRegion[j]
                    ? -1
                    : _search.CostAt(cluster, cluster.PortalTile[j]);
            }
        }
    }

    private void BuildComponents()
    {
        for (int c = 0; c < _clusters.Length; c++)
        {
            for (int r = 0; r < _clusters[c].RegionCount; r++)
            {
                int key = (c * 256) + r;
                _unionParent[key] = key;
            }
        }

        for (int c = 0; c < _clusters.Length; c++)
        {
            Cluster cluster = _clusters[c];
            for (int i = 0; i < cluster.PortalCount; i++)
            {
                int partner = cluster.PortalPartner[i];
                if (partner < 0)
                {
                    continue;
                }

                int a = Find((c * 256) + cluster.PortalRegion[i]);
                Cluster pc = _clusters[partner / MaxPortals];
                int b = Find(((partner / MaxPortals) * 256) + pc.PortalRegion[partner % MaxPortals]);
                if (a != b)
                {
                    // Union by smaller key keeps roots canonical.
                    if (a < b)
                    {
                        _unionParent[b] = a;
                    }
                    else
                    {
                        _unionParent[a] = b;
                    }
                }
            }
        }

        // Canonical labels: components numbered in order of their smallest region key.
        int next = 0;
        for (int c = 0; c < _clusters.Length; c++)
        {
            for (int r = 0; r < _clusters[c].RegionCount; r++)
            {
                int key = (c * 256) + r;
                int root = Find(key);
                if (root == key)
                {
                    _regionComponent[key] = next++;
                }
                else
                {
                    _regionComponent[key] = _regionComponent[root];
                }
            }
        }

        ComponentCount = next;
    }

    private int Find(int key)
    {
        while (_unionParent[key] != key)
        {
            _unionParent[key] = _unionParent[_unionParent[key]];
            key = _unionParent[key];
        }

        return key;
    }

    /// <summary>Per-cluster HPA* data.</summary>
    internal sealed class Cluster
    {
        public Cluster(int x, int y, int w, int h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }

        public int X { get; }

        public int Y { get; }

        public int W { get; }

        public int H { get; }

        public int RegionCount { get; set; }

        public int PortalCount { get; set; }

        public int[] PortalTile { get; } = new int[MaxPortals];

        public int[] PortalOutside { get; } = new int[MaxPortals];

        public int[] PortalPartner { get; } = new int[MaxPortals];

        public byte[] PortalRegion { get; } = new byte[MaxPortals];

        /// <summary>Row i: cost from portal i to portal j within the cluster, -1 if not connected.</summary>
        public int[] IntraCost { get; } = new int[MaxPortals * MaxPortals];

        /// <summary>Per portal: direction from each cluster tile to its parent toward the portal.</summary>
        public byte[][] Trees { get; private set; } = [];

        public int LocalIndex(int tile, int width) => (((tile / width) - Y) * ClusterSize) + (tile % width) - X;

        public void EnsureTrees(int n)
        {
            if (Trees.Length < n)
            {
                byte[][] grown = new byte[Math.Max(n, Trees.Length * 2)][];
                Trees.CopyTo(grown, 0);
                for (int i = Trees.Length; i < grown.Length; i++)
                {
                    grown[i] = new byte[ClusterTiles];
                }

                Trees = grown;
            }
        }
    }
}
