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
    [Trait("Rule", "ECO-010")]
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
        // The peasant stands next to the tree: 10 units at 900 ms each (measured once, ADR 0175) take exactly 180 ticks.
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
        Assert.Equal(180, gathering); // 10 x 900 ms at 50 ms per tick
    }

    [Fact]
    [Trait("Rule", "ECO-011")]
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
    [Trait("Rule", "ECO-013")]
    public void Loads_go_to_the_nearest_completed_drop_off_that_accepts_them()
    {
        // The storage is nearest to the tree but takes only stone and gold; the lumber mill takes wood; the castle
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
    [Trait("Rule", "ECO-014")]
    public void Every_civilizations_storage_takes_gold_and_stone_and_nothing_else()
    {
        EntityTypeDefinition[] storages = [.. Classic.EntityTypes.Where(t => t.Traits.LegacyClass == "storage")];
        Assert.NotEmpty(storages);
        Assert.All(storages, s => Assert.True(s.Traits.AcceptsDropOff(Resource("gold")) && s.Traits.AcceptsDropOff(Resource("stone"))
            && !s.Traits.AcceptsDropOff(Resource("wood")) && !s.Traits.AcceptsDropOff(Resource("food"))));
    }

    [Fact]
    [Trait("Rule", "ECO-025")]
    [Trait("Rule", "ECO-008")]
    public void A_field_is_ready_at_once_and_its_peasant_farms_its_middle_tile()
    {
        // The sixth play-test: a field had to be built first; the original's is born complete and ripe (ReTzar 0x44c7a0),
        // and its peasant walks to its middle tile and digs there (0x42e600).
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 10, 10)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Build, [peon], 12 * Tile, 4 * Tile, arg0: Id("comm.field"));
        d.Step();
        EntityId field = OfType(d.World, "comm.field").Single();
        Assert.False(d.World.Constructions.Has(field));
        Assert.Equal(d.World.Healths.Get(field).Max, d.World.Healths.Get(field).Current);
        Assert.Equal((WorkerTask.Harvest, field.Value), (d.World.Workers.Get(peon).Task, d.World.Workers.Get(peon).Target));

        d.RunUntil(() => d.World.Workers.Get(peon).Phase == WorkerPhase.Gathering && !d.IsMoving(peon), 400);
        (int fx, int fy, int fw, int fh) = d.World.FootprintRect(field);
        Assert.Equal((fx + (fw / 2), fy + (fh / 2)), (d.PositionOf(peon).X / Tile, d.PositionOf(peon).Y / Tile));
    }
}
