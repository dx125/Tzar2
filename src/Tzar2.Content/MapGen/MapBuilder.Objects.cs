using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.MapGen;

/// <summary>Object placement of a generation attempt (castles, starting resources, optional contents).</summary>
internal sealed partial class MapBuilder
{
    // ---- kingdoms ----------------------------------------------------------------------------------------------------

    private bool PlaceKingdoms()
    {
        MapObjectKind castle = _catalog.Kind(MapObjectRole.Castle)!.Value;
        for (int p = 0; p < _players; p++)
        {
            int tx = _sx[p] - (castle.Width / 2), ty = _sy[p] - (castle.Height / 2);
            // Walkways start at the castle centre, so the castle may cover reserved tiles.
            if (!CanPlace(tx, ty, castle.Width, castle.Height, 0, allowReserved: true))
            {
                return Fail($"no room for player {p}'s castle at {_sx[p]},{_sy[p]}");
            }

            Put(MapObjectRole.Castle, castle, (byte)p, tx, ty, _civs[p]);
            ReserveArea(_sx[p], _sy[p], (Math.Max(castle.Width, castle.Height) / 2) + 3);
            if (_catalog.Kind(MapObjectRole.Peasant) is { } peasant)
            {
                for (int i = 0; i < _catalog.StartingPeasants; i++)
                {
                    int x = tx + (i % castle.Width), y = ty + castle.Height + 1 + (i / castle.Width);
                    if (!FreeLand(x, y) && !FindFree(_sx[p], _sy[p], 1, 8, p, out x, out y))
                    {
                        return Fail($"no room for player {p}'s peasants");
                    }

                    PutUnit(MapObjectRole.Peasant, peasant, (byte)p, x, y, _civs[p]);
                }
            }
        }

        // The same starting resources for everyone, in the same distance bands (fairness, GAME-18).
        int rocks = _o.Resources == MapResourceLevel.High ? 2 : 1;
        for (int p = 0; p < _players; p++)
        {
            for (int k = 0; k < rocks; k++)
            {
                if (!PlaceNear(MapObjectRole.Gold, p, 8, 12) || !PlaceNear(MapObjectRole.Stone, p, 8, 12))
                {
                    return Fail($"no room for player {p}'s gold or stone");
                }
            }

            if (_catalog.Kind(MapObjectRole.Tree) is not null && !GrowForest(p, MapGenValidator.MinStartTrees(_o.Resources) + 10))
            {
                return Fail($"no room for player {p}'s forest");
            }

            if ((_o.Features & MapFeatures.Animals) != 0 && _catalog.Kind(MapObjectRole.Cow) is { } cow)
            {
                for (int k = 0; k <= MapGenValidator.MinStartCows; k++)
                {
                    if (!FindFree(_sx[p], _sy[p], 4, 8, p, out int x, out int y))
                    {
                        return Fail($"no room for player {p}'s cows");
                    }

                    PutUnit(MapObjectRole.Cow, cow, MapObject.Nature, x, y, string.Empty);
                }
            }

            if (_naval && _catalog.Kind(MapObjectRole.FishPatch) is not null && !PlaceStartFish(p))
            {
                return Fail($"no fishing water near player {p}'s harbor spot");
            }
        }

        return true;
    }

    // A rock of `role` for player p at a distance of dmin…dmax tiles, on the castle's land.
    private bool PlaceNear(MapObjectRole role, int p, int dmin, int dmax)
    {
        if (_catalog.Kind(role) is not { } kind)
        {
            return true;
        }

        for (int attempt = 0; attempt < 64 + (32 * (dmax - dmin + 1)); attempt++)
        {
            // Random tries first, then a deterministic sweep of the band.
            int d, angle;
            if (attempt < 64)
            {
                d = _rng.NextInt(dmin, dmax + 1);
                angle = _rng.NextInt(Tzar2.Core.Numerics.Angle.UnitsPerTurn);
            }
            else
            {
                d = dmin + ((attempt - 64) / 32);
                angle = ((attempt - 64) % 32) * (Tzar2.Core.Numerics.Angle.UnitsPerTurn / 32);
            }

            (int cx, int cy) = Polar(_sx[p], _sy[p], d, angle);
            int tx = cx - (kind.Width / 2), ty = cy - (kind.Height / 2);
            if (OnStartLand(cx, cy, p) && Mine(p, cx, cy) && CanPlace(tx, ty, kind.Width, kind.Height, 1))
            {
                Put(role, kind, MapObject.Nature, tx, ty, string.Empty);
                return true;
            }
        }

        return false;
    }

