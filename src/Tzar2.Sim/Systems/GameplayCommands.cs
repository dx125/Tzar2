using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Systems;

/// <summary>
/// The gameplay commands of pipeline step 1 (ADR 0039): harvest, build, train, cancel, rally, attack. Each is
/// re-validated when it executes (TDD §8.6: ownership, existence, costs, prerequisites, placement); costs are paid when
/// the command is applied, so two commands of one tick cannot spend the same resources twice.
/// </summary>
internal static class GameplayCommands
{
    /// <summary>Most units one <see cref="CommandKind.Train"/> may queue per producer.</summary>
    public const int MaxTrainCount = ProducerDefinition.MaxQueueCapacity;

    /// <summary>Clears what a unit was doing for its own reasons before a new order (worker task, fight).</summary>
    public static void ClearTasks(World world, EntityId unit, CombatMode newMode = CombatMode.None)
    {
        int w = world.Workers.IndexOf(unit);
        if (w >= 0)
        {
            ref Worker worker = ref world.Workers.At(w);
            worker.Task = WorkerTask.None;
            worker.Phase = WorkerPhase.None;
            worker.Target = 0;
            worker.DropTarget = 0;
            worker.Retries = 0;
            worker.TimerMs = 0;
        }

        int c = world.Combats.IndexOf(unit);
        if (c >= 0)
        {
            ref Combat combat = ref world.Combats.At(c);
            combat.Target = 0;
            combat.HitInMs = -1;
            combat.ChaseTick = -1;
            combat.Resume = 0;
            combat.Mode = newMode;
        }
    }

    public static CommandRejection? Harvest(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        EntityId node = command.Target;
        int s = world.ResourceStocks.IndexOf(node);
        if (!world.Entities.IsAlive(node) || s < 0 || world.ResourceStocks.At(s).Amount == 0 || world.Constructions.Has(node)
            || !Interaction.MayHarvest(world, command.Player, node))
        {
            return CommandRejection.InvalidArgument; // gone, empty, unfinished, or another kingdom's field
        }

        int resource = world.TypeOf(node).Traits.ResourceNode!.Resource;
        Position at = world.Positions.Get(node);
        int applied = 0;
        foreach (EntityId entity in entities)
        {
            int w = OwnedWorker(world, entity, command.Player);
            if (w < 0 || world.TypeOf(entity).Traits.Harvester is not { } harvester || !harvester.CanGather(resource))
            {
                continue;
            }

            ClearTasks(world, entity);
            Interaction.Halt(world, entity);
            ref Worker worker = ref world.Workers.At(world.Workers.IndexOf(entity));
            worker.Task = WorkerTask.Harvest;
            worker.Phase = worker.CarryAmount >= harvester.Capacity ? WorkerPhase.ToDropOff : WorkerPhase.ToNode;
            worker.Target = node.Value;
            worker.NodeResource = (byte)resource;
            worker.NodeX = at.X;
            worker.NodeY = at.Y;
            applied++;
        }

        return applied > 0 ? null : CommandRejection.NoValidEntities;
    }

    public static CommandRejection? Build(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        EntityId site = command.Target;
        ushort typeId;
        if (!site.IsNone)
        {
            // Help build an existing site.
            if (!world.Entities.IsAlive(site) || !world.Constructions.Has(site) || world.Identities.Get(site).Owner != command.Player)
            {
                return CommandRejection.InvalidArgument;
            }

            typeId = world.Identities.Get(site).TypeId;
            return Assign(world, command.Player, site, typeId, entities) > 0 ? null : CommandRejection.NoValidEntities;
        }

        if ((uint)command.Arg0 >= (uint)world.Ruleset.EntityTypes.Count)
        {
            return CommandRejection.InvalidArgument;
        }

        typeId = (ushort)command.Arg0;
        EntityTypeDefinition type = world.Ruleset.EntityTypes[typeId];
        if (!type.HasFootprint || type.Traits.BuildTimeMs <= 0)
        {
            return CommandRejection.InvalidArgument;
        }

        if (!AnyBuilder(world, command.Player, typeId, entities))
        {
            return CommandRejection.NoValidEntities;
        }

        if (!world.Map.ContainsPoint(command.X, command.Y))
        {
            return CommandRejection.OutOfMap;
        }

        if (!RequirementsMet(world, command.Player, type))
        {
            return CommandRejection.RequirementNotMet;
        }

        int tileX = command.X / MapState.UnitsPerTile;
        int tileY = command.Y / MapState.UnitsPerTile;
        if (!CanPlace(world, type, tileX, tileY))
        {
            return CommandRejection.IllegalPlacement;
        }

        if (world.Entities.RemainingCapacity < 1)
        {
            return CommandRejection.EntityLimit;
        }

        if (!TryPay(world, command.Player, type.Traits.Cost))
        {
            return CommandRejection.NotAffordable;
        }

        site = world.PlaceStructure(typeId, command.Player, tileX, tileY, underConstruction: true);
        Position p = world.Positions.Get(site);
        world.Events.Emit(new SimEvent(SimEventKind.ConstructionStarted, command.Player, site, p.X, p.Y, typeId));
        Assign(world, command.Player, site, typeId, entities);
        return null;
    }

