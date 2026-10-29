using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// Land units step cell by cell, one to a tile (ADR 0270, WORLD-020; the sixth play-test: crowds jittered, moving troops
/// shoved standing friends several tiles aside, the rear of a group never went round).
/// </summary>
public class CellMovementTests
{
    private static (int X, int Y) TileOf(SimDriver d, EntityId e) => (d.PositionOf(e).X / Tile, d.PositionOf(e).Y / Tile);

    [Fact]
    [Trait("Rule", "WORLD-020")]
    public void A_crowd_sent_to_one_point_stands_one_to_a_tile_every_tick()
    {
        var units = new List<Tzar2.Content.Compiled.MapObject>();
        for (int i = 0; i < 24; i++)
        {
            units.Add(Unit("euro.footman", 0, 4 + (i % 6), 4 + (i / 6)));
        }

        using var d = new SimDriver(TestMap(40, 24, [.. units]), ruleset: Classic);
        EntityId[] all = [.. OfType(d.World, "euro.footman")];
        d.MoveToTile(0, all, 28, 12);
        d.RunUntil(() =>
        {
            // Standing units never share a tile; a unit mid-step owns the tile it enters.
            var standing = all.Where(u => !d.IsMoving(u)).Select(u => TileOf(d, u)).ToList();
            Assert.Equal(standing.Count, standing.Distinct().Count());
            return !d.AnyMoving(all);
        }, 1200);

        Assert.All(all, u => Assert.True(Math.Abs(TileOf(d, u).X - 28) <= 4 && Math.Abs(TileOf(d, u).Y - 12) <= 4, $"{TileOf(d, u)}"));
        Assert.All(all, u => Assert.Equal(Tile / 2, d.PositionOf(u).X % Tile)); // on tile centres
    }

    [Fact]
    [Trait("Rule", "WORLD-020")]
    public void A_unit_goes_round_standing_friends_and_nobody_is_pushed()
    {
        // A wall of idle footmen with a gap at its end: the walker goes round, the wall stays where it stands.
        var objects = new List<Tzar2.Content.Compiled.MapObject> { Unit("euro.knight", 0, 4, 10) };
        for (int y = 6; y <= 14; y++)
        {
            objects.Add(Unit("euro.footman", 0, 12, y));
        }

        using var d = new SimDriver(TestMap(32, 24, [.. objects]), ruleset: Classic);
        EntityId knight = OfType(d.World, "euro.knight").Single();
        EntityId[] wall = [.. OfType(d.World, "euro.footman")];
        var before = wall.ToDictionary(u => u, u => d.PositionOf(u));
        d.MoveToTile(0, [knight], 22, 10);
        d.RunUntil(() => !d.IsMoving(knight), 1200);

        Assert.Equal((22, 10), TileOf(d, knight));
        Assert.All(wall, u => Assert.Equal(before[u], d.PositionOf(u)));
    }

    [Fact]
    [Trait("Rule", "WORLD-020")]
    public void A_friend_with_no_way_round_steps_aside_and_comes_back()
    {
        // A corridor one tile wide; an idle footman stands in it. The knight passes; the footman makes room and goes back.
        string[] rows =
        [
            "~~~~~~~~~~~~~~~~~~~~~~~~",
            "~~~~~~~~~~~~~~~~~~~~~~~~",
            "........................",
            "~~~~~~~~~~~.~~~~~~~~~~~~",
            "~~~~~~~~~~~~~~~~~~~~~~~~",
        ];
        using var d = new SimDriver(AsciiMap(rows, Unit("euro.knight", 0, 2, 2), Unit("euro.footman", 0, 10, 2)), ruleset: Classic);
        EntityId knight = OfType(d.World, "euro.knight").Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        Position home = d.PositionOf(footman);
        d.MoveToTile(0, [knight], 20, 2);
        int furthest = 0;
        d.RunUntil(() =>
        {
            furthest = Math.Max(furthest, Math.Abs(d.PositionOf(footman).X - home.X) + Math.Abs(d.PositionOf(footman).Y - home.Y));
            return TileOf(d, knight) == (20, 2);
        }, 1200);

        Assert.Equal((20, 2), TileOf(d, knight));
        Assert.InRange(furthest, 1, 2 * Tile); // it stepped aside by a tile, not shoved along
        d.RunUntil(() => d.PositionOf(footman).Equals(home), 400);
        Assert.Equal(home, d.PositionOf(footman)); // and went back
    }

