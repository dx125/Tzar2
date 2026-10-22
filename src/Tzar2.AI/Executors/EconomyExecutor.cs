using System;
using System.Collections.Generic;
using Tzar2.AI.Runtime;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;

namespace Tzar2.AI.Executors;

/// <summary>
/// Peasant allocation (REQ-AI-02 economy manager; ADR 0052). Gatherers are shared between the resources the kingdom
/// can gather (a node and an own drop-off exist) by base shares plus a share in proportion to what the unmet goals
/// still need; idle workers go to the resource furthest below its share, and a few workers per run move from the most
/// over- to the most under-staffed resource. Nodes are chosen near the drop-offs, with a cap of workers per node. Food
/// comes from fields: when the food share is larger than the kingdom's fields, it builds one more next to the farm.
/// With the tactics' <see cref="TacticsLevel.AvoidTowers"/> (ADR 0178), nodes in reach of a hostile tower are avoided
/// and their gatherers sent elsewhere.
/// </summary>
public sealed class EconomyExecutor : IAiExecutor
{
    private const int ShortlistSize = 24;

    private readonly int[] _current = new int[Ruleset.MaxResourceTypes];
    private readonly int[] _desired = new int[Ruleset.MaxResourceTypes];
    private readonly long[] _share = new long[Ruleset.MaxResourceTypes];
    private readonly long[] _demand = new long[Ruleset.MaxResourceTypes];
    private readonly bool[] _gatherable = new bool[Ruleset.MaxResourceTypes];
    private readonly bool[] _resourceGoalSeen = new bool[Ruleset.MaxResourceTypes];
    private readonly List<EntityId> _idle = [];
    private readonly List<EntityId> _endangered = [];
    private readonly List<EntityId> _dropOffs = [];
    private readonly List<(long Score, int Node)>[] _shortlist = new List<(long, int)>[Ruleset.MaxResourceTypes];
    private readonly Dictionary<int, int> _assignedNow = [];

    /// <summary>Creates the executor.</summary>
    public EconomyExecutor()
    {
        for (int r = 0; r < _shortlist.Length; r++)
        {
            _shortlist[r] = [];
        }
    }

    /// <inheritdoc />
    public string Name => "economy";

    /// <inheritdoc />
    public void Run(AiExecutorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.EconomyAi || context.Player.Workers.Count == 0)
        {
            return;
        }

        World world = context.World;
        int resources = context.Model.ResourceCount;
        Array.Clear(_current);
        Array.Clear(_desired);
        Array.Clear(_share);
        Array.Clear(_demand);
        Array.Clear(_gatherable);
        Array.Clear(_resourceGoalSeen);
        _idle.Clear();
        _endangered.Clear();
        _assignedNow.Clear();
        CollectDropOffs(context);
        BuildShortlists(context);

        int gatherers = 0;
        foreach (EntityId w in context.Player.Workers)
        {
            if (world.TypeOf(w).Traits.Harvester is null || context.IsClaimed(w))
            {
                continue;
            }

            Worker state = world.Workers.Get(w);
            if (state.Task == WorkerTask.Build)
            {
                continue;
            }

            gatherers++;
            if (state.Task == WorkerTask.Harvest && state.NodeResource < resources)
            {
                _current[state.NodeResource]++;
                if (InTowerReach(context, state.Target))
                {
                    _endangered.Add(w);
                }
            }
            else if (!context.IsMoving(w))
            {
                _idle.Add(w);
            }
        }