    public static CommandRejection? Train(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        if ((uint)command.Arg0 >= (uint)world.Ruleset.EntityTypes.Count || command.Arg1 is < 1 or > MaxTrainCount)
        {
            return CommandRejection.InvalidArgument;
        }

        ushort typeId = (ushort)command.Arg0;
        EntityTypeDefinition type = world.Ruleset.EntityTypes[typeId];
        CommandRejection? reason = CommandRejection.NoValidEntities;
        int queued = 0;
        foreach (EntityId entity in entities)
        {
            if (!IsOwned(world, entity, command.Player) || world.Productions.IndexOf(entity) is not (>= 0 and var q)
                || !world.TypeOf(entity).Traits.Producer!.CanProduce(typeId))
            {
                continue;
            }

            if (!RequirementsMet(world, command.Player, type))
            {
                reason = CommandRejection.RequirementNotMet;
                continue;
            }

            int capacity = world.TypeOf(entity).Traits.Producer!.QueueCapacity;
            for (int n = 0; n < command.Arg1; n++)
            {
                ref Production production = ref world.Productions.At(q);
                if (production.Count >= capacity)
                {
                    reason = queued > 0 ? null : CommandRejection.QueueFull;
                    break;
                }

                if (!TryPay(world, command.Player, type.Traits.Cost))
                {
                    reason = queued > 0 ? null : CommandRejection.NotAffordable;
                    break;
                }

                production.Queue[production.Count++] = typeId;
                queued++;
            }
        }

        return queued > 0 ? null : reason;
    }

    public static CommandRejection? Cancel(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        int cancelled = 0;
        foreach (EntityId entity in entities)
        {
            if (!IsOwned(world, entity, command.Player))
            {
                continue;
            }

            if (world.Constructions.Has(entity))
            {
                ushort typeId = world.Identities.Get(entity).TypeId;
                Refund(world, command.Player, world.Ruleset.EntityTypes[typeId].Traits.Cost, world.Ruleset.Gameplay.ConstructionRefundBasisPoints);
                world.Events.Emit(new SimEvent(SimEventKind.ConstructionCancelled, command.Player, entity, 0, 0, typeId));
                world.Destroy(entity);
                cancelled++;
                continue;
            }

            int q = world.Productions.IndexOf(entity);
            if (q < 0)
            {
                continue;
            }

            ref Production production = ref world.Productions.At(q);
            int slot = command.Arg0 < 0 ? production.Count - 1 : command.Arg0;
            if (slot < 0 || slot >= production.Count)
            {
                continue;
            }

            ushort item = production.Queue[slot];
            for (int k = slot + 1; k < production.Count; k++)
            {
                production.Queue[k - 1] = production.Queue[k];
            }

            production.Count--;
            production.Queue[production.Count] = 0;
            if (slot == 0)
            {
                production.ProgressMs = 0;
                production.Stall = 0;
            }

            Refund(world, command.Player, world.Ruleset.EntityTypes[item].Traits.Cost, world.Ruleset.Gameplay.ProductionRefundBasisPoints);
            cancelled++;
        }

        return cancelled > 0 ? null : CommandRejection.NoValidEntities;
    }

    public static CommandRejection? SetRally(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        bool clear = command.Arg0 == 1;
        if (!clear && !world.Map.ContainsPoint(command.X, command.Y))
        {
            return CommandRejection.OutOfMap;
        }

        int applied = 0;
        foreach (EntityId entity in entities)
        {
            if (!IsOwned(world, entity, command.Player) || world.Productions.IndexOf(entity) is not (>= 0 and var q))
            {
                continue;
            }

            ref Production production = ref world.Productions.At(q);
            production.HasRally = clear ? (byte)0 : (byte)1;
            production.RallyX = clear ? 0 : command.X;
            production.RallyY = clear ? 0 : command.Y;
            applied++;
        }

        return applied > 0 ? null : CommandRejection.NoValidEntities;
    }

