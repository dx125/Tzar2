using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 6 (activities, TDD §8.5): the state machines of workers (ADRs 0035, 0036, 0042-0044). A harvester walks
/// to its node, gathers (step 5), carries the load to the nearest own completed drop-off that accepts it from its kind
/// of worker (boats deliver fish to harbors), and returns; when the node is gone it takes the nearest node of the same
/// resource within <c>nodeSearchRadiusTiles</c> it can get to, toward the drop-off (ADR 0180). A builder walks to its site
/// and builds (step 4) until the site is complete, then moves on to the next unfinished segment of the wall it built. A
/// repairer walks to its target and repairs it (step 4) until it is whole. A capturer walks to its target and digs (step
/// 12), or takes an animal at once. A peasant that stands idle for <c>idleWorkAfterMs</c> finds work nearby by itself.
/// Movement goes through the normal path requests (step 7); a worker that cannot get within reach after
/// <see cref="MaxRetries"/> moves gives up.
/// </summary>
public sealed class WorkerSystem : ISimSystem
{
    /// <summary>Movement attempts without reaching the target before a worker gives up its task.</summary>
    public const int MaxRetries = 5;

    /// <summary>Ticks between searches for a drop-off while none is available.</summary>
    public const int DropOffSearchInterval = 20;

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Activities;

