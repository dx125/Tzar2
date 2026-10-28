using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Kingdoms;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>GAME-14 (ADR 0103): caravans between two markets, profit by distance and load; tavern conversion, gambling, loans.</summary>
public class TradeTests
{
    private static SimDriver Markets(int distance, bool grant = true)
    {
        var d = new SimDriver(
            TestMap(64, 16, Structure("euro.market", 0, 4, 6), Structure("euro.market", 0, 4 + distance, 6), Unit("euro.trader", 0, 8, 12), Structure("euro.tavern", 0, 30, 1)),
            ruleset: Classic);
        if (grant)
        {
            ResearchTests.Grant(d, 0, Classic, "te_traderoutes", "te_usury", "te_gambling");
        }

        return d;
    }

    private static void Route(SimDriver d, int goodsA, int goodsB, int amount = 0)
    {
        EntityId[] markets = [.. OfType(d.World, "euro.market").OrderBy(m => d.World.Positions.Get(m).X)];
        Position b = d.World.Positions.Get(markets[1]);
        d.Order(0, CommandKind.Trade, [OfType(d.World, "euro.trader").Single()], b.X, b.Y, target: markets[0], arg0: goodsA | (goodsB << 8), arg1: amount);
    }

    [Fact]
    [Trait("Rule", "TRADE-001")]
    public void A_caravan_carries_goods_both_ways_and_profit_grows_with_distance()
    {
        int wood = Resource("wood");
        int gold = Resource("gold");
        int[] profits = new int[2];
        foreach ((int distance, int index) in new[] { (20, 0), (50, 1) })
        {
            using SimDriver d = Markets(distance);
            Route(d, wood, gold);
            d.RunUntil(() => d.Count(SimEventKind.TradeDelivered) >= 2, 6000);
            SimEvent[] sales = [.. d.Events.Where(e => e.Event.Kind == SimEventKind.TradeDelivered).Select(e => e.Event)];
            Assert.True(sales.Length >= 2, $"{sales.Length} sales");
            Assert.Equal(wood, sales[0].X); // wood loaded at A, sold at B
            Assert.Equal(gold, sales[1].X); // gold loaded at B, sold at A
            // A European caravan carries 500 (TRADE-004); profit = load x tiles x 40 / (map side x 100) (TRADE-001).
            int profit = 500 * distance * 40 / (64 * 100);
            Assert.Equal(profit, sales[0].Y);
            Assert.Equal(500 + profit, sales[0].Arg);
            profits[index] = sales[0].Y;
            Assert.Equal(sales[0].Y + sales[1].Y, d.World.Kingdoms[0].TradeIncome);
        }

        Assert.True(profits[1] > profits[0]);
    }

    [Fact]
    [Trait("Rule", "TRADE-001")]
    public void Close_docks_give_no_profit_routes_need_trade_routes_and_another_order_refunds_the_load()
    {
        int wood = Resource("wood");
        using (SimDriver none = Markets(20, grant: false))
        {
            Route(none, wood, wood);
            none.Step();
            Assert.Contains(CommandRejection.RequirementNotMet, none.Rejections());
        }

        using (SimDriver near = Markets(5))
        {
            Route(near, wood, wood);
            near.RunUntil(() => near.Count(SimEventKind.TradeDelivered) >= 1, 3000);
            SimEvent sale = near.Events.First(e => e.Event.Kind == SimEventKind.TradeDelivered).Event;
            Assert.Equal(0, sale.Y); // closer than 12 tiles: no profit (TRADE-001)
        }

        using SimDriver d = Markets(30);
        World w = d.World;
        EntityId trader = OfType(w, "euro.trader").Single();
        int before = w.Players[0].Resources[wood];
        Route(d, wood, wood, amount: 60);
        d.RunUntil(() => w.TradeRoutes.Has(trader) && w.TradeRoutes.Get(trader).Carry > 0, 3000);
        Assert.Equal(before - 60, w.Players[0].Resources[wood]);
        d.Order(0, CommandKind.Stop, [trader]);
        d.Step();
        Assert.False(w.TradeRoutes.Has(trader));
        Assert.Equal(before, w.Players[0].Resources[wood]); // refunded without profit
    }