    public static CommandRejection? Attack(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        EntityId target = command.Target;
        int t = world.Identities.IndexOf(target);
        if (!world.Entities.IsAlive(target) || t < 0)
        {
            return CommandRejection.InvalidArgument;
        }

        Identity identity = world.Identities.At(t);
        EntityCategory category = world.Ruleset.EntityTypes[identity.TypeId].Traits.Category;
        // Nature's units are hostile to everyone and can be attacked; resources cannot (combat targets, ADR 0038).
        if (identity.Owner == command.Player || category is not (EntityCategory.Unit or EntityCategory.Building))
        {
            return CommandRejection.InvalidArgument;
        }

        int applied = 0;
        foreach (EntityId entity in entities)
        {
            if (!IsOwned(world, entity, command.Player) || world.Combats.IndexOf(entity) < 0 || entity == target)
            {
                continue;
            }

            ClearTasks(world, entity, CombatMode.Explicit);
            Interaction.Halt(world, entity);
            world.Combats.Get(entity).Target = target.Value;
            applied++;
        }

        return applied > 0 ? null : CommandRejection.NoValidEntities;
    }

    /// <summary>Whether the type's footprint fits at (tileX, tileY): on the map, every blocked tile walkable land with no structure on it.</summary>
    internal static bool CanPlace(World world, EntityTypeDefinition type, int tileX, int tileY)
    {
        if (!world.Map.ContainsTile(tileX, tileY) || !world.Map.ContainsTile(tileX + type.FootprintWidth - 1, tileY + type.FootprintHeight - 1))
        {
            return false;
        }

        ReadOnlySpan<byte> mask = type.Footprint;
        for (int dy = 0; dy < type.FootprintHeight; dy++)
        {
            for (int dx = 0; dx < type.FootprintWidth; dx++)
            {
                // Free mask cells (cut corners) may hold anything; blocked ones need walkable land without a structure.
                if (mask[(dy * type.FootprintWidth) + dx] != 0 && !world.Map.IsPassable(Locomotor.Foot, tileX + dx, tileY + dy))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Whether the player has completed every required building and the type needs no technology (none is researchable in M2).</summary>
    internal static bool RequirementsMet(World world, byte player, EntityTypeDefinition type)
    {
        if (type.Traits.RequiredTechnology.Length > 0)
        {
            return false;
        }

        foreach (ushort required in type.Traits.RequiredTypes)
        {
            bool found = false;
            ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
            for (int i = 0; i < structures.Length && !found; i++)
            {
                Identity identity = world.Identities.Get(structures[i]);
                found = identity.Owner == player && identity.TypeId == required && !world.Constructions.Has(structures[i]);
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static int Assign(World world, byte player, EntityId site, ushort typeId, ReadOnlySpan<EntityId> entities)
    {
        int assigned = 0;
        foreach (EntityId entity in entities)
        {
            if (OwnedWorker(world, entity, player) < 0 || !world.TypeOf(entity).Traits.CanBuild(typeId))
            {
                continue;
            }

            ClearTasks(world, entity);
            Interaction.Halt(world, entity);
            ref Worker worker = ref world.Workers.Get(entity);
            worker.Task = WorkerTask.Build;
            worker.Phase = WorkerPhase.ToSite;
            worker.Target = site.Value;
            assigned++;
        }

        return assigned;
    }

    private static bool AnyBuilder(World world, byte player, ushort typeId, ReadOnlySpan<EntityId> entities)
    {
        foreach (EntityId entity in entities)
        {
            if (OwnedWorker(world, entity, player) >= 0 && world.TypeOf(entity).Traits.CanBuild(typeId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryPay(World world, byte player, System.Collections.Immutable.ImmutableArray<int> cost)
    {
        ref PlayerState state = ref world.Players[player];
        for (int r = 0; r < cost.Length; r++)
        {
            if (state.Resources[r] < cost[r])
            {
                return false;
            }
        }

        for (int r = 0; r < cost.Length; r++)
        {
            state.Resources[r] -= cost[r];
        }

        return true;
    }

    private static void Refund(World world, byte player, System.Collections.Immutable.ImmutableArray<int> cost, int basisPoints)
    {
        ref PlayerState state = ref world.Players[player];
        for (int r = 0; r < cost.Length; r++)
        {
            state.Resources[r] = (int)Math.Min(state.Resources[r] + ((long)cost[r] * basisPoints / 10_000), int.MaxValue);
        }
    }

    private static int OwnedWorker(World world, EntityId entity, byte player)
        => IsOwned(world, entity, player) ? world.Workers.IndexOf(entity) : -1;

    private static bool IsOwned(World world, EntityId entity, byte player)
    {
        if (!world.Entities.IsAlive(entity))
        {
            return false;
        }

        int position = world.Identities.IndexOf(entity);
        return position >= 0 && world.Identities.At(position).Owner == player;
    }
}
