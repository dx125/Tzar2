using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// The sixth play-test's construction findings, as ReTzar reads the original: sites placed one after another are built in
/// the order placed; a wooden wall turns to stone with its whole row; builders work from the tiles next to the site,
/// facing it.
/// </summary>
public class ConstructionOrderTests
{
    [Fact]
    [Trait("Rule", "BLD-025")]
    public void A_peasant_builds_the_sites_it_placed_in_the_order_placed()
    {
        using var d = new SimDriver(TestMap(40, 16, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 10, 8)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Build, [peon], 14 * Tile, 6 * Tile, arg0: Id("euro.house"));
        d.Step();
        d.Order(0, CommandKind.Build, [peon], 24 * Tile, 6 * Tile, arg0: Id("euro.house"));
        d.Step();
        EntityId first = OfType(d.World, "euro.house").Single(h => d.PositionOf(h).X < 20 * Tile);
        EntityId second = OfType(d.World, "euro.house").Single(h => d.PositionOf(h).X > 20 * Tile);
        Assert.Equal(first.Value, d.World.Workers.Get(peon).Target); // it keeps the first

        d.RunUntil(() => !d.World.Constructions.Has(first), 4000);
        Assert.False(d.World.Constructions.Has(first));
        Assert.True(d.World.Constructions.Has(second));
        d.Step();
        Assert.Equal((WorkerTask.Build, second.Value), (d.World.Workers.Get(peon).Task, d.World.Workers.Get(peon).Target)); // then the next
        d.RunUntil(() => !d.World.Constructions.Has(second), 4000);
        Assert.False(d.World.Constructions.Has(second));
    }

    [Fact]
    [Trait("Rule", "BLD-011")]
    public void A_dragged_wall_is_an_L_with_the_longer_side_first_and_Alt_takes_the_other()
    {
        var row = new List<(int X, int Y)>();
        PlacementQueries.WallRow(20, 4, 14, 7, alt: false, builder: null, row);
        Assert.Equal([(20, 4), (19, 4), (18, 4), (17, 4), (16, 4), (15, 4), (14, 4), (14, 5), (14, 6), (14, 7)], row);
        PlacementQueries.WallRow(20, 4, 14, 7, alt: true, builder: null, row);
        Assert.Equal([(20, 4), (20, 5), (20, 6), (20, 7), (19, 7), (18, 7), (17, 7), (16, 7), (15, 7), (14, 7)], row);

        // A straight line has no other side; one tile is one segment; the row begins at the end nearer the builder.
        PlacementQueries.WallRow(3, 5, 3, 2, alt: true, builder: null, row);
        Assert.Equal([(3, 5), (3, 4), (3, 3), (3, 2)], row);
        PlacementQueries.WallRow(3, 5, 3, 5, alt: false, builder: null, row);
        Assert.Equal([(3, 5)], row);
        PlacementQueries.WallRow(20, 4, 14, 7, alt: false, builder: (12, 8), row);
        Assert.Equal((14, 7), row[0]);
        Assert.Equal((20, 4), row[^1]);
    }

