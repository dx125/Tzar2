using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Tzar2.Content.Compiled;
using Tzar2.Core.Rng;

namespace Tzar2.Content.MapGen;

/// <summary>
/// The deterministic random map generator (GAME-18, ADR 0060): integer math only, randomness only from the
/// <c>mapgen</c> stream (REQ-SIM-06). An attempt shapes land and water for the map type, paints the terrain-set corners
/// so every tile is drawable by the transition rule, reserves walkways, places the kingdoms and their starting resources,
/// then the optional contents; the result is validated (<see cref="MapGenValidator"/>) and retried a bounded number of
/// times.
/// </summary>
public static class MapGenerator
{
    /// <summary>Most players a map of <paramref name="size"/> takes (⚠ ours: room for fair starts).</summary>
    public static int MaxPlayersFor(RandomMapSize size) => size switch
    {
        RandomMapSize.Tiny => 4,
        RandomMapSize.Small => 6,
        _ => MapGenOptions.MaxPlayers,
    };

    /// <summary>Generates the map of <paramref name="options"/> from the <c>mapgen</c> stream of its <see cref="MapGenOptions.Seed"/>.</summary>
    /// <exception cref="ArgumentException">The options are inconsistent.</exception>
    /// <exception cref="MapGenException">No attempt passed validation.</exception>
    public static GeneratedMap Generate(MapGenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Xoshiro256StarStar mapGen = RngStreams.Derive(options.Seed, RngStreamKind.MapGen, 0);
        return Generate(options, ref mapGen);
    }

    /// <summary>Generates a map drawing from <paramref name="mapGen"/> (a match's <see cref="RngStreams.MapGen"/>, by reference).</summary>
    /// <exception cref="ArgumentException">The options are inconsistent.</exception>
    /// <exception cref="MapGenException">No attempt passed validation.</exception>
    public static GeneratedMap Generate(MapGenOptions options, ref Xoshiro256StarStar mapGen)
    {
        ArgumentNullException.ThrowIfNull(options);
        Check(options);
        RandomMapType type = options.Type == RandomMapType.Random ? (RandomMapType)(1 + mapGen.NextInt(5)) : options.Type;
        string[] civs = new string[options.Players];
        for (int p = 0; p < civs.Length; p++)
        {
            string wanted = options.Civilizations.IsDefaultOrEmpty ? "random" : options.Civilizations[p];
            civs[p] = wanted == "random" ? options.Catalog.Civilizations[mapGen.NextInt(options.Catalog.Civilizations.Length)] : wanted;
        }

        string failure = string.Empty;
        var failures = new List<string>();
        for (int attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            var builder = new MapBuilder(options, type, civs, mapGen.NextUInt64(), failures);
            GeneratedMap? map = builder.Build(attempt);
            if (map is null)
            {
                failure = builder.Failure;
                failures.Add(string.Create(CultureInfo.InvariantCulture, $"attempt {attempt}: {failure}"));
                continue;
            }

            IReadOnlyList<string> issues = MapGenValidator.Validate(map);
            if (issues.Count == 0)
            {
                return map;
            }

            failure = string.Join("; ", issues);
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"attempt {attempt}: {failure}"));
        }

        throw new MapGenException(string.Create(CultureInfo.InvariantCulture,
            $"No valid {RandomMapNames.Name(type)} map after {options.MaxAttempts} attempts (seed {options.Seed:X16}): {failure}"));
    }

    private static void Check(MapGenOptions o)
    {
        if (!Enum.IsDefined(o.Type) || !Enum.IsDefined(o.Size) || !Enum.IsDefined(o.TerrainSet) || !Enum.IsDefined(o.Resources))
        {
            throw new ArgumentException("Unknown map type, size, terrain set or resource level.", nameof(o));
        }

        if (o.Players < 1 || o.Players > MaxPlayersFor(o.Size))
        {
            throw new ArgumentException($"A {RandomMapNames.Name(o.Size)} map takes 1…{MaxPlayersFor(o.Size)} players, not {o.Players}.", nameof(o));
        }

        if (o.MaxAttempts < 1)
        {
            throw new ArgumentException("MaxAttempts must be at least 1.", nameof(o));
        }

        if (!o.Teams.IsDefaultOrEmpty)
        {
            if (o.Teams.Length != o.Players)
            {
                throw new ArgumentException($"{o.Teams.Length} teams for {o.Players} players.", nameof(o));
            }

            foreach (int t in o.Teams)
            {
                if ((uint)t >= MapGenOptions.MaxPlayers)
                {
                    throw new ArgumentException($"Team {t} is not in 0…{MapGenOptions.MaxPlayers - 1}.", nameof(o));
                }
            }
        }

        if (!o.Civilizations.IsDefaultOrEmpty)
        {
            if (o.Civilizations.Length != o.Players)
            {
                throw new ArgumentException($"{o.Civilizations.Length} civilizations for {o.Players} players.", nameof(o));
            }

            foreach (string c in o.Civilizations)
            {
                if (c != "random" && !o.Catalog.Civilizations.Contains(c))
                {
                    throw new ArgumentException($"Civilization '{c}' is not in the catalog.", nameof(o));
                }
            }
        }

        if (o.Catalog.Kind(MapObjectRole.Castle) is not { HasFootprint: true })
        {
            throw new ArgumentException("The catalog needs a castle with a footprint.", nameof(o));
        }
    }
}