    // A forest of `need` trees grown from a seed 9…14 tiles from player p's castle, within the resource radius.
    private bool GrowForest(int p, int need)
    {
        MapObjectKind tree = _catalog.Kind(MapObjectRole.Tree)!.Value;
        int limit = MapGenValidator.ResourceRadius - 1;
        for (int seedTry = 0; seedTry < 16 && need > 0; seedTry++)
        {
            (int sx, int sy) = Polar(_sx[p], _sy[p], _rng.NextInt(9, 15), _rng.NextInt(Tzar2.Core.Numerics.Angle.UnitsPerTurn));
            if ((uint)sx >= (uint)_s || (uint)sy >= (uint)_s || !TreeAllowed(Idx(sx, sy)) || !OnStartLand(sx, sy, p))
            {
                continue;
            }

            Array.Fill(_from, -1);
            int head = 0, tail = 0;
            _queue[tail++] = Idx(sx, sy);
            _from[Idx(sx, sy)] = 0;
            while (head < tail && need > 0)
            {
                int t = _queue[head++];
                int x = t % _s, y = t / _s;
                int d = MapGenValidator.Distance(x, y, _sx[p], _sy[p]);
                if (!TreeAllowed(t) || d > limit || d <= _startRadius - 2 || d != _startDist[t])
                {
                    continue;
                }

                Put(MapObjectRole.Tree, tree, MapObject.Nature, x, y, string.Empty);
                need--;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if ((uint)nx < (uint)_s && (uint)ny < (uint)_s && _from[Idx(nx, ny)] < 0)
                    {
                        _from[Idx(nx, ny)] = 0;
                        if (_rng.NextInt(4) != 0)
                        {
                            _queue[tail++] = Idx(nx, ny);
                        }
                    }
                }
            }
        }