    [Fact]
    [Trait("Rule", "WORLD-020")]
    public void Friends_head_on_in_a_corridor_pass_each_other()
    {
        string[] rows =
        [
            "~~~~~~~~~~~~~~~~~~~~~~~~",
            "........................",
            "~~~~~~~~~~~~~~~~~~~~~~~~",
        ];
        using var d = new SimDriver(AsciiMap(rows, Unit("euro.footman", 0, 2, 1), Unit("euro.footman", 0, 3, 1), Unit("euro.footman", 0, 20, 1), Unit("euro.footman", 0, 21, 1)), ruleset: Classic);
        EntityId[] west = [.. OfType(d.World, "euro.footman").Where(u => d.PositionOf(u).X < 10 * Tile)];
        EntityId[] east = [.. OfType(d.World, "euro.footman").Where(u => d.PositionOf(u).X > 10 * Tile)];
        d.MoveToTile(0, west, 21, 1);
        d.MoveToTile(0, east, 2, 1);
        d.RunUntil(() => west.All(u => TileOf(d, u).X >= 19) && east.All(u => TileOf(d, u).X <= 4), 2400);

        Assert.All(west, u => Assert.True(TileOf(d, u).X >= 19, $"west at {TileOf(d, u)}"));
        Assert.All(east, u => Assert.True(TileOf(d, u).X <= 4, $"east at {TileOf(d, u)}"));
    }