/// <summary>One generation attempt. Tile and corner layers are row-major; corner (x, y) is the top-left of tile (x, y).</summary>
internal sealed partial class MapBuilder
{
    // Tile occupancy.
    private const byte Free = 0;
    private const byte Blocked = 1;
    private const byte Reserved = 2;

    private readonly MapGenOptions _o;
    private readonly RandomMapType _type;
    private readonly TerrainProfile _profile;
    private readonly MapGenCatalog _catalog;
    private readonly string[] _civs;
    private readonly int _s;
    private readonly int _players;
    private readonly bool _naval;
    private readonly byte[] _land;      // tile: 1 = land (shaping)
    private readonly byte[] _set;       // corner: terrain-set index
    private readonly byte[] _walk;      // tile: 1 = all four corners land
    private readonly byte[] _ship;      // tile: 1 = all four corners water
    private readonly byte[] _cliff;     // tile: 1 = cliff
    private readonly byte[] _occ;       // tile: Free / Blocked / Reserved
    private readonly int[] _startDist;  // tile: distance in tiles to the nearest castle centre
    private readonly int[] _queue;
    private readonly int[] _from;
    private readonly int[] _sx;
    private readonly int[] _sy;
    private readonly int[] _coast;      // naval: tile index of each start's harbor spot
    private readonly List<MapObject> _objects = [];
    private readonly int[] _roleCounts = new int[(int)MapObjectRole.GroundPatch + 1];
    private readonly List<string> _notes = [];
    private Xoshiro256StarStar _rng;
    private int[] _label = [];
    private int _startRadius;

    public MapBuilder(MapGenOptions options, RandomMapType type, string[] civs, ulong seed, IReadOnlyList<string> earlierFailures)
    {
        _notes.AddRange(earlierFailures);
        _o = options;
        _type = type;
        _civs = civs;
        _profile = TerrainProfile.For(options.TerrainSet);
        _catalog = options.Catalog;
        _s = RandomMapNames.Side(options.Size);
        _players = options.Players;
        _naval = type == RandomMapType.Archipelagos;
        _rng = Xoshiro256StarStar.FromSeed(seed);
        int tiles = _s * _s;
        _land = new byte[tiles];
        _set = new byte[(_s + 1) * (_s + 1)];
        _walk = new byte[tiles];
        _ship = new byte[tiles];
        _cliff = new byte[tiles];
        _occ = new byte[tiles];
        _startDist = new int[tiles];
        _queue = new int[tiles];
        _from = new int[tiles];
        _sx = new int[_players];
        _sy = new int[_players];
        _coast = new int[_players];
    }

    /// <summary>Why the last <see cref="Build"/> returned null.</summary>
    public string Failure { get; private set; } = string.Empty;

    public GeneratedMap? Build(int attempt)
    {
        PlanStarts();
        ShapeLand();
        if ((_o.Features & MapFeatures.Rivers) != 0)
        {
            CarveRivers();
        }

        ForceStartLand();
        Cleanup();
        if (!_naval)
        {
            ConnectStarts();
        }

        PaintTerrain();
        if (!ReserveWalkways())
        {
            return null;
        }

        if ((_o.Features & MapFeatures.Cliffs) != 0)
        {
            PlaceCliffs();
        }

        _label = MapGenValidator.Label(Passable(), _s, _s);
        if (!PlaceKingdoms())
        {
            return null;
        }

        PlaceFeatures();
        return Finish(attempt);
    }

