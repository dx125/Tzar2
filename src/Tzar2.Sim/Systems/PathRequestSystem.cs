using System;
using System.Collections.Generic;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;
using Tzar2.Sim.Navigation;
using Tzar2.Sim.Threading;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 7: path request servicing (REQ-PATH-01…05, REQ-PATH-03 budget; ADR 0022).
/// </summary>
/// <remarks>
/// <para>1. <b>Prepare</b> (sequential, in queue order) the first <see cref="BatchLimit"/> live requests: fix the start,
/// resolve the goal (unreachable → nearest reachable tile of the unit's component, reported with a
/// <see cref="SimEventKind.GoalUnreachable"/> event), and for a group plan the arrival slots and choose flow-field
/// steering from <see cref="FlowThreshold"/> units. From then on a request's result is a pure function of its fields
/// and the navigation layer.</para>
/// <para>2. <b>Compute</b> the batch: flow fields sequentially (one per distinct goal), individual paths as fork/join jobs
/// on the worker pool, each job with its own search scratch. Results do not depend on the job split or worker count.</para>
/// <para>3. <b>Accept</b> results in FIFO order while the tick's budget of node expansions lasts (at least one per tick);
/// each request costs what its fresh computation expands, so the outcome never depends on caches. Results computed but
/// not accepted are kept for the next tick (valid while the request and the navigation version are unchanged).</para>
/// </remarks>
public sealed class PathRequestSystem : ISimSystem
{
    /// <summary>Most live requests prepared and computed per tick.</summary>
    public const int BatchLimit = 512;

    /// <summary>Group size from which a group follows a shared flow field (REQ-PATH-04).</summary>
    public const int FlowThreshold = 6;

    /// <summary>
    /// Most flow fields in use at once (counted from the units' state, so it is deterministic); further groups plan
    /// individual paths. Bounds the memory of the flow-field cache.
    /// </summary>
    public const int MaxFlowGroups = 48;

    /// <summary>Fork/join jobs per batch (data-defined; each has its own search scratch).</summary>
    public const int JobCount = 16;

    private readonly ArrivalPlanner _planner = new();
    private readonly PathJob _job = new();
    private readonly List<int> _batch = [];
    private readonly List<PathResult> _compute = [];
    private readonly List<FlowField> _flowBuilds = [];
    private readonly PathSearch _inlineSearch = new();
    private long _pathCostEstimate = 4000;
    private long _flowCostEstimate = 8000;
    private readonly Dictionary<int, PathResult> _carried = [];
    private readonly Stack<PathResult> _freeResults = new();
    private readonly PathResult?[] _resultOf = new PathResult?[BatchLimit];
    private int[] _mx = new int[64];
    private int[] _my = new int[64];
    private int[] _mid = new int[64];
    private int[] _tx = new int[64];
    private int[] _ty = new int[64];
    private int[] _member = new int[64];
    private int[] _memberQueue = new int[64];
    private bool[] _done = new bool[64];
    private long[] _flowKeys = new long[64];
    private int _liveFlowGroups = -1;

    /// <summary>Creates the system with its result pool (computed and carried results never exceed two batches).</summary>
    public PathRequestSystem()
    {
        for (int i = 0; i < 2 * BatchLimit; i++)
        {
            _freeResults.Push(new PathResult());
        }
    }

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.PathRequests;

    /// <inheritdoc />
    public string Name => "path-requests";

    /// <summary>Requests accepted in the last tick (diagnostics).</summary>
    public int LastAccepted { get; private set; }

    /// <summary>Node expansions charged in the last tick (diagnostics).</summary>
    public int LastSpent { get; private set; }

    /// <summary>Paths and flow fields computed by the fork/join jobs in the last tick (diagnostics).</summary>
    public int LastComputed { get; private set; }

    /// <summary>Paths computed on demand during acceptance in the last tick (diagnostics).</summary>
    public int LastInline { get; private set; }

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        PathRequestQueue queue = world.PathQueue;
        LastAccepted = 0;
        LastSpent = 0;
        LastComputed = 0;
        LastInline = 0;
        if (queue.Count == 0)
        {
            return;
        }

        NavigationGrid nav = world.Navigation;
        nav.EnsureUpToDate();
        if (queue.Count > (2 * world.Motions.Count) + 1024)
        {
            queue.RemoveWhere(world, static (w, r) => !IsLive(w, r));
        }

