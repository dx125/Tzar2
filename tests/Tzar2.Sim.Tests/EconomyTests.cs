using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>Gathering, drop-off and depletion (GAME-02; ADR 0035) on synthetic test maps with the classic ruleset.</summary>
public class EconomyTests
{
    [Fact]
    public void A_worker_gathers_wood_and_the_stockpile_grows_by_exactly_what_it_delivers()
    {
        // Castle at tiles 2..6, a tree 4 tiles east of it, one peasant between them.
        using var d = new SimDriver(TestMap(24, 16, Structure("euro.castle", 0, 2, 2), Tree(11, 4), Unit("euro.peon", 0, 9, 4)), ruleset: Classic);
        int wood = Resource("wood");
        EntityId peon = OfType(d.World, "euro.peon").Single();
        EntityId tree = OfType(d.World, "comm.tree").Single();
        Assert.Equal(500, d.World.Players[0].Resources[wood]); // medium starting resources

        d.Order(0, CommandKind.Harvest, [peon], target: tree);
        int expected = 500;
        int deliveries = 0;
        for (int t = 0; t < 3000 && deliveries < 3; t++)
        {
            d.Step();
            foreach (SimEvent e in d.World.Events.Events)
            {
                if (e.Kind == SimEventKind.ResourceDelivered)
                {
                    Assert.Equal(peon, e.Entity);
                    Assert.Equal(wood, e.X);
                    Assert.Equal(10, e.Arg); // a full load: carry capacity 10 (UNVERIFIED, UNK-17)
                    expected += e.Arg;
                    deliveries++;
                }
            }

            // The stockpile changes only by deliveries, by exactly their amounts.
            Assert.Equal(expected, d.World.Players[0].Resources[wood]);
        }

        Assert.Equal(3, deliveries);
        Assert.Equal(530, d.World.Players[0].Resources[wood]);
        int carried = d.World.Workers.Get(peon).CarryAmount;
        Assert.Equal(100 - 30 - carried, d.World.ResourceStocks.Get(tree).Amount);
        Assert.Empty(d.Rejections());
    }

    [Fact]
    public void Gathering_takes_the_gather_time_per_unit()
    {
        // The peasant stands next to the tree: 10 units at 1,000 ms each take exactly 200 ticks of gathering.
        using var d = new SimDriver(TestMap(24, 16, Structure("euro.castle", 0, 2, 2), Tree(12, 4), Unit("euro.peon", 0, 11, 4)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Harvest, [peon], target: OfType(d.World, "comm.tree").Single());
        int gathering = 0;
        int ticks = d.RunUntil(() =>
        {
            Worker w = d.World.Workers.Get(peon);
            gathering += w.Phase == WorkerPhase.Gathering ? 1 : 0;
            return w.Phase == WorkerPhase.ToDropOff;
        }, 1000);
        Assert.True(ticks < 1000);
        Assert.Equal(10, d.World.Workers.Get(peon).CarryAmount);
        Assert.Equal(200, gathering); // 10 x 1,000 ms at 50 ms per tick
    }

    [Fact]
    public void A_depleted_node_is_removed_frees_its_tile_and_the_worker_moves_on_to_the_next()
    {
        Ruleset ruleset = ClassicWith("comm.tree: { ResourceNode: { amount: 15 } }");
        using var d = new SimDriver(TestMap(24, 16, Structure("euro.castle", 0, 2, 2), Tree(11, 4), Tree(12, 5), Unit("euro.peon", 0, 9, 4)), ruleset: ruleset);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        EntityId first = OfType(d.World, "comm.tree").First();
        d.Order(0, CommandKind.Harvest, [peon], target: first);
        int wood = ruleset.ResourceId("wood");
        d.RunUntil(() => d.Count(SimEventKind.ResourceDepleted) == 2 && d.World.Workers.Get(peon).CarryAmount == 0, 6000);

        Assert.Equal(2, d.Count(SimEventKind.ResourceDepleted));
        Assert.Equal(530, d.World.Players[0].Resources[wood]); // 2 trees x 15
        Assert.Empty(OfType(d.World, "comm.tree"));
        Assert.True(d.World.Map.IsPassable(Locomotor.Foot, 11, 4));
        Assert.True(d.World.Map.IsPassable(Locomotor.Foot, 12, 5));
        d.RunUntil(() => d.World.Workers.Get(peon).Task == WorkerTask.None, 200);
        Assert.Equal(WorkerTask.None, d.World.Workers.Get(peon).Task); // nothing left within the search radius
    }

    [Fact]
    public void Loads_go_to_the_nearest_completed_drop_off_that_accepts_them()
    {
        // The storage is nearest to the tree but takes only stone/gold/iron; the lumber mill takes wood; the castle
        // (everything) is far away.
        using var d = new SimDriver(
            TestMap(48, 16,
                Structure("euro.castle", 0, 2, 2),
                Structure("euro.storage", 0, 33, 4),
                Structure("euro.lumber", 0, 38, 8),
                Tree(36, 4),
                Structure("comm.res_gold", MapObject.Nature, 40, 2),
                Unit("euro.peon", 0, 35, 6),
                Unit("euro.peon", 0, 39, 5)),
            ruleset: Classic);
        EntityId[] peons = [.. OfType(d.World, "euro.peon")];
        EntityId lumber = OfType(d.World, "euro.lumber").Single();
        EntityId storage = OfType(d.World, "euro.storage").Single();
        d.Order(0, CommandKind.Harvest, [peons[0]], target: OfType(d.World, "comm.tree").Single());
        d.Order(0, CommandKind.Harvest, [peons[1]], target: OfType(d.World, "comm.res_gold").Single());
        d.RunUntil(() => d.Count(SimEventKind.ResourceDelivered) >= 4, 4000);

        foreach ((int _, SimEvent e) in d.Events.Where(x => x.Event.Kind == SimEventKind.ResourceDelivered))
        {
            int dropIndex = e.X == Resource("wood") ? lumber.Index : storage.Index;
            Assert.Equal(dropIndex, e.Y);
        }

        Assert.Equal(500, d.World.Players[0].Resources[Resource("stone")]);
        Assert.True(d.World.Players[0].Resources[Resource("gold")] > 500);
        Assert.True(d.World.Players[0].Resources[Resource("wood")] > 500);
    }

    [Fact]
    public void Construction_sites_are_not_drop_offs_and_fields_under_construction_are_not_harvestable()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 10, 10)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Build, [peon], 12 * Tile, 4 * Tile, arg0: Id("comm.field"));
        d.Step();
        EntityId field = OfType(d.World, "comm.field").Single();
        Assert.True(d.World.Constructions.Has(field));
        Assert.False(d.World.ResourceStocks.Has(field));
        d.Order(0, CommandKind.Harvest, [peon], target: field);
        d.Step();
        Assert.Equal(CommandRejection.InvalidArgument, d.Rejections().Single());
    }
}