    private int Idx(int x, int y) => (y * _s) + x;

    private bool Fail(FormattableString why)
    {
        Failure = why.ToString(CultureInfo.InvariantCulture);
        return false;
    }

    // ---- starts ------------------------------------------------------------------------------------------------------

    private void PlanStarts()
    {
        int ringPermille = _type switch
        {
            RandomMapType.Island => 270,
            RandomMapType.Continental => 330,
            RandomMapType.Archipelagos => 340,
            _ => 360,
        };
        int ring = _s * ringPermille / 1000;
        int centre = _s / 2;
        _startRadius = Math.Clamp(_s / 9, 7, 12);
        int margin = _startRadius + 2;

        // Teammates take neighbouring ring slots: slot order = players sorted by (team, player).
        int[] order = new int[_players];
        for (int p = 0; p < _players; p++)
        {
            order[p] = p;
        }

        Array.Sort(order, (a, b) => TeamOf(a) != TeamOf(b) ? TeamOf(a).CompareTo(TeamOf(b)) : a.CompareTo(b));
        int turn = Tzar2.Core.Numerics.Angle.UnitsPerTurn;
        int a0 = _rng.NextInt(turn);
        int angleJitter = turn / (_players * 16);
        int radiusJitter = ring / 25;
        for (int slot = 0; slot < _players; slot++)
        {
            int p = order[slot];
            int angle = a0 + (slot * turn / _players) + _rng.NextInt(-angleJitter, angleJitter + 1);
            int r = ring + _rng.NextInt(-radiusJitter, radiusJitter + 1);
            var a = Tzar2.Core.Numerics.Angle.FromRaw(angle);
            int x = centre + (int)((r * Tzar2.Core.Numerics.Trig.Cos(a).Raw) >> Tzar2.Core.Numerics.Fixed.FractionBits);
            int y = centre + (int)((r * Tzar2.Core.Numerics.Trig.Sin(a).Raw) >> Tzar2.Core.Numerics.Fixed.FractionBits);
            _sx[p] = Math.Clamp(x, margin, _s - 1 - margin);
            _sy[p] = Math.Clamp(y, margin, _s - 1 - margin);
        }

        if (_naval)
        {
            // Every kingdom gets its own island: the island must fit between the channels.
            int closest = int.MaxValue;
            for (int p = 0; p < _players; p++)
            {
                for (int q = p + 1; q < _players; q++)
                {
                    closest = Math.Min(closest, MapGenValidator.Distance(_sx[p], _sy[p], _sx[q], _sy[q]));
                }
            }

            if (_players > 1)
            {
                _startRadius = Math.Clamp(((closest - ChannelWidth) / 2) - 3, 6, _startRadius);
            }
        }

        for (int y = 0; y < _s; y++)
        {
            for (int x = 0; x < _s; x++)
            {
                long best = long.MaxValue;
                for (int p = 0; p < _players; p++)
                {
                    long dx = x - _sx[p], dy = y - _sy[p];
                    best = Math.Min(best, (dx * dx) + (dy * dy));
                }

                _startDist[Idx(x, y)] = (int)Tzar2.Core.Numerics.IntMath.ISqrt(best);
            }
        }
    }

    private int TeamOf(int player) => _o.Teams.IsDefaultOrEmpty ? player : _o.Teams[player];

    private int ChannelWidth => 3 + (_s / 96);

    // ---- land and water ----------------------------------------------------------------------------------------------

