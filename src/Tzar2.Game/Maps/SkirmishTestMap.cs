using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Rng;
using Tzar2.Game.Session;

namespace Tzar2.Game.Maps;

/// <summary>
/// The skirmish test map of M3 stream D (ADR 0070): a generated grassland with lakes, forests, gold and stone, and a
/// start base per kingdom (castle, peasants, two footmen). It stands in until the random map generator (stream C) and
/// the legacy map converter (M4) provide real maps through <see cref="IMapSource"/>. Deterministic from the match seed;
/// it uses only ruleset keys (<c>&lt;civ&gt;.castle</c>, <c>.peon</c>, <c>.footman</c>, <c>comm.tree</c>,
/// <c>comm.res_gold</c>, <c>comm.res_stone</c>) and the terrain keys <c>grass</c> and <c>water</c>.
/// </summary>
public sealed class SkirmishTestMap : IMapSource
{
    private const int Edge = 2;

    /// <summary>Creates the source.</summary>
    /// <param name="size">Map side in tiles (48-512).</param>
    public SkirmishTestMap(int size = 96)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 48);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 512);
        Size = size;
    }

    /// <summary>Map side in tiles.</summary>
    public int Size { get; }

    /// <summary>Peasants per kingdom at the start.</summary>
    public int StartPeasants { get; init; } = 6;

    /// <summary>Footmen per kingdom at the start.</summary>
    public int StartFootmen { get; init; } = 2;

    /// <summary>Whether to add lakes.</summary>
    public bool Lakes { get; init; } = true;

    /// <inheritdoc />
    public string Name => $"Skirmish test map {Size}×{Size}";

    /// <summary>The castle's top-left tile of each slot (bases on the corners, then the edges' midpoints).</summary>
    public IReadOnlyList<(int X, int Y)> BaseTiles(int players)
    {
        int lo = 10, hi = Size - 18, mid = (Size / 2) - 4;
        (int, int)[] spots = [(lo, lo), (hi, hi), (hi, lo), (lo, hi), (mid, lo), (mid, hi), (lo, mid), (hi, mid)];
        return spots.AsSpan(0, Math.Min(players, spots.Length)).ToArray();
    }

    /// <inheritdoc />
    public MapData Create(Ruleset ruleset, MatchSetup setup)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(setup);
        int n = Size;
        byte grass = TerrainId(ruleset, "grass", water: false);
        byte waterId = TerrainId(ruleset, "water", water: true);
        var rng = Xoshiro256StarStar.FromSeed(setup.Seed ^ 0x51A9_0001UL);
        IReadOnlyList<(int X, int Y)> bases = BaseTiles(setup.Players.Count);

        // Terrain: water tiles (lakes away from the bases), then the corner grid (a corner is water when every tile
        // touching it is water, as in ScenarioMaps).
        bool[] water = new bool[n * n];
        if (Lakes)
        {
            int lakes = 2 + (n / 64);
            for (int l = 0, attempts = 0; l < lakes && attempts < 200; attempts++)
            {
                int cx = rng.NextInt(12, n - 12), cy = rng.NextInt(12, n - 12), r = rng.NextInt(3, 6 + (n / 48));
                if (NearBase(bases, cx, cy, r + 12))
                {
                    continue;
                }

                for (int y = cy - r; y <= cy + r; y++)
                {
                    for (int x = cx - r - 1; x <= cx + r + 1; x++)
                    {
                        int dx = x - cx, dy = y - cy;
                        if (x >= Edge && y >= Edge && x < n - Edge && y < n - Edge && (dx * dx) + (dy * dy * 5 / 4) <= r * r)
                        {
                            water[(y * n) + x] = true;
                        }
                    }
                }

                l++;
            }
        }

        byte[] corners = new byte[(n + 1) * (n + 1)];
        for (int cy = 0; cy <= n; cy++)
        {
            for (int cx = 0; cx <= n; cx++)
            {
                bool all = true;
                for (int ty = cy - 1; ty <= cy && all; ty++)
                {
                    for (int tx = cx - 1; tx <= cx; tx++)
                    {
                        if ((uint)tx < (uint)n && (uint)ty < (uint)n && !water[(ty * n) + tx])
                        {
                            all = false;
                            break;
                        }
                    }
                }

                corners[(cy * (n + 1)) + cx] = all ? waterId : grass;
            }
        }

        // A tile takes structures only when all four corners are land (the sim's passability for land locomotors).
        bool[] used = new bool[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                if (corners[(y * (n + 1)) + x] == waterId || corners[(y * (n + 1)) + x + 1] == waterId
                    || corners[((y + 1) * (n + 1)) + x] == waterId || corners[((y + 1) * (n + 1)) + x + 1] == waterId)
                {
                    used[(y * n) + x] = true;
                }
            }
        }

        var objects = new List<MapObject>();
        for (int p = 0; p < setup.Players.Count; p++)
        {
            PlayerSetup player = setup.Players[p];
            (int bx, int by) = bases[p];
            string civ = player.Civilization;
            // The seat and the starting worker the ruleset marks (classic: the castle and the peasant).
            EntityTypeDefinition castle = ruleset.EntityTypes[ruleset.SeatTypeId(civ)];
            string worker = ruleset.EntityTypes[ruleset.StartingWorkerTypeId(civ)].Key;
            Place(ruleset, objects, used, n, castle.Key, player.Slot, bx, by);
            int below = by + castle.FootprintHeight + 1;
            for (int w = 0; w < StartPeasants; w++)
            {
                PlaceUnit(ruleset, objects, used, n, worker, player.Slot, bx + (w % 4), below + (w / 4));
            }

            for (int f = 0; f < StartFootmen; f++)
            {
                PlaceUnit(ruleset, objects, used, n, civ + ".footman", player.Slot, bx + castle.FootprintWidth + 1, by + 1 + f);
            }

            // Resources: gold and stone toward the centre, a forest on the outer side.
            int towardX = bx < n / 2 ? 1 : -1, towardY = by < n / 2 ? 1 : -1;
            int rx = bx + (towardX > 0 ? castle.FootprintWidth + 5 : -9);
            Place(ruleset, objects, used, n, "comm.res_gold", MapObject.Nature, rx, by + 1);
            Place(ruleset, objects, used, n, "comm.res_stone", MapObject.Nature, rx, by + 6);
            int fx = towardX > 0 ? Math.Max(Edge, bx - 9) : Math.Min(n - Edge - 8, bx + castle.FootprintWidth + 2);
            int fy = towardY > 0 ? Math.Max(Edge, by - 8) : Math.Min(n - Edge - 7, by + castle.FootprintHeight + 2);
            Forest(ruleset, objects, used, n, ref rng, fx, fy, 9, 7, 75);
        }

        // Shared forests and resources between the bases.
        int forests = n * n / 900;
        for (int f = 0, attempts = 0; f < forests && attempts < forests * 10; attempts++)
        {
            int x = rng.NextInt(Edge, n - 12), y = rng.NextInt(Edge, n - 10);
            if (NearBase(bases, x + 4, y + 4, 14))
            {
                continue;
            }

            Forest(ruleset, objects, used, n, ref rng, x, y, rng.NextInt(5, 11), rng.NextInt(4, 9), 60);
            f++;
        }

        int rocks = Math.Max(2, n / 32);
        for (int r = 0, attempts = 0; r < rocks && attempts < 400; attempts++)
        {
            int x = rng.NextInt(Edge + 2, n - 8), y = rng.NextInt(Edge + 2, n - 8);
            if (!NearBase(bases, x, y, 16) && Place(ruleset, objects, used, n, r % 2 == 0 ? "comm.res_gold" : "comm.res_stone", MapObject.Nature, x, y))
            {
                r++;
            }
        }

        return new MapData($"skirmish-{n}-{setup.Seed:X}", n, n, corners, [], [], objects);
    }

    private static bool NearBase(IReadOnlyList<(int X, int Y)> bases, int x, int y, int distance)
    {
        foreach ((int bx, int by) in bases)
        {
            if (Math.Abs(bx + 3 - x) < distance && Math.Abs(by + 3 - y) < distance)
            {
                return true;
            }
        }

        return false;
    }

    private static byte TerrainId(Ruleset ruleset, string key, bool water)
    {
        foreach (TerrainTypeDefinition t in ruleset.TerrainTypes)
        {
            if (t.Key == key)
            {
                return t.Id;
            }
        }

        foreach (TerrainTypeDefinition t in ruleset.TerrainTypes)
        {
            if (t.IsWater == water)
            {
                return t.Id;
            }
        }

        return 0;
    }

    private static void Forest(Ruleset ruleset, List<MapObject> objects, bool[] used, int n, ref Xoshiro256StarStar rng, int x0, int y0, int w, int h, int percent)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                // Denser in the middle of the patch.
                int edge = Math.Min(Math.Min(x - x0, x0 + w - 1 - x), Math.Min(y - y0, y0 + h - 1 - y));
                if (rng.NextInt(100) < (edge == 0 ? percent / 2 : percent))
                {
                    Place(ruleset, objects, used, n, "comm.tree", MapObject.Nature, x, y);
                }
            }
        }
    }

    private static bool Place(Ruleset ruleset, List<MapObject> objects, bool[] used, int n, string key, byte owner, int tx, int ty)
    {
        int id = ruleset.EntityTypeId(key);
        if (id < 0)
        {
            return false;
        }

        EntityTypeDefinition type = ruleset.EntityTypes[id];
        int w = Math.Max(1, type.FootprintWidth), h = Math.Max(1, type.FootprintHeight);
        if (tx < Edge || ty < Edge || tx + w > n - Edge || ty + h > n - Edge)
        {
            return false;
        }

        for (int y = ty - 1; y <= ty + h; y++)
        {
            for (int x = tx - 1; x <= tx + w; x++)
            {
                // Keep a one-tile gap around buildings and rocks so units can walk around them; trees may touch.
                bool inside = x >= tx && x < tx + w && y >= ty && y < ty + h;
                if ((inside || key != "comm.tree") && (uint)x < (uint)n && (uint)y < (uint)n && used[(y * n) + x])
                {
                    return false;
                }
            }
        }

        for (int y = ty; y < ty + h; y++)
        {
            for (int x = tx; x < tx + w; x++)
            {
                used[(y * n) + x] = true;
            }
        }

        objects.Add(MapObject.AtTile(key, owner, tx, ty));
        return true;
    }

    private static void PlaceUnit(Ruleset ruleset, List<MapObject> objects, bool[] used, int n, string key, byte owner, int tx, int ty)
    {
        if (ruleset.EntityTypeId(key) < 0)
        {
            return;
        }

        for (int r = 0; r < 6; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = tx + dx, y = ty + dy;
                    if (x >= Edge && y >= Edge && x < n - Edge && y < n - Edge && !used[(y * n) + x])
                    {
                        used[(y * n) + x] = true;
                        objects.Add(new MapObject(key, owner, (x * MapData.UnitsPerTile) + (MapData.UnitsPerTile / 2), (y * MapData.UnitsPerTile) + (MapData.UnitsPerTile / 2)));
                        return;
                    }
                }
            }
        }
    }
}