        ComputeShares(context, gatherers);
        BuildFields(context);
        AssignIdle(context);
        Rebalance(context);
        Evacuate(context);
    }

    // A node a hostile tower reaches, with a tile to spare (tactics AvoidTowers).
    private static bool InTowerReach(AiExecutorContext context, uint node)
    {
        if (!context.Tactics.AvoidTowers || node == 0)
        {
            return false;
        }

        EntityId e = EntityId.FromValue(node);
        if (!context.World.Entities.IsAlive(e) || context.World.Positions.IndexOf(e) is not (>= 0 and var i))
        {
            return false;
        }

        Position p = context.World.Positions.At(i);
        return context.Threats.Covered(p.X, p.Y, MapState.UnitsPerTile);
    }

    // Gatherers on a node in reach of a hostile tower go to a safe node of the same resource, or home.
    private void Evacuate(AiExecutorContext context)
    {
        World world = context.World;
        int moved = 0;
        foreach (EntityId w in _endangered)
        {
            if (moved >= context.Options.Economy.AssignmentsPerRun || context.CommandsLeft <= 0)
            {
                return;
            }

            if (context.IsClaimed(w))
            {
                continue;
            }

            moved++;
            if (SendTo(context, w, world.Workers.Get(w).NodeResource))
            {
                continue;
            }

            context.Selection.Clear();
            context.Selection.Add(w);
            (int tx, int ty) = context.Player.Town;
            context.Issue(CommandKind.Move, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(context.Selection), tx, ty);
        }
    }

    private void CollectDropOffs(AiExecutorContext context)
    {
        _dropOffs.Clear();
        foreach (EntityId b in context.Player.Buildings)
        {
            if (context.World.TypeOf(b).Traits.DropOffMask != 0)
            {
                _dropOffs.Add(b);
            }
        }
    }

    // Per resource: the best nodes by distance to the nearest own drop-off accepting it (one pass over the nodes).
    private void BuildShortlists(AiExecutorContext context)
    {
        World world = context.World;
        EconomyOptions options = context.Options.Economy;
        long limit = (long)options.MaxNodeDistanceTiles * MapState.UnitsPerTile;
        foreach (List<(long Score, int Node)> list in _shortlist)
        {
            list.Clear();
        }

        ReadOnlySpan<AiNode> nodes = context.Census.Nodes;
        for (int i = 0; i < nodes.Length; i++)
        {
            AiNode node = nodes[i];
            if (node.Owner != context.Slot && world.HasPlayer(node.Owner))
            {
                continue; // another kingdom's field
            }

            long best = long.MaxValue;
            foreach (EntityId d in _dropOffs)
            {
                if (!world.TypeOf(d).Traits.AcceptsDropOff(node.Resource))
                {
                    continue;
                }

                Position p = world.Positions.Get(d);
                best = Math.Min(best, AiExecutorContext.Distance(p.X, p.Y, node.X, node.Y));
            }

            if (best > limit || node.Resource >= _shortlist.Length)
            {
                continue;
            }

            if (context.Census.WorkersOn(node.Id.Index) >= Capacity(context, node))
            {
                continue;
            }

            if (context.Tactics.AvoidTowers && context.Threats.Covered(node.X, node.Y, MapState.UnitsPerTile))
            {
                continue;
            }

            _gatherable[node.Resource] = true;
            Insert(_shortlist[node.Resource], (best, i));
        }
    }

    private static void Insert(List<(long Score, int Node)> list, (long Score, int Node) item)
    {
        if (list.Count >= ShortlistSize && item.Score >= list[^1].Score)
        {
            return;
        }

        int at = list.Count;
        while (at > 0 && list[at - 1].Score > item.Score)
        {
            at--;
        }

        list.Insert(at, item);
        if (list.Count > ShortlistSize)
        {
            list.RemoveAt(list.Count - 1);
        }
    }

    private void ComputeShares(AiExecutorContext context, int gatherers)
    {
        World world = context.World;
        int resources = context.Model.ResourceCount;
        EconomyOptions options = context.Options.Economy;
        AiCensusPlayer player = context.Player;
        bool fields = FieldType(context) >= 0;
        int food = world.Ruleset.ResourceId("food");
        if (food >= 0 && fields && HasDropOff(context, food))
        {
            _gatherable[food] = true; // food comes from fields the economy builds
        }

        // What the first unmet goals still need.
        int considered = 0;
        foreach (AiGoal goal in context.Goals)
        {
            if (considered >= 16)
            {
                break;
            }

            AiSymbol symbol = context.SymbolOf(goal);
            if (goal.Kind == AiGoalKind.Resource && symbol.Id < resources && !_resourceGoalSeen[symbol.Id])
            {
                _resourceGoalSeen[symbol.Id] = true; // the first (highest-priority) goal for a resource counts
                _demand[symbol.Id] += Math.Max(0, (goal.Number < 0 ? 1 : goal.Number) - world.Players[context.Slot].Resources[symbol.Id]);
                considered++;
            }
            else if (goal.Kind == AiGoalKind.Object && symbol.OwnType >= 0)
            {
                int deficit = context.TargetOf(goal) - player.Completed[symbol.Id] - context.InProgress(symbol.Id);
                if (deficit <= 0 || !context.FirstAttempt(symbol.Id, 2))
                {
                    continue;
                }

                System.Collections.Immutable.ImmutableArray<int> cost = world.Ruleset.EntityTypes[symbol.OwnType].Traits.Cost;
                for (int r = 0; r < cost.Length && r < resources; r++)
                {
                    _demand[r] += Math.Max(0, ((long)cost[r] * Math.Min(deficit, 4)) - world.Players[context.Slot].Resources[r]);
                }

                considered++;
            }
        }

        Distribute(context, gatherers);

        // Fields: one peasant tends each (ECO-021), so the food gatherers just asked for need as many fields. What the
        // missing ones (up to four) cost is demand too, and the gatherers are shared out again with it: otherwise the
        // kingdom never has the wood for them.
        int fieldType = FieldType(context);
        if (food >= 0 && fieldType >= 0 && _gatherable[food])
        {
            int cls = context.Model.ClassOf(fieldType);
            int missing = Math.Min(4, _desired[food] - (cls < 0 ? 0 : player.Completed[cls] + context.InProgress(cls)));
            if (missing > 0)
            {
                System.Collections.Immutable.ImmutableArray<int> cost = world.Ruleset.EntityTypes[fieldType].Traits.Cost;
                for (int r = 0; r < cost.Length && r < resources; r++)
                {
                    _demand[r] += Math.Max(0, ((long)cost[r] * missing) - world.Players[context.Slot].Resources[r]);
                }

                Array.Clear(_desired);
                Array.Clear(_share);
                Distribute(context, gatherers);
            }
        }
    }

    // The gatherers shared out by the demand and the base shares.
    private void Distribute(AiExecutorContext context, int gatherers)
    {
        World world = context.World;
        int resources = context.Model.ResourceCount;
        EconomyOptions options = context.Options.Economy;
        long demandSum = 0;
        for (int r = 0; r < resources; r++)
        {
            demandSum += _gatherable[r] ? _demand[r] : 0;
        }

        long total = 0;
        for (int r = 0; r < resources; r++)
        {
            if (!_gatherable[r])
            {
                continue;
            }

            // A resource stocked beyond the surplus mark keeps only the gatherers its goals ask for.
            int stock = world.Players[context.Slot].Resources[r];
            int baseShare = options.BaseShares.TryGetValue(world.Ruleset.ResourceTypes[r], out int b) && stock < options.SurplusStock ? b : 0;
            _share[r] = baseShare + (demandSum > 0 ? options.DemandShare * _demand[r] / demandSum : 0);
            total += _share[r];
        }

        if (total == 0)
        {
            return;
        }

        // Largest remainder, ties to the lower resource id.
        int given = 0;
        for (int r = 0; r < resources; r++)
        {
            _desired[r] = (int)(gatherers * _share[r] / total);
            given += _desired[r];
        }

        while (given < gatherers)
        {
            int best = -1;
            long bestRemainder = -1;
            for (int r = 0; r < resources; r++)
            {
                long remainder = _share[r] == 0 ? -1 : (gatherers * _share[r]) % total;
                if (remainder > bestRemainder && _share[r] > 0)
                {
                    bestRemainder = remainder;
                    best = r;
                }
            }

            if (best < 0)
            {
                break;
            }

            _desired[best]++;
            _share[best] = 0; // one extra each
            given++;
        }
    }

    // One field per run while the food share has more gatherers than the kingdom has fields: one peasant tends each field
    // (ECO-021). Fields have their own limit of sites at once, so other buildings do not leave farmers waiting.
    private void BuildFields(AiExecutorContext context)
    {
        int field = FieldType(context);
        int food = context.World.Ruleset.ResourceId("food");
        if (field < 0 || food < 0 || !HasDropOff(context, food) || context.CommandsLeft <= 0)
        {
            return;
        }

        int cls = context.Model.ClassOf(field);
        int fields = cls < 0 ? 0 : context.Player.Completed[cls] + context.InProgress(cls);
        EntityTypeDefinition type = context.World.Ruleset.EntityTypes[field];
        int sites = Math.Max(2 + (context.Player.Workers.Count / 15), (_desired[food] - fields + 1) / 2);
        if (fields >= _desired[food] || (cls >= 0 && context.InProgress(cls) >= sites)
            || !context.CanAfford(type.Traits.Cost, LockedFor(context)) || !Sim.Systems.PlacementQueries.RequirementsMet(context.World, context.Slot, (ushort)field))
        {
            return;
        }

        if (ConstructionExecutor.TryStart(context, (ushort)field))
        {
            foreach (EntityId w in context.Selection)
            {
                _idle.Remove(w);
            }
        }
    }

    private void AssignIdle(AiExecutorContext context)
    {
        int resources = context.Model.ResourceCount;
        int assigned = 0;
        Span<bool> tried = stackalloc bool[resources];
        foreach (EntityId w in _idle)
        {
            if (assigned >= context.Options.Economy.AssignmentsPerRun || context.CommandsLeft <= 0)
            {
                return;
            }

            if (context.IsClaimed(w))
            {
                continue;
            }

            // The resource short of the most gatherers first; one with no room left (every field tended: ECO-021) gives
            // way to the next.
            tried.Clear();
            for (int attempt = 0; attempt < resources; attempt++)
            {
                int best = -1;
                int bestGap = int.MinValue;
                for (int r = 0; r < resources; r++)
                {
                    int gap = _desired[r] - _current[r];
                    if (!tried[r] && _gatherable[r] && _shortlist[r].Count > 0 && gap > bestGap)
                    {
                        bestGap = gap;
                        best = r;
                    }
                }

                if (best < 0)
                {
                    break;
                }

                tried[best] = true;
                if (SendTo(context, w, best))
                {
                    _current[best]++;
                    assigned++;
                    break;
                }
            }
        }
    }

    private void Rebalance(AiExecutorContext context)
    {
        World world = context.World;
        int resources = context.Model.ResourceCount;
        for (int move = 0; move < context.Options.Economy.RebalancesPerRun && context.CommandsLeft > 0; move++)
        {
            int from = -1;
            int to = -1;
            for (int r = 0; r < resources; r++)
            {
                if (_current[r] - _desired[r] >= 2 && (from < 0 || _current[r] - _desired[r] > _current[from] - _desired[from]))
                {
                    from = r;
                }

                if (_gatherable[r] && _shortlist[r].Count > 0 && _desired[r] - _current[r] >= 2 && (to < 0 || _desired[r] - _current[r] > _desired[to] - _current[to]))
                {
                    to = r;
                }
            }

            if (from < 0 || to < 0)
            {
                return;
            }

            // The first worker on 'from' that carries nothing (it would lose nothing by switching).
            EntityId mover = EntityId.None;
            foreach (EntityId w in context.Player.Workers)
            {
                Worker state = world.Workers.Get(w);
                if (!context.IsClaimed(w) && state.Task == WorkerTask.Harvest && state.NodeResource == from && state.CarryAmount == 0)
                {
                    mover = w;
                    break;
                }
            }

            if (mover.IsNone || !SendTo(context, mover, to))
            {
                return;
            }

            _current[from]--;
            _current[to]++;
        }
    }

    // Harvest the shortlisted node with the best score: distance to a drop-off + a quarter of the walk + crowding.
    private bool SendTo(AiExecutorContext context, EntityId worker, int resource)
    {
        World world = context.World;
        if (world.TypeOf(worker).Traits.Harvester is not { } harvester || !harvester.CanGather(resource))
        {
            return false;
        }

        Position at = world.Positions.Get(worker);
        ReadOnlySpan<AiNode> nodes = context.Census.Nodes;
        int bestNode = -1;
        long bestScore = long.MaxValue;
        foreach ((long score, int index) in _shortlist[resource])
        {
            AiNode node = nodes[index];
            int crowd = context.Census.WorkersOn(node.Id.Index) + (_assignedNow.TryGetValue(index, out int n) ? n : 0);
            if (crowd >= Capacity(context, node))
            {
                continue;
            }

            long s = score + (AiExecutorContext.Distance(at.X, at.Y, node.X, node.Y) / 4) + ((long)crowd * 2 * MapState.UnitsPerTile);
            if (s < bestScore)
            {
                bestScore = s;
                bestNode = index;
            }
        }

        if (bestNode < 0)
        {
            return false;
        }

        context.Selection.Clear();
        context.Selection.Add(worker);
        if (!context.Issue(CommandKind.Harvest, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(context.Selection), target: nodes[bestNode].Id))
        {
            return false;
        }

        _assignedNow[bestNode] = (_assignedNow.TryGetValue(bestNode, out int before) ? before : 0) + 1;
        return true;
    }

    private static int Capacity(AiExecutorContext context, AiNode node)
    {
        EconomyOptions options = context.Options.Economy;
        ResourceNodeDefinition definition = context.World.TypeOf(node.Id).Traits.ResourceNode!;
        if (definition.Amount == ResourceNodeDefinition.Unlimited)
        {
            return options.WorkersPerField;
        }

        return definition.Amount <= options.SmallNodeAmount ? options.WorkersPerSmallNode : options.WorkersPerLargeNode;
    }

    // The civilization's field type (an unlimited food node the workers can build), or -1.
    private static int FieldType(AiExecutorContext context)
    {
        World world = context.World;
        foreach (EntityId w in context.Player.Workers)
        {
            foreach (ushort t in world.TypeOf(w).Traits.Builds)
            {
                if (world.Ruleset.EntityTypes[t].Traits.ResourceNode is { Amount: ResourceNodeDefinition.Unlimited })
                {
                    return t;
                }
            }

            break; // all workers of a kingdom build the same
        }

        return -1;
    }

    private bool HasDropOff(AiExecutorContext context, int resource)
    {
        foreach (EntityId d in _dropOffs)
        {
            if (context.World.TypeOf(d).Traits.AcceptsDropOff(resource))
            {
                return true;
            }
        }

        return false;
    }

    // Fields are economy infrastructure: they may not spend what an active needs_locked saves (the lock at the pass's end).
    private static byte LockedFor(AiExecutorContext context) => context.Goals.Length > 0 ? context.Goals[^1].LockedResources : (byte)0;
}
