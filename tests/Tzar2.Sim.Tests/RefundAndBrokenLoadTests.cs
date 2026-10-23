using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>What a cancelled site gives back and what broken-off gathering gives, as ReTzar reads the original (ADR 0251).</summary>
public class RefundAndBrokenLoadTests
{
    [Fact]
    [Trait("Rule", "ECO-002")]
    public void Gathering_broken_off_before_the_load_is_whole_carries_nothing()
    {
        using var d = new SimDriver(TestMap(24, 16, Structure("euro.castle", 0, 2, 2), Tree(12, 4), Unit("euro.peon", 0, 11, 4)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        EntityId tree = OfType(d.World, "comm.tree").Single();
        int stock = d.World.ResourceStocks.Get(tree).Amount;
        d.Order(0, CommandKind.Harvest, [peon], target: tree);
        d.RunUntil(() => d.World.Workers.Get(peon).Phase == WorkerPhase.Gathering, 400);
        d.RunUntil(() => false, 150); // most of the 180 ticks of a load
        Assert.Equal(0, d.World.Workers.Get(peon).CarryAmount);
        d.MoveToTile(0, [peon], 16, 8);
        d.RunUntil(() => false, 40);
        Assert.Equal(0, d.World.Workers.Get(peon).CarryAmount);
        Assert.Equal(stock, d.World.ResourceStocks.Get(tree).Amount); // the tree gave nothing
    }

    [Theory]
    [InlineData(0, 10_000)]
    [InlineData(25, 3_750)]
    [InlineData(50, 2_500)]
    [InlineData(99, 50)]
    [Trait("Rule", "BLD-020")]
    public void A_cancelled_site_gives_its_cost_back_before_any_work_and_half_the_rest_after(int percentDone, int basisPoints)
    {
        // ReTzar's reading of the original (0x41d6a8): all of it before any work, else cost x f / 1000 with
        // f = (1000 T - 1000 p) / (2 T).
        Assert.Equal(basisPoints, Systems.GameplayCommands.SiteRefundBasisPoints(10_000, 100_000, percentDone * 1_000));
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(24, false)]
    [Trait("Rule", "AI-003")]
    public void Only_a_peasant_standing_idle_near_a_damaged_tower_repairs_it(int tileX, bool repaired)
    {
        // The computer has no repair plan (ReTzar, 0x4153a0): a long-idle peasant repairs the damaged building nearest it.
        using var d = new SimDriver(TestMap(40, 16, Structure("euro.castle", 1, 30, 10), Structure("comm.tower_wood", 1, 2, 2), Unit("euro.peon", 1, tileX, 4)), ruleset: Classic);
        EntityId tower = OfType(d.World, "comm.tower_wood").Single();
        ref Health health = ref d.World.Healths.Get(tower);
        health.Current = health.Max / 2;
        int half = health.Current;
        d.RunUntil(() => false, (d.World.Ruleset.Gameplay.IdleWorkAfterMs / 50) + 400);
        Assert.Equal(repaired, d.World.Healths.Get(tower).Current > half);
    }

    [Fact]
    [Trait("Rule", "BLD-020")]
    public void A_site_cancelled_after_work_began_gives_back_half_of_what_is_left()
    {
        const int Tile = Map.MapState.UnitsPerTile;
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 13, 4)), ruleset: Classic);
        int wood = Resource("wood");
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Build, [peon], 14 * Tile, 4 * Tile, arg0: Id("euro.house"));
        d.Step();
        EntityId house = OfType(d.World, "euro.house").Single();
        int paid = 500 - d.World.Players[0].Resources[wood];
        d.RunUntil(() => d.World.Constructions.Get(house).ProgressMs * 3 >= d.World.Constructions.Get(house).TotalMs, 4000);
        Construction site = d.World.Constructions.Get(house);
        int before = d.World.Players[0].Resources[wood];
        d.Order(0, CommandKind.Cancel, [house]);
        d.Step();
        long f = ((1000L * site.TotalMs) - (1000L * site.ProgressMs)) / (2L * site.TotalMs);
        Assert.InRange(f, 300, 340); // about a third built: a third of the cost back
        Assert.Equal(before + (int)(paid * f / 1000), d.World.Players[0].Resources[wood]);
    }
}
