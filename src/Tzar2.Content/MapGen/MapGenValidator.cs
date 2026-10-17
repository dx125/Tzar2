using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Tzar2.Content.Compiled;
using Tzar2.Content.Terrain;

namespace Tzar2.Content.MapGen;

/// <summary>
/// The acceptance rules of a random map (GAME-18, ADR 0060), checked on the finished <see cref="MapData"/> independently
/// of how it was generated. The generator retries an attempt that fails them; tests run them on every generated map.
/// </summary>
public static class MapGenValidator
{
    /// <summary>Radius in tiles around a castle centre that must hold the starting resources (⚠ UNVERIFIED value).</summary>
    public const int ResourceRadius = 16;

    /// <summary>On naval maps a fish patch must lie within this many tiles of the kingdom's own land (⚠ UNVERIFIED value).</summary>
    public const int FishReach = 8;

    /// <summary>Cows a start needs within <see cref="ResourceRadius"/> when animals are on (⚠ UNVERIFIED value).</summary>
    public const int MinStartCows = 3;

    /// <summary>Trees a start needs within <see cref="ResourceRadius"/> (⚠ UNVERIFIED values).</summary>
    public static int MinStartTrees(MapResourceLevel level) => level switch
    {
        MapResourceLevel.Low => 30,
        MapResourceLevel.High => 60,
        _ => 40,
    };

    /// <summary>Smallest allowed distance in tiles between two castles (⚠ UNVERIFIED values).</summary>
    public static int MinSeparation(int side, int players) => Math.Max(12, side * (players switch
    {
        <= 2 => 400,
        <= 4 => 290,
        <= 6 => 200,
        _ => 150,
    }) / 1000);

    /// <summary>Checks <paramref name="map"/>; returns the failed rules (empty = valid).</summary>
    public static IReadOnlyList<string> Validate(GeneratedMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return Validate(map.Map, map.Starts, map.Options, map.ResolvedType);
    }

    /// <summary>Checks a map generated from <paramref name="options"/> as <paramref name="type"/>; returns the failed rules.</summary>
    public static IReadOnlyList<string> Validate(MapData map, ImmutableArray<StartPosition> starts, MapGenOptions options, RandomMapType type)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(options);
        var issues = new List<string>();
        TerrainProfile profile = TerrainProfile.For(options.TerrainSet);
        int w = map.Width, h = map.Height, stride = w + 1;
        if (map.TerrainSet != profile.Id || map.SetTerrainCorners.Length != stride * (h + 1))
        {
            issues.Add($"terrain set is '{map.TerrainSet}', expected '{profile.Id}' with set corners");
            return issues;
        }

        ReadOnlySpan<byte> set = map.SetTerrainCorners;
        ReadOnlySpan<byte> ids = map.TerrainCorners;
        ReadOnlySpan<byte> cliffs = map.CliffTiles;
        for (int i = 0; i < set.Length; i++)
        {
            byte expected = set[i] == TerrainProfile.Water ? options.WaterTerrainId : options.LandTerrainId;
            if (set[i] >= profile.TerrainCount || ids[i] != expected)
            {
                issues.Add(Say($"corner {i % stride},{i / stride}: set terrain {set[i]} with ruleset terrain {ids[i]}"));
                break;
            }
        }

        // Land / ship passability per tile from the corners (the sim's rule: AND of the four corners, §9.1).
        byte[] land = new byte[w * h];
        byte[] ship = new byte[w * h];
        int undrawn = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int c = (y * stride) + x;
                (int, int, int, int) corners = (set[c], set[c + 1], set[c + stride + 1], set[c + stride]);
                if (!TerrainComposer.IsDrawnByRule(corners, profile.HasTransition) && undrawn++ == 0)
                {
                    issues.Add(Say($"tile {x},{y}: corners {corners} are not drawable by the transition rule"));
                }

