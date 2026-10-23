using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>Jewelry: the markets sell and buy items, as ReTzar reads the original (TECH-030, ADR 0259).</summary>
public class JewelryTests
{
    private static int Ticks(SimDriver d, int ms) => ms / d.World.Config.TickLengthMilliseconds;

    [Fact]
    [Trait("Rule", "TECH-030")]
    public void With_Jewelry_one_market_gets_an_offer_a_second_after_it_stands_then_every_minute_keeping_the_newest_eight()
    {
        using var d = new SimDriver(TestMap(48, 16, Structure("euro.market", 0, 4, 4), Structure("euro.market", 0, 20, 4)), ruleset: Classic);
        d.RunUntil(() => false, Ticks(d, 5000));
        Assert.Equal(0, d.World.MarketStocks.Count); // not without Jewelry

        Grant(d.World, 0, "te_jewelry");
        d.RunUntil(() => d.Count(SimEventKind.ItemOffered) == 1, Ticks(d, 3000));
        EntityId market = MarketItems.StockMarket(d.World, 0);
        Assert.False(market.IsNone);
        Assert.Equal(1, d.World.MarketStocks.Count); // one market of the kingdom holds the offers
        d.RunUntil(() => false, Ticks(d, 60_000));
        Assert.Equal(2, d.World.MarketStocks.Get(market).Count);

        d.RunUntil(() => false, Ticks(d, 8 * 60_000));
        MarketStock stock = d.World.MarketStocks.Get(market);
        Assert.Equal(8, stock.Count);
        Assert.Equal(10, d.Count(SimEventKind.ItemOffered));
        for (int k = 0; k < stock.Count; k++)
        {
            EntityId item = EntityId.FromValue(stock.Items[k]);
            Assert.True(MarketItems.Value(d.World, item) > 0);
            Assert.Equal(market.Value, d.World.Helds.Get(item).Holder);
        }

        int[] offered = [.. Of(d, SimEventKind.ItemOffered).Select(e => (int)e.Entity.Value)];
        Assert.Equal(offered[^8..], Enumerable.Range(0, 8).Select(k => (int)stock.Items[k])); // the oldest went first
    }

    [Fact]
    [Trait("Rule", "TECH-030")]
    public void Any_market_of_the_kingdom_sells_an_offer_for_four_times_its_value_and_buys_carried_items_for_their_value()
    {
        using var d = new SimDriver(
            TestMap(48, 16, Structure("euro.market", 0, 4, 4), Structure("euro.market", 0, 20, 4), Unit("euro.footman", 0, 30, 10), Unit("comm.item_health", MapObject.Nature, 31, 10)),
            ruleset: Classic);
        Grant(d.World, 0, "te_jewelry");
        int gold = d.World.Ruleset.ResourceId("gold");
        d.World.Players[0].Resources[gold] = 100_000;
        d.RunUntil(() => d.Count(SimEventKind.ItemOffered) == 1, Ticks(d, 3000));
        EntityId stockMarket = MarketItems.StockMarket(d.World, 0);
        EntityId other = OfType(d.World, "euro.market").Single(m => m != stockMarket);
        EntityId offer = EntityId.FromValue(d.World.MarketStocks.Get(stockMarket).Items[0]);
        int value = MarketItems.Value(d.World, offer);

        d.Order(0, CommandKind.MarketBuy, [other], target: offer);
        d.Step();
        Assert.Empty(d.Rejections());
        Assert.Equal(100_000 - (4 * value), d.World.Players[0].Resources[gold]);
        Assert.False(d.World.IsHeld(offer)); // on the ground by the market that sold it
        Assert.Equal(0, d.World.MarketStocks.Get(stockMarket).Count);
        (int fx, int fy, int w, int h) = d.World.FootprintRect(other);
        Position at = d.PositionOf(offer);
        Assert.InRange(at.X / SimDriver.Tile, fx - 1, fx + w);
        Assert.InRange(at.Y / SimDriver.Tile, fy - 1, fy + h);

        // A potion a footman carries far away sells for its value.
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId potion = OfType(d.World, "comm.item_health").Single(i => i != offer);
        d.Order(0, CommandKind.PickItem, [footman], target: potion);
        d.RunUntil(() => d.World.IsHeld(potion), 400);
        int potionValue = MarketItems.Value(d.World, potion);
        Assert.True(potionValue > 0);
        int before = d.World.Players[0].Resources[gold];
        d.Order(0, CommandKind.MarketSell, [other], target: potion);
        d.Step();
        Assert.Equal(before + potionValue, d.World.Players[0].Resources[gold]);
        Assert.False(d.World.Entities.IsAlive(potion));
    }

    [Fact]
    [Trait("Rule", "TECH-030")]
    public void Items_are_worth_what_the_original_rates_them()
    {
        // ReTzar's reading of vt+0x2c4: power items 4 x their power, books their charges x the spell's rate x 1.5, potions
        // health + mana + 5 x experience + 2 x (attack + defence).
        using var d = new SimDriver(TestMap(24, 12, Unit("comm.item_plusplus", MapObject.Nature, 4, 4), Unit("comm.item_magic", MapObject.Nature, 6, 4), Unit("comm.item_health", MapObject.Nature, 8, 4)), ruleset: Classic);
        EntityId sword = OfType(d.World, "comm.item_plusplus").Single();
        EntityId book = OfType(d.World, "comm.item_magic").Single();
        EntityId potion = OfType(d.World, "comm.item_health").Single();
        d.World.Items.Get(sword).Power = 12;
        d.World.Items.Get(book).Spell = (ushort)(Spell(d.World.Ruleset, "spell_freeze") + 1);
        d.World.Items.Get(book).Charges = 3;
        d.World.Items.Get(potion).Effect = ItemEffect.Health;
        d.World.Items.Get(potion).Power = 60;
        Assert.Equal(48, MarketItems.Value(d.World, sword));
        Assert.Equal((50 * 3) + (50 * 3 / 2), MarketItems.Value(d.World, book));
        Assert.Equal(60, MarketItems.Value(d.World, potion));
    }
}