        // 1. Prepare.
        _batch.Clear();
        _liveFlowGroups = -1;
        for (int i = 0; i < queue.Count && _batch.Count < BatchLimit; i++)
        {
            ref PathRequest request = ref queue[i];
            if (!IsLive(world, request))
            {
                continue;
            }

            if ((request.Flags & PathRequestFlags.Prepared) == 0)
            {
                if (request.Group != 0)
                {
                    PrepareGroup(world, nav, queue, i);
                }
                else
                {
                    PrepareSingle(world, nav, ref request);
                }
            }

            _batch.Add(i);
        }

        // 2. Compute.
        _compute.Clear();
        _flowBuilds.Clear();
        int version = nav.Version;

        // Compute ahead only about what the budget will accept (a performance estimate, never a result input:
        // whatever is not computed here is computed on demand during acceptance).
        long window = (long)world.Config.PathBudgetPerTick * 11 / 10;
        long estimate = 0;
        for (int b = 0; b < _batch.Count; b++)
        {
            _resultOf[b] = null;
        }

        for (int b = 0; b < _batch.Count && estimate < window; b++)
        {
            ref PathRequest request = ref queue[_batch[b]];
            estimate += request.PrepCost;
            if (request.TargetX == int.MinValue)
            {
                continue;
            }

            if ((request.Flags & PathRequestFlags.Flow) != 0)
            {
                ref Motion fm = ref world.Motions.Get(EntityId.FromValue(request.Entity));
                FlowField field = world.FlowFields.Reserve(nav.Layers[fm.Layer], fm.GoalTile, world.Tick.Value);
                if (field.Version != version && !_flowBuilds.Contains(field))
                {
                    FlowFieldCache.PrepareReserved(field);
                    _flowBuilds.Add(field);
                    estimate += _flowCostEstimate;
                }

                continue;
            }

            EntityId entity = EntityId.FromValue(request.Entity);
            if (_carried.Remove(entity.Index, out PathResult? carried))
            {
                if (carried.Matches(request, version))
                {
                    _resultOf[b] = carried;
                    estimate += carried.Expanded;
                    continue;
                }

                _freeResults.Push(carried);
            }

            PathResult result = _freeResults.Count > 0 ? _freeResults.Pop() : new PathResult();
            result.Reset(request, version, world.Motions.Get(entity).Layer, world.Motions.Get(entity).Radius);
            _resultOf[b] = result;
            _compute.Add(result);
            estimate += _pathCostEstimate;
        }

        if (_compute.Count > 0 || _flowBuilds.Count > 0)
        {
            LastComputed = _compute.Count + _flowBuilds.Count;
            _job.Prepare(world, nav, _compute, _flowBuilds);
            context.Workers.Run(_job, Math.Min(JobCount, _compute.Count + _flowBuilds.Count));
            world.FlowFields.CountBuilds(_flowBuilds.Count);
        }

        // 3. Accept.
        int budget = world.Config.PathBudgetPerTick;
        int spent = 0;
        int accepted = 0;
        int consumed = 0;
        int nextBatch = 0;
        for (int i = 0; i < queue.Count; i++)
        {
            ref PathRequest request = ref queue[i];
            if (!IsLive(world, request))
            {
                consumed = i + 1;
                continue;
            }

            if (nextBatch >= _batch.Count || _batch[nextBatch] != i || (spent >= budget && accepted > 0))
            {
                break;
            }

            PathResult? result = _resultOf[nextBatch];
            _resultOf[nextBatch] = null;
            nextBatch++;
            if (result is null && request.TargetX != int.MinValue && (request.Flags & PathRequestFlags.Flow) == 0)
            {
                // Beyond the compute window: compute now (same pure result).
                EntityId e = EntityId.FromValue(request.Entity);
                result = _freeResults.Count > 0 ? _freeResults.Pop() : new PathResult();
                result.Reset(request, version, world.Motions.Get(e).Layer, world.Motions.Get(e).Radius);
                result.Compute(nav, _inlineSearch);
                LastInline++;
            }

            if (result is not null)
            {
                _pathCostEstimate += (result.Expanded - _pathCostEstimate) / 8;
            }
            spent += Accept(world, nav, request, result);
            if (result is not null)
            {
                _freeResults.Push(result);
            }

            accepted++;
            consumed = i + 1;
        }

