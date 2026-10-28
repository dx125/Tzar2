using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Sim.Components;
using Tzar2.Sim.Ecs;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;
using Tzar2.Sim.Navigation;
using Tzar2.Sim.Threading;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 8: movement, local avoidance and occupancy (TDD §9.2 REQ-PATH-05…07, ADR 0025).
/// </summary>
/// <remarks>
/// <para><b>Pre-pass</b> (sequential): follow orders, path re-validation after obstacle changes, flow-field sub-goals
/// (the farthest visible tile of the field's descent within <see cref="LookAhead"/> steps) and the switch to the final
/// approach; then a flat copy of every mobile unit (position, radius, owner, plane, velocity) and a spatial grid.</para>
/// <para><b>Steer</b> (fork/join over chunks of <see cref="ChunkSize"/> units): toward the steering point at the
/// unit's (or its group's) speed, with integer avoidance: units ahead in the way are passed on the right ("keep
/// right", so opposing columns pass each other), units moving the same way are followed instead. Terrain collision
/// slides along walls. Progress is tracked: a unit near its target that cannot get closer for
/// <see cref="SettleTicks"/> settles there (it never orbits, ORIG-02); a unit without progress for
/// <see cref="ReplanTicks"/> re-plans, and after <see cref="MaxReplans"/> it gives up.</para>
/// <para><b>Resolve</b> (fork/join): overlaps are pushed apart (Jacobi step from the steered positions). Idle friendly
/// units yield to movers (shoving, REQ-PATH-06); enemies do not yield. A unit standing on an unwalkable spot (a
/// building placed on it) is pushed to the nearest walkable tile.</para>
/// <para><b>Merge</b> (sequential, ascending unit order): positions, facing, occupancy, events, re-plans. Jobs read only
/// the flat copy and write only their own units' outputs, so results do not depend on the worker count (ADR 0017).</para>
/// </remarks>
public sealed partial class MovementSystem : ISimSystem
{
    /// <summary>Units per job.</summary>
    public const int ChunkSize = 256;

    /// <summary>Spatial grid cell in world units (two tiles; covers every neighbour query).</summary>
    public const int GridCell = 2 * MapState.UnitsPerTile;

    /// <summary>A waypoint counts as reached within this distance.</summary>
    public const int AdvanceRadius = 640;

    /// <summary>Flow-field look-ahead in tiles.</summary>
    public const int LookAhead = 10;

    /// <summary>Ticks between flow sub-goal refreshes.</summary>
    public const int RefreshTicks = 8;

    /// <summary>Ticks without progress after which a unit near its target settles (1 s).</summary>
    public const int SettleTicks = 20;

    /// <summary>Ticks without progress after which a unit re-plans (REQ-PATH-07: 2 s).</summary>
    public const int ReplanTicks = 40;

    /// <summary>Re-plans before a unit stuck alone (or held by an enemy) gives up.</summary>
    public const int MaxReplans = 3;

    /// <summary>Re-plans before a unit jammed in a friendly crowd gives up (about 60 s without progress).</summary>
    public const int MaxCrowdReplans = 30;

    /// <summary>Tiles searched for walkable ground around a unit standing inside a structure (largest footprint / 2 + margin).</summary>
    public const int EscapeRadius = (EntityTypeDefinition.MaxFootprintTiles / 2) + EntityTypeDefinition.MaxClearanceTiles + 1;

    /// <summary>Ticks between follow checks.</summary>
    public const int FollowInterval = 10;

    /// <summary>How far beyond its follow range an idle follower may have been shoved and still walk back.</summary>
    public const int FollowDriftTiles = 6;

    /// <summary>Most neighbours a unit considers per tick (bounds the work in pathological stacks).</summary>
    public const int MaxNeighbours = 48;

    private const int OutArrived = 1;
    private const int OutReplan = 2;
    private const int OutGiveUp = 4;

    private readonly SteerJob _steer = new();
    private readonly RouteJob _route = new();
    private readonly RouteScratch _routeScratch = new();
    private readonly ResolveJob _resolve = new();
    private readonly Frame _frame = new();

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Movement;

    /// <inheritdoc />
    public string Name => "movement";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        NavigationGrid nav = world.Navigation;
        ITickProfiler? profiler = context.Profiler;
        nav.EnsureUpToDate();
        world.RefreshGateBars();
        if (world.CellSteps.Count != 0)
        {
            DropCarriedSteps(world);
        }

        profiler?.Begin("pre-pass");
        PrePass(context, world, nav);
        profiler?.End();
        profiler?.Begin("capture");
        _frame.Capture(world);
        profiler?.End();
        int jobs = (_frame.Count + ChunkSize - 1) / ChunkSize;
        if (jobs == 0)
        {
            return;
        }

        _steer.Frame = _frame;
        _steer.World = world;
        profiler?.Begin("steer");
        context.Workers.Run(_steer, jobs);
        profiler?.End();
        _resolve.Frame = _frame;
        _resolve.World = world;
        profiler?.Begin("resolve");
        context.Workers.Run(_resolve, jobs);
        profiler?.End();
        if (_frame.Cells)
        {
            profiler?.Begin("cells");
            CellPass(world, nav); // land units step cell by cell (ADR 0270)
            profiler?.End();
        }