    [Fact]
    [Trait("Rule", "TRADE-003")]
    public void A_caravan_trades_with_an_enemys_market_and_its_kingdom_takes_the_profit()
    {
        int wood = Resource("wood");
        using var d = new SimDriver(
            TestMap(64, 16, Structure("euro.market", 0, 4, 6), Structure("euro.market", 1, 34, 6), Unit("euro.trader", 0, 8, 12)),
            ruleset: Classic);
        ResearchTests.Grant(d, 0, Classic, "te_traderoutes");
        Assert.Equal(Stance.Enemy, d.World.Diplomacy.StanceOf(0, 1));
        EntityId mine = OfType(d.World, "euro.market", 0).Single();
        EntityId theirs = OfType(d.World, "euro.market", 1).Single();
        Position b = d.World.Positions.Get(theirs);
        int enemyWood = d.World.Players[1].Resources[wood];
        d.Order(0, CommandKind.Trade, [OfType(d.World, "euro.trader").Single()], b.X, b.Y, target: mine, arg0: wood | (wood << 8), arg1: 100);
        d.RunUntil(() => d.Count(SimEventKind.TradeDelivered) >= 1, 6000);
        Assert.Empty(d.Rejections());
        SimEvent sale = d.Events.First(e => e.Event.Kind == SimEventKind.TradeDelivered).Event;
        Assert.Equal(0, sale.Player);
        Assert.True(sale.Y > 0);
        Assert.Equal(enemyWood, d.World.Players[1].Resources[wood]); // the market's kingdom gets nothing
    }

    [Fact]
    [Trait("Rule", "TRADE-004")]
    public void A_peasant_with_the_trader_trait_trades_between_markets()
    {
        // Asian peasants trade instead of caravans (MENU.INI [a_peon] trade; ReTzar 0x4e8060: 100 a trip). The synthetic
        // tables have no Asian civilization, so a European peasant gets the Asian peasant's trait here; the import's
        // ^asia.peon is checked on the original's data (OriginTechnologyTests).
        Content.Compiled.Ruleset rules = ClassicWith("^euro.peon: { Trader: { capacity: 100, access: land, technology: te_traderoutes } }");
        int wood = Resource("wood");
        using var d = new SimDriver(
            TestMap(64, 16, Structure("euro.market", 0, 4, 6), Structure("euro.market", 0, 34, 6), Unit("euro.peon", 0, 8, 12)),
            ruleset: rules);
        ResearchTests.Grant(d, 0, rules, "te_traderoutes");
        EntityId peon = OfType(d.World, "euro.peon").Single();
        EntityId[] markets = [.. OfType(d.World, "euro.market").OrderBy(m => d.World.Positions.Get(m).X)];
        Position b = d.World.Positions.Get(markets[1]);
        d.Order(0, CommandKind.Trade, [peon], b.X, b.Y, target: markets[0], arg0: wood | (wood << 8));
        d.RunUntil(() => d.Count(SimEventKind.TradeDelivered) >= 1, 6000);
        Assert.Equal(100 + (100 * 30 * 40 / (64 * 100)), d.Events.First(e => e.Event.Kind == SimEventKind.TradeDelivered).Event.Arg);
        Assert.True(d.World.TradeRoutes.Has(peon)); // idle work leaves a trading peasant alone
    }

    [Fact]
    [Trait("Rule", "HOLD-008")]
    public void Infantry_boards_a_caravan_rides_with_it_and_gets_off()
    {
        using var d = new SimDriver(TestMap(48, 16, Unit("euro.trader", 0, 8, 6), Unit("euro.footman", 0, 6, 6)), ruleset: Classic);
        EntityId trader = OfType(d.World, "euro.trader").Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        Assert.Equal(9, d.World.HolderCapacity(trader)); // synthetic TRADER.INI max_obj_in_holder (the original: 8), [accept] infantryunits
        d.Order(0, CommandKind.Board, [footman], target: trader);
        d.RunUntil(() => d.World.IsHeld(footman), 400);
        Assert.True(d.World.IsHeld(footman));
        d.MoveToTile(0, [trader], 30, 6);
        d.RunUntil(() => d.World.Positions.Get(trader).X > 28 * Map.MapState.UnitsPerTile, 2000);
        Position at = d.World.Positions.Get(trader);
        d.Order(0, CommandKind.Unload, [trader], at.X, at.Y);
        d.RunUntil(() => !d.World.IsHeld(footman), 400);
        Assert.False(d.World.IsHeld(footman));
        Assert.True(d.World.Positions.Get(footman).X > 25 * Map.MapState.UnitsPerTile);
    }