    private void ShapeLand()
    {
        int n = _s * _s;
        if (_type == RandomMapType.Dry)
        {
            Array.Fill(_land, (byte)1);
            return;
        }

        int[] noise = new int[n];
        int[] score = new int[n];
        int centre = _s / 2;
        switch (_type)
        {
            case RandomMapType.Lakes:
                Noise.Fill(noise, _s, _s, _rng.NextUInt64(), Math.Max(8, _s / 5), 4);
                Array.Copy(noise, score, n);
                Keep(score, 870);
                break;
            case RandomMapType.Continental:
            {
                Noise.Fill(noise, _s, _s, _rng.NextUInt64(), Math.Max(8, _s / 4), 4);
                int band = Math.Max(4, _s * 28 / 100);
                for (int y = 0; y < _s; y++)
                {
                    for (int x = 0; x < _s; x++)
                    {
                        int edge = Math.Min(Math.Min(x, y), Math.Min(_s - 1 - x, _s - 1 - y));
                        int bias = edge >= band ? Noise.Max : edge * Noise.Max / band;
                        score[Idx(x, y)] = ((2 * noise[Idx(x, y)]) + bias) / 3;
                    }
                }

                Keep(score, 680);
                break;
            }

            case RandomMapType.Island:
            {
                Noise.Fill(noise, _s, _s, _rng.NextUInt64(), Math.Max(8, _s / 4), 4);
                long r2 = (long)centre * centre;
                for (int y = 0; y < _s; y++)
                {
                    for (int x = 0; x < _s; x++)
                    {
                        long dx = x - centre, dy = y - centre;
                        long bias = Math.Max(0, Noise.Max - (((dx * dx) + (dy * dy)) * Noise.Max / r2));
                        score[Idx(x, y)] = (int)((noise[Idx(x, y)] + bias) / 2);
                    }
                }

                Keep(score, 450);
                break;
            }

            default: // Archipelagos: an island around every start plus small extra islands.
            {
                Noise.Fill(noise, _s, _s, _rng.NextUInt64(), Math.Max(6, _s / 8), 3);
                int extras = 3 + (_s / 24);
                int[] ix = new int[_players + extras], iy = new int[_players + extras], ir = new int[_players + extras];
                for (int p = 0; p < _players; p++)
                {
                    (ix[p], iy[p], ir[p]) = (_sx[p], _sy[p], _startRadius + 5 + (_s / 20));
                }

                for (int e = _players; e < ix.Length; e++)
                {
                    (ix[e], iy[e], ir[e]) = (_rng.NextInt(4, _s - 4), _rng.NextInt(4, _s - 4), _rng.NextInt(3, Math.Max(4, _startRadius + (_s / 40))));
                }

                for (int y = 0; y < _s; y++)
                {
                    for (int x = 0; x < _s; x++)
                    {
                        long bias = 0;
                        for (int i = 0; i < ix.Length; i++)
                        {
                            long dx = x - ix[i], dy = y - iy[i], rr = (long)ir[i] * ir[i];
                            bias = Math.Max(bias, Noise.Max - (((dx * dx) + (dy * dy)) * Noise.Max / rr));
                        }

                        score[Idx(x, y)] = (int)(bias + ((noise[Idx(x, y)] - (Noise.Max / 2)) / 2));
                    }
                }

                for (int i = 0; i < n; i++)
                {
                    score[i] = score[i] > Noise.Max / 4 ? 1 : 0;
                }

                break;
            }
        }

        for (int i = 0; i < n; i++)
        {
            _land[i] = (byte)(score[i] > 0 ? 1 : 0);
        }

        Smooth();
        Smooth();
        if (_type is RandomMapType.Island or RandomMapType.Continental or RandomMapType.Archipelagos)
        {
            for (int y = 0; y < _s; y++)
            {
                for (int x = 0; x < _s; x++)
                {
                    if (Math.Min(Math.Min(x, y), Math.Min(_s - 1 - x, _s - 1 - y)) < 2)
                    {
                        _land[Idx(x, y)] = 0;
                    }
                }
            }
        }
    }

    // Leaves score > 0 on the highest `permille` ‰ of the cells, 0 elsewhere.
    private static void Keep(int[] score, int permille)
    {
        int[] sorted = (int[])score.Clone();
        Array.Sort(sorted);
        int keep = (int)((long)sorted.Length * permille / 1000);
        int threshold = keep <= 0 ? int.MaxValue : sorted[sorted.Length - keep];
        for (int i = 0; i < score.Length; i++)
        {
            score[i] = score[i] >= threshold ? 1 : 0;
        }
    }

    // 3 × 3 majority filter (cells off the map count as water).
    private void Smooth()
    {
        byte[] next = new byte[_land.Length];
        for (int y = 0; y < _s; y++)
        {
            for (int x = 0; x < _s; x++)
            {
                int count = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx < (uint)_s && (uint)ny < (uint)_s)
                        {
                            count += _land[Idx(nx, ny)];
                        }
                    }
                }

