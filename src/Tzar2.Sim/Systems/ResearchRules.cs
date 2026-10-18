using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Prerequisites, costs and completion of research and upgrades (GAME-06; ADR 0040). A requirement set applies when
/// its civilization is the actor's (the builder, producer, or researching building): technology trees are per
/// civilization, so a captured Asian blacksmith researches by the Asian tree.
/// </summary>
internal static class ResearchRules
{
    /// <summary>Whether <paramref name="player"/> meets every requirement set of <paramref name="civilization"/> and the required building types.</summary>
    public static bool RequirementsMet(World world, byte player, string civilization, ImmutableArray<RequirementSet> sets, ImmutableArray<ushort> requiredTypes)
    {
        if (!world.HasPlayer(player))
        {
            return false;
        }

        ref PlayerState state = ref world.Players[player];
        foreach (RequirementSet set in sets)
        {
            if (!set.AppliesTo(civilization))
            {
                continue;
            }

            if (set.Unsatisfiable)
            {
                return false;
            }

            foreach (ushort tech in set.Technologies)
            {
                if (!state.HasTechnology(tech))
                {
                    return false;
                }
            }

            if (!HasCompleted(world, player, set.Types))
            {
                return false;
            }
        }

        return HasCompleted(world, player, requiredTypes);
    }

    /// <summary>Whether <paramref name="player"/> may build or train <paramref name="type"/> through an actor of <paramref name="civilization"/>.</summary>
    public static bool CanMake(World world, byte player, string civilization, EntityTypeDefinition type)
        => RequirementsMet(world, player, civilization, type.Traits.Requirements, type.Traits.RequiredTypes);

    /// <summary>The cost of <paramref name="type"/> for <paramref name="player"/> after technology effects (Feudalism: cheaper peasants).</summary>
    public static int CostOf(World world, byte player, EntityTypeDefinition type, int resource)
        => world.Stats.Apply(player, ModifierStat.Cost, type.Id, type.Traits.Cost[resource]);

    /// <summary>Pays the effective cost of <paramref name="type"/>; false (nothing paid) if the player cannot afford it.</summary>
    public static bool TryPayFor(World world, byte player, EntityTypeDefinition type)
    {
        ref PlayerState state = ref world.Players[player];
        ImmutableArray<int> cost = type.Traits.Cost;
        for (int r = 0; r < cost.Length; r++)
        {
            if (state.Resources[r] < CostOf(world, player, type, r))
            {
                return false;
            }
        }

        for (int r = 0; r < cost.Length; r++)
        {
            state.Resources[r] -= CostOf(world, player, type, r);
        }

        return true;
    }

    /// <summary>Refunds a share of the effective cost of <paramref name="type"/>.</summary>
    public static void RefundFor(World world, byte player, EntityTypeDefinition type, int basisPoints)
    {
        ref PlayerState state = ref world.Players[player];
        for (int r = 0; r < type.Traits.Cost.Length; r++)
        {
            state.Resources[r] = (int)Math.Min(state.Resources[r] + ((long)CostOf(world, player, type, r) * basisPoints / 10_000), int.MaxValue);
        }
    }

    /// <summary>Pays a plain cost (technologies, upgrades); false (nothing paid) if the player cannot afford it.</summary>
    public static bool TryPay(World world, byte player, ImmutableArray<int> cost)
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

    /// <summary>Refunds a share of a plain cost.</summary>
    public static void Refund(World world, byte player, ImmutableArray<int> cost, int basisPoints)
    {
        ref PlayerState state = ref world.Players[player];
        for (int r = 0; r < cost.Length; r++)
        {
            state.Resources[r] = (int)Math.Min(state.Resources[r] + ((long)cost[r] * basisPoints / 10_000), int.MaxValue);
        }
    }

