using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 4 (TDD §8.5): population (GAME-05), construction progress by builders (GAME-03) and training queues
/// (GAME-05), in that order. Runs on the sim thread in ascending entity order (ADRs 0034, 0036, 0037).
/// </summary>
public sealed class ProductionSystem : ISimSystem
{
    private readonly List<EntityId> _completed = [];

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
    /// <c>PopulationProvided</c>, at most the match's limit). Counting from the entities keeps it exact after any kill,
    /// capture or restore.
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
                players[owner].PopulationCap += t.PopulationProvided;
            }
        }

        for (int p = 0; p < players.Length; p++)
        {
            players[p].PopulationCap = Math.Min(players[p].PopulationCap, players[p].PopulationLimit);
        }
    }

    // Builders standing at their site add the tick's time each (linear in the number of builders: UNVERIFIED, UNK-18).
    // Health grows with progress from 1 to the maximum; damage taken meanwhile stays taken.
    private void AdvanceConstruction(World world)
    {
        int tickMs = world.Config.TickLengthMilliseconds;
        ReadOnlySpan<EntityId> workers = world.Workers.Entities;
        Span<Worker> states = world.Workers.Components;
        for (int i = 0; i < workers.Length; i++)
        {
            ref Worker w = ref states[i];
            if (w.Task != WorkerTask.Build || w.Phase != WorkerPhase.Building)
            {
                continue;
            }

            EntityId site = EntityId.FromValue(w.Target);
            int c = world.Constructions.IndexOf(site);
            if (c < 0 || !world.Entities.IsAlive(site) || !Interaction.Reaches(world, world.Positions.Get(workers[i]), world.TypeOf(workers[i]).Radius, site))
            {
                continue;
            }

            ref Construction construction = ref world.Constructions.At(c);
            int before = construction.ProgressMs;
            construction.ProgressMs = Math.Min(construction.TotalMs, before + tickMs);
            ref Health health = ref world.Healths.Get(site);
            health.Current = Math.Min(health.Max, health.Current + HealthAt(health.Max, construction.ProgressMs, construction.TotalMs) - HealthAt(health.Max, before, construction.TotalMs));
        }

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
        }
    }

    private static int HealthAt(int max, int progress, int total) => total <= 0 ? max : 1 + (int)((long)(max - 1) * progress / total);

    private static void AdvanceQueues(World world)
    {
        int tickMs = world.Config.TickLengthMilliseconds;
        for (int i = 0; i < world.Productions.Count; i++)
        {
            ref Production q = ref world.Productions.At(i);
            if (q.Count == 0)
            {
                continue;
            }

            EntityId producer = world.Productions.Entities[i];
            ushort typeId = q.Queue[0];
            EntityTypeDefinition type = world.Ruleset.EntityTypes[typeId];
            if (q.ProgressMs < type.Traits.BuildTimeMs)
            {
                q.ProgressMs = Math.Min(type.Traits.BuildTimeMs, q.ProgressMs + tickMs);
                if (q.ProgressMs < type.Traits.BuildTimeMs)
                {
                    continue;
                }
            }

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

            q.Stall = 0;
            q.ProgressMs = 0;
            for (int k = 1; k < q.Count; k++)
            {
                q.Queue[k - 1] = q.Queue[k];
            }

            q.Count--;
            q.Queue[q.Count] = 0;
        }
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
            Interaction.MoveTo(world, unit, q.RallyX, q.RallyY);
        }

        return ProductionStall.None;
    }
}