                next[Idx(x, y)] = (byte)(count >= 5 ? 1 : 0);
            }
        }

        Array.Copy(next, _land, next.Length);
    }

    private void CarveRivers()
    {
        if (_type is RandomMapType.Dry or RandomMapType.Archipelagos)
        {
            _notes.Add($"no rivers on {RandomMapNames.Name(_type)} maps");
            return;
        }

        int rivers = 1 + (_s / 256);
        int radius = _s >= 256 ? 2 : 1;
        for (int river = 0; river < rivers; river++)
        {
            int side = _rng.NextInt(4);
            (int x0, int y0) = BorderPoint(side);
            (int x1, int y1) = BorderPoint((side + 2) % 4);
            long fx = (long)x0 << 8, fy = (long)y0 << 8;
            int heading = Tzar2.Core.Numerics.Trig.Atan2(y1 - y0, x1 - x0).Raw;
            for (int step = 0; step < _s * 3; step++)
            {
                int x = (int)(fx >> 8), y = (int)(fy >> 8);
                if (step > 4 && ((uint)x >= (uint)_s || (uint)y >= (uint)_s))
                {
                    break;
                }

                // Fords: a land crossing every 28 steps.
                bool ford = step % 28 >= 24;
                if (!ford)
                {
                    Disc(x, y, radius, 0, keepStarts: true);
                }

                int toTarget = Tzar2.Core.Numerics.Trig.Atan2(y1 - y, x1 - x).Raw;
                int turn = (short)(toTarget - heading);
                heading += (turn / 8) + _rng.NextInt(-2600, 2601);
                var a = Tzar2.Core.Numerics.Angle.FromRaw(heading);
                fx += Tzar2.Core.Numerics.Trig.Cos(a).Raw >> 8;
                fy += Tzar2.Core.Numerics.Trig.Sin(a).Raw >> 8;
            }
        }
    }

    private (int X, int Y) BorderPoint(int side)
    {
        int t = _rng.NextInt(_s / 5, _s - (_s / 5));
        return side switch
        {
            0 => (t, 0),
            1 => (_s - 1, t),
            2 => (t, _s - 1),
            _ => (0, t),
        };
    }

    // Sets tiles within `radius` of (x, y) to `value`; with keepStarts, tiles near a castle are left alone.
    private void Disc(int cx, int cy, int radius, byte value, bool keepStarts)
    {
        for (int y = cy - radius; y <= cy + radius; y++)
        {
            for (int x = cx - radius; x <= cx + radius; x++)
            {
                if ((uint)x < (uint)_s && (uint)y < (uint)_s && ((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) <= (radius * radius) + radius
                    && (!keepStarts || _startDist[Idx(x, y)] > _startRadius + 6))
                {
                    _land[Idx(x, y)] = value;
                }
            }
        }
    }

    private void ForceStartLand()
    {
        if (_naval && _players > 1)
        {
            // Water along the Voronoi borders between the starts keeps every kingdom on its own island.
            int channel = ChannelWidth;
            for (int y = 0; y < _s; y++)
            {
                for (int x = 0; x < _s; x++)
                {
                    long d1 = long.MaxValue, d2 = long.MaxValue;
                    for (int p = 0; p < _players; p++)
                    {
                        long dx = x - _sx[p], dy = y - _sy[p], d = (dx * dx) + (dy * dy);
                        if (d < d1)
                        {
                            (d1, d2) = (d, d1);
                        }
                        else if (d < d2)
                        {
                            d2 = d;
                        }
                    }

                    long near = Tzar2.Core.Numerics.IntMath.ISqrt(d1) + channel;
                    if (d2 < near * near)
                    {
                        _land[Idx(x, y)] = 0;
                    }
                }
            }
        }

        for (int p = 0; p < _players; p++)
        {
            Disc(_sx[p], _sy[p], _naval ? _startRadius : _startRadius + 4, 1, keepStarts: false);
        }
    }

    private void Cleanup()
    {
        // Ponds and specks: water bodies under 16 tiles that do not touch the border become land, land specks under 10
        // tiles become water (no start is that small: start discs are at least 6 tiles across).
        byte[] water = new byte[_land.Length];
        for (int i = 0; i < water.Length; i++)
        {
            water[i] = (byte)(1 - _land[i]);
        }

        Fill(water, 16, 1, keepBorder: true);
        Fill(_land, 10, 0, keepBorder: false);
    }

    // Components of `cells` smaller than `minSize` are written to `_land` as `value`.
    private void Fill(byte[] cells, int minSize, byte value, bool keepBorder)
    {
        int[] labels = MapGenValidator.Label(cells, _s, _s);
        int count = 0;
        foreach (int l in labels)
        {
            count = Math.Max(count, l + 1);
        }

        int[] sizes = new int[count];
        bool[] border = new bool[count];
        for (int i = 0; i < labels.Length; i++)
        {
            if (labels[i] >= 0)
            {
                sizes[labels[i]]++;
                int x = i % _s, y = i / _s;
                border[labels[i]] |= x == 0 || y == 0 || x == _s - 1 || y == _s - 1;
            }
        }

        for (int i = 0; i < labels.Length; i++)
        {
            if (labels[i] >= 0 && sizes[labels[i]] < minSize && !(keepBorder && border[labels[i]]))
            {
                _land[i] = value;
            }
        }
    }

    private void ConnectStarts()
    {
        for (int round = 0; round < _players; round++)
        {
            int[] labels = MapGenValidator.Label(_land, _s, _s);
            int root = labels[Idx(_sx[0], _sy[0])];
            int lonely = -1;
            for (int p = 1; p < _players && lonely < 0; p++)
            {
                if (labels[Idx(_sx[p], _sy[p])] != root)
                {
                    lonely = p;
                }
            }

            if (lonely < 0)
            {
                return;
            }

            // A land bridge to the nearest start that is already connected.
            int target = 0;
            for (int p = 0; p < _players; p++)
            {
                if (labels[Idx(_sx[p], _sy[p])] == root
                    && MapGenValidator.Distance(_sx[lonely], _sy[lonely], _sx[p], _sy[p]) < MapGenValidator.Distance(_sx[lonely], _sy[lonely], _sx[target], _sy[target]))
                {
                    target = p;
                }
            }

            int steps = Math.Max(Math.Abs(_sx[target] - _sx[lonely]), Math.Abs(_sy[target] - _sy[lonely]));
            for (int i = 0; i <= steps; i++)
            {
                int x = _sx[lonely] + ((_sx[target] - _sx[lonely]) * i / Math.Max(1, steps));
                int y = _sy[lonely] + ((_sy[target] - _sy[lonely]) * i / Math.Max(1, steps));
                Disc(x, y, 2, 1, keepStarts: false);
            }
        }
    }

    // ---- terrain -----------------------------------------------------------------------------------------------------

    private void PaintTerrain()
    {
        int cs = _s + 1;
        for (int cy = 0; cy <= _s; cy++)
        {
            for (int cx = 0; cx <= _s; cx++)
            {
                bool water = true;
                for (int ty = cy - 1; ty <= cy && water; ty++)
                {
                    for (int tx = cx - 1; tx <= cx; tx++)
                    {
                        if ((uint)tx < (uint)_s && (uint)ty < (uint)_s && _land[Idx(tx, ty)] != 0)
                        {
                            water = false;
                            break;
                        }
                    }
                }

                _set[(cy * cs) + cx] = water ? (byte)TerrainProfile.Water : (byte)TerrainProfile.Coast;
            }
        }

        int[] field = new int[cs * cs];
        int[] values = new int[cs * cs];
        foreach (TerrainLayer layer in _profile.Layers)
        {
            Noise.Fill(field, cs, cs, _rng.NextUInt64(), layer.CellTiles * (1 + (_s / 256)), 3);
            int eligible = 0;
            for (int i = 0; i < field.Length; i++)
            {
                if (_set[i] == layer.Parent)
                {
                    values[eligible++] = field[i];
                }
            }

            if (eligible == 0)
            {
                continue;
            }

            Array.Sort(values, 0, eligible);
            int keep = (int)((long)eligible * layer.CoverPermille / 1000);
            if (keep == 0)
            {
                continue;
            }

            int threshold = values[eligible - keep];
            for (int i = 0; i < field.Length; i++)
            {
                if (_set[i] == layer.Parent && field[i] >= threshold)
                {
                    _set[i] = (byte)layer.Terrain;
                }
            }
        }

        Erode();
        for (int y = 0; y < _s; y++)
        {
            for (int x = 0; x < _s; x++)
            {
                int c = (y * cs) + x;
                bool any = _set[c] == 0 || _set[c + 1] == 0 || _set[c + cs] == 0 || _set[c + cs + 1] == 0;
                bool all = _set[c] == 0 && _set[c + 1] == 0 && _set[c + cs] == 0 && _set[c + cs + 1] == 0;
                _walk[Idx(x, y)] = (byte)(any ? 0 : 1);
                _ship[Idx(x, y)] = (byte)(all ? 1 : 0);
            }
        }
    }

    // Demotes painted corners that border a terrain other than their own, parent or child to their parent, until every
    // pair of neighbouring corners is equal or parent/child. Then every tile has at most two corner terrains, a pair with
    // a transition strip (TerrainProfile remarks). Depth only decreases, so it terminates.
    private void Erode()
    {
        int cs = _s + 1;
        byte[] demote = new byte[_set.Length];
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int cy = 0; cy < cs; cy++)
            {
                for (int cx = 0; cx < cs; cx++)
                {
                    int t = _set[(cy * cs) + cx];
                    int depth = _profile.DepthOf(t);
                    if (depth < 2)
                    {
                        continue;
                    }

                    bool bad = false;
                    for (int dy = -1; dy <= 1 && !bad; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx, ny = cy + dy;
                            if ((uint)nx < (uint)cs && (uint)ny < (uint)cs)
                            {
                                int n = _set[(ny * cs) + nx];
                                if (!_profile.MayBorder(t, n) && _profile.DepthOf(n) <= depth)
                                {
                                    bad = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (bad)
                    {
                        demote[(cy * cs) + cx] = 1;
                        changed = true;
                    }
                }
            }

            for (int i = 0; i < demote.Length; i++)
            {
                if (demote[i] != 0)
                {
                    _set[i] = (byte)_profile.ParentOf(_set[i]);
                    demote[i] = 0;
                }
            }
        }
    }

    // ---- walkways and cliffs -----------------------------------------------------------------------------------------

    // Land maps: a walkway between the starts along a spanning tree; naval maps: a walkway from every start to a harbor
    // spot on the open sea. Walkways stay free of trees, rocks and cliffs, so the starts stay connected.
    private bool ReserveWalkways()
    {
        if (_naval)
        {
            int[] shipLabels = MapGenValidator.Label(_ship, _s, _s);
            int ocean = MapGenValidator.LargestLabel(shipLabels);
            for (int p = 0; p < _players; p++)
            {
                int goal = Bfs(Idx(_sx[p], _sy[p]), t => _walk[t] != 0, t => NextTo(shipLabels, ocean, t));
                if (goal < 0)
                {
                    return Fail($"player {p}'s island has no coast on the open sea");
                }

                _coast[p] = goal;
                ReservePath(goal, 1);
                ReserveArea(goal % _s, goal / _s, 2);
            }

            return true;
        }

        // Prim's spanning tree over the starts (ties: lower index).
        bool[] inTree = new bool[_players];
        inTree[0] = true;
        for (int added = 1; added < _players; added++)
        {
            int bestFrom = -1, bestTo = -1, best = int.MaxValue;
            for (int a = 0; a < _players; a++)
            {
                for (int b = 0; b < _players && inTree[a]; b++)
                {
                    int d = MapGenValidator.Distance(_sx[a], _sy[a], _sx[b], _sy[b]);
                    if (!inTree[b] && d < best)
                    {
                        (bestFrom, bestTo, best) = (a, b, d);
                    }
                }
            }

            int target = Idx(_sx[bestTo], _sy[bestTo]);
            if (Bfs(Idx(_sx[bestFrom], _sy[bestFrom]), t => _walk[t] != 0, t => t == target) < 0)
            {
                return Fail($"players {bestFrom} and {bestTo} are not connected by land");
            }

            ReservePath(target, 1);
            inTree[bestTo] = true;
        }

        return true;
    }

    private bool NextTo(int[] labels, int label, int t)
    {
        int x = t % _s, y = t / _s;
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((uint)nx < (uint)_s && (uint)ny < (uint)_s && labels[Idx(nx, ny)] == label)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Breadth-first search over 4-neighbours from `start` through `pass` tiles; returns the first tile that satisfies
    // `goal` (−1 if none). `_from` holds the search tree for ReservePath.
    private int Bfs(int start, Func<int, bool> pass, Func<int, bool> goal)
    {
        Array.Fill(_from, -1);
        int head = 0, tail = 0;
        _queue[tail++] = start;
        _from[start] = start;
        while (head < tail)
        {
            int t = _queue[head++];
            if (goal(t))
            {
                return t;
            }

            int x = t % _s, y = t / _s;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if ((uint)nx >= (uint)_s || (uint)ny >= (uint)_s)
                {
                    continue;
                }

                int n = Idx(nx, ny);
                if (_from[n] < 0 && pass(n))
                {
                    _from[n] = t;
                    _queue[tail++] = n;
                }
            }
        }

        return -1;
    }

    private void ReservePath(int end, int radius)
    {
        for (int t = end; ; t = _from[t])
        {
            ReserveArea(t % _s, t / _s, radius);
            if (_from[t] == t)
            {
                break;
            }
        }
    }

    private void ReserveArea(int cx, int cy, int radius)
    {
        for (int y = cy - radius; y <= cy + radius; y++)
        {
            for (int x = cx - radius; x <= cx + radius; x++)
            {
                if ((uint)x < (uint)_s && (uint)y < (uint)_s && _occ[Idx(x, y)] == Free)
                {
                    _occ[Idx(x, y)] = Reserved;
                }
            }
        }
    }

    private void PlaceCliffs()
    {
        int ridges = Math.Max(1, _s * _s / 5000);
        for (int r = 0; r < ridges; r++)
        {
            int x = _rng.NextInt(_s), y = _rng.NextInt(_s);
            for (int pick = 0; pick < 30 && !CliffAllowed(x, y); pick++)
            {
                (x, y) = (_rng.NextInt(_s), _rng.NextInt(_s));
            }

            int dir = _rng.NextInt(8);
            int length = 6 + _rng.NextInt(Math.Max(1, _s / 10));
            for (int step = 0; step < length && CliffAllowed(x, y); step++)
            {
                _cliff[Idx(x, y)] = 1;
                _occ[Idx(x, y)] = Blocked;
                int turn = _rng.NextInt(8);
                dir = (dir + (turn == 0 ? 1 : turn == 1 ? 7 : 0)) % 8;
                x += dir is 1 or 2 or 3 ? 1 : dir is 5 or 6 or 7 ? -1 : 0;
                y += dir is 3 or 4 or 5 ? 1 : dir is 7 or 0 or 1 ? -1 : 0;
            }
        }
    }

    private bool CliffAllowed(int x, int y)
    {
        if ((uint)x >= (uint)_s || (uint)y >= (uint)_s || _occ[Idx(x, y)] != Free || _startDist[Idx(x, y)] <= _startRadius + 6)
        {
            return false;
        }

        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((uint)nx >= (uint)_s || (uint)ny >= (uint)_s || _walk[Idx(nx, ny)] == 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private byte[] Passable()
    {
        byte[] pass = new byte[_s * _s];
        for (int i = 0; i < pass.Length; i++)
        {
            pass[i] = (byte)(_walk[i] != 0 && _cliff[i] == 0 && _occ[i] != Blocked ? 1 : 0);
        }

        return pass;
    }

    // ---- result ------------------------------------------------------------------------------------------------------

    private GeneratedMap Finish(int attempt)
    {
        byte[] ids = new byte[_set.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = _set[i] == TerrainProfile.Water ? _o.WaterTerrainId : _o.LandTerrainId;
        }

        string name = _o.Name ?? string.Create(CultureInfo.InvariantCulture,
            $"random-{RandomMapNames.Name(_type)}-{RandomMapNames.Name(_o.TerrainSet)}-{RandomMapNames.Name(_o.Size)}-{_o.Seed:X16}");
        var map = new MapData(name, _s, _s, ids, [], _cliff, _objects, _profile.Id, _set);
        var starts = ImmutableArray.CreateBuilder<StartPosition>(_players);
        for (int p = 0; p < _players; p++)
        {
            starts.Add(new StartPosition(p, _sx[p], _sy[p], TeamOf(p), _civs[p]));
        }

        int land = 0, water = 0, cliffs = 0;
        for (int i = 0; i < _walk.Length; i++)
        {
            land += _walk[i];
            water += _ship[i];
            cliffs += _cliff[i];
        }

        var stats = new MapGenStats(land, water, cliffs, [.. _roleCounts]);
        return new GeneratedMap(map, _o, _type, starts.MoveToImmutable(), attempt, stats, _notes);
    }
}
