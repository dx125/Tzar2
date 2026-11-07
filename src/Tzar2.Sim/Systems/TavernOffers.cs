using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Systems;

/// <summary>One tavern offer (TRADE-009; ADR 0108, ADR 0280).</summary>
/// <param name="TypeId">The unit type.</param>
/// <param name="Count">Units in the offer (mercenaries: 8 to 12; a hero: 1).</param>
/// <param name="Level">Their experience level.</param>
/// <param name="IsHero">A hero (named, hero flag).</param>
public readonly record struct TavernOffer(ushort TypeId, int Count, int Level, bool IsHero);

/// <summary>
/// The tavern's mercenaries and heroes (TRADE-009, ADR 0280; ReTzar's reading of the original, 0x4d24c0, 0x4d2030,
/// 0x4d23b0).
/// </summary>
/// <remarks>
/// Every tavern of a kingdom keeps two lists (<see cref="TavernStock"/>). A second after it stands, and then every
/// <c>world.tavernEveryMs</c> (<c>tavernDeathmatchEveryMs</c> in a Deathmatch), the mercenary list gains an offer when the
/// kingdom knows Mercenaries and the hero list one when it knows Heroes' Quest: a unit of a random civilization's barracks,
/// 8 to 12 of it at level 0 or one hero of level 10 to 30, Fame adding 4 levels to either; a full list of
/// <see cref="TavernStock.ListSize"/> loses its oldest. Hiring an offer takes it off its list.
/// </remarks>
public static class TavernOffers
{
    /// <summary>Called by the magic step every tick: the taverns' lists grow at their time.</summary>
    internal static void Step(World world, int tick)
    {
        WorldSettings w = world.Ruleset.World;
        int tickMs = world.Config.TickLengthMilliseconds;
        if (tick % Math.Max(1, 1000 / tickMs) != 0)
        {
            return; // once a second
        }

        // In the structures' stored order (deterministic, and kept by snapshots); no allocation in a steady tick.
        ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
        for (int i = 0; i < structures.Length; i++)
        {
            EntityId tavern = structures[i];
            if (!world.TypeOf(tavern).Traits.World.IsTavern || world.Constructions.Has(tavern) || !world.HasPlayer(world.Identities.Get(tavern).Owner))
            {
                continue;
            }

            if (world.TavernStocks.IndexOf(tavern) is not (>= 0 and var s))
            {
                world.TavernStocks.Set(tavern, new TavernStock { NextTick = tick + Math.Max(1, w.TavernFirstMs / tickMs) });
                continue;
            }

            ref TavernStock stock = ref world.TavernStocks.At(s);
            if (tick < stock.NextTick)
            {
                continue;
            }

            int every = world.Config.GameType == GameType.Deathmatch ? w.TavernDeathmatchEveryMs : w.TavernEveryMs;
            stock.NextTick = tick + Math.Max(1, every / tickMs);
            byte owner = world.Identities.Get(tavern).Owner;
            if (HasTechnology(world, owner, w.MercenariesTechnology))
            {
                Add(world, ref stock, owner, hero: false);
            }

            if (HasTechnology(world, owner, w.HeroesTechnology))
            {
                Add(world, ref stock, owner, hero: true);
            }
        }
    }