    /// <summary>Whether any of <paramref name="player"/>'s queues holds research of <paramref name="technology"/> (one research per technology, 1.10 fix).</summary>
    public static bool IsResearchPending(World world, byte player, int technology)
    {
        ushort item = QueueItems.Research(technology);
        ReadOnlySpan<EntityId> ids = world.Productions.Entities;
        Span<Production> queues = world.Productions.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            if (world.Identities.Get(ids[i]).Owner != player)
            {
                continue;
            }

            ref Production q = ref queues[i];
            for (int k = 0; k < q.Count; k++)
            {
                if (q.Queue[k] == item)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="entity"/> already has level upgrade <paramref name="upgrade"/> or has it queued.</summary>
    public static bool HasOrQueuedUpgrade(World world, EntityId entity, int upgrade)
    {
        if (world.UpgradeLevels.IndexOf(entity) is >= 0 and var u && (world.UpgradeLevels.At(u).Mask & (1u << upgrade)) != 0)
        {
            return true;
        }

        if (world.Productions.IndexOf(entity) is >= 0 and var p)
        {
            ref Production q = ref world.Productions.At(p);
            for (int k = 0; k < q.Count; k++)
            {
                if (q.Queue[k] == QueueItems.Upgrade(upgrade))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="entity"/> has every level upgrade <paramref name="upgrade"/> comes after (bought or queued before it).</summary>
    public static bool HasPrecedingLevels(World world, EntityId entity, UpgradeDefinition upgrade)
    {
        foreach (ushort before in upgrade.After)
        {
            if (!HasOrQueuedUpgrade(world, entity, before))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a queue item is one the producer can hold (snapshot validation).</summary>
    public static bool IsValidQueueItem(World world, ProducerDefinition definition, ushort item)
    {
        if (QueueItems.IsResearch(item))
        {
            return QueueItems.Id(item) < world.Ruleset.Technologies.Count && definition.CanResearch((ushort)QueueItems.Id(item));
        }

        if (QueueItems.IsUpgrade(item))
        {
            return QueueItems.Id(item) < world.Ruleset.Upgrades.Count && definition.CanUpgrade((ushort)QueueItems.Id(item));
        }

        return definition.CanProduce(item);
    }

    /// <summary>
    /// Completes research of <paramref name="technology"/> for <paramref name="player"/>: the bit, the modifier tables,
    /// and the hit points of the player's entities whose maximum changed.
    /// </summary>
    public static void CompleteResearch(World world, byte player, int technology, EntityId site)
    {
        ref PlayerState state = ref world.Players[player];
        state.AddTechnology(technology);
        world.Stats.Rebuild(player, state);
        bool health = false;
        foreach (ModifierDefinition m in world.Ruleset.Technologies[technology].Effects)
        {
            health |= m.Stat == ModifierStat.MaxHealth;
        }

        if (health)
        {
            ReadOnlySpan<EntityId> ids = world.Healths.Entities;
            for (int i = 0; i < ids.Length; i++)
            {
                if (world.Identities.Get(ids[i]).Owner == player)
                {
                    world.RefreshMaxHealth(ids[i]);
                }
            }
        }

        world.Events.Emit(new SimEvent(SimEventKind.ResearchCompleted, player, site, 0, 0, technology));
    }

    /// <summary>
    /// Completes upgrade <paramref name="upgrade"/> on <paramref name="entity"/>: a conversion turns it into its new
    /// type (queued items the new type cannot hold are refunded); a level is recorded on the entity.
    /// </summary>
    public static void CompleteUpgrade(World world, EntityId entity, int upgrade)
    {
        UpgradeDefinition u = world.Ruleset.Upgrades[upgrade];
        Identity identity = world.Identities.Get(entity);
        if (u.IsConversion)
        {
            int target = u.ConvertedType(identity.TypeId);
            if (target < 0)
            {
                return; // validated when queued; a type without the conversion cannot hold the item
            }

            world.ConvertType(entity, (ushort)target);
            if (world.Productions.IndexOf(entity) is >= 0 and var p && world.TypeOf(entity).Traits.Producer is { } producer)
            {
                DropInvalidItems(world, entity, ref world.Productions.At(p), producer, identity.Owner);
            }
        }
        else
        {
            if (!world.UpgradeLevels.Has(entity))
            {
                world.UpgradeLevels.Set(entity, default);
            }

            world.UpgradeLevels.Get(entity).Mask |= 1u << upgrade;
        }

        world.Events.Emit(new SimEvent(SimEventKind.UpgradeCompleted, identity.Owner, entity, identity.TypeId, world.Identities.Get(entity).TypeId, upgrade));
    }

    /// <summary>The level-upgrade bonus of <paramref name="entity"/> for <paramref name="stat"/> (ship cannons; 0 without levels).</summary>
    public static int LevelBonus(World world, EntityId entity, ModifierStat stat, int baseValue)
    {
        if (world.UpgradeLevels.IndexOf(entity) is not (>= 0 and var u))
        {
            return baseValue;
        }

        uint mask = world.UpgradeLevels.At(u).Mask;
        int add = 0;
        int bp = 0;
        ushort type = world.Identities.Get(entity).TypeId;
        for (int i = 0; mask != 0 && i < world.Ruleset.Upgrades.Count; i++, mask >>= 1)
        {
            if ((mask & 1) == 0)
            {
                continue;
            }

            foreach (ModifierDefinition m in world.Ruleset.Upgrades[i].Effects)
            {
                if (m.Stat == stat && m.AppliesTo(type))
                {
                    add += m.Add;
                    bp += m.BasisPoints;
                }
            }
        }

        return PlayerStats.Combine(baseValue, add, bp);
    }

    // After a conversion, queued items the new type cannot hold leave the queue with a full research refund.
    private static void DropInvalidItems(World world, EntityId entity, ref Production q, ProducerDefinition producer, byte owner)
    {
        int write = 0;
        for (int k = 0; k < q.Count; k++)
        {
            ushort item = q.Queue[k];
            if (IsValidQueueItem(world, producer, item))
            {
                q.Queue[write++] = item;
                continue;
            }

            ImmutableArray<int> cost = QueueItems.IsResearch(item) ? world.Ruleset.Technologies[QueueItems.Id(item)].Cost
                : QueueItems.IsUpgrade(item) ? world.Ruleset.Upgrades[QueueItems.Id(item)].Cost
                : world.Ruleset.EntityTypes[item].Traits.Cost;
            Refund(world, owner, cost, 10_000);
        }

        for (int k = write; k < q.Count; k++)
        {
            q.Queue[k] = 0;
        }

        if (write == 0 || q.Count != write)
        {
            q.ProgressMs = 0;
            q.Stall = 0;
        }

        q.Count = (byte)write;
    }

    private static bool HasCompleted(World world, byte player, ImmutableArray<ushort> types)
    {
        foreach (ushort required in types)
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

    /// <summary>The technology ids <paramref name="player"/> has researched, ascending (tests and tools).</summary>
    public static List<int> Researched(World world, byte player)
    {
        var list = new List<int>();
        for (int t = 0; t < world.Ruleset.Technologies.Count; t++)
        {
            if (world.Players[player].HasTechnology(t))
            {
                list.Add(t);
            }
        }

        return list;
    }
}
