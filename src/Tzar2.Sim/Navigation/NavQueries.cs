using System;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Navigation;

/// <summary>Goal resolution queries on a <see cref="NavLayer"/> (REQ-PATH-01: unreachable goals → nearest reachable point).</summary>
public static class NavQueries
{
    /// <summary>
    /// The walkable tile nearest to tile (<paramref name="tx"/>, <paramref name="ty"/>) within
    /// <paramref name="radius"/> tiles (Chebyshev rings, ties by squared distance then tile index), or -1.
    /// </summary>
    public static int NearestWalkable(NavLayer layer, int tx, int ty, int radius)
    {
        ArgumentNullException.ThrowIfNull(layer);
        for (int r = 0; r <= radius; r++)
        {
            int best = -1;
            int bestD = int.MaxValue;
            for (int y = ty - r; y <= ty + r; y++)
            {
                for (int x = tx - r; x <= tx + r; x++)
                {
                    if (Math.Max(Math.Abs(x - tx), Math.Abs(y - ty)) != r || !layer.IsWalkable(x, y))
                    {
                        continue;
                    }

                    int d = ((x - tx) * (x - tx)) + ((y - ty) * (y - ty));
                    int tile = (y * layer.Width) + x;
                    if (d < bestD || (d == bestD && tile < best))
                    {
                        bestD = d;
                        best = tile;
                    }
                }
            }

            if (best >= 0)
            {
                return best;
            }
        }

        return -1;
    }

    /// <summary>
    /// The tile of component <paramref name="component"/> nearest to the world point (<paramref name="x"/>,
    /// <paramref name="y"/>), by squared distance from the tile's unit-center point (ties: lower tile index), or -1.
    /// Searches clusters in rings around the point and skips clusters without a region of the component, so the cost
    /// is bounded by the cluster count (§9.2 acceptance 4: "unreachable" handled within one tick).
    /// </summary>
    public static int NearestInComponent(NavLayer layer, int component, int x, int y, out int scanned)
    {
        ArgumentNullException.ThrowIfNull(layer);
        scanned = 0;
        const int ClusterUnits = NavLayer.ClusterSize * MapState.UnitsPerTile;
        int gcx = Math.Clamp(layer.TileX(x) / NavLayer.ClusterSize, 0, layer.ClustersX - 1);
        int gcy = Math.Clamp(layer.TileY(y) / NavLayer.ClusterSize, 0, layer.ClustersY - 1);
        int best = -1;
        long bestD = long.MaxValue;
        int maxRing = Math.Max(layer.ClustersX, layer.ClustersY);
        for (int r = 0; r <= maxRing; r++)
        {
            // Every cluster of ring r is at least (r - 1) clusters away from the point; one more cluster of slack
            // covers unit-center offsets of large clearances.
            long lower = (long)Math.Max(0, r - 2) * ClusterUnits;
            if (best >= 0 && lower * lower > bestD)
            {
                break;
            }

            for (int cy = gcy - r; cy <= gcy + r; cy++)
            {
                for (int cx = gcx - r; cx <= gcx + r; cx++)
                {
                    if ((uint)cx >= (uint)layer.ClustersX || (uint)cy >= (uint)layer.ClustersY
                        || Math.Max(Math.Abs(cx - gcx), Math.Abs(cy - gcy)) != r)
                    {
                        continue;
                    }

                    int c = (cy * layer.ClustersX) + cx;
                    NavLayer.Cluster cluster = layer.ClusterAt(c);
                    bool has = false;
                    for (int region = 0; region < cluster.RegionCount && !has; region++)
                    {
                        has = layer.ComponentOfRegion(c, (byte)region) == component;
                    }

                    if (!has)
                    {
                        continue;
                    }

                    for (int ty = cluster.Y; ty < cluster.Y + cluster.H; ty++)
                    {
                        for (int tx = cluster.X; tx < cluster.X + cluster.W; tx++)
                        {
                            int tile = (ty * layer.Width) + tx;
                            scanned++;
                            if (layer.RegionOf(tile) == NavLayer.NoRegion || layer.ComponentOf(tile) != component)
                            {
                                continue;
                            }

                            long dx = layer.CenterX(tile) - (long)x;
                            long dy = layer.CenterY(tile) - (long)y;
                            long d = (dx * dx) + (dy * dy);
                            if (d < bestD || (d == bestD && tile < best))
                            {
                                bestD = d;
                                best = tile;
                            }
                        }
                    }
                }
            }
        }

        return best;
    }
}