    /// <summary>The offers of <paramref name="tavern"/>: the mercenaries' slots, then the heroes' (from <see cref="TavernStock.ListSize"/>).</summary>
    public static int Offers(World world, EntityId tavern, Span<(int Slot, TavernOffer Offer)> into)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.TavernStocks.IndexOf(tavern) is not (>= 0 and var s))
        {
            return 0;
        }

        TavernStock stock = world.TavernStocks.At(s);
        int n = 0;
        for (int i = 0; i < stock.Mercenaries && n < into.Length; i++)
        {
            into[n++] = (i, Unpack(stock.Offers[i], hero: false));
        }

        for (int i = 0; i < stock.Heroes && n < into.Length; i++)
        {
            into[n++] = (TavernStock.ListSize + i, Unpack(stock.Offers[TavernStock.ListSize + i], hero: true));
        }

        return n;
    }

    /// <summary>
    /// The price of an offer per resource type id (TRADE-009; 0x4d23b0): the type's cost for the kingdom × the count × 1.6,
    /// truncated, plus 30 gold a level and 15 a level of each other resource the type costs.
    /// </summary>
    public static void CostOf(World world, int player, TavernOffer offer, Span<int> into)
    {
        ArgumentNullException.ThrowIfNull(world);
        WorldSettings w = world.Ruleset.World;
        EntityTypeDefinition type = world.Ruleset.EntityTypes[offer.TypeId];
        int gold = world.Ruleset.ResourceId("gold");
        into.Clear();
        for (int r = 0; r < type.Traits.Cost.Length && r < into.Length; r++)
        {
            long cost = world.HasPlayer(player) ? ResearchRules.CostOf(world, (byte)player, type, r) : type.Traits.Cost[r];
            long price = cost * offer.Count * w.HirePricePercent / 100;
            if (r == gold || price != 0)
            {
                price += (long)(r == gold ? w.HireGoldPerLevel : w.HireOtherPerLevel) * offer.Level;
            }

            into[r] = (int)Math.Min(price, int.MaxValue);
        }
    }

    /// <summary>
    /// <see cref="CommandKind.Hire"/>: offer Arg0 (its slot) of a listed own finished tavern, if the kingdom knows the
    /// list's technology (Mercenaries, Heroes' Quest), there is population room and an entity slot per unit, and the price is
    /// paid (shared resources apply). The units appear next to the tavern at their level; a hero gets the hero flag and a
    /// name; the offer leaves its list.
    /// </summary>
    internal static CommandRejection? Hire(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        byte player = command.Player;
        int slot = command.Arg0;
        if ((uint)slot >= TavernStock.Capacity)
        {
            return CommandRejection.InvalidArgument;
        }

        EntityId tavern = OwnTavern(world, player, entities);
        if (tavern.IsNone)
        {
            return CommandRejection.NoValidEntities;
        }

        bool heroSlot = slot >= TavernStock.ListSize;
        if (!HasTechnology(world, player, heroSlot ? world.Ruleset.World.HeroesTechnology : world.Ruleset.World.MercenariesTechnology))
        {
            return CommandRejection.RequirementNotMet;
        }

        if (world.TavernStocks.IndexOf(tavern) is not (>= 0 and var s))
        {
            return CommandRejection.InvalidArgument;
        }

        ref TavernStock stock = ref world.TavernStocks.At(s);
        int index = heroSlot ? slot - TavernStock.ListSize : slot;
        if (index >= (heroSlot ? stock.Heroes : stock.Mercenaries))
        {
            return CommandRejection.OfferTaken; // hired already, or never offered
        }

        TavernOffer offer = Unpack(stock.Offers[slot], heroSlot);
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

        Take(ref stock, slot);
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

    // A new offer at the end of its list (0x4d2030): a civilization drawn from the three, a unit its barracks train, then
    // the count and level; a full list loses its oldest first.
    private static void Add(World world, ref TavernStock stock, byte owner, bool hero)
    {
        WorldSettings w = world.Ruleset.World;
        IReadOnlyList<string> all = world.Ruleset.Civilizations;
        int kingdoms = 0;
        for (int c = 0; c < all.Count; c++)
        {
            kingdoms += world.Ruleset.CanFoundKingdom(all[c]) ? 1 : 0;
        }

        if (kingdoms == 0)
        {
            return;
        }

        int pick = world.Rng.Misc.NextInt(kingdoms);
        string civilization = string.Empty;
        for (int c = 0; c < all.Count; c++)
        {
            if (world.Ruleset.CanFoundKingdom(all[c]) && pick-- == 0)
            {
                civilization = all[c];
                break;
            }
        }
        ReadOnlySpan<ushort> pool = world.BarracksPool(civilization);
        if (pool.IsEmpty)
        {
            return;
        }

        ushort type = pool[world.Rng.Misc.NextInt(pool.Length)];
        int count = hero ? 1 : w.MercenaryGroupMin + world.Rng.Misc.NextInt(w.MercenaryGroupMax - w.MercenaryGroupMin + 1);
        int level = hero ? w.HeroLevelMin + world.Rng.Misc.NextInt(w.HeroLevelMax - w.HeroLevelMin + 1) : w.MercenaryLevel;
        if (HasTechnology(world, owner, w.FameTechnology))
        {
            level += w.FameLevelBonus;
        }

        int start = hero ? TavernStock.ListSize : 0;
        ref byte length = ref hero ? ref stock.Heroes : ref stock.Mercenaries;
        if (length >= TavernStock.ListSize)
        {
            for (int i = 0; i < TavernStock.ListSize - 1; i++)
            {
                stock.Offers[start + i] = stock.Offers[start + i + 1];
            }

            length = TavernStock.ListSize - 1;
        }

        stock.Offers[start + length] = Pack(type, count, level);
        length++;
    }

    // Takes an offer off its list, the later ones moving up.
    private static void Take(ref TavernStock stock, int slot)
    {
        bool hero = slot >= TavernStock.ListSize;
        int start = hero ? TavernStock.ListSize : 0;
        ref byte length = ref hero ? ref stock.Heroes : ref stock.Mercenaries;
        for (int i = slot; i < start + length - 1; i++)
        {
            stock.Offers[i] = stock.Offers[i + 1];
        }

        stock.Offers[start + length - 1] = 0;
        length--;
    }

    private static uint Pack(ushort type, int count, int level) => type | ((uint)Math.Min(count, 255) << 16) | ((uint)Math.Min(level, 255) << 24);

    private static TavernOffer Unpack(uint packed, bool hero) => new((ushort)(packed & 0xFFFF), (int)((packed >> 16) & 0xFF), (int)(packed >> 24), hero);

    // Starting level (and, for heroes, the hero flag and a name), whatever the kingdom's level cap: the tavern sells
    // trained soldiers.
    private static void Train(World world, EntityId unit, TavernOffer offer)
    {
        if (world.Experiences.IndexOf(unit) is not (>= 0 and var x))
        {
            return;
        }

        ref Experience xp = ref world.Experiences.At(x);
        if (offer.Level > 0 && xp.Level < offer.Level)
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
