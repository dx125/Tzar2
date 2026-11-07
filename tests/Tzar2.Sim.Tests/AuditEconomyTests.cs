using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>The 2026-11-07 audit's gathering rules (ADR 0280): fields, fish patches, cows' milk and ships' loads.</summary>
public class AuditEconomyTests
{
    // ECO-028 is not turned on in the classic rules yet (see its note); the countdown itself is the original's.
    private static readonly Ruleset Digs = ClassicWith("^class.field: { ResourceNode: { loadCyclesMin: 12, loadCyclesMax: 18, cycleMs: 1196 } }");

    [Fact]
    public void A_field_with_the_originals_countdown_gives_its_load_after_12_to_18_digs()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Structure("comm.field", 0, 12, 4), Unit("euro.peon", 0, 12, 8)), ruleset: Digs);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Harvest, [peon], target: OfType(d.World, "comm.field").Single());
        d.RunUntil(() => d.World.Workers.Get(peon).Phase == WorkerPhase.Gathering, 400);
        int start = d.World.Tick.Value;
        d.RunUntil(() => d.World.Workers.Get(peon).CarryAmount > 0, 800);
        int ms = (d.World.Tick.Value - start) * d.World.Config.TickLengthMilliseconds;
        Assert.InRange(ms, (12 * 1196) - 50, (18 * 1196) + 50); // ReTzar's reading of the original (0x4ad088)
    }

    [Fact]
    [Trait("Rule", "ECO-029")]
    public void A_fish_patch_nobody_fishes_grows_by_5_percent_every_10_s_up_to_2000()
    {
        using var d = new SimDriver(
            AsciiMap(NavalTests.Bay, Structure("euro.castle", 0, 0, 0), new MapObject("comm.fishpatch", MapObject.Nature, (17 * Tile) + (Tile / 2), (7 * Tile) + (Tile / 2))),
            ruleset: Classic);
        EntityId fish = OfType(d.World, "comm.fishpatch").Single();
        int start = d.World.ResourceStocks.Get(fish).Amount;
        d.RunUntil(() => false, 201);
        Assert.Equal(start + (start * 5 / 100), d.World.ResourceStocks.Get(fish).Amount);
        d.World.ResourceStocks.Get(fish).Amount = 1990;
        d.RunUntil(() => false, 200);
        Assert.Equal(2000, d.World.ResourceStocks.Get(fish).Amount);
        Assert.Equal((50, 350), (Classic.EntityTypes[Id("comm.fishing_boat")].Traits.Harvester!.Capacity, Classic.EntityTypes[Id("comm.fishing_boat")].Traits.Harvester!.GatherMs[Classic.ResourceId("food")]));
    }

    [Fact]
    [Trait("Rule", "ECO-027")]
    public void A_cows_milk_comes_back_two_a_second_for_a_kingdom_one_for_nature_and_not_while_it_is_milked()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Unit("comm.cow", 0, 12, 4), Unit("comm.cow", MapObject.Nature, 20, 10)), ruleset: Classic);
        EntityId own = OfType(d.World, "comm.cow", 0).Single();
        EntityId wild = OfType(d.World, "comm.cow", MapObject.Nature).Single();
        d.World.ResourceStocks.Get(own).Amount = 0;
        d.World.ResourceStocks.Get(wild).Amount = 0;
        d.RunUntil(() => false, 100); // 5 s
        Assert.Equal(10, d.World.ResourceStocks.Get(own).Amount);
        Assert.Equal(5, d.World.ResourceStocks.Get(wild).Amount);
    }

    [Fact]
    [Trait("Rule", "TRADE-004")]
    public void A_galleon_carries_1000_a_trip_a_hulk_500_and_a_xebec_300()
    {
        Assert.Equal(
            (1000, 500, 300),
            (Classic.EntityTypes[Id("comm.galleon")].Traits.World.Trader!.Capacity, Classic.EntityTypes[Id("comm.hulk")].Traits.World.Trader!.Capacity, Classic.EntityTypes[Id("comm.xebec")].Traits.World.Trader!.Capacity));
    }
}