    [Fact]
    [Trait("Rule", "WORLD-020")]
    public void Many_attackers_of_one_enemy_surround_it()
    {
        var objects = new List<Tzar2.Content.Compiled.MapObject> { Unit("euro.knight", 1, 20, 12) };
        for (int i = 0; i < 10; i++)
        {
            objects.Add(Unit("euro.footman", 0, 6 + (i % 2), 8 + i));
        }

        using var d = new SimDriver(TestMap(40, 24, [.. objects]), ruleset: Classic);
        EntityId knight = OfType(d.World, "euro.knight").Single();
        EntityId[] footmen = [.. OfType(d.World, "euro.footman")];
        d.Order(1, CommandKind.SetStance, [knight], arg0: (int)CombatStance.HoldFire);
        d.Order(1, CommandKind.Cheat, [], arg0: (int)CheatCode.God); // it stands while they all come
        d.Order(0, CommandKind.Attack, footmen, target: knight);
        int most = 0;
        d.RunUntil(() =>
        {
            if (!d.World.Entities.IsAlive(knight))
            {
                return true;
            }

            (int kx, int ky) = TileOf(d, knight);
            most = Math.Max(most, footmen.Count(f => d.World.Entities.IsAlive(f) && !d.IsMoving(f) && Math.Max(Math.Abs(TileOf(d, f).X - kx), Math.Abs(TileOf(d, f).Y - ky)) == 1));
            return most >= 8;
        }, 1200);

        Assert.True(most >= 6, $"only {most} footmen stood round the knight");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Rule", "WORLD-021")]
    public void A_unit_goes_the_long_way_round_a_gap_held_by_units_that_keep_it(bool workers)
    {
        // The seventh play-test: a unit walked up to a gap held by units and stood there instead of going round. A row of
        // houses across the map, its gap held by creatures of Nature, or by peasants chopping the trees at its sides.
        var objects = new List<Tzar2.Content.Compiled.MapObject> { Structure("euro.castle", 0, 2, 2), Unit("euro.knight", 0, 10, 16) };
        for (int y = 2; y < 30; y += 2)
        {
            if (y != 16 && !(workers && y is 14 or 18))
            {
                objects.Add(Structure("euro.house", 0, 20, y));
            }
        }

        if (workers)
        {
            foreach (int y in new[] { 14, 15, 18, 19 })
            {
                objects.Add(Tree(20, y));
                objects.Add(Tree(21, y));
            }
        }

        byte holder = workers ? (byte)0 : Tzar2.Content.Compiled.MapObject.Nature;
        string kind = workers ? "euro.peon" : "euro.footman";
        foreach ((int x, int y) in new[] { (20, 16), (21, 16), (20, 17), (21, 17) })
        {
            objects.Add(Unit(kind, holder, x, y));
        }

        using var d = new SimDriver(TestMap(48, 34, [.. objects]), ruleset: Classic);
        if (workers)
        {
            EntityId[] trees = [.. OfType(d.World, "comm.tree")];
            foreach (EntityId peon in OfType(d.World, "euro.peon"))
            {
                (int X, int Y) at = TileOf(d, peon);
                EntityId tree = trees.OrderBy(t => Math.Abs(TileOf(d, t).X - at.X) + Math.Abs(TileOf(d, t).Y - at.Y)).ThenBy(t => t.Value).First();
                d.Order(0, CommandKind.Harvest, [peon], target: tree);
            }
        }

        EntityId knight = OfType(d.World, "euro.knight").Single();
        d.MoveToTile(0, [knight], 36, 16);
        int stood = 0;
        int longest = 0;
        (int X, int Y) last = TileOf(d, knight);
        int ticks = d.RunUntil(() =>
        {
            (int X, int Y) now = TileOf(d, knight);
            stood = now == last ? stood + 1 : 0;
            longest = Math.Max(longest, stood);
            last = now;
            return now == (36, 16) && !d.IsMoving(knight);
        }, 1200);

        // It goes round at once (the long way takes about 350 ticks); it stood about 20 s at the gap before, and as long as
        // the peasants worked there.
        Assert.True(ticks < 450, $"arrived after {ticks} ticks");
        Assert.True(longest < 40, $"stood {longest} ticks on one tile");
    }

    [Fact]
    [Trait("Rule", "WORLD-021")]
    public void The_back_ranks_go_round_the_front_ones_that_have_stopped()
    {
        // The eighth play-test: the back ranks did not go round the front ones when those stopped. A rank of friends
        // standing idle between a knight and its goal on open ground: the knight goes round it, as the original plans its
        // way again with every unit's tile a wall, and nobody in the rank has to step aside.
        var objects = new List<Tzar2.Content.Compiled.MapObject> { Structure("euro.castle", 0, 2, 2), Unit("euro.knight", 0, 10, 16) };
        for (int y = 10; y < 23; y++)
        {
            objects.Add(Unit("euro.footman", 0, 16, y));
        }

        using var d = new SimDriver(TestMap(40, 34, [.. objects]), ruleset: Classic);
        EntityId[] block = [.. OfType(d.World, "euro.footman")];
        var places = block.ToDictionary(e => e, e => TileOf(d, e));
        EntityId knight = OfType(d.World, "euro.knight").Single();
        d.MoveToTile(0, [knight], 24, 16);
        int stepped = 0;
        int ticks = d.RunUntil(() =>
        {
            stepped += block.Count(e => TileOf(d, e) != places[e]);
            return TileOf(d, knight) == (24, 16) && !d.IsMoving(knight);
        }, 1200);
        Assert.True(TileOf(d, knight) == (24, 16), $"the knight is at {TileOf(d, knight)} after {ticks} ticks");
        Assert.Equal(0, stepped); // nobody stepped aside on its way: it went round
    }
}
