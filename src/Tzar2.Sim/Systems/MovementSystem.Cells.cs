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

    /// <summary>Most tiles one search for a way round looks at (its whole square of DetourRadius).</summary>
    public const int DetourNodes = 640;

    /// <summary>A unit held up for a while looks for its way round only this often (ticks; a crowd costs less).</summary>
    public const int DetourEvery = 8;

    /// <summary>
    /// Most tiles a search over the whole map for a way round the units in the way looks at, when the near one finds none
    /// (the original's re-plan with the tiles units stand on as walls, ReTzar 0x4a9060, 0x4df540).
    /// </summary>
    public const int RerouteNodes = 8192;

    /// <summary>Most such searches in a tick; the units after wait for the next (a crowd held up at once costs less).</summary>
    public const int ReroutesPerTick = 4;

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
    private int _reroutes;
    private readonly List<int> _way = [];
    private int[] _wayPoints = [];

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
        _reroutes = 0;
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
        if (m.State == MotionState.Moving && m.Mode != MotionMode.Waiting)
        {
            m.StallTicks++; // reset by progress (NextTile)
        }
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
        s.Rerouted = 0;
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

        // Progress is coming nearer the goal (or on along a path); a unit that steps without it for a while, round and
        // round a crowd or along a bank its straight line crossed, plans again.
        int cursor = m.PathCursor;
        int toward = SteeringTile(world, layer, f, i, ref m, here, goal);
        long nearer = Octile(here % layer.Width, here / layer.Width, goal % layer.Width, goal / layer.Width);
        if (m.Mode == MotionMode.Path && m.PathCursor != cursor)
        {
            m.BestDistance = int.MaxValue;
        }

        // A route or a flow field tracks its own progress along the way (the pre-pass: tiles left along the tree, the
        // field's cost); a path or a straight walk is judged here by the distance to the goal.
        bool tracked = m.Mode is MotionMode.Route or MotionMode.Flow;
        if (!tracked && nearer < m.BestDistance)
        {
            m.BestDistance = (int)Math.Min(nearer, int.MaxValue);
            m.StallTicks = 0;
        }
        else if (m.StallTicks >= 3 * ReplanTicks)
        {
            f.Out[i] = OutReplan;
            m.StallTicks = 0;
            m.BestDistance = int.MaxValue;
            s.Rerouted = 0;
            return -1;
        }

        int best = BestNeighbour(world, layer, f.Owner[i], here, toward);
        if (best >= 0 && Free(layer, best, i))
        {
            return best;
        }

        int blocker = best >= 0 ? _tileUnit[best] - 1 : -1;

        // The goal is held by a unit standing on it: an idle one that makes way steps off it (the order named that place:
        // a trigger's area, a work spot); one at work or of an enemy keeps it, and the unit settles next to it.
        if (Chebyshev(layer, here, goal) <= 1 && _tileUnit[goal] != 0 && _tileUnit[goal] - 1 != i && _tileEntering[goal] == 0)
        {
            int holder = _tileUnit[goal] - 1;
            if (goal == best && f.Moving[holder] == 0 && f.Anchored[holder] == 0 && MakesWay(world, f, i, holder))
            {
                int made = YieldAside(world, layer, f, holder, here, goal, tick);
                if (made == 2)
                {
                    return goal; // the two change places
                }

                if (made == 1)
                {
                    return -1; // it steps off: wait for it
                }
            }

            f.Out[i] = OutArrived;
            return -1;
        }

        // Head on with a friend that wants this unit's tile: the two pass each other (ORIG-02: columns cross a bridge, as
        // the original's units squeezed past on open ground, ADR 0188).
        if (blocker >= 0 && _tileEntering[best] == 0 && SwapWith(world, layer, f, i, blocker, here, best))
        {
            return best;
        }

        // A unit entering the tile, or making room, is waited for (the original: ten times 250 ms); one standing there,
        // with an order to walk or not, is gone round at once (ReTzar 0x4dec92: only a unit entering the cell is waited for;
        // the eighth play-test: units waited behind ones that waited themselves, and a crowd stood).
        if (blocker >= 0 && (_tileEntering[best] != 0 || MakingRoom(world, f, blocker)) && s.Waits < CellWaitMs / tickMs)
        {
            s.Waits++;
            return -1;
        }

        // Standing in the way, or waited long enough: as the original, the way is planned again with every unit's tile a
        // wall, friends and those on the move too (ReTzar 0x4debb0, 0x4df540 with flag 1): near by first, and over the
        // whole map when the near search cannot get on (the seventh play-test: units stood at a held gap instead of going
        // round the houses; the eighth: the back ranks did not go round the front ones that had stopped).
        bool due = m.StallTicks < DetourEvery || (tick + i) % DetourEvery == 0;
        bool reached = true;
        int round = due ? Detour(world, layer, f, i, here, goal, toward, everyUnit: true, out reached) : -1;
        if (due && !reached && Reroute(world, layer, f, i, ref m, ref s, here, goal, everyUnit: true) is >= 0 and var rerouted)
        {
            round = rerouted;
        }

        // No way round the units at all (a crowd across a street, a bridge): Tzar2's own, the way may lead through units
        // that make way (not an enemy's), dearer than free ground, and its first step asks the one there to step aside
        // (the sixth play-test: friends make way and come back instead of being shoved).
        if (due && round < 0)
        {
            round = Detour(world, layer, f, i, here, goal, toward, everyUnit: false, out reached);
            if (!reached && Reroute(world, layer, f, i, ref m, ref s, here, goal, everyUnit: false) is >= 0 and var through)
            {
                round = through; // through friends, it asks them below
            }
        }

        if (round >= 0 && Free(layer, round, i))
        {
            return round;
        }

        int there = round >= 0 ? _tileUnit[round] - 1 : -1;
        if (there >= 0 && MakesWay(world, f, i, there))
        {
            // Head on with it: the two pass each other.
            if (_tileEntering[round] == 0 && SwapWith(world, layer, f, i, there, here, round))
            {
                return round;
            }

            // Standing idle: it makes room (the owner's sixth play-test: allies step aside and come back instead of being
            // shoved along).
            if (f.Moving[there] == 0 && f.Anchored[there] == 0)
            {
                int made = YieldAside(world, layer, f, there, here, round, tick);
                if (made == 2)
                {
                    return round; // the two change places
                }

                if (made == 1)
                {
                    s.Waits = 0;
                    return -1; // it walks off the way: wait for it
                }
            }

            // Jammed itself (a crowd of walkers locked together): the two change places, so one of them gets on.
            if (SwapJammed(world, layer, f, i, there, here, round, tick))
            {
                return round;
            }
        }

        // Stuck: as the continuous model, settle near the goal, re-plan, give up.
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
            m.BestDistance = int.MaxValue;
            s.Rerouted = 0;
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
        => Detour(world, layer, f, i, here, goal, toward, everyUnit: false, out _);

    // As above, with every unit's tile a wall when `everyUnit` (the original's re-plan); `reached` is false when the search
    // found no way to where it aims within DetourRadius and DetourNodes and got not even half that far on toward it (the
    // units and the ground near by close the way).
    private int Detour(World world, NavLayer layer, Frame f, int i, int here, int goal, int toward, bool everyUnit, out bool reached)
    {
        int aim = Chebyshev(layer, here, goal) <= DetourRadius ? goal : toward;
        int end = Search(world, layer, f, i, here, aim, DetourRadius, DetourNodes, everyUnit, out reached);
        int w = layer.Width;
        reached |= end >= 0 && Octile(end % w, end / w, aim % w, aim / w) <= Octile(here % w, here / w, aim % w, aim / w) - (DetourRadius / 2 * NavLayer.StraightCost);
        return FirstStep(here, end);
    }

    // The original's re-plan round units (ReTzar 0x4a9060, 0x4df540): a search over the whole map to the goal (next to
    // it when a unit stands there) with every unit's tile a wall when `everyUnit`, else the tiles of units that do not
    // make way, friends that do dearer; the unit takes the way found as its path. Returns its first step, or -1 when
    // there is none or the tick's searches are spent. A unit searches so once a way (every unit, then through friends)
    // until it steps on or plans again.
    private int Reroute(World world, NavLayer layer, Frame f, int i, ref Motion m, ref CellStep s, int here, int goal, bool everyUnit)
    {
        byte flag = everyUnit ? (byte)1 : (byte)2;
        if ((s.Rerouted & flag) != 0 || _reroutes >= ReroutesPerTick)
        {
            return -1;
        }

        _reroutes++;
        s.Rerouted |= flag;
        int end = Search(world, layer, f, i, here, goal, int.MaxValue, RerouteNodes, everyUnit, out bool reached);
        if (!reached || end < 0)
        {
            return -1;
        }

        _way.Clear();
        for (int t = end; t != here && t >= 0; t = _from[t])
        {
            _way.Add(t);
        }

        // The path's points: the tiles where the way turns, and its end (a unit steps straight along a run on its own).
        int width = layer.Width;
        int count = 0;
        if (_wayPoints.Length < 2 * _way.Count)
        {
            _wayPoints = new int[Math.Max(2 * _way.Count, 2 * _wayPoints.Length)];
        }

        int previous = here;
        for (int k = _way.Count - 1; k >= 0; k--)
        {
            int t = _way[k];
            int next = k > 0 ? _way[k - 1] : -1;
            bool turns = next < 0 || (t % width) - (previous % width) != (next % width) - (t % width) || (t / width) - (previous / width) != (next / width) - (t / width);
            if (turns)
            {
                _wayPoints[count++] = layer.CenterX(t);
                _wayPoints[count++] = layer.CenterY(t);
            }

            previous = t;
        }

        world.Paths.Set(f.Entity[i].Index, _wayPoints.AsSpan(0, count));
        m.Mode = MotionMode.Path;
        m.PathCursor = 0;
        m.SteerX = _wayPoints[0];
        m.SteerY = _wayPoints[1];
        m.BestDistance = int.MaxValue;
        m.StallTicks = 0;
        return _way[^1];
    }

    // The first step of the way the last search found to `end`, or -1.
    private int FirstStep(int here, int end)
    {
        if (end < 0)
        {
            return -1;
        }

        int step = end;
        while (_from[step] != here && _from[step] >= 0)
        {
            step = _from[step];
        }

        return _from[step] == here ? step : -1;
    }

    // A search from `here` toward `aim` within `radius` tiles over walkable tiles: every unit is a wall when `everyUnit`,
    // else units that do not make way are walls and friends cost more; the tile it reached (`aim`, or next to it when a
    // unit stands there), or the one nearest `aim` when it did not (`reached` false), or -1; _from leads back from it.
    private int Search(World world, NavLayer layer, Frame f, int i, int here, int aim, int radius, int nodes, bool everyUnit, out bool reached)
    {
        int width = layer.Width;
        reached = false;
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
        while (_open.Count > 0 && expanded < nodes)
        {
            int node = _open.Pop(out _);
            expanded++;
            int nx0 = node % width;
            int ny0 = node / width;
            long h = Octile(nx0, ny0, ax, ay);
            if (node != here && (node == aim || (aimHeld && Math.Max(Math.Abs(nx0 - ax), Math.Abs(ny0 - ay)) <= 1)))
            {
                bestNode = node;
                reached = true;
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
                if (Math.Abs(nx - hx) > radius || Math.Abs(ny - hy) > radius || !Walkable(layer, nx0, ny0, nx, ny))
                {
                    continue;
                }

                int n = (ny * width) + nx;
                int extra = 0;
                if (_tileUnit[n] != 0 && _tileUnit[n] - 1 != i)
                {
                    int k = _tileUnit[n] - 1;
                    if (everyUnit || Holds(world, f, i, k))
                    {
                        continue; // an enemy, a creature of Nature, a friend at work or fighting where it stands: a wall
                    }

                    extra = f.Moving[k] != 0 || _tileEntering[n] != 0 ? MovingUnitCost : StandingUnitCost;
                }

                if (Barred(world, layer, f.Owner[i], node, n))
                {
                    continue;
                }

                int g = _cost[node] + (d % 2 == 0 ? NavLayer.StraightCost : NavLayer.DiagonalCost) + extra;
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

        return bestNode;
    }

    // What going through a unit's tile costs a way round, beyond the step: one walking on soon leaves it; one standing has
    // to make room.
    private const int MovingUnitCost = 2 * NavLayer.StraightCost;
    private const int StandingUnitCost = 6 * NavLayer.StraightCost;

    // Whether unit k keeps its tile against unit i: one that does not make way, or a friend standing at work (a peasant
    // chopping, building, farming) or fighting where it stands; those never step aside (YieldAside), so a way round goes
    // round them as round a wall (the original's occupied tiles, ReTzar 0x4df540).
    private static bool Holds(World world, Frame f, int i, int k)
        => !MakesWay(world, f, i, k)
           || (f.Moving[k] == 0 && (f.Anchored[k] != 0 || (world.Combats.IndexOf(f.Entity[k]) is >= 0 and var c && world.Combats.At(c).Target != 0)));

    // Whether unit j makes way for unit i (steps aside, or passes it head on): a friend, or a unit of a kingdom neither of
    // the two counts as an enemy. Enemies and creatures of Nature do not.
    private static bool MakesWay(World world, Frame f, int i, int j)
    {
        if (f.Friends(i, j))
        {
            return true;
        }

        byte a = f.Owner[i];
        byte b = f.Owner[j];
        return world.HasPlayer(a) && world.HasPlayer(b)
            && world.Diplomacy.StanceOf(a, b) != Kingdoms.Stance.Enemy && world.Diplomacy.StanceOf(b, a) != Kingdoms.Stance.Enemy;
    }

    // Two friends head on: j stands on `there`, on its way, and its next tile is i's `here`. Both step at once, each onto
    // the other's tile; i's step begins in Step.
    private bool SwapWith(World world, NavLayer layer, Frame f, int i, int j, int here, int there)
    {
        if (f.Cell[j] == 0 || f.Moving[j] == 0 || !MakesWay(world, f, i, j) || layer.Clearance != 1)
        {
            return false;
        }

        ref Motion mj = ref world.Motions.At(j);
        int s = world.CellSteps.IndexOf(f.Entity[j]);
        NavLayer lj = LayerOf(world, j);
        if (lj.Clearance != 1 || s < 0 || world.CellSteps.At(s).To >= 0)
        {
            return false;
        }

        // j's way goes through i's tile: it would come nearer where it heads for (on its own layer: a ninja climbs).
        int goal = lj.TileOf(mj.TargetX, mj.TargetY);
        int toward = SteeringTile(world, lj, f, j, ref mj, there, goal);
        int w = layer.Width;
        if (goal == there || Octile(here % w, here / w, toward % w, toward / w) >= Octile(there % w, there / w, toward % w, toward / w)
            || !CanStep(world, lj, f.Owner[j], there, here))
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
        if (s < 0 || world.CellSteps.At(s).To >= 0 || f.Cell[j] == 0 || LayerOf(world, j).Clearance != 1
            || layer.Clearance != 1 || world.Combats.IndexOf(e) is >= 0 and var c && world.Combats.At(c).Target != 0)
        {
            return 0; // walking, of another size, or fighting
        }

        // Making room already but hemmed in (a crowd): it changes places instead. Its own layer says where it may stand.
        NavLayer lj = LayerOf(world, j);
        ref CellStep step = ref world.CellSteps.At(s);
        int aside = step.Aside < 0 ? OffTheWay(world, lj, f, j, asker, at) : -1;
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
        if (i < 0 || !CanStep(world, lj, f.Owner[j], at, asker))
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

    // A walker that has not got on for this long gives its tile to one that needs it (a jam, ORIG-02).
    private const int JamTicks = 20;

    // j, a walker that makes way, stands jammed on `there` (no step under way and no progress for JamTicks), neither at
    // work nor striking: the two change places, j stepping back onto i's tile.
    private bool SwapJammed(World world, NavLayer layer, Frame f, int i, int j, int here, int there, int tick)
    {
        EntityId e = f.Entity[j];
        int s = world.CellSteps.IndexOf(e);
        if (s < 0 || f.Cell[j] == 0 || f.Moving[j] == 0 || f.Anchored[j] != 0 || layer.Clearance != 1 || LayerOf(world, j).Clearance != 1
            || world.CellSteps.At(s).To >= 0 || world.Motions.At(j).StallTicks < JamTicks
            || (world.Combats.IndexOf(e) is >= 0 and var c && world.Combats.At(c).Striking != 0)
            || !CanStep(world, LayerOf(world, j), f.Owner[j], there, here))
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
        return true;
    }

    // The navigation layer unit j walks on (its locomotor and clearance: a ninja's climbs).
    private static NavLayer LayerOf(World world, int j) => world.Navigation.Layers[world.Motions.At(j).Layer];

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