        profiler?.Begin("merge");
        Merge(world);
        profiler?.End();
    }

    // ----- pre-pass ----------------------------------------------------------------------------------------------

    private void PrePass(SimContext context, World world, NavigationGrid nav)
    {
        ComponentStore<Motion> motions = world.Motions;
        int tick = world.Tick.Value;

        // Route following is the bulk of the pre-pass and touches only the unit's own motion: it runs as fork/join jobs
        // first; its plan clears and re-plan requests are applied below in unit order, so the outcome (including the
        // path-request queue order) is the same as a sequential pass (ADR 0080).
        // If a route job throws, other jobs have already marked their re-plans without the Apply below; the simulation
        // is then faulted and refuses to continue (Simulation.IsFaulted), so the half-applied state is never used.
        _route.Prepare(world, nav, tick);
        context.Workers.Run(_route, _route.JobCount);
        _routeScratch.Reset();
        ReadOnlySpan<RouteAction> routed = _route.Actions;
        for (int i = 0; i < motions.Count; i++)
        {
            EntityId entity = motions.Entities[i];
            ref Motion m = ref motions.At(i);
            if (m.Order == MotionOrder.Follow && (tick + entity.Index) % FollowInterval == 0)
            {
                CheckFollow(world, entity, ref m);
            }

            if (routed[i] != RouteAction.Skipped)
            {
                Apply(world, entity, ref m, routed[i]);
                continue;
            }

            if (m.State != MotionState.Moving || m.Layer == NavigationGrid.NoLayer)
            {
                continue;
            }

            NavLayer layer = nav.Layers[m.Layer];
            if (m.Mode == MotionMode.Path && m.PlanVersion != nav.Version)
            {
                ValidatePath(world, entity, ref m, layer, nav.Version);
            }
            else if (m.Mode == MotionMode.Flow)
            {
                UpdateFlow(world, entity, ref m, layer, nav.Version, tick);
            }
            else if (m.Mode == MotionMode.Route)
            {
                Apply(world, entity, ref m, UpdateRoute(world, entity, ref m, layer, nav.Version, tick, _routeScratch));
            }
            else if (m.Mode == MotionMode.Direct && m.PlanVersion != nav.Version)
            {
                // A building may now stand between the unit and its target.
                Position p = world.Positions.Get(entity);
                if (PathSearch.LineWalkable(layer, p.X, p.Y, m.TargetX, m.TargetY, out _))
                {
                    m.PlanVersion = nav.Version;
                }
                else
                {
                    Replan(world, entity, ref m);
                }
            }
        }
    }

    // Whether the route job handles the unit: a moving unit on a route that has no follow check this tick (a follow
    // check may change its motion first, so such a unit is done in the sequential pass).
    private static bool RoutedInParallel(in Motion m, EntityId entity, int tick)
        => m.State == MotionState.Moving && m.Layer != NavigationGrid.NoLayer && m.Mode == MotionMode.Route
           && !(m.Order == MotionOrder.Follow && (tick + entity.Index) % FollowInterval == 0);

    private static void Apply(World world, EntityId entity, ref Motion m, RouteAction action)
    {
        if (action == RouteAction.Replan)
        {
            world.Paths.Clear(entity.Index);
            world.PathQueue.Enqueue(new PathRequest { Entity = entity.Value, Serial = m.RequestSerial, Group = 0 });
        }
        else if (action == RouteAction.ClearPlan)
        {
            world.Paths.Clear(entity.Index);
        }
    }

    // The motion part of Replan; the caller applies the plan clear and the request (Apply).
    private static RouteAction MarkReplan(ref Motion m)
    {
        m.RequestSerial++;
        m.GoalTile = -1;
        m.Mode = MotionMode.Waiting;
        m.StallTicks = 0;
        m.BestDistance = int.MaxValue;
        return RouteAction.Replan;
    }

    private static void CheckFollow(World world, EntityId entity, ref Motion m)
    {
        EntityId target = EntityId.FromValue(m.FollowTarget);
        int t = world.Positions.IndexOf(target);
        if (!world.Entities.IsAlive(target) || t < 0)
        {
            m.Order = MotionOrder.None;
            m.FollowTarget = 0;
            OrderQueue.Next(world, entity); // the next object of a guarded set (CTRL-027)
            return;
        }

        if (world.Combats.IndexOf(entity) is >= 0 and var c && world.Combats.At(c).Target != 0)
        {
            return; // a guard in a fight: it comes back once the fight is over
        }

        Position tp = world.Positions.At(t);
        Position p = world.Positions.Get(entity);
        // ORD-001 (ReTzar 0x4e4f1f, 0x42dd00): a guard stands within guardRadiusTiles of what it guards (of its rect: the
        // footprint, or a unit's tile, grown by one), not against it, and walks back from further away; it stops as soon
        // as it is within, so a group of guards stands in a ring about four tiles from a building (the seventh play-test).
        int away = Interaction.GuardTiles(world, p, target);
        int guard = world.Ruleset.Combat.GuardRadiusTiles;
        bool far = away > guard;
        bool moved = IntMath.DistanceSquared(m.OrderX, m.OrderY, tp.X, tp.Y) > 4L * MapState.UnitsPerTile * MapState.UnitsPerTile;
        // Re-plan when the target has moved since the last request (an unreachable idle target is not chased again), or
        // when an idle follower was shoved out of range of an idle target: it is at most FollowDriftTiles beyond the
        // follow range (a follower stopped at the nearest reachable point of an unreachable target usually is not).
        bool drifted = far && m.State == MotionState.Idle && away <= guard + FollowDriftTiles;
        if ((far && moved) || drifted)
        {
            m.OrderX = tp.X;
            m.OrderY = tp.Y;
            m.Replans = 0;
            MovementOrders.Request(world, entity, ref m, 0, tp.X, tp.Y);
        }
        else if (!far && m.State == MotionState.Moving)
        {
            MovementOrders.Finish(world, entity, ref m, MotionState.Idle);
        }
    }

    // After an obstacle change: keep the path if its remaining segments are still clear.
    private static void ValidatePath(World world, EntityId entity, ref Motion m, NavLayer layer, int version)
    {
        ReadOnlySpan<int> w = world.Paths.Get(entity.Index);
        Position p = world.Positions.Get(entity);
        int x = p.X;
        int y = p.Y;
        for (int k = 2 * m.PathCursor; k + 1 < w.Length; k += 2)
        {
            if (!PathSearch.LineWalkable(layer, x, y, w[k], w[k + 1], out _))
            {
                Replan(world, entity, ref m);
                return;
            }

            x = w[k];
            y = w[k + 1];
        }

        m.PlanVersion = version;
    }

    private static void UpdateFlow(World world, EntityId entity, ref Motion m, NavLayer layer, int version, int tick)
    {
        Position p = world.Positions.Get(entity);
        int margin = Math.Min(world.TypeOf(entity).TerrainRadius, (MapState.UnitsPerTile / 2) - 128);
        bool reached = IntMath.DistanceSquared(p.X, p.Y, m.SteerX, m.SteerY) <= 768L * 768;
        bool refresh = reached || (tick + entity.Index) % RefreshTicks == 0;

        // Open ground to the slot: walk straight there, so a formation translates instead of funnelling into the
        // field's single goal tile.
        if (refresh && PathSearch.CorridorClear(layer, p.X, p.Y, m.TargetX, m.TargetY, margin, out _))
        {
            m.Mode = MotionMode.Direct;
            m.SteerX = m.TargetX;
            m.SteerY = m.TargetY;
            m.BestDistance = int.MaxValue;
            m.StallTicks = 0;
            return;
        }

        FlowField field = world.FlowFields.Get(layer, m.GoalTile, version, tick);
        int tile = layer.TileOf(p.X, p.Y);
        if (!layer.IsWalkable(tile))
        {
            return; // the resolve pass pushes the unit back onto walkable ground first
        }

        // Progress in flow mode is the field's cost-to-goal, which strictly decreases along the descent.
        if (!field.HasLocal(layer.ClusterOf(tile)))
        {
            world.FlowFields.EnsureLocal(field, layer, layer.ClusterOf(tile));
        }

        int cost = field.CostAt(layer, tile);
        if (cost != int.MaxValue && (long)cost + 512 < m.BestDistance)
        {
            if (m.BestDistance != int.MaxValue)
            {
                m.Replans = 0; // real progress down the field
            }

            m.BestDistance = cost;
            m.StallTicks = 0;
        }

        if (!refresh)
        {
            return;
        }

        int bestX = int.MinValue;
        int bestY = 0;
        int width = layer.Width;
        for (int k = 0; k < LookAhead; k++)
        {
            int cluster = layer.ClusterOf(tile);
            if (!field.HasLocal(cluster))
            {
                world.FlowFields.EnsureLocal(field, layer, cluster);
            }

            byte code = field.DirectionAt(layer, tile);
            if (code == NavLayer.TreeRoot)
            {
                bestX = m.TargetX;
                bestY = m.TargetY;
                break;
            }

            if (code == NavLayer.TreeNone)
            {
                break;
            }

            tile += NavLayer.DirX[code] + (NavLayer.DirY[code] * width);
            int cx = layer.CenterX(tile);
            int cy = layer.CenterY(tile);
            if (k > 0 && !PathSearch.CorridorClear(layer, p.X, p.Y, cx, cy, margin, out _))
            {
                break;
            }

            bestX = cx;
            bestY = cy;
        }

        if (bestX == int.MinValue)
        {
            // Not covered by the field (pushed into a pocket): plan an own path to the slot.
            Replan(world, entity, ref m);
            return;
        }

        m.SteerX = bestX;
        m.SteerY = bestY;
    }

    // Route mode: find the exit portal of the unit's cluster on the route and steer down that portal's tree (crossing
    // into the next cluster's portal tree, then the goal's tree), up to LookAhead visible tiles ahead.
    private static RouteAction UpdateRoute(World world, EntityId entity, ref Motion m, NavLayer layer, int version, int tick, RouteScratch scratch)
    {
        if (m.PlanVersion != version)
        {
            // Passability changed somewhere: keep the route if its remaining portals are unchanged, else plan again.
            if (!PathSearch.RouteValid(layer, world.Paths.Get(entity.Index), m.PathCursor - 2))
            {
                return MarkReplan(ref m);
            }

            m.PlanVersion = version;
        }

        Position p = world.Positions.Get(entity);
        int tile = layer.TileOf(p.X, p.Y);
        if (!layer.IsWalkable(tile))
        {
            return RouteAction.None;
        }

        ReadOnlySpan<int> plan = world.Paths.Get(entity.Index);
        ReadOnlySpan<int> nodes = plan[..(plan.Length / 2)];
        int len = nodes.Length;
        int cluster = layer.ClusterOf(tile);
        int goalTile = layer.TileOf(m.TargetX, m.TargetY);
        int exit = ExitFrom(nodes, m.PathCursor - 2, cluster); // from two back: a unit pushed back across a portal resumes
        if (exit >= 0 && tile == layer.PortalTile(nodes[exit]))
        {
            exit = exit + 1 < len ? exit + 1 : len; // at the portal: next is the partner across, or the goal
        }

        if (exit < 0)
        {
            if (cluster == layer.ClusterOf(goalTile) && m.PathCursor >= len - 1)
            {
                exit = len;
            }
            else
            {
                return MarkReplan(ref m); // pushed off the route
            }
        }

        if (exit != m.PathCursor)
        {
            if (exit > m.PathCursor)
            {
                m.Replans = 0; // real progress along the route
            }

            m.PathCursor = exit;
            m.BestDistance = int.MaxValue;
            m.StallTicks = 0;
        }

        bool reached = IntMath.DistanceSquared(p.X, p.Y, m.SteerX, m.SteerY) <= 768L * 768;
        if (!reached && (tick + entity.Index) % RefreshTicks != 0)
        {
            return RouteAction.None;
        }

        // Progress: tiles left along the tree to the portal (or goal) being approached (Euclidean distance would
        // grow on winding legs).
        int left = TreeDistance(layer, nodes, exit, tile, goalTile, scratch);
        if (left >= 0 && ((long)left * NavLayer.StraightCost) + 256 < m.BestDistance)
        {
            m.BestDistance = left * NavLayer.StraightCost;
            m.StallTicks = 0;
        }

        int margin = Math.Min(world.TypeOf(entity).TerrainRadius, (MapState.UnitsPerTile / 2) - 128);
        if ((exit >= len - 1 || layer.ClusterOf(goalTile) == cluster) && PathSearch.CorridorClear(layer, p.X, p.Y, m.TargetX, m.TargetY, margin, out _))
        {
            m.Mode = MotionMode.Direct;
            m.SteerX = m.TargetX;
            m.SteerY = m.TargetY;
            m.BestDistance = int.MaxValue;
            m.StallTicks = 0;
            return RouteAction.ClearPlan;
        }

        int width = layer.Width;
        bool goalTreeReady = false;
        int bestX = int.MinValue;
        int bestY = 0;
        int k = exit;
        int cur = tile;
        for (int step = 0; step < LookAhead; step++)
        {
            byte code;
            int c = layer.ClusterOf(cur);
            if (k >= len)
            {
                if (c != layer.ClusterOf(goalTile))
                {
                    break;
                }

                if (!goalTreeReady)
                {
                    scratch.GoalTree(layer, c, goalTile);
                    goalTreeReady = true;
                }

                code = scratch.Tree[layer.ClusterAt(c).LocalIndex(cur, width)];
                if (code == NavLayer.TreeRoot)
                {
                    bestX = m.TargetX;
                    bestY = m.TargetY;
                    break;
                }
            }
            else
            {
                int node = nodes[k];
                if (c != node / NavLayer.MaxPortals)
                {
                    // Standing on the previous exit portal: the next step crosses to this node's tile.
                    if (k == 0 || cur != layer.PortalTile(nodes[k - 1]) || layer.PortalPartner(nodes[k - 1]) != node)
                    {
                        break;
                    }

                    cur = layer.PortalTile(node);
                    k = ExitFrom(nodes, k, layer.ClusterOf(cur));
                    if (k < 0)
                    {
                        k = len;
                    }

                    if (!Visible(layer, p, cur, margin, step, ref bestX, ref bestY))
                    {
                        break;
                    }

                    continue;
                }

                code = layer.ClusterAt(c).Trees[node % NavLayer.MaxPortals][layer.ClusterAt(c).LocalIndex(cur, width)];
                if (code == NavLayer.TreeRoot)
                {
                    // At the exit portal: cross to the partner, which starts the next cluster's leg.
                    if (k + 1 >= len)
                    {
                        k = len;
                        continue;
                    }

                    cur = layer.PortalTile(nodes[k + 1]);
                    k = ExitFrom(nodes, k + 1, layer.ClusterOf(cur));
                    if (k < 0)
                    {
                        k = len;
                    }

                    if (!Visible(layer, p, cur, margin, step, ref bestX, ref bestY))
                    {
                        break;
                    }

                    continue;
                }
            }

            if (code == NavLayer.TreeNone)
            {
                break;
            }

            cur += NavLayer.DirX[code] + (NavLayer.DirY[code] * width);
            if (!Visible(layer, p, cur, margin, step, ref bestX, ref bestY))
            {
                break;
            }
        }

        if (bestX == int.MinValue)
        {
            return MarkReplan(ref m);
        }

        m.SteerX = bestX;
        m.SteerY = bestY;
        return RouteAction.None;
    }

    // Steps from `tile` to the root of the tree it descends (the exit portal, or the goal), or -1.
    private static int TreeDistance(NavLayer layer, ReadOnlySpan<int> nodes, int exit, int tile, int goalTile, RouteScratch scratch)
    {
        int c = layer.ClusterOf(tile);
        NavLayer.Cluster cluster = layer.ClusterAt(c);
        byte[] tree;
        if (exit < nodes.Length)
        {
            if (nodes[exit] / NavLayer.MaxPortals != c)
            {
                return -1;
            }

            tree = cluster.Trees[nodes[exit] % NavLayer.MaxPortals];
        }
        else
        {
            if (layer.ClusterOf(goalTile) != c)
            {
                return -1;
            }

            scratch.GoalTree(layer, c, goalTile);
            tree = scratch.Tree;
        }

        int width = layer.Width;
        for (int steps = 0; steps <= NavLayer.ClusterTiles; steps++)
        {
            byte code = tree[cluster.LocalIndex(tile, width)];
            if (code == NavLayer.TreeRoot)
            {
                return steps;
            }

            if (code == NavLayer.TreeNone)
            {
                return -1;
            }

            tile += NavLayer.DirX[code] + (NavLayer.DirY[code] * width);
        }

        return -1;
    }

    // The first route index from `from` on (within a few nodes) whose portal lies in `cluster` and leaves it.
    private static int ExitFrom(ReadOnlySpan<int> nodes, int from, int cluster)
    {
        int len = nodes.Length;
        for (int j = Math.Max(0, from); j < len && j <= from + 10; j++)
        {
            if (nodes[j] / NavLayer.MaxPortals == cluster && (j + 1 == len || nodes[j + 1] / NavLayer.MaxPortals != cluster))
            {
                return j;
            }
        }

        return -1;
    }

    // Accepts the tile as the steering point if it is visible (the first step is always accepted).
    private static bool Visible(NavLayer layer, Position p, int tile, int margin, int step, ref int bestX, ref int bestY)
    {
        int cx = layer.CenterX(tile);
        int cy = layer.CenterY(tile);
        if (step > 0 && !PathSearch.CorridorClear(layer, p.X, p.Y, cx, cy, margin, out _))
        {
            return false;
        }

        bestX = cx;
        bestY = cy;
        return true;
    }

    /// <summary>Re-plans an own path to the current target (keeps the group's slot, speed and settle radius).</summary>
    private static void Replan(World world, EntityId entity, ref Motion m) => Apply(world, entity, ref m, MarkReplan(ref m));

    // ----- merge -------------------------------------------------------------------------------------------------

    private void Merge(World world)
    {
        Frame f = _frame;
        MapState map = world.Map;
        ComponentStore<Motion> motions = world.Motions;
        ComponentStore<OccupancyCell> cells = world.OccupancyCells;
        for (int i = 0; i < f.Count; i++)
        {
            EntityId entity = f.Entity[i];
            int nx = f.Fx[i];
            int ny = f.Fy[i];
            if ((nx != f.Px[i] || ny != f.Py[i]) && world.GateBars(f.Owner[i], f.Px[i], f.Py[i], nx, ny))
            {
                // An open gate lets only its owner and the kingdoms it treats as allies through (ADR 0102).
                nx = f.Px[i];
                ny = f.Py[i];
            }
            if (nx != f.Px[i] || ny != f.Py[i])
            {
                ref Position position = ref world.Positions.Get(entity);
                position.X = nx;
                position.Y = ny;
                int cellPosition = cells.IndexOf(entity);
                if (cellPosition >= 0)
                {
                    ref OccupancyCell cell = ref cells.At(cellPosition);
                    int newCell = map.CellIndexAt(nx, ny);
                    if (newCell != cell.Cell)
                    {
                        if (cell.Cell >= 0)
                        {
                            map.RemoveOccupant(cell.Cell);
                        }

                        map.AddOccupant(newCell);
                        cell.Cell = newCell;
                    }
                }
            }

            ref Motion m = ref motions.At(i);
            if (f.Moving[i] != 0 && (m.VelocityX != 0 || m.VelocityY != 0))
            {
                world.Identities.Get(entity).Facing = (byte)Trig.Atan2(m.VelocityY, m.VelocityX).ToDirection8();
            }

            int outFlags = f.Out[i];
            if (outFlags == 0)
            {
                continue;
            }

            byte owner = f.Owner[i];
            if ((outFlags & OutArrived) != 0)
            {
                world.Events.Emit(new SimEvent(SimEventKind.Arrived, owner, entity, nx, ny, 0));
                MovementOrders.Finish(world, entity, ref m, MotionState.Idle);
                if (m.Order == MotionOrder.Patrol && OrderQueue.NextPatrolPoint(world, entity, ref m))
                {
                    // ORD-006: on along the patrol path.
                }
                else if (m.Order == MotionOrder.Patrol)
                {
                    // The reached end becomes the reachable point; a patrol whose ends coincide (e.g. ordered just
                    // across a wall) ends instead of arriving every tick.
                    m.OrderX = nx;
                    m.OrderY = ny;
                    (m.PatrolX, m.OrderX) = (m.OrderX, m.PatrolX);
                    (m.PatrolY, m.OrderY) = (m.OrderY, m.PatrolY);
                    long apart = Math.Max(m.SettleRadius, 2L * MapState.UnitsPerTile);
                    if (IntMath.DistanceSquared(nx, ny, m.OrderX, m.OrderY) <= apart * apart)
                    {
                        m.Order = MotionOrder.None;
                    }
                    else
                    {
                        m.Replans = 0;
                        MovementOrders.Request(world, entity, ref m, 0, m.OrderX, m.OrderY);
                    }
                }
                else if (m.Order == MotionOrder.Move)
                {
                    m.Order = MotionOrder.None;
                    if (world.Combats.IndexOf(entity) is not (>= 0 and var fight) || world.Combats.At(fight).Target == 0)
                    {
                        OrderQueue.Next(world, entity); // a unit that reached its target in a chase moves on after the fight
                    }
                }
            }
            else if ((outFlags & OutGiveUp) != 0)
            {
                world.Events.Emit(new SimEvent(SimEventKind.Blocked, owner, entity, nx, ny, 0));
                MovementOrders.Finish(world, entity, ref m, MotionState.Blocked);
                m.Order = MotionOrder.None;
                world.WaypointQueues.Remove(entity); // stuck: the rest of the way is dropped
            }
            else if ((outFlags & OutReplan) != 0)
            {
                m.Replans++;
                Replan(world, entity, ref m);
            }
        }
    }

    // ----- shared helpers for the jobs ---------------------------------------------------------------------------

    private static bool Valid(World world, NavLayer? layer, int radius, int x, int y)
    {
        if (!world.Map.ContainsPoint(x, y))
        {
            return false;
        }

        if (layer is null)
        {
            return true;
        }

        if (!layer.IsWalkablePoint(x, y))
        {
            return false;
        }

        if (layer.Clearance > 1)
        {
            return true;
        }

        int r = Math.Min(radius, (MapState.UnitsPerTile / 2) - 64);
        return layer.IsWalkablePoint(x + r, y) && layer.IsWalkablePoint(x - r, y)
            && layer.IsWalkablePoint(x, y + r) && layer.IsWalkablePoint(x, y - r);
    }

    // Enemies are hard obstacles (ADR 0025): a step that brings the unit closer to an enemy it overlaps is replaced by
    // its axis components, or cancelled, so movers never walk through enemies (they stop; attacking them is gameplay).
    private static (int X, int Y) HoldOffEnemies(World world, NavLayer? layer, Frame f, int i, int px, int py, int qx, int qy)
    {
        if (!IntrudesEnemy(f, i, px, py, qx, qy))
        {
            return (qx, qy);
        }

        if (qx != px && !IntrudesEnemy(f, i, px, py, qx, py) && Valid(world, layer, f.Terrain[i], qx, py))
        {
            return (qx, py);
        }

        if (qy != py && !IntrudesEnemy(f, i, px, py, px, qy) && Valid(world, layer, f.Terrain[i], px, qy))
        {
            return (px, qy);
        }

        // Owner decision (M2 part C): a mover held by an enemy first goes around it. Side-step perpendicular to the
        // wanted step, right first (keep right), then left; only when neither is free does the unit stop.
        int dx = qx - px;
        int dy = qy - py;
        if ((dx != 0 || dy != 0) && !IntrudesEnemy(f, i, px, py, px - dy, py + dx) && Valid(world, layer, f.Terrain[i], px - dy, py + dx))
        {
            return (px - dy, py + dx);
        }

        if ((dx != 0 || dy != 0) && !IntrudesEnemy(f, i, px, py, px + dy, py - dx) && Valid(world, layer, f.Terrain[i], px + dy, py - dx))
        {
            return (px + dy, py - dx);
        }

        return (px, py);
    }

    private static bool IntrudesEnemy(Frame f, int i, int fromX, int fromY, int x, int y)
    {
        int cx = Math.Clamp(f.Px[i] / GridCell, 0, f.GridW - 1);
        int cy = Math.Clamp(f.Py[i] / GridCell, 0, f.GridH - 1);
        for (int gy = Math.Max(0, cy - 1); gy <= Math.Min(f.GridH - 1, cy + 1); gy++)
        {
            for (int gx = Math.Max(0, cx - 1); gx <= Math.Min(f.GridW - 1, cx + 1); gx++)
            {
                int cell = (gy * f.GridW) + gx;
                for (int k = f.CellStart[cell]; k < f.CellStart[cell + 1]; k++)
                {
                    int j = f.CellItems[k];
                    if (j == i || f.Plane[j] != f.Plane[i] || f.Friends(i, j))
                    {
                        continue;
                    }

                    long r = f.Radius[i] + f.Radius[j];
                    long after = IntMath.DistanceSquared(x, y, f.Px[j], f.Py[j]);
                    if (after < r * r && after < IntMath.DistanceSquared(fromX, fromY, f.Px[j], f.Py[j]))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // Moves from (px, py) by (vx, vy) without entering invalid ground; slides along an axis when blocked.
    private static (int X, int Y) Slide(World world, NavLayer? layer, int radius, int px, int py, int vx, int vy)
    {
        int qx = px + vx;
        int qy = py + vy;
        if (Valid(world, layer, radius, qx, qy))
        {
            return (qx, qy);
        }

        if (layer is not null && !layer.IsWalkablePoint(px, py))
        {
            // Standing on unwalkable ground (a building appeared): any step that stays on the map is allowed.
            return world.Map.ContainsPoint(qx, qy) ? (qx, qy) : (px, py);
        }

        if (layer is not null && !Valid(world, layer, radius, px, py) && layer.IsWalkablePoint(qx, qy))
        {
            // Overlapping a blocked tile with the body (e.g. just pushed out of a new building): moving the center
            // over walkable ground is allowed, so the unit can step away.
            return (qx, qy);
        }

        // Along one axis. A unit that meets the corner of a blocked tile almost head-on slides past it at the step's full
        // length: the tiny sideways part alone would take it hundreds of ticks, and every re-plan pulls it back to the
        // middle of its tile (a galleon stuck at a corner of the coast, ADR 0220).
        if (vx != 0 && Valid(world, layer, radius, qx, py))
        {
            int along = Math.Abs(vy) > 4 * Math.Abs(vx) ? Math.Sign(vx) * Math.Abs(vy) : vx;
            return along != vx && Valid(world, layer, radius, px + along, py) ? (px + along, py) : (qx, py);
        }

        if (vy != 0 && Valid(world, layer, radius, px, qy))
        {
            int along = Math.Abs(vx) > 4 * Math.Abs(vy) ? Math.Sign(vy) * Math.Abs(vx) : vy;
            return along != vy && Valid(world, layer, radius, px, py + along) ? (px, py + along) : (px, qy);
        }

        // Wedged against a corner: edge sideways (right first, then left) at half speed.
        if (Valid(world, layer, radius, px - (vy / 2), py + (vx / 2)))
        {
            return (px - (vy / 2), py + (vx / 2));
        }

        if (Valid(world, layer, radius, px + (vy / 2), py - (vx / 2)))
        {
            return (px + (vy / 2), py - (vx / 2));
        }

        return (px, py);
    }

    /// <summary>What the route update leaves to the sequential pass.</summary>
    private enum RouteAction : byte
    {
        /// <summary>Nothing.</summary>
        None,

        /// <summary>Clear the plan (switched to direct steering).</summary>
        ClearPlan,

        /// <summary>Clear the plan and queue the re-plan request (the motion is already marked).</summary>
        Replan,

        /// <summary>Not handled by the route job.</summary>
        Skipped,
    }

    /// <summary>
    /// Scratch for route updates: the goal cluster's tree. A cluster search is a pure function of the layer, the cluster
    /// and the root, so the last tree is reused while those match (the layer does not change during the pre-pass).
    /// </summary>
    private sealed class RouteScratch
    {
        private readonly LocalSearch _search = new();
        private NavLayer? _layer;
        private int _cluster = -1;
        private int _root = -1;

        public byte[] Tree { get; } = new byte[NavLayer.ClusterTiles];

        public void Reset() => _layer = null;

        public void GoalTree(NavLayer layer, int cluster, int root)
        {
            if (ReferenceEquals(layer, _layer) && cluster == _cluster && root == _root)
            {
                return;
            }

            _search.Dijkstra(layer, layer.ClusterAt(cluster), root, Tree, null);
            _layer = layer;
            _cluster = cluster;
            _root = root;
        }
    }

    /// <summary>Route updates over chunks of <see cref="ChunkSize"/> units; each job has its own scratch.</summary>
    private sealed class RouteJob : IParallelJob
    {
        private RouteScratch[] _scratch = [];
        private RouteAction[] _actions = [];
        private World _world = null!;
        private NavigationGrid _nav = null!;
        private int _tick;
        private int _count;

        public int JobCount { get; private set; }

        public ReadOnlySpan<RouteAction> Actions => _actions.AsSpan(0, _count);

        public void Prepare(World world, NavigationGrid nav, int tick)
        {
            _world = world;
            _nav = nav;
            _tick = tick;
            _count = world.Motions.Count;
            JobCount = (_count + ChunkSize - 1) / ChunkSize;
            if (_actions.Length < _count)
            {
                _actions = new RouteAction[Math.Max(_count, _actions.Length * 2)];
            }

            if (_scratch.Length < JobCount)
            {
                RouteScratch[] grown = new RouteScratch[JobCount];
                _scratch.CopyTo(grown, 0);
                for (int j = _scratch.Length; j < grown.Length; j++)
                {
                    grown[j] = new RouteScratch();
                }

                _scratch = grown;
            }

            for (int j = 0; j < JobCount; j++)
            {
                _scratch[j].Reset();
            }
        }

        public void Execute(int jobIndex)
        {
            ComponentStore<Motion> motions = _world.Motions;
            RouteScratch scratch = _scratch[jobIndex];
            int version = _nav.Version;
            int end = Math.Min(_count, (jobIndex + 1) * ChunkSize);
            for (int i = jobIndex * ChunkSize; i < end; i++)
            {
                EntityId entity = motions.Entities[i];
                ref Motion m = ref motions.At(i);
                _actions[i] = RoutedInParallel(m, entity, _tick)
                    ? UpdateRoute(_world, entity, ref m, _nav.Layers[m.Layer], version, _tick, scratch)
                    : RouteAction.Skipped;
            }
        }
    }

    // The object a worker with a task works at or walks to, or the building a unit attacks; 0 for none.
    private static uint TargetOf(World world, EntityId unit)
    {
        if (world.Workers.IndexOf(unit) is >= 0 and var w && world.Workers.At(w).Task != WorkerTask.None)
        {
            return world.Workers.At(w).Target;
        }

        return BuildingAttacked(world, unit);
    }

    // The building a unit attacks (Combat.Target with a footprint), 0 for none.
    private static uint BuildingAttacked(World world, EntityId unit)
        => world.Combats.IndexOf(unit) is >= 0 and var c && world.Combats.At(c).Target is not 0 and var t && world.Footprints.Has(EntityId.FromValue(t)) ? t : 0;

    // Whether a unit works where it stands: a worker gathering, building, repairing or capturing, or a unit attacking a
    // building.
    private static bool AtWork(World world, EntityId unit)
        => (world.Workers.IndexOf(unit) is >= 0 and var w
            && world.Workers.At(w).Phase is WorkerPhase.Gathering or WorkerPhase.Building or WorkerPhase.Repairing or WorkerPhase.Capturing)
            || BuildingAttacked(world, unit) != 0;

    /// <summary>The per-tick flat copy of the mobile units, parallel to the motion store.</summary>
    private sealed class Frame
    {
        public int Count;
        public EntityId[] Entity = [];
        public int[] Px = [];
        public int[] Py = [];
        public int[] Qx = [];
        public int[] Qy = [];
        public int[] Fx = [];
        public int[] Fy = [];
        public int[] Vx = [];
        public int[] Vy = [];
        public int[] Radius = [];

        // Per kingdom (below 16): the kingdoms whose units count as friends (WORLD-016), itself included.
        // At most 16 kingdoms: 8 players + Nature + Rescue + passive + margin; the Friends check guards > 15.
        public readonly ulong[] Allies = new ulong[16];

        public bool Friends(int i, int j) => Owner[i] == Owner[j] || (Owner[i] < Allies.Length && Owner[j] < Allies.Length && ((Allies[Owner[i]] >> Owner[j]) & 1) != 0);

        // The radius each unit slides along the terrain with (its type's TerrainRadius, WORLD-015).
        public int[] Terrain = [];
        public byte[] Owner = [];
        public byte[] Plane = [];
        public byte[] Moving = [];

        // Units at work (a worker gathering, building, repairing or capturing) hold their place against the workers of
        // the same object, coming or at work (WORLD-019, the owner's fifth play-test); everyone else shoves them as before.
        public byte[] Anchored = [];

        // The object a worker works at or walks to (Worker.Target), 0 for none.
        public uint[] WorkTarget = [];

        // Land units that step cell by cell (ADR 0270), and those whose tile another unit holds already.
        public byte[] Cell = [];
        public byte[] Displaced = [];
        public bool Cells;
        public int[] Out = [];
        public int GridW;
        public int GridH;
        public int[] CellStart = [];
        public int[] CellItems = [];
        public int MaxRadius;

        public void Capture(World world)
        {
            ComponentStore<Motion> motions = world.Motions;
            int n = motions.Count;
            Count = n;
            if (Entity.Length < n)
            {
                int size = Math.Max(n, Entity.Length * 2);
                Entity = new EntityId[size];
                Px = new int[size];
                Py = new int[size];
                Qx = new int[size];
                Qy = new int[size];
                Fx = new int[size];
                Fy = new int[size];
                Vx = new int[size];
                Vy = new int[size];
                Radius = new int[size];
                Terrain = new int[size];
                Owner = new byte[size];
                Plane = new byte[size];
                Moving = new byte[size];
                Anchored = new byte[size];
                WorkTarget = new uint[size];
                Cell = new byte[size];
                Displaced = new byte[size];
                Out = new int[size];
                CellItems = new int[size];
            }

            GridW = (world.Map.WorldWidth + GridCell - 1) / GridCell;
            GridH = (world.Map.WorldHeight + GridCell - 1) / GridCell;
            if (CellStart.Length != (GridW * GridH) + 1)
            {
                CellStart = new int[(GridW * GridH) + 1];
            }

            ReadOnlySpan<EntityId> entities = motions.Entities;
            Span<Motion> data = motions.Components;
            MaxRadius = 0;

            // Kingdoms in a mutual alliance treat each other's units as friends: they shove idle ones aside and queue
            // behind them, instead of bouncing off them like enemies (ADR 0220).
            for (int a = 0; a < Allies.Length; a++)
            {
                Allies[a] = 1UL << a;
                for (int b = 0; b < Allies.Length && a < world.Players.Length; b++)
                {
                    if (b != a && b < world.Players.Length && world.HasPlayer(a) && world.HasPlayer(b) && world.Diplomacy.AreMutualAllies(a, b))
                    {
                        Allies[a] |= 1UL << b;
                    }
                }
            }

            Array.Clear(CellStart);
            Cells = false;
            for (int i = 0; i < n; i++)
            {
                EntityId e = entities[i];
                ref Motion m = ref data[i];
                Position p = world.Positions.Get(e);
                Identity identity = world.Identities.Get(e);
                Entity[i] = e;
                Px[i] = p.X;
                Py[i] = p.Y;
                Vx[i] = m.VelocityX;
                Vy[i] = m.VelocityY;
                Radius[i] = m.Radius;
                Terrain[i] = Math.Min(m.Radius, world.Ruleset.EntityTypes[identity.TypeId].TerrainRadius);
                MaxRadius = Math.Max(MaxRadius, m.Radius);
                Owner[i] = identity.Owner;
                Locomotor locomotor = world.Ruleset.EntityTypes[identity.TypeId].Locomotor;
                Plane[i] = locomotor == Locomotor.Air ? (byte)2 : locomotor == Locomotor.Ship ? (byte)1 : (byte)0;
                Moving[i] = m.State == MotionState.Moving && m.Mode != MotionMode.Waiting ? (byte)1 : (byte)0;
                WorkTarget[i] = TargetOf(world, e);
                Anchored[i] = Moving[i] == 0 && AtWork(world, e) ? (byte)1 : (byte)0;
                Cell[i] = IsCellUnit(world, identity.TypeId) && m.Layer != NavigationGrid.NoLayer && !world.IsHeld(e) ? (byte)1 : (byte)0;
                Cells |= Cell[i] != 0;
                Displaced[i] = 0;
                if (Cell[i] == 0 && world.CellSteps.Count != 0 && world.CellSteps.IndexOf(e) is >= 0 and var cs)
                {
                    world.CellSteps.At(cs).To = -1; // carried away (a holder): no step under way
                }

                Out[i] = 0;
                CellStart[CellOf(p.X, p.Y) + 1]++;
            }

            for (int c = 1; c < CellStart.Length; c++)
            {
                CellStart[c] += CellStart[c - 1];
            }

            // Counting sort, stable: ascending unit order within a cell.
            if (_fill.Length != CellStart.Length)
            {
                _fill = new int[CellStart.Length];
            }

            CellStart.AsSpan().CopyTo(_fill);
            for (int i = 0; i < n; i++)
            {
                CellItems[_fill[CellOf(Px[i], Py[i])]++] = i;
            }
        }

        private int[] _fill = [];

        public int CellOf(int x, int y) => (Math.Clamp(y / GridCell, 0, GridH - 1) * GridW) + Math.Clamp(x / GridCell, 0, GridW - 1);
    }

    private sealed class SteerJob : IParallelJob
    {
        public Frame Frame { get; set; } = null!;

        public World World { get; set; } = null!;

        public void Execute(int jobIndex)
        {
            Frame f = Frame;
            World world = World;
            NavigationGrid nav = world.Navigation;
            ComponentStore<Motion> motions = world.Motions;
            Span<Motion> data = motions.Components;
            int tick = world.Tick.Value;
            int end = Math.Min(f.Count, (jobIndex + 1) * ChunkSize);
            for (int i = jobIndex * ChunkSize; i < end; i++)
            {
                f.Qx[i] = f.Px[i];
                f.Qy[i] = f.Py[i];
                ref Motion m = ref data[i];
                if (f.Cell[i] != 0)
                {
                    continue; // steps cell by cell (CellPass)
                }

                if (f.Moving[i] == 0)
                {
                    if (m.State != MotionState.Moving)
                    {
                        m.VelocityX = 0;
                        m.VelocityY = 0;
                    }

                    continue;
                }

                Steer(world, nav, f, i, ref m, tick);
            }
        }

        private static void Steer(World world, NavigationGrid nav, Frame f, int i, ref Motion m, int tick)
        {
            NavLayer? layer = m.Layer == NavigationGrid.NoLayer ? null : nav.Layers[m.Layer];
            int px = f.Px[i];
            int py = f.Py[i];
            bool final;
            if (m.Mode == MotionMode.Path)
            {
                ReadOnlySpan<int> w = world.Paths.Get(f.Entity[i].Index);
                int last = (w.Length / 2) - 1;
                int cursor = Math.Min(m.PathCursor, last);
                while (cursor < last && IntMath.DistanceSquared(px, py, w[2 * cursor], (w[(2 * cursor) + 1])) <= (long)AdvanceRadius * AdvanceRadius)
                {
                    cursor++;
                    m.BestDistance = int.MaxValue;
                    m.StallTicks = 0;
                    m.Replans = 0; // real progress: the crowd is moving
                }

                // Pushed past a corner: skip ahead when the next waypoint is already in sight.
                if (cursor < last && layer is not null && (tick + i) % 4 == 0
                    && PathSearch.CorridorClear(layer, px, py, w[2 * (cursor + 1)], w[(2 * (cursor + 1)) + 1], Math.Min(f.Terrain[i], 896), out _))
                {
                    cursor++;
                    m.BestDistance = int.MaxValue;
                }

                m.PathCursor = cursor;
                m.SteerX = w[2 * cursor];
                m.SteerY = w[(2 * cursor) + 1];
                final = cursor == last;
            }
            else
            {
                if (m.Mode == MotionMode.Direct)
                {
                    m.SteerX = m.TargetX;
                    m.SteerY = m.TargetY;
                }

                final = m.Mode == MotionMode.Direct;
            }

            int speed = m.GroupSpeed > 0 ? Math.Min(m.SpeedPerTick, m.GroupSpeed) : m.SpeedPerTick;
            long dx = (long)m.SteerX - px;
            long dy = (long)m.SteerY - py;
            long dist = IntMath.ISqrt((dx * dx) + (dy * dy));
            long toTarget = IntMath.ISqrt(IntMath.DistanceSquared(px, py, m.TargetX, m.TargetY));

            if (final && dist <= speed && !IntrudesEnemy(f, i, px, py, m.SteerX, m.SteerY))
            {
                (f.Qx[i], f.Qy[i]) = Slide(world, layer, f.Terrain[i], px, py, (int)dx, (int)dy);
                m.VelocityX = (int)dx;
                m.VelocityY = (int)dy;
                f.Out[i] = OutArrived;
                return;
            }

            long vx = 0;
            long vy = 0;
            int contacts = 0;
            if (dist > 0)
            {
                long cap = speed;
                long ax = 0;
                long ay = 0;
                contacts = Avoid(f, i, dx, dy, dist, speed, ref ax, ref ay, ref cap);
                vx = (dx * 1024 / dist) + ax;
                vy = (dy * 1024 / dist) + ay;
                long length = IntMath.ISqrt((vx * vx) + (vy * vy));
                if (length > 0)
                {
                    long v = Math.Min(cap, final ? dist : cap);
                    vx = vx * v / length;
                    vy = vy * v / length;
                }
            }

            (int sx, int sy) = Slide(world, layer, f.Terrain[i], px, py, (int)vx, (int)vy);
            (int qx, int qy) = HoldOffEnemies(world, layer, f, i, px, py, sx, sy);
            bool heldByEnemy = qx != sx || qy != sy;
            f.Qx[i] = qx;
            f.Qy[i] = qy;
            m.VelocityX = qx - px;
            m.VelocityY = qy - py;

            // Progress toward the steering point (flow mode: tracked on the field's cost by the pre-pass).
            long after = IntMath.ISqrt(IntMath.DistanceSquared(qx, qy, m.SteerX, m.SteerY));
            if (m.Mode is MotionMode.Flow or MotionMode.Route)
            {
                m.StallTicks++;
            }
            else if (after + 128 < m.BestDistance)
            {
                m.BestDistance = (int)Math.Min(int.MaxValue, after);
                m.StallTicks = 0;
            }
            else
            {
                m.StallTicks++;
            }

            bool nearTarget = toTarget <= m.SettleRadius;
            if (nearTarget && (final || m.Mode is MotionMode.Flow or MotionMode.Route) && m.StallTicks >= SettleTicks)
            {
                f.Out[i] = OutArrived; // settle where the crowd allows (ORIG-02: never orbit)
            }
            else if (final && m.StallTicks >= SettleTicks && (contacts >> 16) > 0 && toTarget <= m.SettleRadius + MapState.UnitsPerTile)
            {
                f.Out[i] = OutArrived; // the goal itself is taken by a unit standing there: settle next to it (ADR 0146)
            }
            else if (m.StallTicks >= ReplanTicks && (contacts >> 16) > 0 && toTarget <= (2L * m.SettleRadius) + (2 * MapState.UnitsPerTile))
            {
                f.Out[i] = OutArrived; // jammed against the units that already settled around the goal: settle here
            }
            else if (m.StallTicks >= ReplanTicks)
            {
                // A unit held up by a crowd keeps trying (REQ-PATH-07 deadlock re-plan); only a unit that is stuck with
                // nobody around, after several re-plans, gives up.
                // Give up: stuck alone or against an enemy after a few re-plans, or jammed in a crowd for good.
                bool giveUp = layer is null || m.Replans >= MaxCrowdReplans
                    || (m.Replans >= MaxReplans && ((contacts & 0xFFFF) == 0 || heldByEnemy));
                f.Out[i] = giveUp ? OutGiveUp : OutReplan;
            }
        }

        // Integer avoidance: lateral push around units in the way (keep right), follow units going the same way.
        // Returns the number of units in contact range (crowding); bits 16+ count those that are not moving.
        private static int Avoid(Frame f, int i, long dx, long dy, long dist, int speed, ref long ax, ref long ay, ref long cap)
        {
            int contacts = 0;
            int considered = 0;
            int px = f.Px[i];
            int py = f.Py[i];
            int ri = f.Radius[i];
            int cx = Math.Clamp(px / GridCell, 0, f.GridW - 1);
            int cy = Math.Clamp(py / GridCell, 0, f.GridH - 1);
            long sideX = -dy * 1024 / dist; // right-hand side of the direction (y grows south)
            long sideY = dx * 1024 / dist;
            for (int gy = Math.Max(0, cy - 1); gy <= Math.Min(f.GridH - 1, cy + 1); gy++)
            {
                for (int gx = Math.Max(0, cx - 1); gx <= Math.Min(f.GridW - 1, cx + 1); gx++)
                {
                    int cell = (gy * f.GridW) + gx;
                    for (int k = f.CellStart[cell]; k < f.CellStart[cell + 1]; k++)
                    {
                        int j = f.CellItems[k];
                        if (j == i || f.Plane[j] != f.Plane[i])
                        {
                            continue;
                        }

                        long rx = f.Px[j] - (long)px;
                        long ry = f.Py[j] - (long)py;
                        // Units coming the other way squeeze past with their terrain footprints, not their bodies (WORLD-015:
                        // otherwise opposing columns jam at a bridge, ORIG-02).
                        long r = f.Moving[j] != 0 && ((f.Vx[j] * dx) + (f.Vy[j] * dy)) < 0 ? f.Terrain[i] + (long)f.Terrain[j] : ri + f.Radius[j];
                        long reach = r + (4L * speed);
                        long d2 = (rx * rx) + (ry * ry);
                        if (d2 > reach * reach)
                        {
                            continue;
                        }

                        if (++considered > MaxNeighbours)
                        {
                            break;
                        }

                        if (d2 < (r + 256) * (r + 256))
                        {
                            // Low 16 bits: friendly contacts (a crowd to wait for); high bits: contacts with units that
                            // are not moving (anyone already settled, or an enemy blocking the way).
                            contacts += (f.Moving[j] == 0 ? 0x10000 : 0) + (f.Friends(i, j) ? 1 : 0);
                        }

                        long forward = ((rx * dx) + (ry * dy)) / dist;
                        if (forward <= 0)
                        {
                            continue;
                        }

                        long lateral = ((dx * ry) - (dy * rx)) / dist;
                        if (Math.Abs(lateral) >= r)
                        {
                            continue;
                        }

                        long vjx = f.Vx[j];
                        long vjy = f.Vy[j];
                        long vj = IntMath.ISqrt((vjx * vjx) + (vjy * vjy));
                        bool sameWay = f.Moving[j] != 0 && vj > 0 && 4 * ((vjx * dx) + (vjy * dy)) > 3 * vj * dist;
                        if (sameWay && f.Friends(i, j))
                        {
                            // Queue behind: do not run into the unit ahead.
                            long gap = Math.Max(0, forward - r);
                            cap = Math.Min(cap, Math.Max(speed / 4, Math.Max(gap, vj)));
                            continue;
                        }

                        long strength = (r - Math.Abs(lateral)) * 1024 / r * (reach - forward) / reach;
                        if (f.Moving[j] == 0 && f.Friends(i, j))
                        {
                            strength /= 2; // idle friends step aside (shoving)
                        }

                        long side = lateral > 0 ? -1 : 1; // obstacle on the right: go left; dead ahead: go right
                        ax += side * sideX * strength / 1024;
                        ay += side * sideY * strength / 1024;
                    }
                }
            }

            long magnitude = IntMath.ISqrt((ax * ax) + (ay * ay));
            if (magnitude > 1536)
            {
                ax = ax * 1536 / magnitude;
                ay = ay * 1536 / magnitude;
            }

            return contacts;
        }
    }

    private sealed class ResolveJob : IParallelJob
    {
        public Frame Frame { get; set; } = null!;

        public World World { get; set; } = null!;

        public void Execute(int jobIndex)
        {
            Frame f = Frame;
            World world = World;
            NavigationGrid nav = world.Navigation;
            Span<Motion> data = world.Motions.Components;
            int end = Math.Min(f.Count, (jobIndex + 1) * ChunkSize);
            for (int i = jobIndex * ChunkSize; i < end; i++)
            {
                if (f.Cell[i] != 0)
                {
                    continue; // steps cell by cell (CellPass)
                }

                ref Motion m = ref data[i];
                NavLayer? layer = m.Layer == NavigationGrid.NoLayer ? null : nav.Layers[m.Layer];
                int qx = f.Qx[i];
                int qy = f.Qy[i];
                (long sx, long sy) = Push(f, i);
                // Escape: the center is on blocked ground, or an idle unit's body still overlaps a blocked tile.
                if (layer is not null && (!layer.IsWalkablePoint(qx, qy) || (m.State != MotionState.Moving && !Valid(world, layer, f.Terrain[i], qx, qy))))
                {
                    // Escape toward the nearest walkable tile.
                    int tile = NavQueries.NearestWalkable(layer, layer.TileX(qx), layer.TileY(qy), EscapeRadius);
                    if (tile >= 0)
                    {
                        long ex = layer.CenterX(tile) - (long)qx;
                        long ey = layer.CenterY(tile) - (long)qy;
                        long el = IntMath.ISqrt((ex * ex) + (ey * ey));
                        int step = Math.Max(m.SpeedPerTick, 256);
                        if (el > 0)
                        {
                            sx = ex * Math.Min(el, step) / el;
                            sy = ey * Math.Min(el, step) / el;
                        }
                    }
                }

                (int fx, int fy) = sx == 0 && sy == 0 ? (qx, qy) : Slide(world, layer, f.Terrain[i], qx, qy, (int)sx, (int)sy);

                // Pushes from friends behind must not drive the unit into an enemy either.
                (f.Fx[i], f.Fy[i]) = HoldOffEnemies(world, layer, f, i, qx, qy, fx, fy);
            }
        }

        // Sum of the pushes from overlapping units, in world units; Jacobi step from the steered positions.
        private static (long X, long Y) Push(Frame f, int i)
        {
            int qx = f.Qx[i];
            int qy = f.Qy[i];
            int ri = f.Radius[i];
            int cx = Math.Clamp(f.Px[i] / GridCell, 0, f.GridW - 1);
            int cy = Math.Clamp(f.Py[i] / GridCell, 0, f.GridH - 1);
            long sx = 0;
            long sy = 0;
            bool iMoving = f.Moving[i] != 0;
            int considered = 0;
            for (int gy = Math.Max(0, cy - 1); gy <= Math.Min(f.GridH - 1, cy + 1); gy++)
            {
                for (int gx = Math.Max(0, cx - 1); gx <= Math.Min(f.GridW - 1, cx + 1); gx++)
                {
                    int cell = (gy * f.GridW) + gx;
                    for (int k = f.CellStart[cell]; k < f.CellStart[cell + 1]; k++)
                    {
                        int j = f.CellItems[k];
                        if (j == i || f.Plane[j] != f.Plane[i])
                        {
                            continue;
                        }

                        long rx = qx - (long)f.Qx[j];
                        long ry = qy - (long)f.Qy[j];
                        bool jMoving = f.Moving[j] != 0;
                        bool opposing = iMoving && jMoving && (((long)f.Vx[i] * f.Vx[j]) + ((long)f.Vy[i] * f.Vy[j])) < 0;
                        long r = opposing ? f.Terrain[i] + (long)f.Terrain[j] : ri + f.Radius[j]; // squeezing past (WORLD-015)
                        long d2 = (rx * rx) + (ry * ry);
                        if (d2 >= r * r)
                        {
                            continue;
                        }

                        if (++considered > MaxNeighbours)
                        {
                            break;
                        }

                        bool friendly = f.Friends(i, j);
                        int share; // quarters of the overlap this unit takes
                        bool together = f.WorkTarget[i] != 0 && f.WorkTarget[i] == f.WorkTarget[j];
                        bool iHolds = together && f.Anchored[i] != 0;
                        bool jHolds = together && f.Anchored[j] != 0;
                        if (iHolds || jHolds)
                        {
                            share = iHolds ? 0 : 4; // a unit at work holds its place against workers (WORLD-019)
                        }
                        else if (iMoving == jMoving)
                        {
                            share = 2;
                        }
                        else if (friendly)
                        {
                            share = iMoving ? 1 : 3; // idle friends are shoved aside
                        }
                        else
                        {
                            share = iMoving ? 4 : 0; // enemies do not yield
                        }

                        if (share == 0)
                        {
                            continue;
                        }

                        long d = IntMath.ISqrt(d2);
                        if (d == 0)
                        {
                            // Same spot (e.g. spawned together): a direction fixed by the pair, opposite for the two.
                            int lo = Math.Min(f.Entity[i].Index, f.Entity[j].Index);
                            int hi = Math.Max(f.Entity[i].Index, f.Entity[j].Index);
                            Angle angle = Angle.FromRaw((lo * 40503) ^ (hi * 9973));
                            rx = Trig.Cos(angle).Raw >> 6;
                            ry = Trig.Sin(angle).Raw >> 6;
                            if (f.Entity[i].Index == hi)
                            {
                                rx = -rx;
                                ry = -ry;
                            }

                            d = Math.Max(1, IntMath.ISqrt((rx * rx) + (ry * ry)));
                        }

                        long overlap = r - d;
                        sx += rx * overlap * share / (4 * d);
                        sy += ry * overlap * share / (4 * d);
                    }
                }
            }

            long length = IntMath.ISqrt((sx * sx) + (sy * sy));
            long max = Math.Max(64, ri / 2);
            if (length > max)
            {
                sx = sx * max / length;
                sy = sy * max / length;
            }

            return (sx, sy);
        }
    }
}
