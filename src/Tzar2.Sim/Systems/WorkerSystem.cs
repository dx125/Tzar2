using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 6 (activities, TDD §8.5): the state machines of workers (ADRs 0035, 0036). A harvester walks to its
/// node, gathers (step 5), carries the load to the nearest own completed drop-off that accepts it, and returns; when
/// the node is gone it takes the nearest node of the same resource within <c>nodeSearchRadiusTiles</c>. A builder
/// walks to its site and builds (step 4) until the site is complete. Movement goes through the normal path requests
/// (step 7); a worker that cannot get within reach after <see cref="MaxRetries"/> moves gives up.
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
        for (int i = 0; i < world.Workers.Count; i++)
        {
            // Index-based: the passes below never add or remove workers.
            EntityId worker = world.Workers.Entities[i];
            ref Worker w = ref world.Workers.At(i);
            switch (w.Task)
            {
                case WorkerTask.Harvest:
                    Harvest(world, worker, ref w, tick);
                    break;
                case WorkerTask.Build:
                    Build(world, worker, ref w);
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
                if (w.CarryAmount >= type.Traits.Harvester!.Capacity)
                {
                    StartDropOff(ref w);
                    return;
                }

                EntityId node = EntityId.FromValue(w.Target);
                byte owner = world.Identities.Get(worker).Owner;
                if (!IsHarvestable(world, node, w.NodeResource, owner))
                {
                    node = Interaction.NearestNode(world, owner, w.NodeResource, w.NodeX, w.NodeY, world.Ruleset.Gameplay.NodeSearchRadiusTiles);
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

                if (w.CarryAmount > 0 && w.CarryResource != w.NodeResource)
                {
                    StartDropOff(ref w);
                    return;
                }

                if (Interaction.Reaches(world, position, type.Radius, node))
                {
                    Interaction.Halt(world, worker);
                    w.Phase = WorkerPhase.Gathering;
                    w.TimerMs = 0;
                    w.Retries = 0;
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
                if (!Interaction.IsCompletedStructure(world, drop) || world.Identities.Get(drop).Owner != owner || !world.TypeOf(drop).Traits.AcceptsDropOff(w.CarryResource))
                {
                    w.DropTarget = 0;
                    if (w.Retries > 0 && (tick + worker.Index) % DropOffSearchInterval != 0)
                    {
                        return; // no drop-off last time: search again only now and then
                    }

                    drop = Interaction.NearestDropOff(world, owner, w.CarryResource, position);
                    if (drop.IsNone)
                    {
                        w.Retries = 1;
                        return;
                    }

                    w.DropTarget = drop.Value;
                    w.Retries = 0;
                }

                if (!Interaction.Reaches(world, position, type.Radius, drop) && !Approach(world, worker, ref w, drop, position, type))
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
            Idle(ref w); // completed, cancelled or destroyed
            return;
        }

        EntityTypeDefinition type = world.TypeOf(worker);
        Position position = world.Positions.Get(worker);
        if (Interaction.Reaches(world, position, type.Radius, site))
        {
            if (w.Phase != WorkerPhase.Building)
            {
                Interaction.Halt(world, worker);
                w.Phase = WorkerPhase.Building;
                w.Retries = 0;
            }

            return;
        }

        w.Phase = WorkerPhase.ToSite;
        if (!Approach(world, worker, ref w, site, position, type))
        {
            Idle(ref w);
        }
    }

    // Keeps the worker walking to the structure; returns false once it has tried too often.
    private static bool Approach(World world, EntityId worker, ref Worker w, EntityId structure, Position position, EntityTypeDefinition type)
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
        if (!Interaction.ApproachPoint(world, structure, position.X, position.Y, type.Locomotor, out int x, out int y))
        {
            Position p = world.Positions.Get(structure);
            (x, y) = (p.X, p.Y); // enclosed: let the pathfinder pick the nearest reachable point
        }

        Interaction.MoveTo(world, worker, x, y);
        return true;
    }

    private static bool IsHarvestable(World world, EntityId node, byte resource, byte owner)
    {
        int s = world.ResourceStocks.IndexOf(node);
        return s >= 0 && world.Entities.IsAlive(node) && world.ResourceStocks.At(s).Amount != 0 && !world.Constructions.Has(node)
            && world.TypeOf(node).Traits.ResourceNode!.Resource == resource && Interaction.MayHarvest(world, owner, node);
    }

    private static void StartDropOff(ref Worker w)
    {
        w.Phase = WorkerPhase.ToDropOff;
        w.DropTarget = 0;
        w.Retries = 0;
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