    [Fact]
    [Trait("Rule", "TRADE-005")]
    [Trait("Rule", "TRADE-006")]
    [Trait("Rule", "TRADE-007")]
    [Trait("Rule", "TRADE-011")]
    public void The_tavern_converts_ten_to_one_gambles_deterministically_and_lends_more_after_a_repayment()
    {
        using SimDriver d = Markets(20);
        World w = d.World;
        EntityId tavern = OfType(w, "euro.tavern").Single();
        int wood = Resource("wood");
        int gold = Resource("gold");
        w.Players[0].Resources[wood] = 105;
        w.Players[0].Resources[gold] = 0;
        d.Order(0, CommandKind.Convert, [tavern], arg0: wood | (gold << 8), arg1: 105);
        d.Step();
        Assert.Equal(5, w.Players[0].Resources[wood]); // whole units only, paid at once: 100 wood for 10 gold
        Assert.Equal(0, w.Players[0].Resources[gold]);

        // TRADE-011: the gold comes a unit every 300 ms; another exchange waits for this one.
        d.Order(0, CommandKind.Convert, [tavern], arg0: wood | (gold << 8), arg1: 10);
        d.Step();
        Assert.Equal(CommandRejection.ExchangeUnderWay, d.Rejections().Last());
        int unit = (w.Ruleset.World.ExchangeUnitMs + w.Config.TickLengthMilliseconds - 1) / w.Config.TickLengthMilliseconds;
        d.RunUntil(() => w.Players[0].Resources[gold] >= 5, 100);
        Assert.Equal(5, w.Players[0].Resources[gold]);
        Assert.Equal(5, w.Kingdoms[0].ExchangeLeft);
        d.RunUntil(() => d.Count(SimEventKind.Converted) == 1, 10 * unit);
        Assert.Equal(10, w.Players[0].Resources[gold]);
        Assert.Equal(0, w.Kingdoms[0].ExchangeLeft);

        // Loans: 1,000, outstanding blocks another, repaid, then 1,200 after the delay (manual ch. 5 §10).
        d.Order(0, CommandKind.Loan, [tavern], arg0: gold, arg1: 0);
        d.Order(0, CommandKind.Loan, [tavern], arg0: gold, arg1: 0);
        d.Step();
        Assert.Equal(1010, w.Players[0].Resources[gold]);
        Assert.Contains(CommandRejection.LoanOutstanding, d.Rejections());
        d.Order(0, CommandKind.Loan, [tavern], arg1: 1);
        d.Step();
        Assert.Equal(10, w.Players[0].Resources[gold]);
        Assert.Equal(KingdomState.NoLoan, w.Kingdoms[0].LoanResource);
        int delay = w.Ruleset.World.LoanGrowDelayMs / w.Config.TickLengthMilliseconds;
        for (int t = 0; t <= delay; t++)
        {
            d.Step();
        }

        d.Order(0, CommandKind.Loan, [tavern], arg0: gold, arg1: 0);
        d.Step();
        Assert.Equal(1210, w.Players[0].Resources[gold]);

        // Gambling is seeded by the match: the same bets give the same results in every run.
        long Bets()
        {
            using SimDriver g = Markets(20);
            g.World.Players[0].Resources[gold] = 10_000;
            EntityId t = OfType(g.World, "euro.tavern").Single();
            for (int i = 0; i < 20; i++)
            {
                g.Order(0, CommandKind.Gamble, [t], arg0: gold | (0 << 8), arg1: 100);
                g.Step();
            }

            Assert.Equal(20, g.Count(SimEventKind.Gambled));
            return g.World.Players[0].Resources[gold];
        }

        long first = Bets();
        Assert.Equal(first, Bets());
        Assert.NotEqual(10_000 - 2000, first); // at odds 2 with a 10% edge some bets win
        Assert.Equal(0, (10_000 - first) % 100);
    }
}