    [Fact]
    [Trait("Rule", "BLD-011")]
    public void A_dragged_wall_is_begun_at_its_end_nearer_the_builder()
    {
        using var d = new SimDriver(TestMap(40, 16, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 12, 8)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.World.Players[0].Resources[Resource("wood")] = 5000;
        d.Order(0, CommandKind.BuildWall, [peon], 20 * Tile, 4 * Tile, arg0: Id("comm.wall_wood"), arg1: (7 << 16) | 14);
        d.Step();
        EntityId[] segments = [.. OfType(d.World, "comm.wall_wood")];
        Assert.Equal(10, segments.Length);
        Assert.Contains(segments, s => d.World.FootprintRect(s) == (14, 5, 1, 1)); // the corner and the side down
        EntityId first = segments.Single(s => d.World.FootprintRect(s) == (14, 7, 1, 1));
        Assert.Equal(first.Value, d.World.Workers.Get(peon).Target);

        // With Alt, the other side first.
        d.Order(0, CommandKind.BuildWall, [peon], 30 * Tile, 2 * Tile, arg0: Id("comm.wall_wood"), arg1: (5 << 16) | 33, flags: CommandFlags.Alt);
        d.Step();
        Assert.Contains(OfType(d.World, "comm.wall_wood"), s => d.World.FootprintRect(s) == (30, 5, 1, 1));
        Assert.DoesNotContain(OfType(d.World, "comm.wall_wood"), s => d.World.FootprintRect(s) == (33, 2, 1, 1));
    }

    [Fact]
    [Trait("Rule", "BLD-027")]
    public void A_building_is_taken_down_over_seconds_for_three_quarters_of_all_it_cost()
    {
        using var d = new SimDriver(
            TestMap(40, 16, Structure("euro.castle", 0, 2, 2), Structure("euro.house", 0, 14, 6), Unit("euro.peon", 0, 24, 10)), ruleset: Classic);
        EntityId house = OfType(d.World, "euro.house").Single(), castle = OfType(d.World, "euro.castle").Single();
        EntityId peon = OfType(d.World, "euro.peon").Single();
        var houseCost = Classic.EntityTypes[Id("euro.house")].Traits.Cost;
        int Stock(int r) => d.World.Players[0].Resources[r];
        int[] before = [.. Enumerable.Range(0, houseCost.Length).Select(Stock)];
        d.Order(0, CommandKind.Dismantle, [house]);
        d.Step();

        // It comes down over 5 to 10 s by its health, then gives back its share.
        Assert.True(d.World.Entities.IsAlive(house));
        int total = d.World.Dismantlings.Get(house).TotalMs;
        Assert.InRange(total, 5_000, 10_000);
        Assert.Equal(before, Enumerable.Range(0, houseCost.Length).Select(Stock));
        d.RunUntil(() => !d.World.Entities.IsAlive(house), 400);
        Assert.False(d.World.Entities.IsAlive(house));
        Assert.Equal(1, d.Count(SimEventKind.Dismantled));
        Assert.All(Enumerable.Range(0, houseCost.Length), r => Assert.Equal(houseCost[r] * 7_500L / 10_000, Stock(r) - before[r]));

        // A wooden tower built and raised twice gives back three quarters of all three prices.
        ResearchTests.Grant(d, 0, Classic, "te_architecture");
        d.World.Players[0].Resources[Resource("wood")] = 5000;
        d.World.Players[0].Resources[Resource("stone")] = 5000;
        d.World.Players[0].Resources[Resource("gold")] = 5000;
        d.Order(0, CommandKind.Build, [peon], 24 * Tile, 4 * Tile, arg0: Id("comm.tower_wood"));
        d.Step();
        EntityId tower = OfType(d.World, "comm.tower_wood").Single();
        d.RunUntil(() => !d.World.Constructions.Has(tower), 4000);
        d.Order(0, CommandKind.Upgrade, [tower], arg0: Classic.UpgradeId("to_wtower2"));
        d.RunUntil(() => d.Count(SimEventKind.UpgradeCompleted) == 1, 3000);
        d.Order(0, CommandKind.Upgrade, [tower], arg0: Classic.UpgradeId("to_stower"));
        d.RunUntil(() => d.Count(SimEventKind.UpgradeCompleted) == 2, 3000);
        Assert.Equal(Id("comm.tower_stone"), d.World.Identities.Get(tower).TypeId);
        var all = Classic.EntityTypes[Id("comm.tower_wood")].Traits.Cost.Select((c, r) => (long)c + Classic.Upgrades[Classic.UpgradeId("to_wtower2")].Cost[r] + Classic.Upgrades[Classic.UpgradeId("to_stower")].Cost[r]).ToArray();
        Assert.True(all.Sum() > Classic.EntityTypes[Id("comm.tower_stone")].Traits.Cost.Sum());
        int[] stock = [.. Enumerable.Range(0, all.Length).Select(Stock)];
        d.Order(0, CommandKind.Dismantle, [tower]);
        d.RunUntil(() => !d.World.Entities.IsAlive(tower), 400);
        Assert.All(Enumerable.Range(0, all.Length), r => Assert.Equal(all[r] * 7_500 / 10_000, Stock(r) - stock[r]));

        // The castle only with Shift, as Delete; a site is cancelled, not taken down.
        d.Order(0, CommandKind.Dismantle, [castle]);
        d.Step();
        Assert.Equal(CommandRejection.ShiftDeleteRequired, d.Rejections().Last());
        d.Order(0, CommandKind.Build, [peon], 30 * Tile, 4 * Tile, arg0: Id("euro.house"));
        d.Step();
        EntityId site = OfType(d.World, "euro.house").Single();
        d.Order(0, CommandKind.Dismantle, [site]);
        d.Step();
        Assert.Equal(CommandRejection.NoValidEntities, d.Rejections().Last());
        Assert.True(d.World.Entities.IsAlive(site));
    }

    [Fact]
    [Trait("Rule", "BLD-026")]
    public void A_wooden_wall_turns_to_stone_with_its_whole_row()
    {
        // Three joined wooden walls, a fourth touching the row only at a corner, and one standing apart.
        using var d = new SimDriver(
            TestMap(40, 20, Structure("euro.castle", 0, 2, 2), Structure("comm.wall_wood", 0, 12, 10), Structure("comm.wall_wood", 0, 13, 10),
                Structure("comm.wall_wood", 0, 14, 10), Structure("comm.wall_wood", 0, 15, 11), Structure("comm.wall_wood", 0, 30, 10)),
            ruleset: Classic);
        EntityId[] walls = [.. OfType(d.World, "comm.wall_wood")];
        EntityId apart = walls.Single(w => d.PositionOf(w).X > 25 * Tile);
        EntityId start = walls.Single(w => d.PositionOf(w).X / Tile == 12);
        ResearchTests.Grant(d, 0, Classic, "te_architecture");
        int upgrade = Classic.UpgradeId("to_wall_stone");
        int Stock() => Enumerable.Range(0, Classic.Upgrades[upgrade].Cost.Length).Sum(r => d.World.Players[0].Resources[r]);
        int before = Stock();
        d.Order(0, CommandKind.Upgrade, [start], arg0: upgrade);
        d.Step();

        // Three joined walls: three times the price.
        Assert.True(Classic.Upgrades[upgrade].Cost.Sum() > 0);
        Assert.Equal(3 * Classic.Upgrades[upgrade].Cost.Sum(), before - Stock());
        d.RunUntil(() => d.Count(SimEventKind.UpgradeCompleted) >= 1, 4000);
        Assert.Equal(3, OfType(d.World, "comm.wall_stone").Count());
        Assert.Equal(Id("comm.wall_wood"), d.World.Identities.Get(apart).TypeId);
    }

    [Fact]
    [Trait("Rule", "WORLD-019")]
    public void Builders_work_from_the_tiles_next_to_the_site_facing_it()
    {
        using var d = new SimDriver(TestMap(40, 16, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 24, 12)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Build, [peon], 14 * Tile, 6 * Tile, arg0: Id("euro.house"));
        d.RunUntil(() => d.World.Workers.Get(peon).Phase == WorkerPhase.Building && !d.IsMoving(peon), 600);
        EntityId house = OfType(d.World, "euro.house").Single();
        (int fx, int fy, int fw, int fh) = d.World.FootprintRect(house);
        (int tx, int ty) = (d.PositionOf(peon).X / Tile, d.PositionOf(peon).Y / Tile);
        Assert.True(tx >= fx - 1 && tx <= fx + fw && ty >= fy - 1 && ty <= fy + fh, $"the builder stands at ({tx},{ty}), not next to the site");

        // Facing the site: the direction to its middle.
        int dx = Math.Sign((((2 * fx) + fw) * Tile / 2) - d.PositionOf(peon).X);
        int dy = Math.Sign((((2 * fy) + fh) * Tile / 2) - d.PositionOf(peon).Y);
        int facing = d.World.Identities.Get(peon).Facing;
        int[] fx8 = [1, 1, 0, -1, -1, -1, 0, 1];
        int[] fy8 = [0, 1, 1, 1, 0, -1, -1, -1];
        Assert.True((fx8[facing] == dx || dx == 0 || fx8[facing] == 0) && (fy8[facing] == dy || dy == 0 || fy8[facing] == 0), $"facing {facing} away from the site ({dx},{dy})");
    }
}
