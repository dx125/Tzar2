using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 4 (TDD §8.5): population (GAME-05), construction and repair progress by builders (GAME-03), and
/// production queues — units, research and upgrades (GAME-05/06) — in that order. Runs on the sim thread in ascending
/// entity order (ADRs 0034, 0036, 0037, 0040, 0043).
/// </summary>
public sealed class ProductionSystem : ISimSystem
{
    private readonly List<EntityId> _completed = [];
    private readonly List<(EntityId Entity, ushort Item)> _finished = [];
    private readonly List<(EntityId Target, int Repairers)> _repairs = [];

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Production;

    /// <inheritdoc />
    public string Name => "production";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        UpdatePopulation(world);
        AdvanceConstruction(world);
        AdvanceQueues(world);
    }

    /// <summary>
    /// Recounts every player's population (units' <c>PopulationCost</c>) and room (completed buildings'
    /// <c>PopulationProvided</c> after technology effects, at most the match's limit). Counting from the entities keeps it
    /// exact after any kill, capture or restore.
    /// </summary>
    internal static void UpdatePopulation(World world)
    {
        Span<PlayerState> players = world.Players;
        for (int p = 0; p < players.Length; p++)
        {
            players[p].Population = 0;
            players[p].PopulationCap = 0;
        }

        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        Span<Identity> identities = world.Identities.Components;
        IReadOnlyList<EntityTypeDefinition> types = world.Ruleset.EntityTypes;
        for (int i = 0; i < ids.Length; i++)
        {
            int owner = identities[i].Owner;
            if (owner >= players.Length)
            {
                continue;
            }

            EntityTraits t = types[identities[i].TypeId].Traits;
            players[owner].Population += t.PopulationCost;
            if (t.PopulationProvided > 0 && !world.Constructions.Has(ids[i]))
            {
                players[owner].PopulationCap += world.Stats.Apply(owner, ModifierStat.PopulationProvided, identities[i].TypeId, t.PopulationProvided);
            }
        }

        for (int p = 0; p < players.Length; p++)
        {
            players[p].PopulationCap = Math.Min(players[p].PopulationCap, players[p].PopulationLimit);
        }
    }

    // Builders standing at their site add the tick's time each (linear in the number of builders: UNVERIFIED, UNK-18),
    // sped up by the owner's build rate (Architecture). Health grows with progress from 1 to the maximum; damage taken
    // meanwhile stays taken. Repairers restore hit points at the same rate a full repair takes repairTimePercent of the
    // build time (UNK-18).
    private void AdvanceConstruction(World world)
    {
        int tickMs = world.Config.TickLengthMilliseconds;
        ReadOnlySpan<EntityId> workers = world.Workers.Entities;
        Span<Worker> states = world.Workers.Components;
        for (int i = 0; i < workers.Length; i++)
        {
            ref Worker w = ref states[i];
            if (w.Task == WorkerTask.Repair && w.Phase == WorkerPhase.Repairing)
            {
                CountRepairer(world, workers[i], EntityId.FromValue(w.Target));
                continue;
            }

            if (w.Task != WorkerTask.Build || w.Phase != WorkerPhase.Building)
            {
                continue;
            }

            EntityId site = EntityId.FromValue(w.Target);
            int c = world.Constructions.IndexOf(site);
            if (c < 0 || !world.Entities.IsAlive(site) || !Interaction.WorkerReaches(world, workers[i], site))
            {
                continue;
            }

            ref Construction construction = ref world.Constructions.At(c);
            Identity identity = world.Identities.Get(site);
            (_, int bp) = world.Stats.Modifier(identity.Owner, ModifierStat.BuildRate, identity.TypeId);
            long scaled = world.BuildsInstantly(identity.Owner) ? (long)construction.TotalMs * 10_000 // hmbuildozer (GAME-19)
                : ((long)tickMs * (10_000 + Math.Max(bp, -9_999))) + construction.CarryBasisPoints;
            int before = construction.ProgressMs;
            construction.ProgressMs = (int)Math.Min(construction.TotalMs, before + (scaled / 10_000));
            construction.CarryBasisPoints = construction.ProgressMs >= construction.TotalMs ? 0 : (int)(scaled % 10_000);
            ref Health health = ref world.Healths.Get(site);
            health.Current = Math.Min(health.Max, health.Current + HealthAt(health.Max, construction.ProgressMs, construction.TotalMs) - HealthAt(health.Max, before, construction.TotalMs));
        }

        foreach ((EntityId target, int repairers) in _repairs)
        {
            Repair(world, target, repairers, tickMs);
        }

        _repairs.Clear();
        _completed.Clear();
        ReadOnlySpan<EntityId> sites = world.Constructions.Entities;
        Span<Construction> constructions = world.Constructions.Components;
        for (int i = 0; i < sites.Length; i++)
        {
            if (constructions[i].ProgressMs >= constructions[i].TotalMs)
            {
                _completed.Add(sites[i]);
            }
        }

        foreach (EntityId site in _completed)
        {
            world.CompleteConstruction(site);
            Identity identity = world.Identities.Get(site);
            world.Events.Emit(new SimEvent(SimEventKind.ConstructionCompleted, identity.Owner, site, 0, 0, identity.TypeId));
            AutoGather(world, site);
        }
    }

    // A repairer counts for its target when it is in reach of its own, damaged, completed target (the owner is checked
    // every tick: a building captured in step 12 is not healed by its old owner's peasants).
    private void CountRepairer(World world, EntityId worker, EntityId target)
    {
        if (!world.Entities.IsAlive(target) || world.Healths.IndexOf(target) is not (>= 0 and var h)
            || world.Identities.Get(target).Owner != world.Identities.Get(worker).Owner || world.Constructions.Has(target)
            || world.Healths.At(h).Current >= world.Healths.At(h).Max || !Interaction.WorkerReaches(world, worker, target))
        {
            return; // the worker system re-approaches or gives up
        }

        for (int k = 0; k < _repairs.Count; k++)
        {
            if (_repairs[k].Target == target)
            {
                _repairs[k] = (target, _repairs[k].Repairers + 1);
                return;
            }
        }

        _repairs.Add((target, 1));
    }

    // Repair (GAME-03, owner decisions; UNVERIFIED UNK-18): one repairer restores max x tick / (build time x
    // repairTimePercent); further repairers add less each (repairerFalloffPercent: 100%, 75%, 50%, 25%, then nothing).
    // The hit points cost repairCostPercent of the effective construction cost for a full 0 -> max repair, charged pro
    // rata as they are restored; if the owner cannot pay, the repair pauses.
    private static void Repair(World world, EntityId target, int repairers, int tickMs)
    {
        ref Health health = ref world.Healths.Get(target);
        Identity identity = world.Identities.Get(target);
        EntityTypeDefinition type = world.Ruleset.EntityTypes[identity.TypeId];
        GameplaySettings g = world.Ruleset.Gameplay;
        if (!world.RepairProgresses.Has(target))
        {
            world.RepairProgresses.Set(target, default);
        }

        ref RepairProgress progress = ref world.RepairProgresses.Get(target);
        long crew = 0;
        for (int k = 0; k < repairers; k++)
        {
            crew += Math.Max(0, 10_000 - ((long)g.RepairerFalloffBasisPoints * k));
        }

        (_, int bp) = world.Stats.Modifier(identity.Owner, ModifierStat.BuildRate, identity.TypeId);
        long denominator = Math.Max(1L, (long)Math.Max(type.Traits.BuildTimeMs, 1000) * g.RepairTimeBasisPoints / 10_000);
        long numerator = ((long)health.Max * tickMs * (10_000 + Math.Max(bp, -9_999)) / 10_000 * crew / 10_000) + progress.Carry;
        int gain = (int)Math.Min(numerator / denominator, health.Max - health.Current);
        long carry = numerator % denominator;
        if (gain > 0 && !PayForRepair(world, identity.Owner, type, health.Max, progress.HealedHp, gain))
        {
            return; // the owner cannot pay: the repair pauses (the carry is kept)
        }

        progress.Carry = (int)Math.Min(carry, int.MaxValue);
        progress.HealedHp = (int)Math.Min((long)progress.HealedHp + gain, int.MaxValue / 2);
        health.Current += gain;
        if (health.Current >= health.Max)
        {
            progress.Carry = 0;
            world.Events.Emit(new SimEvent(SimEventKind.Repaired, identity.Owner, target, 0, 0, identity.TypeId));
        }
    }

    // Charges what restoring hit points healed..healed+gain costs: due(h) = ceil(cost x repairCost x h / max) per
    // resource, so the total charged never depends on how the hit points were split over ticks and no hit point is free.
    private static bool PayForRepair(World world, byte owner, EntityTypeDefinition type, int max, int healed, int gain)
    {
        if (!world.HasPlayer(owner) || type.Traits.Cost.IsEmpty || world.Ruleset.Gameplay.RepairCostBasisPoints == 0)
        {
            return true;
        }

        ref PlayerState state = ref world.Players[owner];
        int resources = type.Traits.Cost.Length;
        Span<int> charge = stackalloc int[Ruleset.MaxResourceTypes];
        for (int r = 0; r < resources; r++)
        {
            long full = (long)ResearchRules.CostOf(world, owner, type, r) * world.Ruleset.Gameplay.RepairCostBasisPoints;
            long divisor = 10_000L * Math.Max(1, max);
            charge[r] = (int)(Due(full, healed + (long)gain, divisor) - Due(full, healed, divisor));
            if (state.Resources[r] < charge[r])
            {
                return false;
            }
        }

        for (int r = 0; r < resources; r++)
        {
            state.Resources[r] -= charge[r];
        }

        return true;
    }

    // 1.05: the builders of a completed storage or lumber mill start gathering what it accepts nearby; the builder of a
    // field farms it (UNVERIFIED).
    private static void AutoGather(World world, EntityId building)
    {
        EntityTraits traits = world.TypeOf(building).Traits;
        if (!traits.AutoGather)
        {
            return;
        }

        byte owner = world.Identities.Get(building).Owner;
        Position at = world.Positions.Get(building);
        for (int i = 0; i < world.Workers.Count; i++)
        {
            ref Worker w = ref world.Workers.At(i);
            if (w.Task != WorkerTask.Build || w.Target != building.Value)
            {
                continue;
            }

            EntityId worker = world.Workers.Entities[i];
            if (world.TypeOf(worker).Traits.Harvester is not { } harvester)
            {
                continue;
            }

            EntityId node = EntityId.None;
            if (traits.ResourceNode is not null)
            {
                node = building;
            }
            else
            {
                long best = long.MaxValue;
                for (int r = 0; r < world.Ruleset.ResourceTypes.Count; r++)
                {
                    if (!traits.AcceptsDropOff(r) || !harvester.CanGather(r))
                    {
                        continue;
                    }

                    EntityId candidate = Interaction.NearestNode(world, owner, r, at.X, at.Y, world.Ruleset.Gameplay.NodeSearchRadiusTiles);
                    if (candidate.IsNone)
                    {
                        continue;
                    }

                    long d = Interaction.DistanceSquaredTo(world, at.X, at.Y, candidate);
                    if (d < best || (d == best && candidate.Index < node.Index))
                    {
                        best = d;
                        node = candidate;
                    }
                }
            }

            if (!node.IsNone)
            {
                GameplayCommands.StartHarvest(world, owner, worker, node);
            }
        }
    }

    private static long Due(long full, long healed, long divisor) => ((full * healed) + divisor - 1) / divisor;

    private static int HealthAt(int max, int progress, int total) => total <= 0 ? max : 1 + (int)((long)(max - 1) * progress / total);

    private void AdvanceQueues(World world)
    {
        int tickMs = world.Config.TickLengthMilliseconds;
        _finished.Clear();
        for (int i = 0; i < world.Productions.Count; i++)
        {
            ref Production q = ref world.Productions.At(i);
            if (q.Count == 0)
            {
                continue;
            }

            EntityId producer = world.Productions.Entities[i];
            ushort item = q.Queue[0];
            int timeMs = ItemTime(world, item);
            if (q.ProgressMs < timeMs)
            {
                q.ProgressMs = world.BuildsInstantly(world.Identities.Get(producer).Owner) ? timeMs : Math.Min(timeMs, q.ProgressMs + tickMs);
                if (q.ProgressMs < timeMs)
                {
                    continue;
                }
            }

            if (QueueItems.IsResearch(item) || QueueItems.IsUpgrade(item))
            {
                PopFirst(ref q);
                _finished.Add((producer, item)); // applied after the pass: an upgrade may change the entity's stores
                continue;
            }

            EntityTypeDefinition type = world.Ruleset.EntityTypes[item];
            ProductionStall stall = TryRelease(world, producer, ref q, type);
            if (stall != ProductionStall.None)
            {
                if ((byte)stall != q.Stall)
                {
                    q.Stall = (byte)stall;
                    world.Events.Emit(new SimEvent(SimEventKind.ProductionStalled, world.Identities.Get(producer).Owner, producer, 0, 0, (int)stall));
                }

                continue;
            }

            PopFirst(ref q);
        }

        foreach ((EntityId entity, ushort item) in _finished)
        {
            if (!world.Entities.IsAlive(entity))
            {
                continue;
            }

            if (QueueItems.IsResearch(item))
            {
                ResearchRules.CompleteResearch(world, world.Identities.Get(entity).Owner, QueueItems.Id(item), entity);
            }
            else
            {
                ResearchRules.CompleteUpgrade(world, entity, QueueItems.Id(item));
            }
        }
    }

    private static int ItemTime(World world, ushort item)
        => QueueItems.IsResearch(item) ? world.Ruleset.Technologies[QueueItems.Id(item)].TimeMs
            : QueueItems.IsUpgrade(item) ? world.Ruleset.Upgrades[QueueItems.Id(item)].TimeMs
            : world.Ruleset.EntityTypes[item].Traits.BuildTimeMs;

    private static void PopFirst(ref Production q)
    {
        q.Stall = 0;
        q.ProgressMs = 0;
        for (int k = 1; k < q.Count; k++)
        {
            q.Queue[k - 1] = q.Queue[k];
            q.Paid[k - 1] = q.Paid[k];
        }

        q.Count--;
        q.Queue[q.Count] = 0;
        q.Paid[q.Count] = default;
    }

    // Spawning a unit adds to other stores only (units never produce), so references into the production store stay valid.
    private static ProductionStall TryRelease(World world, EntityId producer, ref Production q, EntityTypeDefinition type)
    {
        Identity owner = world.Identities.Get(producer);
        if (!world.HasPlayer(owner.Owner))
        {
            return ProductionStall.EntityLimit; // only player-owned producers exist (validated on restore)
        }

        ref PlayerState player = ref world.Players[owner.Owner];
        if (type.Traits.PopulationCost > 0 && player.Population + type.Traits.PopulationCost > player.PopulationCap)
        {
            return ProductionStall.Population;
        }

        if (world.Entities.RemainingCapacity < 1)
        {
            return ProductionStall.EntityLimit;
        }

        // Leave toward the rally point, or toward the south side by default.
        (int x0, _, int x1, int y1) = Interaction.Rect(world, producer);
        int towardX = q.HasRally == 1 ? q.RallyX : (x0 + x1) / 2;
        int towardY = q.HasRally == 1 ? q.RallyY : y1 + MapState.UnitsPerTile;
        if (!Interaction.ApproachPoint(world, producer, towardX, towardY, type.Locomotor, out int x, out int y))
        {
            return ProductionStall.NoExit;
        }

        EntityId unit = world.Spawn(type.Id, owner.Owner, x, y);
        player.Population += type.Traits.PopulationCost;
        world.Events.Emit(new SimEvent(SimEventKind.UnitTrained, owner.Owner, unit, producer.Index, 0, type.Id));
        if (q.HasRally == 1)
        {
            Rally(world, unit, owner.Owner, q.RallyMode, EntityId.FromValue(q.RallyTarget), q.RallyX, q.RallyY);
        }

        return ProductionStall.None;
    }

    // TDD §9.3 rally points: Ctrl = move; Alt = the default action on the rally target (harvest a node, help a site,
    // attack an enemy), else a move; Ctrl + Alt = move-advance.
    private static void Rally(World world, EntityId unit, byte owner, RallyMode mode, EntityId target, int x, int y)
    {
        if (mode == RallyMode.DefaultAction && world.Entities.IsAlive(target) && world.Identities.IndexOf(target) is >= 0 and var t)
        {
            Identity identity = world.Identities.At(t);
            if (world.ResourceStocks.Has(target) && Interaction.IsHarvestable(world, target, world.TypeOf(target).Traits.ResourceNode!.Resource, owner)
                && GameplayCommands.StartHarvest(world, owner, unit, target))
            {
                return;
            }

            if (identity.Owner == owner && world.Constructions.Has(target) && world.Workers.IndexOf(unit) is >= 0 and var w
                && world.TypeOf(unit).Traits.CanBuild(identity.TypeId))
            {
                ref Worker worker = ref world.Workers.At(w);
                worker.Task = WorkerTask.Build;
                worker.Phase = WorkerPhase.ToSite;
                worker.Target = target.Value;
                return;
            }

            if (identity.Owner != owner && world.Combats.IndexOf(unit) is >= 0 and var c
                && world.Ruleset.EntityTypes[identity.TypeId].Traits.Category is EntityCategory.Unit or EntityCategory.Building)
            {
                ref Combat combat = ref world.Combats.At(c);
                combat.Target = target.Value;
                combat.Mode = CombatMode.Explicit;
                combat.ChaseTick = -1;
                return;
            }
        }

        if (mode == RallyMode.MoveAdvance && world.Combats.IndexOf(unit) is >= 0 and var a)
        {
            world.Combats.At(a).Mode = CombatMode.AttackMove;
        }

        Interaction.MoveTo(world, unit, x, y);
    }
}
