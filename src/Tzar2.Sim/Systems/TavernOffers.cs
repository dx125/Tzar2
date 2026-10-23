using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Rng;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Systems;

/// <summary>One tavern offer (GAME-14 hiring, ADR 0108).</summary>
/// <param name="TypeId">The unit type.</param>
/// <param name="Count">Units in the offer (a mercenary group; heroes: 1).</param>
/// <param name="Level">Their experience level.</param>
/// <param name="IsHero">A hero (named, hero flag).</param>
public readonly record struct TavernOffer(ushort TypeId, int Count, int Level, bool IsHero);

/// <summary>
/// The tavern's mercenaries and heroes (GAME-14, ADR 0108; manual ch. 6: Mercenaries "makes the recruitment of small
/// groups of soldiers in the taverns possible", Heroes' Quest "makes the recruitment of heroes possible", Fame "more, and
/// better, mercenaries who will be cheaper and more experienced").
/// </summary>
/// <remarks>
/// Offers are a pure function of the match seed, the kingdom, the tavern period (<c>world.tavernRefreshMs</c>) and the
/// slot: slots 0 … <c>mercenaryOffers</c>−1 are mercenary groups, the next <c>heroOffers</c> slots heroes. Each is a
/// military land unit of the kingdom's civilization that some building can train. The only state is which slots of the
/// current period were hired (<see cref="KingdomState.TavernTaken"/>, <see cref="KingdomState.TavernPeriod"/>).
/// </remarks>
public static class TavernOffers
{
    /// <summary>The tavern period of <paramref name="tick"/>.</summary>
    public static int PeriodAt(World world, int tick)
    {
        ArgumentNullException.ThrowIfNull(world);
        return (int)((long)tick * world.Config.TickLengthMilliseconds / world.Ruleset.World.TavernRefreshMs);
    }

    /// <summary>Offer slots per period.</summary>
    public static int SlotCount(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Ruleset.World.MercenaryOffers + world.Ruleset.World.HeroOffers;
    }

    /// <summary>Offer <paramref name="slot"/> of <paramref name="player"/> now, or null when the civilization has no candidates.</summary>
    public static TavernOffer? Offer(World world, int player, int slot)
    {
        ArgumentNullException.ThrowIfNull(world);
        WorldSettings w = world.Ruleset.World;
        if (!world.HasPlayer(player) || (uint)slot >= (uint)SlotCount(world))
        {
            return null;
        }

        ReadOnlySpan<ushort> pool = world.MercenaryPool(player);
        if (pool.IsEmpty)
        {
            return null;
        }

        int period = PeriodAt(world, world.Tick.Value);
        ulong roll = SplitMix64.Mix(world.Config.MatchSeed ^ 0x7A7E_0000_0000_0000UL ^ ((ulong)player << 40) ^ ((ulong)period << 8) ^ (ulong)slot);
        ushort type = pool[(int)(roll % (ulong)pool.Length)];
        bool hero = slot >= w.MercenaryOffers;
        bool fame = HasTechnology(world, player, world.Ruleset.World.FameTechnology);
        if (hero)
        {
            return new TavernOffer(type, 1, world.Ruleset.Combat.HeroLevel, true);
        }

        int spread = w.MercenaryGroupMax - w.MercenaryGroupMin + 1;
        int count = w.MercenaryGroupMin + (int)((roll >> 32) % (ulong)spread) + (fame ? w.FameGroupBonus : 0);
        return new TavernOffer(type, count, w.MercenaryLevel + (fame ? w.FameLevelBonus : 0), false);
    }

    /// <summary>The price of an offer per resource type id (training cost × count × the hire percent; Fame for mercenaries).</summary>
    public static void CostOf(World world, int player, TavernOffer offer, Span<int> into)
    {
        ArgumentNullException.ThrowIfNull(world);
        WorldSettings w = world.Ruleset.World;
        EntityTypeDefinition type = world.Ruleset.EntityTypes[offer.TypeId];
        long percent = offer.IsHero ? w.HeroCostPercent
            : (long)w.MercenaryCostPercent * (HasTechnology(world, player, w.FameTechnology) ? w.FameCostPercent : 100) / 100;
        into.Clear();
        for (int r = 0; r < type.Traits.Cost.Length && r < into.Length; r++)
        {
            into[r] = (int)Math.Min((long)type.Traits.Cost[r] * offer.Count * percent / 100, int.MaxValue);
        }
    }