                bool allWater = set[c] == 0 && set[c + 1] == 0 && set[c + stride] == 0 && set[c + stride + 1] == 0;
                bool anyWater = set[c] == 0 || set[c + 1] == 0 || set[c + stride] == 0 || set[c + stride + 1] == 0;
                land[(y * w) + x] = (byte)(!anyWater && cliffs[(y * w) + x] == 0 ? 1 : 0);
                ship[(y * w) + x] = (byte)(allWater ? 1 : 0);
                if (cliffs[(y * w) + x] != 0 && anyWater)
                {
                    issues.Add(Say($"tile {x},{y}: cliff on water"));
                }
            }
        }

        if (undrawn > 1)
        {
            issues.Add(Say($"{undrawn} tiles are not drawable by the transition rule"));
        }

        if (starts.Length != options.Players)
        {
            issues.Add(Say($"{starts.Length} starts for {options.Players} players"));
            return issues;
        }

        // Footprints: on land, not overlapping. Objects without a footprint: on a free land tile (fish: on open water).
        MapGenCatalog catalog = options.Catalog;
        var roles = RoleLookup(catalog);
        byte[] blocked = new byte[w * h];
        byte[] castle = new byte[w * h];
        byte[] tree = new byte[w * h];
        var positions = new List<(MapObjectRole Role, int X, int Y, int W, int H, byte Owner)>(map.Objects.Length);
        foreach (MapObject o in map.Objects)
        {
            if (!roles.TryGetValue(o.TypeKey, out MapObjectRole role))
            {
                issues.Add($"object '{o.TypeKey}' is not in the catalog");
                continue;
            }

            MapObjectKind kind = catalog.Kind(role)!.Value;
            int tx = o.X / MapData.UnitsPerTile, ty = o.Y / MapData.UnitsPerTile;
            positions.Add((role, tx, ty, kind.Width, kind.Height, o.Owner));
            if (!kind.HasFootprint)
            {
                continue;
            }

            if (tx + kind.Width > w || ty + kind.Height > h)
            {
                issues.Add(Say($"{o.TypeKey} at {tx},{ty} leaves the map"));
                continue;
            }

            int bad = -1;
            for (int y = ty; y < ty + kind.Height && bad < 0; y++)
            {
                for (int x = tx; x < tx + kind.Width && bad < 0; x++)
                {
                    int t = (y * w) + x;
                    if (land[t] == 0 || blocked[t] != 0)
                    {
                        bad = t;
                    }

                    blocked[t] = 1;
                    castle[t] = (byte)(role == MapObjectRole.Castle ? 1 : 0);
                    tree[t] = (byte)(role == MapObjectRole.Tree ? 1 : 0);
                }
            }

            if (bad >= 0)
            {
                issues.Add(Say($"{o.TypeKey} at {tx},{ty}: tile {bad % w},{bad / w} is {(land[bad] == 0 ? "water or cliff" : "taken")}"));
            }
        }

        foreach ((MapObjectRole role, int x, int y, int ow, _, _) in positions)
        {
            if (ow > 0)
            {
                continue;
            }

            int t = (y * w) + x;
            bool ok = role == MapObjectRole.FishPatch ? ship[t] != 0 : land[t] != 0 && blocked[t] == 0;
            if (!ok)
            {
                issues.Add(Say($"{role} at {x},{y} stands on {(role == MapObjectRole.FishPatch ? "land" : "water, a cliff or a footprint")}"));
            }
        }

        // Starts: a castle of the player centred on the start, fair spacing.
        var nearest = new int[starts.Length];
        for (int p = 0; p < starts.Length; p++)
        {
            StartPosition s = starts[p];
            bool found = false;
            foreach ((MapObjectRole role, int x, int y, int ow, int oh, byte owner) in positions)
            {
                found |= role == MapObjectRole.Castle && owner == s.Player && x + (ow / 2) == s.TileX && y + (oh / 2) == s.TileY;
            }

            if (!found)
            {
                issues.Add(Say($"player {s.Player}: no castle centred on {s.TileX},{s.TileY}"));
            }

            nearest[p] = int.MaxValue;
            for (int q = 0; q < starts.Length; q++)
            {
                if (q != p)
                {
                    nearest[p] = Math.Min(nearest[p], Distance(s.TileX, s.TileY, starts[q].TileX, starts[q].TileY));
                }
            }
        }

        if (starts.Length >= 2)
        {
            int min = int.MaxValue, max = 0, sum = 0;
            foreach (int d in nearest)
            {
                min = Math.Min(min, d);
                max = Math.Max(max, d);
                sum += d;
            }

            int limit = MinSeparation(w, starts.Length);
            if (min < limit)
            {
                issues.Add(Say($"castles only {min} tiles apart (at least {limit})"));
            }

            if (starts.Length >= 3 && (max - min) * 4 * starts.Length > sum)
            {
                issues.Add(Say($"unfair spacing: nearest neighbours {min}…{max} tiles"));
            }
        }

        // Reachability: land maps — every castle reaches every other on foot; naval maps — every castle reaches the ocean.
        byte[] passable = new byte[w * h];
        for (int t = 0; t < passable.Length; t++)
        {
            passable[t] = (byte)(land[t] != 0 && (blocked[t] == 0 || castle[t] != 0) ? 1 : 0);
        }

        int[] landLabel = Label(passable, w, h);

        // Resources count when they are reachable once trees are cut.
        byte[] reach = new byte[w * h];
        for (int t = 0; t < reach.Length; t++)
        {
            reach[t] = (byte)(passable[t] != 0 || tree[t] != 0 ? 1 : 0);
        }

        int[] reachLabel = Label(reach, w, h);
        int[] shipLabel = Label(ship, w, h);
        int ocean = LargestLabel(shipLabel);
        bool naval = type == RandomMapType.Archipelagos;
        for (int p = 0; p < starts.Length; p++)
        {
            StartPosition s = starts[p];
            int label = landLabel[(s.TileY * w) + s.TileX];
            if (label < 0)
            {
                issues.Add(Say($"player {s.Player}: castle centre {s.TileX},{s.TileY} is not on walkable land"));
                continue;
            }

            if (!naval && label != landLabel[(starts[0].TileY * w) + starts[0].TileX])
            {
                issues.Add(Say($"player {s.Player} cannot reach player {starts[0].Player} by land"));
            }

            if (naval && (ocean < 0 || !TouchesLabel(landLabel, label, shipLabel, ocean, w, h)))
            {
                issues.Add(Say($"player {s.Player}'s land does not reach the open sea"));
            }

            CheckResources(issues, s, positions, reachLabel, reachLabel[(s.TileY * w) + s.TileX], options, naval, w, h);
        }

        return issues;
    }

    private static void CheckResources(List<string> issues, StartPosition s, List<(MapObjectRole Role, int X, int Y, int W, int H, byte Owner)> positions, int[] landLabel, int label, MapGenOptions options, bool naval, int w, int h)
    {
        int gold = 0, stone = 0, trees = 0, cows = 0, fish = 0;
        foreach ((MapObjectRole role, int x, int y, int ow, int oh, _) in positions)
        {
            int d = Distance(s.TileX, s.TileY, x + (ow / 2), y + (oh / 2));
            bool reachable = ow == 0 ? landLabel[(y * w) + x] == label : Borders(landLabel, label, x, y, ow, oh, w, h);
            if (role == MapObjectRole.FishPatch && Near(landLabel, label, x, y, FishReach, w, h))
            {
                fish++;
            }

            if (d > ResourceRadius || !reachable)
            {
                continue;
            }

            switch (role)
            {
                case MapObjectRole.Gold: gold++; break;
                case MapObjectRole.Stone: stone++; break;
                case MapObjectRole.Tree: trees++; break;
                case MapObjectRole.Cow: cows++; break;
            }
        }

        MapGenCatalog c = options.Catalog;
        Need(issues, s, "gold rocks", gold, c.Kind(MapObjectRole.Gold) is null ? 0 : 1);
        Need(issues, s, "stone rocks", stone, c.Kind(MapObjectRole.Stone) is null ? 0 : 1);
        Need(issues, s, "trees", trees, c.Kind(MapObjectRole.Tree) is null ? 0 : MinStartTrees(options.Resources));
        Need(issues, s, "cows", cows, (options.Features & MapFeatures.Animals) == 0 || c.Kind(MapObjectRole.Cow) is null ? 0 : MinStartCows);
        Need(issues, s, "fish patches", fish, naval && c.Kind(MapObjectRole.FishPatch) is not null ? 1 : 0);
    }

    private static void Need(List<string> issues, StartPosition s, string what, int have, int need)
    {
        if (have < need)
        {
            issues.Add(Say($"player {s.Player}: {have} reachable {what} near the castle, needs {need}"));
        }
    }

    // Whether a tile of component `label` lies within `r` tiles (Chebyshev) of (x0, y0).
    private static bool Near(int[] labels, int label, int x0, int y0, int r, int w, int h)
    {
        for (int y = Math.Max(0, y0 - r); y <= Math.Min(h - 1, y0 + r); y++)
        {
            for (int x = Math.Max(0, x0 - r); x <= Math.Min(w - 1, x0 + r); x++)
            {
                if (labels[(y * w) + x] == label)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Whether a footprint has a 4-neighbour tile in component `label`.
    private static bool Borders(int[] labels, int label, int x0, int y0, int fw, int fh, int w, int h)
    {
        for (int y = y0 - 1; y <= y0 + fh; y++)
        {
            for (int x = x0 - 1; x <= x0 + fw; x++)
            {
                bool edge = (y == y0 - 1 || y == y0 + fh) != (x == x0 - 1 || x == x0 + fw);
                if (edge && (uint)x < (uint)w && (uint)y < (uint)h && labels[(y * w) + x] == label)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TouchesLabel(int[] landLabel, int label, int[] shipLabel, int ocean, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (landLabel[(y * w) + x] != label)
                {
                    continue;
                }

                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx < (uint)w && (uint)ny < (uint)h && shipLabel[(ny * w) + nx] == ocean)
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    /// <summary>4-connected component labels of non-zero cells (−1 elsewhere), numbered in row-major discovery order.</summary>
    internal static int[] Label(byte[] cells, int w, int h)
    {
        int[] labels = new int[w * h];
        Array.Fill(labels, -1);
        int[] queue = new int[w * h];
        int next = 0;
        for (int start = 0; start < cells.Length; start++)
        {
            if (cells[start] == 0 || labels[start] >= 0)
            {
                continue;
            }

            int head = 0, tail = 0;
            queue[tail++] = start;
            labels[start] = next;
            while (head < tail)
            {
                int t = queue[head++];
                int x = t % w, y = t / w;
                if (x > 0)
                {
                    Visit(t - 1);
                }

                if (x < w - 1)
                {
                    Visit(t + 1);
                }

                if (y > 0)
                {
                    Visit(t - w);
                }

                if (y < h - 1)
                {
                    Visit(t + w);
                }
            }

            next++;

            void Visit(int n)
            {
                if (cells[n] != 0 && labels[n] < 0)
                {
                    labels[n] = next;
                    queue[tail++] = n;
                }
            }
        }

        return labels;
    }

    /// <summary>The label with the most cells (lowest label on ties), or −1 when there is none.</summary>
    internal static int LargestLabel(int[] labels)
    {
        int count = 0;
        foreach (int l in labels)
        {
            count = Math.Max(count, l + 1);
        }

        if (count == 0)
        {
            return -1;
        }

        int[] sizes = new int[count];
        foreach (int l in labels)
        {
            if (l >= 0)
            {
                sizes[l]++;
            }
        }

        int best = 0;
        for (int i = 1; i < count; i++)
        {
            if (sizes[i] > sizes[best])
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>Euclidean distance in whole tiles (integer square root).</summary>
    internal static int Distance(int x1, int y1, int x2, int y2)
        => (int)Tzar2.Core.Numerics.IntMath.ISqrt((long)(((x1 - x2) * (x1 - x2)) + ((y1 - y2) * (y1 - y2))));

    private static Dictionary<string, MapObjectRole> RoleLookup(MapGenCatalog catalog)
    {
        var roles = new Dictionary<string, MapObjectRole>(StringComparer.Ordinal);
        for (MapObjectRole r = MapObjectRole.Castle; r <= MapObjectRole.GroundPatch; r++)
        {
            if (catalog.Kind(r) is not { } kind)
            {
                continue;
            }

            foreach (string civ in catalog.Civilizations)
            {
                roles.TryAdd(kind.KeyFor(civ), r);
            }
        }

        return roles;
    }

    private static string Say(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
