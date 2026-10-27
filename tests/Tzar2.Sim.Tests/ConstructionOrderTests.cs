using System;
using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
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