    /// <summary>
    /// <see cref="CommandKind.Hire"/>: offer Arg0 in a listed own completed tavern, if the kingdom has the technology (Mercenaries,
    /// heroes: Heroes' Quest), the offer was not hired this period, there is population room and an entity slot per
    /// unit, and the price is paid (shared resources apply). The units appear next to the tavern at their level; a hero
    /// gets the hero flag and a name.
    /// </summary>
    internal static CommandRejection? Hire(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        byte player = command.Player;
        int slot = command.Arg0;
        if ((uint)slot >= (uint)SlotCount(world))
        {
            return CommandRejection.InvalidArgument;
        }

        EntityId tavern = OwnTavern(world, player, entities);
        if (tavern.IsNone)
        {
            return CommandRejection.NoValidEntities;
        }

        bool heroSlot = slot >= world.Ruleset.World.MercenaryOffers;
        if (!HasTechnology(world, player, heroSlot ? world.Ruleset.World.HeroesTechnology : world.Ruleset.World.MercenariesTechnology))
        {
            return CommandRejection.RequirementNotMet;
        }

        if (Offer(world, player, slot) is not { } offer)
        {
            return CommandRejection.InvalidArgument;
        }

        ref KingdomState k = ref world.Kingdoms[player];
        int period = PeriodAt(world, world.Tick.Value);
        int taken = k.TavernPeriod == period ? k.TavernTaken : 0; // a new period offers everything again
        if ((taken & (1 << slot)) != 0)
        {
            return CommandRejection.OfferTaken;
        }

        EntityTypeDefinition type = world.Ruleset.EntityTypes[offer.TypeId];
        ref PlayerState state = ref world.Players[player];
        (int x0, _, int x1, int y1) = Interaction.Rect(world, tavern);
        if (world.Entities.RemainingCapacity < offer.Count
            || (long)state.Population + ((long)type.Traits.PopulationCost * offer.Count) > state.PopulationCap
            || !Interaction.ApproachPoint(world, tavern, (x0 + x1) / 2, y1 + MapState.UnitsPerTile, type.Locomotor, out int x, out int y))
        {
            return CommandRejection.NoRoom;
        }

        Span<int> cost = stackalloc int[world.Ruleset.ResourceTypes.Count];
        CostOf(world, player, offer, cost);
        if (!SharedStockpile.TryDeduct(world, player, cost))
        {
            return CommandRejection.NotAffordable;
        }

        k.TavernPeriod = period;
        k.TavernTaken = (byte)(taken | (1 << slot));
        EntityId first = EntityId.None;
        for (int n = 0; n < offer.Count; n++)
        {
            EntityId unit = world.Spawn(offer.TypeId, player, x, y);
            first = first.IsNone ? unit : first;
            state.Population += type.Traits.PopulationCost;
            Train(world, unit, offer);
        }

        world.Events.Emit(new SimEvent(SimEventKind.Hired, player, first, slot, offer.Count, offer.TypeId));
        return null;
    }

    // Starting level (and, for heroes, the hero flag and a name), whatever the kingdom's level cap: the tavern sells
    // trained soldiers.
    private static void Train(World world, EntityId unit, TavernOffer offer)
    {
        if (world.Experiences.IndexOf(unit) is not (>= 0 and var x) || offer.Level <= 0)
        {
            return;
        }

        ref Experience xp = ref world.Experiences.At(x);
        if (xp.Level < offer.Level)
        {
            xp.Level = (ushort)Math.Min(offer.Level, ushort.MaxValue);
            xp.Xp = Math.Max(xp.Xp, CombatStats.XpForLevel(world, unit, offer.Level));
        }

        Identity identity = world.Identities.Get(unit);
        if (offer.IsHero && (xp.Flags & ExperienceFlags.Hero) == 0)
        {
            xp.Flags |= ExperienceFlags.Hero;
            int name = ExperienceSystem.NameHero(world, unit, identity.Owner);
            world.Events.Emit(new SimEvent(SimEventKind.HeroPromoted, identity.Owner, unit, name, 0, identity.TypeId));
        }

        world.RefreshMaxHealth(unit);
        ref Health health = ref world.Healths.Get(unit);
        health.Current = health.Max;
    }

    private static bool HasTechnology(World world, int player, string key) => KingdomCommands.HasTechnology(world, (byte)player, key);

    private static EntityId OwnTavern(World world, byte player, ReadOnlySpan<EntityId> entities)
    {
        foreach (EntityId e in entities)
        {
            if (world.Entities.IsAlive(e) && world.Identities.IndexOf(e) is >= 0 and var i && world.Identities.At(i).Owner == player
                && world.Footprints.Has(e) && world.TypeOf(e).Traits.World.IsTavern && !world.Constructions.Has(e))
            {
                return e;
            }
        }

        return EntityId.None;
    }
}
