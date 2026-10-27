using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;
using Tzar2.Sim.Ecs;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;
using Tzar2.Sim.Navigation;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Land units step cell by cell, one to a tile, as the original (ADR 0270; ReTzar's reading of 0x4def20, 0x4debb0):
/// a step takes the next tile at once and the unit slides there, a tile in any of the eight directions taking the same
/// time; a unit entering the next tile is waited for, a unit standing there is gone round; with no way round an idle
/// friend steps aside and comes back. Nobody is pushed. The routes, flow fields and arrival slots are the continuous
/// model's; only the step is the original's.
/// </summary>
public sealed partial class MovementSystem
{
    /// <summary>Ticks a unit waits behind one entering its next tile before it goes round (the original: 10 × 250 ms).</summary>
    public const int CellWaitMs = 2500;

    /// <summary>How far (tiles) a unit looks for its way round the units in its way.</summary>
    public const int DetourRadius = 12;

    /// <summary>Most tiles one search for a way round looks at.</summary>
    public const int DetourNodes = 1200;

    /// <summary>How long a unit that stepped aside for a friend stands before it goes back.</summary>
    public const int YieldReturnMs = 2000;

    /// <summary>After this long a unit that stepped aside no longer goes back.</summary>
    public const int YieldForgetMs = 30_000;

    // The unit on each anchor tile of the land layers (frame index + 1, 0 for none) and whether it is entering it; the
    // tiles set this tick, to clear them next tick.
    private int[] _tileUnit = [];
    private byte[] _tileEntering = [];
    private readonly List<int> _tilesSet = [];

    // The search for a way round.
    private readonly MinHeap _open = new();
    private int[] _seen = [];
    private int[] _cost = [];
    private int[] _from = [];
    private int _search;

    private static readonly int[] StepX = [1, 1, 0, -1, -1, -1, 0, 1];
    private static readonly int[] StepY = [0, 1, 1, 1, 0, -1, -1, -1];

    private static bool IsCellUnit(World world, ushort typeId)
    {
        EntityTypeDefinition type = world.Ruleset.EntityTypes[typeId];
        return world.Ruleset.Gameplay.Movement == MovementModel.Cells && type.Locomotor is not (Locomotor.Ship or Locomotor.Air);
    }

    // A unit carried away (into a holder, which takes its motion) leaves its step behind; every tick, also when no land
    // unit is on the map.
    private static void DropCarriedSteps(World world)
    {
        ComponentStore<CellStep> steps = world.CellSteps;
        for (int k = steps.Count - 1; k >= 0; k--)
        {
            EntityId e = steps.Entities[k];
            if (!world.Motions.Has(e) || world.IsHeld(e))
            {
                steps.RemoveEntity(e);
            }
        }
    }

    // The land units' steps, in unit order (sequential: each step takes its tile for the units after it).
    private void CellPass(World world, NavigationGrid nav)
    {
        Frame f = _frame;
        MapState map = world.Map;
        int tiles = map.Width * map.Height;
        if (_tileUnit.Length != tiles)
        {
            _tileUnit = new int[tiles];
            _tileEntering = new byte[tiles];
            _seen = new int[tiles];
            _cost = new int[tiles];
            _from = new int[tiles];
            _tilesSet.Clear();
            _search = 0;
        }

        foreach (int t in _tilesSet)
        {
            _tileUnit[t] = 0;
            _tileEntering[t] = 0;
        }

        _tilesSet.Clear();
        ComponentStore<Motion> motions = world.Motions;
        ComponentStore<CellStep> steps = world.CellSteps;

        // Who is where: a unit belongs to the tile it enters, else to the one it stands on. A unit whose tile is taken
        // already (spawned there, landed there) is displaced and steps off it.
        for (int i = 0; i < f.Count; i++)
        {
            if (f.Cell[i] == 0)
            {
                continue;
            }

            ref Motion m = ref motions.At(i);
            NavLayer layer = nav.Layers[m.Layer];
            int s = steps.IndexOf(f.Entity[i]);
            bool entering = s >= 0 && steps.At(s).To >= 0;
            int tile = entering ? steps.At(s).To : layer.TileOf(f.Px[i], f.Py[i]);
            f.Displaced[i] = (byte)(Claim(layer, tile, i, entering) ? 0 : 1);
        }

        int tick = world.Tick.Value;
        int tickMs = world.Config.TickLengthMilliseconds;
        for (int i = 0; i < f.Count; i++)
        {
            if (f.Cell[i] == 0)
            {
                continue;
            }

            ref Motion m = ref motions.At(i);
            EntityId e = f.Entity[i];
            if (!steps.Has(e))
            {
                steps.Set(e, new CellStep { To = -1, Home = -1, Aside = -1, FromX = f.Px[i], FromY = f.Py[i] });
            }

            Step(world, nav.Layers[m.Layer], f, i, ref m, ref steps.Get(e), tick, tickMs);
        }
    }

    // One unit's tick: on along its step, then, standing on a tile, the next step.
    private void Step(World world, NavLayer layer, Frame f, int i, ref Motion m, ref CellStep s, int tick, int tickMs)
    {
        int x = f.Px[i];
        int y = f.Py[i];
        int speed = m.GroupSpeed > 0 ? Math.Min(m.SpeedPerTick, m.GroupSpeed) : m.SpeedPerTick;
        int budget = Math.Max(1, speed);
        f.Qx[i] = x;
        f.Qy[i] = y;
        if (s.To >= 0)
        {
            int cx = layer.CenterX(s.To);
            int cy = layer.CenterY(s.To);
            m.VelocityX = cx - s.FromX;
            m.VelocityY = cy - s.FromY;
            s.Progress += budget;
            if (s.Progress < MapState.UnitsPerTile)
            {
                f.Fx[i] = s.FromX + (int)((long)(cx - s.FromX) * s.Progress / MapState.UnitsPerTile);
                f.Fy[i] = s.FromY + (int)((long)(cy - s.FromY) * s.Progress / MapState.UnitsPerTile);
                return;
            }

            budget = s.Progress - MapState.UnitsPerTile; // carried into the next step
            (x, y) = (cx, cy);
            s.To = -1;
            s.Progress = 0;
            m.BestDistance = int.MaxValue; // reached a tile: progress
        }

        f.Fx[i] = x;
        f.Fy[i] = y;
        int here = layer.TileOf(x, y);
        int next = NextTile(world, layer, f, i, ref m, ref s, here, x, y, tick, tickMs);
        if (next < 0)
        {
            if (f.Moving[i] == 0 || m.State != MotionState.Moving)
            {
                m.VelocityX = 0;
                m.VelocityY = 0;
            }

            return;
        }

        // The step begins: the tile is the unit's at once.
        Release(layer, here, i);
        Claim(layer, next, i, entering: true);
        s.To = next;
        s.FromX = x;
        s.FromY = y;
        s.Progress = Math.Min(budget, MapState.UnitsPerTile - 1);
        s.Waits = 0;
        int nx = layer.CenterX(next);
        int ny = layer.CenterY(next);
        m.VelocityX = nx - x;
        m.VelocityY = ny - y;
        f.Fx[i] = x + (int)((long)(nx - x) * s.Progress / MapState.UnitsPerTile);
        f.Fy[i] = y + (int)((long)(ny - y) * s.Progress / MapState.UnitsPerTile);
    }

    // The tile to step to now, or -1 to stand; sets the unit's outcome (arrived, re-plan, give up).
    private int NextTile(World world, NavLayer layer, Frame f, int i, ref Motion m, ref CellStep s, int here, int x, int y, int tick, int tickMs)
    {
        // On ground a structure now covers: to the nearest walkable tile, straight there.
        if (!layer.IsWalkable(here))
        {
            int escape = NavQueries.NearestWalkable(layer, here % layer.Width, here / layer.Width, EscapeRadius);
            return escape >= 0 && Free(layer, escape, i) ? escape : -1;
        }

        // Another unit's tile (spawned or landed on it): off to a free neighbour.
        if (f.Displaced[i] != 0)
        {
            return FreeNeighbour(world, layer, f, i, here, -1);
        }

        bool moving = m.State == MotionState.Moving && m.Mode != MotionMode.Waiting;
        if (!moving)
        {
            // Off its tile's centre (a new unit, or one stopped mid-way by an older save): onto it.
            if (x != layer.CenterX(here) || y != layer.CenterY(here))
            {
                return m.State == MotionState.Moving ? -1 : here;
            }

            if (m.State == MotionState.Moving)
            {
                return -1; // its path is on the way
            }

            // Making room for a friend: on to the tile off its way; then, after a while, back home.
            if (s.Aside >= 0 && (s.Aside == here || tick - s.HomeTick > YieldForgetMs / tickMs))
            {
                s.Aside = -1;
            }

            if (s.Aside >= 0)
            {
                return StepTo(world, layer, f, i, here, s.Aside);
            }

            if (s.Home >= 0 && (s.Home == here || tick - s.HomeTick > YieldForgetMs / tickMs))
            {
                s.Home = -1;
            }

            if (s.Home >= 0 && tick - s.HomeTick >= YieldReturnMs / tickMs && Free(layer, s.Home, i))
            {
                return StepTo(world, layer, f, i, here, s.Home);
            }

            return -1;
        }

        s.Home = -1; // an order: it no longer goes back
        s.Aside = -1;
        int goal = layer.TileOf(m.TargetX, m.TargetY);
        if (here == goal)
        {
            f.Out[i] = OutArrived;
            return -1;
        }

        int toward = SteeringTile(world, layer, f, i, ref m, here, goal);
        int best = BestNeighbour(world, layer, f.Owner[i], here, toward);
        if (best >= 0 && Free(layer, best, i))
        {
            m.StallTicks = 0;
            return best;
        }

        int blocker = best >= 0 ? _tileUnit[best] - 1 : -1;

        // The goal is held by a unit standing on it: the unit settles next to it.
        if (Chebyshev(layer, here, goal) <= 1 && _tileUnit[goal] != 0 && _tileUnit[goal] - 1 != i && _tileEntering[goal] == 0)
        {
            f.Out[i] = OutArrived;
            return -1;
        }

        // Head on with a friend that wants this unit's tile: the two pass each other (ORIG-02: columns cross a bridge, as
        // the original's units squeezed past on open ground, ADR 0188).
        if (blocker >= 0 && _tileEntering[best] == 0 && SwapWith(world, layer, f, i, blocker, here, best))
        {
            m.StallTicks = 0;
            return best;
        }

        // A unit entering the tile, or making room, is waited for (the original: ten times 250 ms).
        if (blocker >= 0 && (_tileEntering[best] != 0 || f.Moving[blocker] != 0 || MakingRoom(world, f, blocker)) && s.Waits < CellWaitMs / tickMs)
        {
            s.Waits++;
            return -1;
        }

        // Standing in the way, or waited long enough: round the units.
        int round = Detour(world, layer, f, i, here, goal, toward);
        if (round >= 0)
        {
            m.StallTicks = 0;
            return round;
        }

        // No way round: an idle friend standing in the way makes room (the owner's sixth play-test: allies step aside and
        // come back instead of being shoved along).
        if (blocker >= 0 && f.Moving[blocker] == 0 && f.Friends(i, blocker) && f.Anchored[blocker] == 0)
        {
            int made = YieldAside(world, layer, f, blocker, here, best, tick);
            if (made == 2)
            {
                m.StallTicks = 0;
                return best; // the two change places
            }

            if (made == 1)
            {
                s.Waits = 0;
                return -1; // it walks off the way: wait for it
            }
        }

        // Stuck: as the continuous model, settle near the goal, re-plan, give up.
        m.StallTicks++;
        long toGoal = Chebyshev(layer, here, goal) * (long)MapState.UnitsPerTile;
        if (toGoal <= m.SettleRadius + MapState.UnitsPerTile && m.StallTicks >= SettleTicks)
        {
            f.Out[i] = OutArrived;
        }
        else if (m.StallTicks >= ReplanTicks)
        {
            bool giveUp = m.Replans >= MaxCrowdReplans || (m.Replans >= MaxReplans && blocker < 0);
            f.Out[i] = giveUp ? OutGiveUp : OutReplan;
            m.StallTicks = 0;
        }

        return -1;
    }

    // The tile the unit heads for now: the next point of its path, flow field or route, or its goal.
    private static int SteeringTile(World world, NavLayer layer, Frame f, int i, ref Motion m, int here, int goal)
    {
        int sx;
        int sy;
        if (m.Mode == MotionMode.Path)
        {
            ReadOnlySpan<int> w = world.Paths.Get(f.Entity[i].Index);
            int last = (w.Length / 2) - 1;
            int cursor = Math.Clamp(m.PathCursor, 0, Math.Max(0, last));
            while (cursor < last && layer.TileOf(w[2 * cursor], w[(2 * cursor) + 1]) == here)
            {
                cursor++;
                m.Replans = 0; // real progress
            }

            m.PathCursor = cursor;
            (sx, sy) = last >= 0 ? (w[2 * cursor], w[(2 * cursor) + 1]) : (m.TargetX, m.TargetY);
        }
        else if (m.Mode is MotionMode.Flow or MotionMode.Route)
        {
            (sx, sy) = (m.SteerX, m.SteerY);
        }
        else
        {
            (sx, sy) = (m.TargetX, m.TargetY);
        }

        m.SteerX = sx;
        m.SteerY = sy;
        int tile = layer.TileOf(sx, sy);
        return tile == here ? goal : tile;
    }

    // The neighbour that brings the unit nearest the tile it heads for (octile), terrain and gates only; -1 if none
    // brings it nearer.
    private static int BestNeighbour(World world, NavLayer layer, byte owner, int here, int toward)
    {
        int hx = here % layer.Width;
        int hy = here / layer.Width;
        int tx = toward % layer.Width;
        int ty = toward / layer.Width;
        int best = -1;
        long bestCost = Octile(hx, hy, tx, ty);
        for (int d = 0; d < 8; d++)
        {
            int nx = hx + StepX[d];
            int ny = hy + StepY[d];
            if (!Walkable(layer, hx, hy, nx, ny))
            {
                continue;
            }

            int n = (ny * layer.Width) + nx;
            long cost = Octile(nx, ny, tx, ty);
            if (cost < bestCost && !Barred(world, layer, owner, here, n))
            {
                bestCost = cost;
                best = n;
            }
        }

        return best;
    }

    // A way round the units in the way: a search over walkable tiles that other units leave free, toward the goal when
    // it is near and free, else toward the tile the unit heads for (or next to it when a unit stands there); the
    // first step of the best way found, or -1 (the original searches again around the units, 0x4debb0).
    private int Detour(World world, NavLayer layer, Frame f, int i, int here, int goal, int toward)
    {
        int width = layer.Width;
        int aim = Chebyshev(layer, here, goal) <= DetourRadius ? goal : toward;
        bool aimHeld = _tileUnit[aim] != 0 && _tileUnit[aim] - 1 != i;
        int ax = aim % width;
        int ay = aim / width;
        int hx = here % width;
        int hy = here / width;
        _search++;
        if (_search == int.MaxValue)
        {
            Array.Clear(_seen);
            _search = 1;
        }

        _open.Clear();
        _seen[here] = _search;
        _cost[here] = 0;
        _from[here] = -1;
        _open.Push((ulong)Octile(hx, hy, ax, ay), here);
        int bestNode = -1;
        long bestH = Octile(hx, hy, ax, ay);
        int expanded = 0;
        while (_open.Count > 0 && expanded < DetourNodes)
        {
            int node = _open.Pop(out _);
            expanded++;
            int nx0 = node % width;
            int ny0 = node / width;
            long h = Octile(nx0, ny0, ax, ay);
            if (node != here && (node == aim || (aimHeld && Math.Max(Math.Abs(nx0 - ax), Math.Abs(ny0 - ay)) <= 1)))
            {
                bestNode = node;
                break;
            }

            if (node != here && h < bestH)
            {
                bestH = h;
                bestNode = node;
            }

            for (int d = 0; d < 8; d++)
            {
                int nx = nx0 + StepX[d];
                int ny = ny0 + StepY[d];
                if (Math.Abs(nx - hx) > DetourRadius || Math.Abs(ny - hy) > DetourRadius || !Walkable(layer, nx0, ny0, nx, ny))
                {
                    continue;
                }

                int n = (ny * width) + nx;
                if ((_tileUnit[n] != 0 && _tileUnit[n] - 1 != i) || Barred(world, layer, f.Owner[i], node, n))
                {
                    continue;
                }

                int g = _cost[node] + (d % 2 == 0 ? NavLayer.StraightCost : NavLayer.DiagonalCost);
                if (_seen[n] == _search && _cost[n] <= g)
                {
                    continue;
                }

                _seen[n] = _search;
                _cost[n] = g;
                _from[n] = node;
                _open.Push((ulong)(g + Octile(nx, ny, ax, ay)), n);
            }
        }

        if (bestNode < 0)
        {
            return -1;
        }

        // Back to the first step.
        int step = bestNode;
        while (_from[step] != here && _from[step] >= 0)
        {
            step = _from[step];
        }

        return _from[step] == here && Free(layer, step, i) ? step : -1;
    }

    // Two friends head on: j stands on `there`, on its way, and its next tile is i's `here`. Both step at once, each onto
    // the other's tile; i's step begins in Step.
    private bool SwapWith(World world, NavLayer layer, Frame f, int i, int j, int here, int there)
    {
        if (f.Cell[j] == 0 || f.Moving[j] == 0 || !f.Friends(i, j) || layer.Clearance != 1)
        {
            return false;
        }

        ref Motion mj = ref world.Motions.At(j);
        int s = world.CellSteps.IndexOf(f.Entity[j]);
        if (mj.Layer != layer.Index || s < 0 || world.CellSteps.At(s).To >= 0)
        {
            return false;
        }

        // j's way goes through i's tile: it would come nearer where it heads for.
        int goal = layer.TileOf(mj.TargetX, mj.TargetY);
        int toward = SteeringTile(world, layer, f, j, ref mj, there, goal);
        int w = layer.Width;
        if (goal == there || Octile(here % w, here / w, toward % w, toward / w) >= Octile(there % w, there / w, toward % w, toward / w)
            || !CanStep(world, layer, f.Owner[j], there, here))
        {
            return false;
        }

        ref CellStep step = ref world.CellSteps.At(s);
        _tileUnit[here] = j + 1;
        _tileEntering[here] = 1;
        _tileUnit[there] = i + 1;
        _tileEntering[there] = 1;
        _tilesSet.Add(here);
        _tilesSet.Add(there);
        step.To = here;
        step.FromX = layer.CenterX(there);
        step.FromY = layer.CenterY(there);
        step.Progress = 0;
        step.Waits = 0;
        mj.StallTicks = 0;
        return true;
    }

    // An idle friend j standing in the way (on `at`) makes room for the unit coming from `asker`: it walks to the nearest
    // free tile off the asker's way (within three steps), or, with none, the two change places; it goes back later
    // (YieldReturnMs). 0: it cannot; 1: it walks off the way; 2: the two change places (the asker steps onto `at` now).
    private int YieldAside(World world, NavLayer layer, Frame f, int j, int asker, int at, int tick)
    {
        EntityId e = f.Entity[j];
        int s = world.CellSteps.IndexOf(e);
        if (s < 0 || world.CellSteps.At(s).To >= 0 || world.CellSteps.At(s).Aside >= 0 || f.Cell[j] == 0 || world.Motions.At(j).Layer != layer.Index
            || layer.Clearance != 1 || world.Combats.IndexOf(e) is >= 0 and var c && world.Combats.At(c).Target != 0)
        {
            return 0; // walking, making room already, of another size, or fighting
        }

        ref CellStep step = ref world.CellSteps.At(s);
        int aside = OffTheWay(world, layer, f, j, asker, at);
        if (aside >= 0)
        {
            if (step.Home < 0)
            {
                step.Home = at;
            }

            step.HomeTick = tick;
            step.Aside = aside;
            return 1;
        }

        // No room off the way (a corridor): the two change places, and j goes back after the asker has passed.
        int i = _tileUnit[asker] - 1;
        if (i < 0 || !CanStep(world, layer, f.Owner[j], at, asker))
        {
            return 0;
        }

        _tileUnit[asker] = j + 1;
        _tileEntering[asker] = 1;
        _tileUnit[at] = i + 1;
        _tileEntering[at] = 1;
        _tilesSet.Add(asker);
        _tilesSet.Add(at);
        step.To = asker;
        step.FromX = layer.CenterX(at);
        step.FromY = layer.CenterY(at);
        step.Progress = 0;
        if (step.Home < 0)
        {
            step.Home = at;
        }

        step.HomeTick = tick;
        return 2;
    }

    // Whether unit j walks off a friend's way.
    private static bool MakingRoom(World world, Frame f, int j)
        => world.CellSteps.IndexOf(f.Entity[j]) is >= 0 and var s && world.CellSteps.At(s).Aside >= 0;

    // The nearest free tile within three steps of `at` (over free walkable tiles) that is not on the asker's way: not
    // the asker's tile and not on the line it walks from `asker` through `at`; -1 if none.
    private int OffTheWay(World world, NavLayer layer, Frame f, int j, int asker, int at)
    {
        int width = layer.Width;
        int ax = at % width;
        int ay = at / width;
        int dx = Math.Sign(ax - (asker % width));
        int dy = Math.Sign(ay - (asker / width));
        _search++;
        if (_search == int.MaxValue)
        {
            Array.Clear(_seen);
            _search = 1;
        }

        _open.Clear();
        _seen[at] = _search;
        _cost[at] = 0;
        _open.Push(0, at);
        while (_open.Count > 0)
        {
            int node = _open.Pop(out ulong depth);
            int nx0 = node % width;
            int ny0 = node / width;
            if (node != at)
            {
                // On the asker's way: at + k (dx, dy) for k >= 1.
                int ox = nx0 - ax;
                int oy = ny0 - ay;
                int k = dx != 0 ? ox / dx : oy / dy;
                bool onWay = k >= 1 && ox == k * dx && oy == k * dy;
                if (!onWay)
                {
                    return node;
                }
            }

            if (depth >= 3)
            {
                continue;
            }

            for (int d = 0; d < 8; d++)
            {
                int nx = nx0 + StepX[d];
                int ny = ny0 + StepY[d];
                if (!Walkable(layer, nx0, ny0, nx, ny))
                {
                    continue;
                }

                int n = (ny * width) + nx;
                if (_seen[n] == _search || n == asker || !Free(layer, n, j) || Barred(world, layer, f.Owner[j], node, n))
                {
                    continue;
                }

                _seen[n] = _search;
                _open.Push(depth + 1, n);
            }
        }

        return -1;
    }

    // The next step toward a tile a few steps away: the free neighbour nearest it, else the way round; -1 to wait.
    private int StepTo(World world, NavLayer layer, Frame f, int i, int here, int target)
    {
        int best = BestNeighbour(world, layer, f.Owner[i], here, target);
        if (best >= 0 && Free(layer, best, i))
        {
            return best;
        }

        return Detour(world, layer, f, i, here, target, target);
    }

    // A free neighbour of a tile, in a fixed order starting across from `avoid`, never `avoid` itself; -1 if none.
    private int FreeNeighbour(World world, NavLayer layer, Frame f, int i, int at, int avoid)
    {
        int ax = at % layer.Width;
        int ay = at / layer.Width;
        int first = 0;
        if (avoid >= 0)
        {
            // Sideways to the asker first: its way goes on through `at`.
            int dx = Math.Sign((avoid % layer.Width) - ax);
            int dy = Math.Sign((avoid / layer.Width) - ay);
            for (int d = 0; d < 8; d++)
            {
                if (StepX[d] == dx && StepY[d] == dy)
                {
                    first = d + 2;
                }
            }
        }

        for (int k = 0; k < 8; k++)
        {
            int offset = (k + 1) / 2 * (k % 2 == 1 ? 1 : -1); // 0, 1, -1, 2, -2, ...
            int d = (first + offset + 16) % 8;
            int nx = ax + StepX[d];
            int ny = ay + StepY[d];
            if (!Walkable(layer, ax, ay, nx, ny))
            {
                continue;
            }

            int n = (ny * layer.Width) + nx;
            if (n != avoid && Free(layer, n, i) && !Barred(world, layer, f.Owner[i], at, n))
            {
                return n;
            }
        }

        return -1;
    }

    // Whether a step from (x0, y0) to its neighbour (x1, y1) is on walkable ground, a diagonal one cutting no corner.
    private static bool Walkable(NavLayer layer, int x0, int y0, int x1, int y1)
        => layer.IsWalkable(x1, y1) && (x0 == x1 || y0 == y1 || (layer.IsWalkable(x1, y0) && layer.IsWalkable(x0, y1)));

    // An open gate lets only its owner and the kingdoms it treats as allies through (ADR 0102).
    private static bool Barred(World world, NavLayer layer, byte owner, int from, int to)
        => world.GateBars(owner, layer.CenterX(from), layer.CenterY(from), layer.CenterX(to), layer.CenterY(to));

    // Whether a unit could step from its tile to a neighbour now: walkable and not barred.
    private static bool CanStep(World world, NavLayer layer, byte owner, int from, int to)
        => Walkable(layer, from % layer.Width, from / layer.Width, to % layer.Width, to / layer.Width) && !Barred(world, layer, owner, from, to);

    private static long Octile(int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        return ((long)NavLayer.StraightCost * Math.Max(dx, dy)) + ((long)(NavLayer.DiagonalCost - NavLayer.StraightCost) * Math.Min(dx, dy));
    }

    private static int Chebyshev(NavLayer layer, int a, int b)
        => Math.Max(Math.Abs((a % layer.Width) - (b % layer.Width)), Math.Abs((a / layer.Width) - (b / layer.Width)));

    // The map tiles a unit of the layer's clearance covers from an anchor tile.
    private bool Free(NavLayer layer, int anchor, int i)
    {
        int ax = anchor % layer.Width;
        int ay = anchor / layer.Width;
        for (int dy = 0; dy < layer.Clearance; dy++)
        {
            for (int dx = 0; dx < layer.Clearance; dx++)
            {
                int t = ((ay + dy) * layer.Width) + ax + dx;
                if (ax + dx >= layer.Width || ay + dy >= layer.Height || (_tileUnit[t] != 0 && _tileUnit[t] - 1 != i))
                {
                    return false;
                }
            }
        }

        return true;
    }

    // Takes the tiles a unit covers from an anchor tile; false when one is someone else's already.
    private bool Claim(NavLayer layer, int anchor, int i, bool entering)
    {
        bool all = true;
        int ax = anchor % layer.Width;
        int ay = anchor / layer.Width;
        for (int dy = 0; dy < layer.Clearance; dy++)
        {
            for (int dx = 0; dx < layer.Clearance; dx++)
            {
                if (ax + dx >= layer.Width || ay + dy >= layer.Height)
                {
                    continue;
                }

                int t = ((ay + dy) * layer.Width) + ax + dx;
                if (_tileUnit[t] != 0 && _tileUnit[t] - 1 != i)
                {
                    all = false;
                    continue;
                }

                _tileUnit[t] = i + 1;
                _tileEntering[t] = entering ? (byte)1 : (byte)0;
                _tilesSet.Add(t);
            }
        }

        return all;
    }

    // Gives back the tiles a unit covered from an anchor tile.
    private void Release(NavLayer layer, int anchor, int i)
    {
        int ax = anchor % layer.Width;
        int ay = anchor / layer.Width;
        for (int dy = 0; dy < layer.Clearance; dy++)
        {
            for (int dx = 0; dx < layer.Clearance; dx++)
            {
                int t = ((ay + dy) * layer.Width) + ax + dx;
                if (ax + dx < layer.Width && ay + dy < layer.Height && _tileUnit[t] == i + 1)
                {
                    _tileUnit[t] = 0;
                    _tileEntering[t] = 0;
                }
            }
        }
    }
}
