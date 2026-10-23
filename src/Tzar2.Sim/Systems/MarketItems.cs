using System;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Jewelry (TECH-030; ReTzar's reading of the original, 0x479c34, 0x47a254, 0x47a1e1, vt+0x2c4): a kingdom with the
/// technology has one market that gets an item on offer a second after it stands and then every minute, drawn from
/// <c>combat.marketOffers</c>; the list keeps the newest <c>marketSize</c>. Any of the kingdom's markets sells an offer for
/// four times its value in gold, which appears at a corner of that market, and buys any book, power item or potion a unit
/// of the kingdom carries for its value.
/// </summary>
public static class MarketItems
{
    /// <summary>Called by the magic step: offers, once a second.</summary>
    internal static void Step(World world, int tick)
    {
        CombatSettings c = world.Ruleset.Combat;
        int tickMs = world.Config.TickLengthMilliseconds;
        if (c.MarketTechnology < 0 || c.MarketOffers.IsDefaultOrEmpty || tick % Math.Max(1, 1000 / tickMs) != 0)
        {
            return;
        }

        for (int p = 0; p < world.Players.Length; p++)
        {
            if (!world.HasPlayer(p) || !world.Players[p].HasTechnology(c.MarketTechnology))
            {
                continue;
            }

            EntityId market = StockMarket(world, p);
            if (market.IsNone)
            {
                market = FirstMarket(world, p);
                if (market.IsNone)
                {
                    continue;
                }

                world.MarketStocks.Set(market, new MarketStock { NextTick = tick + Math.Max(1, c.MarketFirstMs / tickMs) });
            }

            ref MarketStock stock = ref world.MarketStocks.Get(market);
            if (tick < stock.NextTick)
            {
                continue;
            }

            stock.NextTick = tick + Math.Max(1, c.MarketEveryMs / tickMs);
            Offer(world, market, tick);
        }
    }

    /// <summary>The market holding <paramref name="player"/>'s offers, or none.</summary>
    public static EntityId StockMarket(World world, int player)
    {
        ArgumentNullException.ThrowIfNull(world);
        return StockOf(world, player);
    }

    private static EntityId StockOf(World world, int player)
    {
        ReadOnlySpan<EntityId> markets = world.MarketStocks.Entities;
        for (int i = 0; i < markets.Length; i++)
        {
            if (world.Identities.Get(markets[i]).Owner == player)
            {
                return markets[i];
            }
        }

        return EntityId.None;
    }

    /// <summary>The value of an item in gold (0: not for sale), as the original's vt+0x2c4 rates it.</summary>
    public static int Value(World world, EntityId item)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.Items.IndexOf(item) is not (>= 0 and var s) || world.TypeOf(item).Traits.Item is not { } def || !Marketable(world, world.Identities.Get(item).TypeId))
        {
            return 0;
        }

        ItemState state = world.Items.At(s);
        long value;
        switch (def.Kind)
        {
            case ItemKind.SpellBook when world.ItemSpell(item) is >= 0 and var spell:
            {
                SpellDefinition sd = world.Ruleset.Spells[spell];
                long rate = sd.Effect == SpellEffect.Summon && sd.Summon >= 0 && world.Ruleset.ResourceId("gold") is >= 0 and var gold
                    && gold < world.Ruleset.EntityTypes[sd.Summon].Traits.Cost.Length
                    ? world.Ruleset.EntityTypes[sd.Summon].Traits.Cost[gold]
                    : sd.BookValue;
                long x = rate * Math.Max(1, state.Charges);
                value = x + (x / 2);
                break;
            }

            case ItemKind.Potion:
                value = state.Effect switch
                {
                    ItemEffect.Health or ItemEffect.Mana => state.Power,
                    ItemEffect.Levels => 5L * state.Power,
                    ItemEffect.Attack or ItemEffect.Defence => 2L * state.Power,
                    _ => def.PotionHealth + def.PotionMana + (5L * def.PotionLevel) + (2L * (def.PotionAttack + def.PotionDefence)),
                };
                break;
            case ItemKind.Weapon or ItemKind.Armor or ItemKind.PlusPlus:
                value = 4L * state.Power;
                break;
            default:
                value = 0;
                break;
        }

        return (int)Math.Clamp(value, 0, 10_000_000);
    }

    /// <summary>Command <see cref="CommandKind.MarketBuy"/>: the listed market sells offer <see cref="Command.Target"/>.</summary>
    internal static CommandRejection? Buy(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        CombatSettings c = world.Ruleset.Combat;
        if (entities.Length != 1 || !IsOwnMarket(world, entities[0], command.Player) || c.MarketTechnology < 0
            || !world.Players[command.Player].HasTechnology(c.MarketTechnology))
        {
            return CommandRejection.NoValidEntities;
        }

        EntityId item = command.Target;
        EntityId stockMarket = StockMarket(world, command.Player);
        if (stockMarket.IsNone || IndexInStock(world, stockMarket, item) < 0)
        {
            return CommandRejection.InvalidArgument;
        }

        int price = (int)Math.Min((long)Value(world, item) * c.MarketBuyPercent / 100, int.MaxValue);
        Span<int> cost = stackalloc int[world.Ruleset.ResourceTypes.Count];
        if (world.Ruleset.ResourceId("gold") is >= 0 and var gold)
        {
            cost[gold] = price;
        }

        if (!SharedStockpile.TryDeduct(world, command.Player, cost))
        {
            return CommandRejection.NotAffordable;
        }

        RemoveFromStock(world, stockMarket, item);
        (int x, int y) = Corner(world, entities[0]);
        world.Positions.Get(item) = new Position(x, y);
        world.Events.Emit(new SimEvent(SimEventKind.ItemDropped, MapObject.Nature, item, x, y, world.Identities.Get(item).TypeId));
        return null;
    }

    /// <summary>Command <see cref="CommandKind.MarketSell"/>: the listed market buys item <see cref="Command.Target"/>, carried by a unit of the kingdom.</summary>
    internal static CommandRejection? Sell(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        if (entities.Length != 1 || !IsOwnMarket(world, entities[0], command.Player))
        {
            return CommandRejection.NoValidEntities;
        }

        EntityId item = command.Target;
        if (!world.Entities.IsAlive(item) || world.Helds.IndexOf(item) is not (>= 0 and var h)
            || EntityId.FromValue(world.Helds.At(h).Holder) is var holder && (!world.Inventories.Has(holder) || world.Identities.Get(holder).Owner != command.Player))
        {
            return CommandRejection.InvalidArgument;
        }

        int value = Value(world, item);
        if (value <= 0)
        {
            return CommandRejection.InvalidArgument;
        }

        if (world.Ruleset.ResourceId("gold") is >= 0 and var gold)
        {
            world.Credit(command.Player, gold, value);
        }

        world.Destroy(item);
        return null;
    }

    /// <summary>The offers of a stock market, oldest first.</summary>
    public static int IndexInStock(World world, EntityId market, EntityId item)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.MarketStocks.IndexOf(market) is not (>= 0 and var m))
        {
            return -1;
        }

        MarketStock stock = world.MarketStocks.At(m);
        for (int k = 0; k < stock.Count; k++)
        {
            if (stock.Items[k] == item.Value)
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>Destroys a market's offers (it falls or changes hands).</summary>
    internal static void Clear(World world, EntityId market)
    {
        if (world.MarketStocks.IndexOf(market) is not (>= 0 and var m))
        {
            return;
        }

        MarketStock stock = world.MarketStocks.At(m);
        world.MarketStocks.Remove(market);
        for (int k = 0; k < stock.Count; k++)
        {
            EntityId item = EntityId.FromValue(stock.Items[k]);
            world.Helds.Remove(item);
            world.Destroy(item);
        }
    }

    private static bool Marketable(World world, ushort typeId)
    {
        foreach (ImmutableArray<MarketVariant> kind in world.Ruleset.Combat.MarketOffers)
        {
            foreach (MarketVariant v in kind)
            {
                if (v.Item == typeId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsOwnMarket(World world, EntityId market, int player)
        => world.Entities.IsAlive(market) && world.Identities.IndexOf(market) is >= 0 and var i && world.Identities.At(i).Owner == player
            && KingdomCommands.IsDock(world, market, DropOffAccess.Land);

    private static EntityId FirstMarket(World world, int player)
    {
        ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
        EntityId best = EntityId.None;
        for (int i = 0; i < structures.Length; i++)
        {
            if (IsOwnMarket(world, structures[i], player) && (best.IsNone || structures[i].Index < best.Index))
            {
                best = structures[i];
            }
        }

        return best;
    }

    // A new offer: a kind and a variant, each equally likely; a book's charges by the minutes played (1 in the first ten,
    // 1-3 until twenty, 1-10 later; ReTzar, 0x464650).
    private static void Offer(World world, EntityId market, int tick)
    {
        CombatSettings c = world.Ruleset.Combat;
        if (world.Entities.RemainingCapacity < 1)
        {
            return;
        }

        ref Core.Rng.Xoshiro256StarStar rng = ref world.Rng.Misc;
        ImmutableArray<MarketVariant> kind = c.MarketOffers[rng.NextInt(c.MarketOffers.Length)];
        MarketVariant v = kind[rng.NextInt(kind.Length)];
        Position at = world.Positions.Get(market);
        EntityId item = world.Spawn(v.Item, MapObject.Nature, at.X, at.Y);
        ref ItemState state = ref world.Items.Get(item);
        state.Look = (byte)v.Look;
        if (v.Book && !c.MarketBookSpells.IsDefaultOrEmpty)
        {
            state.Spell = (ushort)(c.MarketBookSpells[rng.NextInt(c.MarketBookSpells.Length)] + 1);
            long minutes = (long)tick * world.Config.TickLengthMilliseconds / 60_000;
            state.Charges = minutes <= 10 ? 1 : minutes <= 20 ? 1 + rng.NextInt(3) : 1 + rng.NextInt(10);
        }
        else
        {
            state.Effect = v.Effect;
            state.Power = v.Min + rng.NextInt(v.Max - v.Min + 1);
        }

        ref MarketStock stock = ref world.MarketStocks.Get(market);
        if (stock.Count >= c.MarketSize)
        {
            EntityId oldest = EntityId.FromValue(stock.Items[0]);
            RemoveFromStock(world, market, oldest);
            world.Destroy(oldest);
            stock = ref world.MarketStocks.Get(market);
        }

        stock.Items[stock.Count++] = item.Value;
        world.Helds.Set(item, new Held { Holder = market.Value });
        world.Events.Emit(new SimEvent(SimEventKind.ItemOffered, world.Identities.Get(market).Owner, item, market.Index, 0, v.Item));
    }

    private static void RemoveFromStock(World world, EntityId market, EntityId item)
    {
        ref MarketStock stock = ref world.MarketStocks.Get(market);
        int k = IndexInStock(world, market, item);
        if (k < 0)
        {
            return;
        }

        for (int j = k; j < stock.Count - 1; j++)
        {
            stock.Items[j] = stock.Items[j + 1];
        }

        stock.Count--;
        stock.Items[stock.Count] = 0;
        world.Helds.Remove(item);
    }

    // A corner of the market drawn at random, on the map (ReTzar, 0x47a254).
    private static (int X, int Y) Corner(World world, EntityId market)
    {
        (int fx, int fy, int w, int h) = world.FootprintRect(market);
        (int tx, int ty) = world.Rng.Misc.NextInt(4) switch
        {
            0 => (fx + w, fy + h),
            1 => (fx - 1, fy + h),
            2 => (fx + w, fy - 1),
            _ => (fx - 1, fy - 1),
        };
        tx = Math.Clamp(tx, 0, world.Map.Width - 1);
        ty = Math.Clamp(ty, 0, world.Map.Height - 1);
        return ((tx * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2), (ty * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2));
    }
}