    /// <inheritdoc />
    public string Name => "workers";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        int tick = world.Tick.Value;
        int tickMs = world.Config.TickLengthMilliseconds;
        for (int i = 0; i < world.Workers.Count; i++)
        {
            // Index-based: the passes below never add or remove workers (an instant capture changes an owner only).
            EntityId worker = world.Workers.Entities[i];
            ref Worker w = ref world.Workers.At(i);
            if (w.Task != WorkerTask.None)
            {
                w.IdleMs = 0;
            }

            switch (w.Task)
            {
                case WorkerTask.None:
                    IdleWork(world, worker, ref w, tickMs);
                    break;
                case WorkerTask.Harvest:
                    Harvest(world, worker, ref w, tick);
                    break;
                case WorkerTask.Build:
                    Build(world, worker, ref w);
                    break;
                case WorkerTask.Repair:
                    Repair(world, worker, ref w);
                    break;
                case WorkerTask.Capture:
                    Capture(world, worker, ref w);
                    break;
            }
        }
    }

    private static void Harvest(World world, EntityId worker, ref Worker w, int tick)
    {
        EntityTypeDefinition type = world.TypeOf(worker);
        Position position = world.Positions.Get(worker);
        switch (w.Phase)
        {
            case WorkerPhase.ToNode:
            {
                if (w.CarryResource == w.NodeResource && w.CarryAmount >= GameplayCommands.CarryCapacity(world, worker))
                {
                    StartDropOff(ref w);
                    return;
                }

                EntityId node = EntityId.FromValue(w.Target);
                byte owner = world.Identities.Get(worker).Owner;
                if (!Interaction.IsHarvestable(world, node, w.NodeResource, owner) || !Interaction.HasRoom(world, node, worker))
                {
                    // Gone, empty or taken (one peasant per field, ECO-021): the nearest other node of the resource; a dry
                    // cow is waited for when there is none.
                    EntityId dry = Interaction.IsHarvestable(world, node, w.NodeResource, owner, waitForRegrowth: true) && Interaction.HasRoom(world, node, worker) ? node : EntityId.None;
                    node = Interaction.NextNode(world, worker, w.NodeResource, w.NodeX, w.NodeY, world.Ruleset.Gameplay.NodeSearchRadiusTiles);
                    if (node.IsNone)
                    {
                        node = dry;
                    }

                    if (node.IsNone)
                    {
                        if (w.CarryAmount > 0)
                        {
                            StartDropOff(ref w);
                        }
                        else
                        {
                            Idle(ref w);
                        }

                        return;
                    }

                    w.Target = node.Value;
                    w.Retries = 0;
                    Position p = world.Positions.Get(node);
                    (w.NodeX, w.NodeY) = (p.X, p.Y);
                }

                if (Interaction.WorkerReaches(world, worker, node))
                {
                    Interaction.Halt(world, worker);
                    w.Phase = WorkerPhase.Gathering;
                    w.TimerMs = 0;
                    w.Retries = 0;
                }
                else if (!Interaction.IsMoving(world, worker) && !Interaction.HasWorkSpot(world, node, worker)
                    && Interaction.NextNode(world, worker, w.NodeResource, w.NodeX, w.NodeY, world.Ruleset.Gameplay.NodeSearchRadiusTiles, exclude: node) is { IsNone: false } other)
                {
                    // Every place at the node is taken (at most eight peasants at a tree): the next node with room.
                    w.Target = other.Value;
                    w.Retries = 0;
                    Position p = world.Positions.Get(other);
                    (w.NodeX, w.NodeY) = (p.X, p.Y);
                }
                else if (!Approach(world, worker, ref w, node, position, type))
                {
                    // Cannot get there: try another node next tick (excluding none: the nearest may be the same).
                    if (w.CarryAmount > 0)
                    {
                        StartDropOff(ref w);
                    }
                    else
                    {
                        Idle(ref w);
                    }
                }

                return;
            }

            case WorkerPhase.Gathering:
                return; // step 5 gathers; it switches the phase when full, empty or out of reach

            case WorkerPhase.ToDropOff:
            {
                if (w.CarryAmount == 0)
                {
                    w.Phase = WorkerPhase.ToNode;
                    return;
                }

                EntityId drop = EntityId.FromValue(w.DropTarget);
                byte owner = world.Identities.Get(worker).Owner;
                DropOffAccess access = Interaction.AccessOf(type);
                if (!Interaction.IsCompletedStructure(world, drop) || world.Identities.Get(drop).Owner != owner || !world.TypeOf(drop).Traits.AcceptsDropOff(w.CarryResource)
                    || (world.TypeOf(drop).Traits.DropOffAccess & access) == 0)
                {
                    w.DropTarget = 0;
                    if (w.Retries > 0 && (tick + worker.Index) % DropOffSearchInterval != 0)
                    {
                        return; // no drop-off last time: search again only now and then
                    }

                    drop = Interaction.NearestDropOff(world, owner, w.CarryResource, position, access);
                    if (drop.IsNone)
                    {
                        w.Retries = 1;
                        return;
                    }

                    w.DropTarget = drop.Value;
                    w.Retries = 0;
                }

                if (!Interaction.WorkerReaches(world, worker, drop) && !Approach(world, worker, ref w, drop, position, type))
                {
                    w.DropTarget = 0; // unreachable: look for another one (with the search interval)
                    w.Retries = 1;
                }

                return; // delivery happens in step 5 of the next tick
            }

            default:
                Idle(ref w);
                return;
        }
    }

    private static void Build(World world, EntityId worker, ref Worker w)
    {
        EntityId site = EntityId.FromValue(w.Target);
        if (!world.Entities.IsAlive(site) || !world.Constructions.Has(site) || world.Identities.Get(site).Owner != world.Identities.Get(worker).Owner)
        {
            // Completed, cancelled or destroyed: the next site the peasant placed (BLD-025), else, a wall builder, the next
            // unfinished segment nearby.
            if (world.WaypointQueues.Count != 0 && world.WaypointQueues.IndexOf(worker) is >= 0 and var q
                && world.WaypointQueues.At(q).Points[0].Kind == WaypointKind.Build)
            {
                OrderQueue.Next(world, worker);
                w = world.Workers.Get(worker);
                if (w.Task == WorkerTask.Build)
                {
                    return; // on its way there next tick
                }
            }

            EntityId next = NextWallSegment(world, worker, site);
            if (next.IsNone)
            {
                Idle(ref w);
                return;
            }

            w.Target = next.Value;
            w.Phase = WorkerPhase.ToSite;
            w.Retries = 0;
            site = next;
        }

        EntityTypeDefinition type = world.TypeOf(worker);
        Position position = world.Positions.Get(worker);
        if (Interaction.WorkerReaches(world, worker, site))
        {
            if (w.Phase != WorkerPhase.Building)
            {
                Interaction.Halt(world, worker);
                w.Phase = WorkerPhase.Building;
                w.Retries = 0;
                Interaction.Face(world, worker, site); // the sixth play-test: builders worked turned away from the site
            }

            return;
        }

        w.Phase = WorkerPhase.ToSite;
        if (!Approach(world, worker, ref w, site, position, type))
        {
            Idle(ref w);
        }
    }

    // The nearest own unfinished wall site within wallContinueRadiusTiles that this worker can build (ADR 0043): the
    // rest of a dragged wall.
    private static EntityId NextWallSegment(World world, EntityId worker, EntityId finished)
    {
        if (world.Identities.IndexOf(finished) is not (>= 0 and var f) || !world.Ruleset.EntityTypes[world.Identities.At(f).TypeId].Traits.IsWall)
        {
            return EntityId.None;
        }

        byte owner = world.Identities.Get(worker).Owner;
        EntityTraits builder = world.TypeOf(worker).Traits;
        Position from = world.Positions.Get(worker);
        long limit = (long)world.Ruleset.Gameplay.WallContinueRadiusTiles * Map.MapState.UnitsPerTile;
        long best = (limit * limit) + 1;
        EntityId result = EntityId.None;
        ReadOnlySpan<EntityId> sites = world.Constructions.Entities;
        for (int i = 0; i < sites.Length; i++)
        {
            Identity identity = world.Identities.Get(sites[i]);
            if (identity.Owner != owner || !world.Ruleset.EntityTypes[identity.TypeId].Traits.IsWall || !builder.IsBuilder)
            {
                continue;
            }

            long d = Interaction.DistanceSquaredTo(world, from.X, from.Y, sites[i]);
            if (d < best)
            {
                best = d;
                result = sites[i];
            }
        }

        return result;
    }

    private static void Repair(World world, EntityId worker, ref Worker w)
    {
        EntityId target = EntityId.FromValue(w.Target);
        if (!world.Entities.IsAlive(target) || world.Identities.IndexOf(target) is not (>= 0 and var t)
            || world.Identities.At(t).Owner != world.Identities.Get(worker).Owner || world.Constructions.Has(target)
            || world.Healths.Get(target).Current >= world.Healths.Get(target).Max)
        {
            Idle(ref w); // whole, destroyed, captured or turned into a site
            return;
        }

        EntityTypeDefinition type = world.TypeOf(worker);
        Position position = world.Positions.Get(worker);
        if (Interaction.WorkerReaches(world, worker, target))
        {
            if (w.Phase != WorkerPhase.Repairing)
            {
                Interaction.Halt(world, worker);
                w.Phase = WorkerPhase.Repairing;
                w.Retries = 0;
                w.TimerMs = 0;
            }

            return;
        }

        w.Phase = WorkerPhase.ToTarget;
        w.TimerMs = 0;
        if (world.Dockings.Has(target))
        {
            w.Retries = 0; // the ship is on its way to the shore (ADR 0140): wait for it, do not give up
        }

        if (!Approach(world, worker, ref w, target, position, type))
        {
            Idle(ref w);
        }
    }

    private static void Capture(World world, EntityId worker, ref Worker w)
    {
        EntityId target = EntityId.FromValue(w.Target);
        byte owner = world.Identities.Get(worker).Owner;
        if (!world.Entities.IsAlive(target) || world.Identities.IndexOf(target) is not (>= 0 and var t)
            || world.Identities.At(t).Owner == owner || !world.HasPlayer(world.Identities.At(t).Owner)
            || world.TypeOf(target).Traits.Capturable is not { } capturable || world.Constructions.Has(target)
            || (capturable.RequiredTechnology >= 0 && !world.Players[owner].HasTechnology(capturable.RequiredTechnology)))
        {
            Idle(ref w); // taken (by anyone), destroyed, or no longer allowed
            return;
        }

        EntityTypeDefinition type = world.TypeOf(worker);
        Position position = world.Positions.Get(worker);
        if (Interaction.WorkerReaches(world, worker, target))
        {
            Interaction.Halt(world, worker);
            if (capturable.Instant)
            {
                // Cow Poaching: the animal is taken at once.
                byte previous = world.Identities.At(t).Owner;
                world.ChangeOwner(target, owner);
                world.Events.Emit(new SimEvent(SimEventKind.Captured, owner, target, previous, 0, world.Identities.Get(target).TypeId));
                Idle(ref w);
                return;
            }

            if (w.Phase != WorkerPhase.Capturing)
            {
                w.Phase = WorkerPhase.Capturing;
                w.Retries = 0;
            }

            return;
        }

        w.Phase = WorkerPhase.ToTarget;
        if (!Approach(world, worker, ref w, target, position, type))
        {
            Idle(ref w);
        }
    }

    // Keeps the worker walking to the target; returns false once it has tried too often.
    private static bool Approach(World world, EntityId worker, ref Worker w, EntityId target, Position position, EntityTypeDefinition type)
    {
        if (Interaction.IsMoving(world, worker))
        {
            return true;
        }

        if (w.Retries >= MaxRetries)
        {
            return false;
        }

        w.Retries++;
        (int x, int y) = Interaction.WorkPoint(world, target, position, type.Locomotor, worker);
        Interaction.MoveTo(world, worker, x, y);
        return true;
    }

    private static void StartDropOff(ref Worker w)
    {
        w.Phase = WorkerPhase.ToDropOff;
        w.DropTarget = 0;
        w.Retries = 0;
    }

    // A peasant without a task that has stood still and out of a fight for idleWorkAfterMs takes the nearest work within
    // idleWorkRadiusTiles (the owner's play-tests: the original's peasants did; ⚠ UNVERIFIED delay and radius): help an
    // own site, repair an own damaged building, or gather the resource it can (toward a drop-off that takes it). Boats
    // and units busy otherwise (inside a holder, on an errand, trading, following or patrolling) are left alone.
    private static void IdleWork(World world, EntityId worker, ref Worker w, int tickMs)
    {
        GameplaySettings g = world.Ruleset.Gameplay;
        EntityTypeDefinition type = world.TypeOf(worker);
        if (g.IdleWorkAfterMs <= 0 || type.Locomotor == Locomotor.Ship || !StandsIdle(world, worker))
        {
            w.IdleMs = 0;
            return;
        }

        w.IdleMs = Math.Min(w.IdleMs + tickMs, g.IdleWorkAfterMs);
        if (w.IdleMs < g.IdleWorkAfterMs)
        {
            return;
        }

        w.IdleMs = 0; // nothing found: look again after another wait
        byte owner = world.Identities.Get(worker).Owner;
        Position at = world.Positions.Get(worker);
        long limit = (long)g.IdleWorkRadiusTiles * Map.MapState.UnitsPerTile;
        long best = (limit * limit) + 1;
        EntityId job = EntityId.None;
        WorkerTask task = WorkerTask.None;
        EntityTraits traits = type.Traits;
        if (traits.IsBuilder)
        {
            ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
            for (int i = 0; i < structures.Length; i++)
            {
                EntityId s = structures[i];
                if (world.Identities.Get(s).Owner != owner)
                {
                    continue;
                }

                bool site = world.Constructions.Has(s);
                if (!site && (!world.TypeOf(s).Traits.IsRepairable || world.Healths.IndexOf(s) is not (>= 0 and var h)
                    || world.Healths.At(h).Current >= world.Healths.At(h).Max))
                {
                    continue;
                }

                long d = Interaction.DistanceSquaredTo(world, at.X, at.Y, s);
                if (d < best)
                {
                    best = d;
                    job = s;
                    task = site ? WorkerTask.Build : WorkerTask.Repair;
                }
            }
        }

        EntityId node = EntityId.None;
        if (traits.Harvester is { } harvester)
        {
            long nodeBest = best;
            for (int r = 0; r < world.Ruleset.ResourceTypes.Count; r++)
            {
                if (!harvester.CanGather(r) || Interaction.NearestDropOff(world, owner, r, at, Interaction.AccessOf(type)).IsNone)
                {
                    continue;
                }

                EntityId candidate = Interaction.NextNode(world, worker, r, at.X, at.Y, g.IdleWorkRadiusTiles);
                if (!candidate.IsNone && Interaction.DistanceSquaredTo(world, at.X, at.Y, candidate) is var d && d < nodeBest)
                {
                    nodeBest = d;
                    node = candidate;
                }
            }

            if (!node.IsNone)
            {
                job = EntityId.None;
            }
        }

        if (!node.IsNone)
        {
            GameplayCommands.StartHarvest(world, owner, worker, node);
        }
        else if (!job.IsNone)
        {
            GameplayCommands.StartWork(world, worker, job, task);
        }
    }

    // Standing still with no order, out of a fight, not inside anything and not busy with an errand, a trade or a cast.
    private static bool StandsIdle(World world, EntityId worker)
    {
        if (world.IsHeld(worker) || world.Errands.Has(worker) || world.TradeRoutes.Has(worker) || world.Explorers.Has(worker)
            || (world.Motions.IndexOf(worker) is >= 0 and var m && (world.Motions.At(m).State != MotionState.Idle || world.Motions.At(m).Order != MotionOrder.None))
            || (world.Combats.IndexOf(worker) is >= 0 and var c && world.Combats.At(c).Target != 0)
            || (world.Casters.IndexOf(worker) is >= 0 and var k && world.Casters.At(k).Phase != CastPhase.None))
        {
            return false;
        }

        return true;
    }

    private static void Idle(ref Worker w)
    {
        w.Task = WorkerTask.None;
        w.Phase = WorkerPhase.None;
        w.Target = 0;
        w.DropTarget = 0;
        w.Retries = 0;
        w.TimerMs = 0;
    }
}
