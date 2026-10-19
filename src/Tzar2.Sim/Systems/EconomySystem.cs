using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 5 (TDD §8.5, GAME-02; ADRs 0035, 0042): workers at a node gather one unit per gather time into their
/// load (carry-over timers, REQ-SIM-02; the owner's gather-rate and capacity technologies apply) and workers at a
/// drop-off deliver their load to the stockpile. Nodes that run out are removed after the pass (a tree's tile becomes
/// buildable again) unless they keep (cows regrow their milk). Neglected fields decay. Sequential, ascending worker
/// order, so when several workers share a node the lower index gathers first.
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
        AgeNeglect(world, tickMs);
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

        Regrow(world, tickMs);
        Decay(world, tickMs);
    }

    private void Gather(World world, EntityId worker, ref Worker w, int tickMs)
    {
        EntityId node = EntityId.FromValue(w.Target);
        int s = world.ResourceStocks.IndexOf(node);
        EntityTypeDefinition type = world.TypeOf(worker);
        HarvesterDefinition harvester = type.Traits.Harvester!;
        byte owner = world.Identities.Get(worker).Owner;
        if (s < 0 || world.ResourceStocks.At(s).Amount == 0 || world.Constructions.Has(node)
            || !Interaction.MayHarvest(world, owner, node)
            || !Interaction.WorkerReaches(world, worker, node))
        {
            w.Phase = WorkerPhase.ToNode; // gone, empty, or pushed away: the worker system decides what next
            w.TimerMs = 0;
            return;
        }

        ref ResourceStock stock = ref world.ResourceStocks.At(s);
        EntityTypeDefinition nodeType = world.TypeOf(node);
        ResourceNodeDefinition definition = nodeType.Traits.ResourceNode!;
        int resource = definition.Resource;
        if (w.CarryAmount > 0 && w.CarryResource != resource)
        {
            w.Phase = WorkerPhase.ToDropOff; // deliver the other load first
            return;
        }

        if (world.Neglects.IndexOf(node) is >= 0 and var n)
        {
            world.Neglects.At(n).UntendedMs = 0; // a field is tended while it is worked
        }

        // Gather time: the node's own (cows) or the harvester's, sped up by the owner's technologies for this node
        // (Rotating Fields, Saw, Stone/Gold Mining: ADR 0040).
        int baseMs = definition.GatherMs > 0 ? definition.GatherMs : harvester.GatherMs[resource];
        (_, int bp) = world.Stats.Modifier(owner, ModifierStat.GatherRate, nodeType.Id);
        int gatherMs = Math.Max(1, (int)((long)baseMs * 10_000 / Math.Max(1, 10_000 + bp)));
        int capacity = GameplayCommands.CarryCapacity(world, worker, definition);
        w.TimerMs += tickMs;
        while (w.TimerMs >= gatherMs && w.CarryAmount < capacity && stock.Amount != 0)
        {
            w.TimerMs -= gatherMs;
            w.CarryAmount++;
            w.CarryResource = (byte)resource;
            if (stock.Amount > 0 && --stock.Amount == 0 && !definition.KeepWhenEmpty)
            {
                _depleted.Add(node);
            }
        }

        if (w.CarryAmount >= capacity || stock.Amount == 0)
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
            || (world.TypeOf(drop).Traits.DropOffAccess & Interaction.AccessOf(world.TypeOf(worker))) == 0
            || !Interaction.WorkerReaches(world, worker, drop))
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

    // Cows regrow their milk (ResourceNode.regenMs) up to what they hold full.
    private static void Regrow(World world, int tickMs)
    {
        ReadOnlySpan<EntityId> nodes = world.ResourceStocks.Entities;
        Span<ResourceStock> stocks = world.ResourceStocks.Components;
        for (int i = 0; i < nodes.Length; i++)
        {
            ref ResourceStock stock = ref stocks[i];
            ResourceNodeDefinition definition = world.TypeOf(nodes[i]).Traits.ResourceNode!;
            if (definition.RegenMs <= 0 || stock.Amount < 0 || stock.Amount >= definition.Amount)
            {
                stock.RegenMs = 0;
                continue;
            }

            stock.RegenMs += tickMs;
            while (stock.RegenMs >= definition.RegenMs && stock.Amount < definition.Amount)
            {
                stock.RegenMs -= definition.RegenMs;
                stock.Amount++;
            }

            if (stock.Amount >= definition.Amount)
            {
                stock.RegenMs = 0;
            }
        }
    }

    private static void AgeNeglect(World world, int tickMs)
    {
        Span<Neglect> neglects = world.Neglects.Components;
        for (int i = 0; i < neglects.Length; i++)
        {
            neglects[i].UntendedMs = (int)Math.Min((long)neglects[i].UntendedMs + tickMs, int.MaxValue / 2);
        }
    }

    // Fields neglected longer than Decay.afterMs lose hit points; at 0 they disappear (GAME-02, UNK-17).
    private void Decay(World world, int tickMs)
    {
        _depleted.Clear();
        ReadOnlySpan<EntityId> ids = world.Neglects.Entities;
        Span<Neglect> neglects = world.Neglects.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            ref Neglect n = ref neglects[i];
            DecayDefinition decay = world.TypeOf(ids[i]).Traits.Decay!;
            if (n.UntendedMs <= decay.AfterMs || decay.HealthPerMinute <= 0)
            {
                n.AccumMs = 0;
                continue;
            }

            int msPerPoint = Math.Max(1, 60_000 / decay.HealthPerMinute);
            n.AccumMs += tickMs;
            ref Health health = ref world.Healths.Get(ids[i]);
            while (n.AccumMs >= msPerPoint && health.Current > 0)
            {
                n.AccumMs -= msPerPoint;
                health.Current--;
            }

            n.AccumMs = Math.Min(n.AccumMs, 59_999);
            if (health.Current <= 0)
            {
                _depleted.Add(ids[i]);
            }
        }

        foreach (EntityId field in _depleted)
        {
            Identity identity = world.Identities.Get(field);
            world.Events.Emit(new SimEvent(SimEventKind.Decayed, identity.Owner, field, 0, 0, identity.TypeId));
            world.Destroy(field);
        }
    }
}
