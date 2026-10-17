using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 5 (TDD §8.5, GAME-02; ADR 0035): workers at a node gather one unit per gather time into their load
/// (carry-over timers, REQ-SIM-02) and workers at a drop-off deliver their load to the stockpile. Nodes that run out
/// are removed after the pass (a tree's tile becomes buildable again). Sequential, ascending worker order, so when
/// several workers share a node the lower index gathers first.
/// </summary>
public sealed class EconomySystem : ISimSystem
{
    private readonly List<EntityId> _depleted = [];

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Economy;

    /// <inheritdoc />
    public string Name => "economy";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        int tickMs = world.Config.TickLengthMilliseconds;
        _depleted.Clear();
        ReadOnlySpan<EntityId> workers = world.Workers.Entities;
        Span<Worker> states = world.Workers.Components;
        for (int i = 0; i < workers.Length; i++)
        {
            ref Worker w = ref states[i];
            if (w.Task != WorkerTask.Harvest)
            {
                continue;
            }

            if (w.Phase == WorkerPhase.Gathering)
            {
                Gather(world, workers[i], ref w, tickMs);
            }
            else if (w.Phase == WorkerPhase.ToDropOff && w.CarryAmount > 0)
            {
                Deliver(world, workers[i], ref w);
            }
        }

        foreach (EntityId node in _depleted)
        {
            ushort type = world.Identities.Get(node).TypeId;
            world.Events.Emit(new SimEvent(SimEventKind.ResourceDepleted, world.Identities.Get(node).Owner, node, 0, 0, type));
            world.Destroy(node);
        }
    }

    private void Gather(World world, EntityId worker, ref Worker w, int tickMs)
    {
        EntityId node = EntityId.FromValue(w.Target);
        int s = world.ResourceStocks.IndexOf(node);
        EntityTypeDefinition type = world.TypeOf(worker);
        HarvesterDefinition harvester = type.Traits.Harvester!;
        if (s < 0 || world.ResourceStocks.At(s).Amount == 0 || world.Constructions.Has(node)
            || !Interaction.MayHarvest(world, world.Identities.Get(worker).Owner, node)
            || !Interaction.Reaches(world, world.Positions.Get(worker), type.Radius, node))
        {
            w.Phase = WorkerPhase.ToNode; // gone, empty, or pushed away: the worker system decides what next
            w.TimerMs = 0;
            return;
        }

        ref ResourceStock stock = ref world.ResourceStocks.At(s);
        int resource = world.TypeOf(node).Traits.ResourceNode!.Resource;
        if (w.CarryAmount > 0 && w.CarryResource != resource)
        {
            w.Phase = WorkerPhase.ToDropOff; // deliver the other load first
            return;
        }

        int gatherMs = harvester.GatherMs[resource];
        w.TimerMs += tickMs;
        while (w.TimerMs >= gatherMs && w.CarryAmount < harvester.Capacity && stock.Amount != 0)
        {
            w.TimerMs -= gatherMs;
            w.CarryAmount++;
            w.CarryResource = (byte)resource;
            if (stock.Amount > 0 && --stock.Amount == 0)
            {
                _depleted.Add(node);
            }
        }

        if (w.CarryAmount >= harvester.Capacity || stock.Amount == 0)
        {
            w.Phase = WorkerPhase.ToDropOff;
            w.TimerMs = 0;
            w.Retries = 0;
        }
    }

    private static void Deliver(World world, EntityId worker, ref Worker w)
    {
        EntityId drop = EntityId.FromValue(w.DropTarget);
        if (!Interaction.IsCompletedStructure(world, drop) || world.Identities.Get(drop).Owner != world.Identities.Get(worker).Owner
            || !world.TypeOf(drop).Traits.AcceptsDropOff(w.CarryResource)
            || !Interaction.Reaches(world, world.Positions.Get(worker), world.TypeOf(worker).Radius, drop))
        {
            return;
        }

        byte owner = world.Identities.Get(worker).Owner;
        if (!world.HasPlayer(owner))
        {
            return; // only player-owned workers exist (validated on restore); never index past the players
        }

        ref int stockpile = ref world.Players[owner].Resources[w.CarryResource];
        stockpile = (int)Math.Min((long)stockpile + w.CarryAmount, int.MaxValue);
        world.Events.Emit(new SimEvent(SimEventKind.ResourceDelivered, owner, worker, w.CarryResource, drop.Index, w.CarryAmount));
        w.CarryAmount = 0;
        w.CarryResource = Worker.NoResource;
        w.Phase = WorkerPhase.ToNode;
        w.Retries = 0;
        Interaction.Halt(world, worker);
    }
}