        return need <= 0;
    }

    // Two fish patches on open water next to player p's harbor spot.
    private bool PlaceStartFish(int p)
    {
        MapObjectKind fish = _catalog.Kind(MapObjectRole.FishPatch)!.Value;
        int coast = _coast[p];
        int placed = 0, firstX = -100, firstY = -100;
        Array.Fill(_from, -1);
        int head = 0, tail = 0;
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                int x = (coast % _s) + dx, y = (coast / _s) + dy;
                if ((uint)x < (uint)_s && (uint)y < (uint)_s && _ship[Idx(x, y)] != 0 && _from[Idx(x, y)] < 0)
                {
                    _from[Idx(x, y)] = 0;
                    _queue[tail++] = Idx(x, y);
                }
            }
        }

        while (head < tail && placed < 2)
        {
            int t = _queue[head++];
            int x = t % _s, y = t / _s;
            if (Math.Max(Math.Abs(x - (coast % _s)), Math.Abs(y - (coast / _s))) > MapGenValidator.FishReach - 2)
            {
                continue;
            }

            if (Math.Max(Math.Abs(x - firstX), Math.Abs(y - firstY)) >= 3 && Mine(p, x, y))
            {
                PutUnit(MapObjectRole.FishPatch, fish, MapObject.Nature, x, y, string.Empty);
                (firstX, firstY) = (x, y);
                placed++;
            }

            for (int k = 0; k < 4; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if ((uint)nx < (uint)_s && (uint)ny < (uint)_s && _ship[Idx(nx, ny)] != 0 && _from[Idx(nx, ny)] < 0)
                {
                    _from[Idx(nx, ny)] = 0;
                    _queue[tail++] = Idx(nx, ny);
                }
            }
        }

        return placed > 0;
    }

    // ---- optional contents -------------------------------------------------------------------------------------------

    private void PlaceFeatures()
    {
        MapFeatures f = _o.Features;
        if ((f & MapFeatures.GuardedTowns) != 0)
        {
            PlaceGuardedSpots();
        }

        if ((f & MapFeatures.Gateways) != 0 && Has(MapObjectRole.Gateway))
        {
            PlaceGateways();
        }

        if ((f & MapFeatures.Trees) != 0 && Has(MapObjectRole.Tree))
        {
            PlaceForests();
        }

        int perRock = _o.Resources switch { MapResourceLevel.Low => 3600, MapResourceLevel.High => 1500, _ => 2400 };
        var rocks = new List<(int X, int Y)>();
        ScatterRocks(MapObjectRole.Gold, perRock, rocks);
        ScatterRocks(MapObjectRole.Stone, perRock, rocks);
        if ((f & MapFeatures.Rocks) != 0 && Has(MapObjectRole.GroundObject))
        {
            Scatter(MapObjectRole.GroundObject, LandTiles() / 1800, _startRadius + 2, footprint: true);
        }

        if (Has(MapObjectRole.FishPatch))
        {
            PlaceFish();
        }

        if ((f & MapFeatures.Animals) != 0)
        {
            if (Has(MapObjectRole.Cow))
            {
                Scatter(MapObjectRole.Cow, (LandTiles() / 6000) + _players, 16, footprint: false);
            }

            if (Has(MapObjectRole.Bear))
            {
                Scatter(MapObjectRole.Bear, LandTiles() / 8000, 24, footprint: false);
            }
        }

        if ((f & MapFeatures.Patches) != 0 && Has(MapObjectRole.GroundPatch))
        {
            Scatter(MapObjectRole.GroundPatch, LandTiles() / 800, 0, footprint: false);
        }

        if ((f & MapFeatures.MagicItems) != 0 && Has(MapObjectRole.Item))
        {
            Scatter(MapObjectRole.Item, _players * 2, Math.Max(20, _s / 5), footprint: false);
        }
    }

    private bool Has(MapObjectRole role)
    {
        if (_catalog.Kind(role) is not null)
        {
            return true;
        }

        _notes.Add($"the catalog has no {role}; none placed");
        return false;
    }

    // Spots of `size` × `size` free tiles, as far from the nearest castle and as equidistant from all castles as possible.
    private List<(int X, int Y)> FairSpots(int count, int size, int minFromStarts, int minBetween)
    {
        var chosen = new List<(int X, int Y)>();
        int mainLabel = _label[Idx(_sx[0], _sy[0])];
        for (int k = 0; k < count; k++)
        {
            long best = long.MinValue;
            int bx = -1, by = -1;
            for (int cy = size; cy < _s - size; cy += 4)
            {
                for (int cx = size; cx < _s - size; cx += 4)
                {
                    int t = Idx(cx, cy);
                    if (_startDist[t] < minFromStarts || (!_naval && _label[t] != mainLabel) || TooClose(chosen, cx, cy, minBetween))
                    {
                        continue;
                    }

                    int dmin = int.MaxValue, dmax = 0;
                    for (int p = 0; p < _players; p++)
                    {
                        int d = MapGenValidator.Distance(cx, cy, _sx[p], _sy[p]);
                        dmin = Math.Min(dmin, d);
                        dmax = Math.Max(dmax, d);
                    }

                    long score = (2L * dmin) - (dmax - dmin);
                    if (score > best && CanPlace(cx - (size / 2), cy - (size / 2), size, size, 0))
                    {
                        (best, bx, by) = (score, cx, cy);
                    }
                }
            }

            if (bx < 0)
            {
                break;
            }

            chosen.Add((bx, by));
        }

        return chosen;
    }

    private static bool TooClose(List<(int X, int Y)> points, int x, int y, int distance)
    {
        foreach ((int px, int py) in points)
        {
            if (MapGenValidator.Distance(px, py, x, y) < distance)
            {
                return true;
            }
        }

        return false;
    }

    // Guarded spots: two neutral towers around a treasure (a magic item or a gold rock); the first, most central spot is
    // a small town with two houses (⚠ UNVERIFIED layout: the original's guarded towns are not described anywhere).
    private void PlaceGuardedSpots()
    {
        int wanted = Math.Max(1, _players / 2);
        List<(int X, int Y)> spots = FairSpots(wanted, 10, Math.Max(20, _s / 8), _s / 6);
        if (spots.Count < wanted)
        {
            _notes.Add($"room for {spots.Count} of {wanted} guarded spots");
        }

        for (int i = 0; i < spots.Count; i++)
        {
            (int cx, int cy) = spots[i];
            if (_catalog.Kind(MapObjectRole.Tower) is { } tower)
            {
                TryPut(MapObjectRole.Tower, tower, cx - 4, cy - 4);
                TryPut(MapObjectRole.Tower, tower, cx + 2, cy + 2);
            }

            if (i == 0 && _catalog.Kind(MapObjectRole.TownHouse) is { } house)
            {
                TryPut(MapObjectRole.TownHouse, house, cx + 2, cy - 4);
                TryPut(MapObjectRole.TownHouse, house, cx - 4, cy + 2);
            }

            if ((_o.Features & MapFeatures.MagicItems) != 0 && _catalog.Kind(MapObjectRole.Item) is { } item)
            {
                PutUnit(MapObjectRole.Item, item, MapObject.Nature, cx, cy, string.Empty);
            }
            else if (_catalog.Kind(MapObjectRole.Gold) is { } gold)
            {
                TryPut(MapObjectRole.Gold, gold, cx - 1, cy - 1);
            }

            ReserveArea(cx, cy, 5);
        }
    }

    private void PlaceGateways()
    {
        MapObjectKind gateway = _catalog.Kind(MapObjectRole.Gateway)!.Value;
        int wanted = _s <= 128 ? 1 : _s <= 256 ? 2 : 3;
        List<(int X, int Y)> spots = FairSpots(wanted, gateway.Width + 2, Math.Max(18, _s / 6), _s / 4);
        if (spots.Count < wanted)
        {
            _notes.Add($"room for {spots.Count} of {wanted} gateways");
        }

        foreach ((int cx, int cy) in spots)
        {
            TryPut(MapObjectRole.Gateway, gateway, cx - (gateway.Width / 2), cy - (gateway.Height / 2));
            ReserveArea(cx, cy, (gateway.Width / 2) + 2);
        }
    }

    // WORLD-013 (ADR 0201): the original's forests. A tile grows a tree by the plasma field and [randomtreeset] of the map's
    // side when it may hold one, is not in a kingdom's start area and every tile two steps away is land (0x4b2ab0 by
    // ReTzar's reading).
    private void PlaceForests()
    {
        MapObjectKind tree = _catalog.Kind(MapObjectRole.Tree)!.Value;
        Forests.TreeSettings settings = Forests.ForSide(_s);
        byte[] field = Forests.Field(_rng.NextUInt64(), settings.Roughness, settings.Divisor);
        var draws = Tzar2.Core.Rng.Xoshiro256StarStar.FromSeed(_rng.NextUInt64());
        for (int t = 0; t < _s * _s; t++)
        {
            int x = t % _s, y = t / _s;
            if (!TreeAllowed(t) || _startDist[t] <= _startRadius + 3 || !LandRing(x, y))
            {
                continue;
            }

            if (Forests.Grows(settings, Forests.Sample(field, _s, x, y), _profile.TreePictures, ref draws))
            {
                Put(MapObjectRole.Tree, tree, MapObject.Nature, x, y, string.Empty);
            }
        }
    }

    // The eight tiles two steps away are land, or off the map.
    private bool LandRing(int x, int y)
    {
        for (int dy = -2; dy <= 2; dy += 2)
        {
            for (int dx = -2; dx <= 2; dx += 2)
            {
                int nx = x + dx, ny = y + dy;
                if ((dx != 0 || dy != 0) && (uint)nx < (uint)_s && (uint)ny < (uint)_s && !LandTile(nx, ny))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool LandTile(int x, int y)
    {
        int cs = _s + 1, c = (y * cs) + x;
        return _set[c] != TerrainProfile.Water && _set[c + 1] != TerrainProfile.Water && _set[c + cs] != TerrainProfile.Water && _set[c + cs + 1] != TerrainProfile.Water;
    }

    private void ScatterRocks(MapObjectRole role, int perRock, List<(int X, int Y)> rocks)
    {
        if (_catalog.Kind(role) is not { } kind)
        {
            return;
        }

        int wanted = Math.Max(_players, LandTiles() / perRock);
        int placed = 0;
        for (int attempt = 0; attempt < wanted * 40 && placed < wanted; attempt++)
        {
            int cx = _rng.NextInt(_s), cy = _rng.NextInt(_s);
            int tx = cx - (kind.Width / 2), ty = cy - (kind.Height / 2);
            if (_startDist[Idx(cx, cy)] >= _startRadius + 10 && !TooClose(rocks, cx, cy, 9) && CanPlace(tx, ty, kind.Width, kind.Height, 1))
            {
                Put(role, kind, MapObject.Nature, tx, ty, string.Empty);
                rocks.Add((cx, cy));
                placed++;
            }
        }
    }

    private void PlaceFish()
    {
        MapObjectKind fish = _catalog.Kind(MapObjectRole.FishPatch)!.Value;
        var coastal = new List<int>();
        for (int y = 0; y < _s; y++)
        {
            for (int x = 0; x < _s; x++)
            {
                if (_ship[Idx(x, y)] != 0 && LandWithin(x, y, 3))
                {
                    coastal.Add(Idx(x, y));
                }
            }
        }

        int per = _o.Resources switch { MapResourceLevel.Low => 120, MapResourceLevel.High => 50, _ => 80 };
        int wanted = coastal.Count / per;
        var placed = new List<(int X, int Y)>();
        for (int attempt = 0; attempt < wanted * 20 && placed.Count < wanted; attempt++)
        {
            int t = coastal[_rng.NextInt(coastal.Count)];
            int x = t % _s, y = t / _s;
            if (!TooClose(placed, x, y, 5))
            {
                PutUnit(MapObjectRole.FishPatch, fish, MapObject.Nature, x, y, string.Empty);
                placed.Add((x, y));
            }
        }
    }

    // `count` objects of `role` on free land at least `minFromStarts` tiles from every castle.
    private void Scatter(MapObjectRole role, int count, int minFromStarts, bool footprint)
    {
        MapObjectKind kind = _catalog.Kind(role)!.Value;
        int placed = 0;
        for (int attempt = 0; attempt < count * 40 && placed < count; attempt++)
        {
            int x = _rng.NextInt(_s), y = _rng.NextInt(_s);
            if (_startDist[Idx(x, y)] < minFromStarts)
            {
                continue;
            }

            if (footprint && kind.HasFootprint ? CanPlace(x, y, kind.Width, kind.Height, 0) : FreeLand(x, y))
            {
                if (kind.HasFootprint)
                {
                    Put(role, kind, MapObject.Nature, x, y, string.Empty);
                }
                else
                {
                    PutUnit(role, kind, MapObject.Nature, x, y, string.Empty);
                }

                placed++;
            }
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private (int X, int Y) Polar(int x, int y, int distance, int angle)
    {
        var a = Tzar2.Core.Numerics.Angle.FromRaw(angle);
        return (x + (int)((distance * Tzar2.Core.Numerics.Trig.Cos(a).Raw) >> Tzar2.Core.Numerics.Fixed.FractionBits),
                y + (int)((distance * Tzar2.Core.Numerics.Trig.Sin(a).Raw) >> Tzar2.Core.Numerics.Fixed.FractionBits));
    }

    // Castle p is (one of) the nearest castles to (x, y): starting resources are not shared with a neighbour.
    private bool Mine(int p, int x, int y)
        => MapGenValidator.Distance(x, y, _sx[p], _sy[p]) == _startDist[Idx(x, y)];

    private bool OnStartLand(int x, int y, int p)
        => (uint)x < (uint)_s && (uint)y < (uint)_s && _label[Idx(x, y)] >= 0 && _label[Idx(x, y)] == _label[Idx(_sx[p], _sy[p])];

    // A free tile within dmin…dmax of (cx, cy) on player p's land (deterministic sweep after random tries).
    private bool FindFree(int cx, int cy, int dmin, int dmax, int p, out int x, out int y)
    {
        for (int attempt = 0; attempt < 48 + (32 * (dmax - dmin + 1)); attempt++)
        {
            int d = attempt < 48 ? _rng.NextInt(dmin, dmax + 1) : dmin + ((attempt - 48) / 32);
            int angle = attempt < 48 ? _rng.NextInt(Tzar2.Core.Numerics.Angle.UnitsPerTurn) : ((attempt - 48) % 32) * (Tzar2.Core.Numerics.Angle.UnitsPerTurn / 32);
            (x, y) = Polar(cx, cy, d, angle);
            if (FreeLand(x, y) && OnStartLand(x, y, p) && Mine(p, x, y))
            {
                return true;
            }
        }

        (x, y) = (-1, -1);
        return false;
    }

    // Walkable land without a footprint on it.
    private bool FreeLand(int x, int y)
        => (uint)x < (uint)_s && (uint)y < (uint)_s && _walk[Idx(x, y)] != 0 && _cliff[Idx(x, y)] == 0 && _occ[Idx(x, y)] != Blocked;

    // A footprint fits: its tiles walkable and unused; with a margin, the ring around it walkable and unblocked.
    private bool CanPlace(int tx, int ty, int w, int h, int margin, bool allowReserved = false)
    {
        if (tx - margin < 0 || ty - margin < 0 || tx + w + margin > _s || ty + h + margin > _s)
        {
            return false;
        }

        for (int y = ty - margin; y < ty + h + margin; y++)
        {
            for (int x = tx - margin; x < tx + w + margin; x++)
            {
                int t = Idx(x, y);
                bool inside = x >= tx && x < tx + w && y >= ty && y < ty + h;
                if (_walk[t] == 0 || _cliff[t] != 0 || (inside && !allowReserved ? _occ[t] != Free : _occ[t] == Blocked))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool TreeAllowed(int t)
    {
        if (_walk[t] == 0 || _cliff[t] != 0 || _occ[t] != Free)
        {
            return false;
        }

        int x = t % _s, y = t / _s, cs = _s + 1, c = (y * cs) + x;
        return _profile.GrowsTrees(_set[c]) && _profile.GrowsTrees(_set[c + 1]) && _profile.GrowsTrees(_set[c + cs]) && _profile.GrowsTrees(_set[c + cs + 1]);
    }

    private bool LandWithin(int x, int y, int r)
    {
        for (int dy = -r; dy <= r; dy++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((uint)nx < (uint)_s && (uint)ny < (uint)_s && _walk[Idx(nx, ny)] != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private int LandTiles()
    {
        int n = 0;
        foreach (byte b in _walk)
        {
            n += b;
        }

        return n;
    }

    private void TryPut(MapObjectRole role, MapObjectKind kind, int tx, int ty)
    {
        if (CanPlace(tx, ty, kind.Width, kind.Height, 0))
        {
            Put(role, kind, MapObject.Nature, tx, ty, string.Empty);
        }
    }

    private void Put(MapObjectRole role, MapObjectKind kind, byte owner, int tx, int ty, string civ)
    {
        _objects.Add(MapObject.AtTile(kind.KeyFor(civ), owner, tx, ty));
        _roleCounts[(int)role]++;
        for (int y = ty; y < ty + kind.Height; y++)
        {
            for (int x = tx; x < tx + kind.Width; x++)
            {
                _occ[Idx(x, y)] = Blocked;
            }
        }
    }

    private void PutUnit(MapObjectRole role, MapObjectKind kind, byte owner, int tx, int ty, string civ)
    {
        int half = MapData.UnitsPerTile / 2;
        _objects.Add(new MapObject(kind.KeyFor(civ), owner, (tx * MapData.UnitsPerTile) + half, (ty * MapData.UnitsPerTile) + half));
        _roleCounts[(int)role]++;
    }
}