        // Keep the computed but unaccepted results for the next tick.
        for (int b = nextBatch; b < _batch.Count; b++)
        {
            if (_resultOf[b] is { } result)
            {
                if (_carried.Remove(EntityId.FromValue(result.Entity).Index, out PathResult? old))
                {
                    _freeResults.Push(old);
                }

                _carried[EntityId.FromValue(result.Entity).Index] = result;
                _resultOf[b] = null;
            }
        }

        queue.Dequeue(consumed);
        LastAccepted = accepted;
        LastSpent = spent;
    }

    private static bool IsLive(World world, in PathRequest request)
    {
        EntityId entity = EntityId.FromValue(request.Entity);
        if (!world.Entities.IsAlive(entity))
        {
            return false;
        }

        int p = world.Motions.IndexOf(entity);
        if (p < 0)
        {
            return false;
        }

        ref Motion m = ref world.Motions.At(p);
        return m.RequestSerial == request.Serial && m.Mode == MotionMode.Waiting;
    }

    // Fixes start and goal of a single unit's request.
    private static void PrepareSingle(World world, NavigationGrid nav, ref PathRequest request)
    {
        EntityId entity = EntityId.FromValue(request.Entity);
        ref Motion m = ref world.Motions.Get(entity);
        Position position = world.Positions.Get(entity);
        NavLayer layer = nav.Layers[m.Layer];
        request.Flags |= PathRequestFlags.Prepared;
        if (!ResolveStart(layer, position.X, position.Y, out int startTile, out request.StartX, out request.StartY))
        {
            request.TargetX = int.MinValue;
            return;
        }

        int component = layer.ComponentOf(startTile);
        request.PrepCost = 1 + ResolveGoal(world, layer, component, entity, m.TargetX, m.TargetY, out request.TargetX, out request.TargetY) / 16;
        m.TargetX = request.TargetX;
        m.TargetY = request.TargetY;
    }

    private static bool ResolveStart(NavLayer layer, int x, int y, out int tile, out int startX, out int startY)
    {
        tile = layer.TileOf(x, y);
        startX = x;
        startY = y;
        if (layer.IsWalkablePoint(x, y))
        {
            return true;
        }

        // Standing somewhere unwalkable (a building was placed on it): start from the nearest walkable tile.
        tile = NavQueries.NearestWalkable(layer, layer.TileX(x), layer.TileY(y), 4);
        if (tile < 0)
        {
            return false;
        }

        startX = layer.CenterX(tile);
        startY = layer.CenterY(tile);
        return true;
    }

    // Returns the scan cost; writes the (possibly adjusted) target and reports an unreachable goal.
    private static int ResolveGoal(World world, NavLayer layer, int component, EntityId entity, int x, int y, out int targetX, out int targetY)
    {
        int goal = layer.TileOf(x, y);
        targetX = Math.Clamp(x, 0, world.Map.WorldWidth - 1);
        targetY = Math.Clamp(y, 0, world.Map.WorldHeight - 1);
        if (layer.IsWalkablePoint(targetX, targetY) && layer.ComponentOf(goal) == component)
        {
            return 0;
        }

        int nearest = NavQueries.NearestInComponent(layer, component, x, y, out int scanned);
        targetX = layer.CenterX(nearest);
        targetY = layer.CenterY(nearest);
        world.Events.Emit(new SimEvent(SimEventKind.GoalUnreachable, world.Identities.Get(entity).Owner, entity, targetX, targetY, 0));
        return scanned;
    }

    private void PrepareGroup(World world, NavigationGrid nav, PathRequestQueue queue, int first)
    {
        int group = queue[first].Group;
        int n = 0;
        for (int i = first; i < queue.Count && queue[i].Group == group; i++)
        {
            if (IsLive(world, queue[i]))
            {
                Grow(n + 1);
                _memberQueue[n++] = i;
            }
        }

        // Partition by (layer, start component), in order of the first member.
        bool[] done = _done.Length >= n ? _done : (_done = new bool[Math.Max(n, _done.Length * 2)]);

        // Members prepared by an earlier call (another start component of the same order) keep their slots.
        for (int k = 0; k < n; k++)
        {
            done[k] = (queue[_memberQueue[k]].Flags & PathRequestFlags.Prepared) != 0;
        }
        int prepCost = 0;
        for (int a = 0; a < n; a++)
        {
            if (done[a])
            {
                continue;
            }

            ref PathRequest lead = ref queue[_memberQueue[a]];
            EntityId leadEntity = EntityId.FromValue(lead.Entity);
            ref Motion leadMotion = ref world.Motions.Get(leadEntity);
            NavLayer layer = nav.Layers[leadMotion.Layer];
            int component = -2;
            int count = 0;
            int minSpeed = int.MaxValue;
            int maxRadius = 0;
            for (int b = a; b < n; b++)
            {
                if (done[b])
                {
                    continue;
                }

                ref PathRequest r = ref queue[_memberQueue[b]];
                EntityId e = EntityId.FromValue(r.Entity);
                ref Motion m = ref world.Motions.Get(e);
                if (m.Layer != leadMotion.Layer)
                {
                    continue;
                }

                Position p = world.Positions.Get(e);
                r.Flags |= PathRequestFlags.Prepared;
                bool movable = ResolveStart(layer, p.X, p.Y, out int startTile, out r.StartX, out r.StartY);
                int c = movable ? layer.ComponentOf(startTile) : -1;
                if (!movable)
                {
                    r.TargetX = int.MinValue;
                    done[b] = true;
                    continue;
                }

                if (component == -2)
                {
                    component = c;
                }

                if (c != component)
                {
                    r.Flags &= ~PathRequestFlags.Prepared;
                    continue;
                }

                done[b] = true;
                _member[count] = _memberQueue[b];
                _mx[count] = r.StartX;
                _my[count] = r.StartY;
                _mid[count] = e.Index;
                minSpeed = Math.Min(minSpeed, m.SpeedPerTick);
                maxRadius = Math.Max(maxRadius, m.Radius);
                count++;
            }

            if (count == 0)
            {
                continue;
            }

            // The group goal (adjusted once for the partition), then the slots.
            ref Motion first0 = ref world.Motions.Get(EntityId.FromValue(queue[_member[0]].Entity));
            int scan = ResolveGoalForGroup(world, layer, component, first0.OrderX, first0.OrderY, out int goalX, out int goalY, out bool adjusted);
            prepCost += 1 + (scan / 16);
            int settle;
            if (count >= 2)
            {
                settle = _planner.Plan(world.Map, layer, component, goalX, goalY, maxRadius, _mx.AsSpan(0, count), _my.AsSpan(0, count), _mid.AsSpan(0, count), _tx.AsSpan(0, count), _ty.AsSpan(0, count));
                prepCost += _planner.Cost;
            }
            else
            {
                _tx[0] = goalX;
                _ty[0] = goalY;
                settle = 0;
            }

            bool flow = count >= FlowThreshold && LiveFlowGroups(world) < MaxFlowGroups;
            if (flow)
            {
                _liveFlowGroups++;
            }

            int goalTile = layer.TileOf(goalX, goalY);
            for (int k = 0; k < count; k++)
            {
                ref PathRequest r = ref queue[_member[k]];
                ref Motion m = ref world.Motions.Get(EntityId.FromValue(r.Entity));
                r.TargetX = _tx[k];
                r.TargetY = _ty[k];
                m.TargetX = _tx[k];
                m.TargetY = _ty[k];
                m.GroupSpeed = count >= 2 ? minSpeed : 0;
                m.SettleRadius = settle + (3 * MapState.UnitsPerTile / 2);
                if (adjusted)
                {
                    EntityId member = EntityId.FromValue(r.Entity);
                    world.Events.Emit(new SimEvent(SimEventKind.GoalUnreachable, world.Identities.Get(member).Owner, member, goalX, goalY, 0));
                }

                if (flow)
                {
                    r.Flags |= PathRequestFlags.Flow;
                    m.GoalTile = goalTile;
                    if (k == 0)
                    {
                        r.Flags |= PathRequestFlags.FlowHead;
                    }
                }
            }
        }

        queue[first].PrepCost += prepCost;
    }

    private static int ResolveGoalForGroup(World world, NavLayer layer, int component, int x, int y, out int goalX, out int goalY, out bool adjusted)
    {
        adjusted = false;
        int goal = layer.TileOf(x, y);
        goalX = Math.Clamp(x, 0, world.Map.WorldWidth - 1);
        goalY = Math.Clamp(y, 0, world.Map.WorldHeight - 1);
        if (layer.IsWalkablePoint(goalX, goalY) && layer.ComponentOf(goal) == component)
        {
            return 0;
        }

        int nearest = NavQueries.NearestInComponent(layer, component, x, y, out int scanned);
        goalX = layer.CenterX(nearest);
        goalY = layer.CenterY(nearest);
        adjusted = true;
        return scanned;
    }

    // Applies a result; returns its cost.
    private int Accept(World world, NavigationGrid nav, in PathRequest request, PathResult? result)
    {
        EntityId entity = EntityId.FromValue(request.Entity);
        ref Motion m = ref world.Motions.Get(entity);
        int cost = request.PrepCost;
        if (request.TargetX == int.MinValue)
        {
            Position p = world.Positions.Get(entity);
            world.Events.Emit(new SimEvent(SimEventKind.GoalUnreachable, world.Identities.Get(entity).Owner, entity, p.X, p.Y, 1));
            MovementOrders.Finish(world, entity, ref m, MotionState.Idle);
            return cost;
        }

        m.PlanVersion = nav.Version;
        m.StallTicks = 0;
        m.BestDistance = int.MaxValue;
        if ((request.Flags & PathRequestFlags.Flow) != 0)
        {
            FlowField field = world.FlowFields.Get(nav.Layers[m.Layer], m.GoalTile, nav.Version, world.Tick.Value);
            if ((request.Flags & PathRequestFlags.FlowHead) != 0)
            {
                cost += field.Cost;
                _flowCostEstimate += (field.Cost - _flowCostEstimate) / 4;
            }

            m.Mode = MotionMode.Flow;
            Position p = world.Positions.Get(entity);
            m.SteerX = p.X;
            m.SteerY = p.Y;
            return cost;
        }

        PathResult r = result!;
        cost += r.Expanded;
        if (!r.Found)
        {
            // Connected by the components but not by the search: cannot happen; fail safe by going straight.
            m.Mode = MotionMode.Direct;
            m.SteerX = request.TargetX;
            m.SteerY = request.TargetY;
            return cost;
        }

        world.Paths.Set(entity.Index, r.Waypoints);
        if (r.IsRoute)
        {
            m.Mode = MotionMode.Route;
            m.PathCursor = 0;
            Position p = world.Positions.Get(entity);
            m.SteerX = p.X;
            m.SteerY = p.Y;
            return cost;
        }

        m.Mode = MotionMode.Path;
        m.PathCursor = 0;
        m.SteerX = r.Waypoints[0];
        m.SteerY = r.Waypoints[1];
        return cost;
    }

    // Distinct flow fields referenced by units (following one, or prepared to), counted once per tick from state.
    private int LiveFlowGroups(World world)
    {
        if (_liveFlowGroups >= 0)
        {
            return _liveFlowGroups;
        }

        int n = 0;
        Span<Motion> motions = world.Motions.Components;
        if (_flowKeys.Length < motions.Length)
        {
            _flowKeys = new long[Math.Max(motions.Length, _flowKeys.Length * 2)];
        }

        foreach (ref Motion m in motions)
        {
            if (m.GoalTile >= 0 && (m.Mode == MotionMode.Flow || m.Mode == MotionMode.Waiting))
            {
                _flowKeys[n++] = ((long)m.Layer << 32) | (uint)m.GoalTile;
            }
        }

        Array.Sort(_flowKeys, 0, n);
        int distinct = 0;
        for (int i = 0; i < n; i++)
        {
            distinct += i == 0 || _flowKeys[i] != _flowKeys[i - 1] ? 1 : 0;
        }

        _liveFlowGroups = distinct;
        return distinct;
    }

    private void Grow(int n)
    {
        if (n <= _mx.Length)
        {
            return;
        }

        int size = Math.Max(n, _mx.Length * 2);
        Array.Resize(ref _mx, size);
        Array.Resize(ref _my, size);
        Array.Resize(ref _mid, size);
        Array.Resize(ref _tx, size);
        Array.Resize(ref _ty, size);
        Array.Resize(ref _member, size);
        Array.Resize(ref _memberQueue, size);
    }

    /// <summary>One individual path computation (pure function of the request fields and the layer).</summary>
    private sealed class PathResult
    {
        private int[] _waypoints = new int[64];
        private int _waypointInts;

        public uint Entity { get; private set; }

        public int Serial { get; private set; }

        public int Version { get; private set; }

        public byte Layer { get; private set; }

        public int Radius { get; private set; }

        public int StartX { get; private set; }

        public int StartY { get; private set; }

        public int TargetX { get; private set; }

        public int TargetY { get; private set; }

        public bool Found { get; private set; }

        public bool IsRoute { get; private set; }

        public int Expanded { get; private set; }

        public ReadOnlySpan<int> Waypoints => _waypoints.AsSpan(0, _waypointInts);

        public void Reset(in PathRequest request, int version, byte layer, int radius)
        {
            Entity = request.Entity;
            Serial = request.Serial;
            Version = version;
            Layer = layer;
            Radius = radius;
            StartX = request.StartX;
            StartY = request.StartY;
            TargetX = request.TargetX;
            TargetY = request.TargetY;
            Found = false;
            IsRoute = false;
            Expanded = 0;
            _waypointInts = 0;
        }

        public bool Matches(in PathRequest request, int version)
            => request.Entity == Entity && request.Serial == Serial && version == Version
               && request.StartX == StartX && request.StartY == StartY && request.TargetX == TargetX && request.TargetY == TargetY;

        public void Compute(NavigationGrid nav, PathSearch search)
        {
            NavLayer layer = nav.Layers[Layer];
            Found = search.FindRoute(layer, layer.TileOf(StartX, StartY), layer.TileOf(TargetX, TargetY));
            IsRoute = Found && search.IsRoute;
            if (IsRoute)
            {
                ReadOnlySpan<int> nodes = search.RouteNodes;
                if (_waypoints.Length < nodes.Length)
                {
                    _waypoints = new int[Math.Max(nodes.Length, _waypoints.Length * 2)];
                }

                nodes.CopyTo(_waypoints);
                _waypointInts = nodes.Length;
            }
            else if (Found)
            {
                search.Smooth(layer, StartX, StartY, TargetX, TargetY, Math.Min(Radius, (MapState.UnitsPerTile / 2) - 128));
                ReadOnlySpan<int> w = search.Waypoints;
                if (_waypoints.Length < w.Length)
                {
                    _waypoints = new int[Math.Max(w.Length, _waypoints.Length * 2)];
                }

                w.CopyTo(_waypoints);
                _waypointInts = w.Length;
            }

            Expanded = search.Expanded;
        }
    }

    private sealed class PathJob : IParallelJob
    {
        private readonly PathSearch[] _searches = new PathSearch[JobCount];
        private readonly LocalSearch[] _locals = new LocalSearch[JobCount];
        private readonly MinHeap[] _heaps = new MinHeap[JobCount];
        private World _world = null!;
        private NavigationGrid _nav = null!;
        private List<PathResult> _items = [];
        private List<FlowField> _fields = [];

        public PathJob()
        {
            for (int i = 0; i < JobCount; i++)
            {
                _searches[i] = new PathSearch();
                _locals[i] = new LocalSearch();
                _heaps[i] = new MinHeap(4096);
            }
        }

        public void Prepare(World world, NavigationGrid nav, List<PathResult> items, List<FlowField> fields)
        {
            _world = world;
            _nav = nav;
            _items = items;
            _fields = fields;
        }

        // Work item k < fields: build field k; else compute path k - fields. Each item touches only its own output.
        public void Execute(int jobIndex)
        {
            int total = _fields.Count + _items.Count;
            int jobs = Math.Min(JobCount, total);
            for (int k = jobIndex; k < total; k += jobs)
            {
                if (k < _fields.Count)
                {
                    FlowField field = _fields[k];
                    _world.FlowFields.BuildReserved(field, _nav.Layers[field.Layer], _nav.Version, _locals[jobIndex], _heaps[jobIndex]);
                }
                else
                {
                    _items[k - _fields.Count].Compute(_nav, _searches[jobIndex]);
                }
            }
        }
    }
}
