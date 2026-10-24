using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// Workers stand one to a tile next to what they work at (WORLD-019; the owner's fifth play-test; ReTzar 0x451cc0,
/// 0x451b40): each takes a free tile of the footprint grown by one, so at most eight peasants cut at a tree and a group of
/// builders spreads around its site instead of pushing at the near side.
/// </summary>
public class WorkSpotTests
{
    private const int Tile = SimDriver.Tile;

    [Fact]
    [Trait("Rule", "WORLD-019")]
    public void At_most_eight_peasants_cut_at_one_tree_all_around_it_nobody_shoves_them_and_the_others_take_the_next_tree()
    {
        var objects = new List<Tzar2.Content.Compiled.MapObject> { Structure("euro.castle", 0, 2, 9), Tree(20, 12), Tree(26, 12) };
        for (int i = 0; i < 10; i++)
        {
            objects.Add(Unit("euro.peon", 0, 12 + (i % 2), 9 + (i / 2)));
        }

        using var d = new SimDriver(TestMap(40, 24, [.. objects]), ruleset: Classic);
        EntityId[] peons = [.. OfType(d.World, "euro.peon")];
        EntityId tree = OfType(d.World, "comm.tree").OrderBy(t => d.PositionOf(t).X).First();
        EntityId next = OfType(d.World, "comm.tree").OrderBy(t => d.PositionOf(t).X).Last();
        d.Order(0, CommandKind.Harvest, peons, target: tree);

        int most = 0;
        bool beyond = false;
        var standing = new Dictionary<EntityId, Position>();
        d.RunUntil(() =>
        {
            List<EntityId> cutting = [.. peons.Where(p => Gathers(d, p, tree))];
            Assert.True(cutting.Count <= 8, $"{cutting.Count} peasants cut at one tree");
            foreach (EntityId p in cutting)
            {
                // Next to the tree, and held where it started cutting while the others come; the two walking past to
                // the next tree nudge it a quarter of a tile at most, as any unit does.
                Position at = d.PositionOf(p);
                int gapX = Math.Max(0, Math.Max((20 * Tile) - at.X, at.X - (21 * Tile)));
                int gapY = Math.Max(0, Math.Max((12 * Tile) - at.Y, at.Y - (13 * Tile)));
                Assert.True(Math.Max(gapX, gapY) <= Tile + (Tile / 4), $"a cutter stands {Math.Max(gapX, gapY) / 64} px from the tree");
                if (standing.TryGetValue(p, out Position first))
                {
                    Assert.True(Math.Abs(first.X - at.X) + Math.Abs(first.Y - at.Y) <= 8 * 64, $"a cutter was shoved {(Math.Abs(first.X - at.X) + Math.Abs(first.Y - at.Y)) / 64.0:F1} px at tick {d.World.Tick.Value}");
                }
                else
                {
                    standing[p] = at;
                }

                beyond |= at.X >= 21 * Tile; // on the tree's far side from where they came
            }

            foreach (EntityId p in standing.Keys.Where(p => !cutting.Contains(p)).ToList())
            {
                standing.Remove(p);
            }

            most = Math.Max(most, cutting.Count);
            return false;
        }, 400);

        Assert.True(most >= 6, $"only {most} peasants cut at once");
        Assert.True(beyond, "they cut from the near side only");
        Assert.Contains(peons, p => Gathers(d, p, next)); // the rest went to the next tree
    }

    [Fact]
    [Trait("Rule", "WORLD-019")]
    [Trait("Rule", "BLD-010")]
    public void Builders_sent_together_take_the_free_places_around_the_site()
    {
        var objects = new List<Tzar2.Content.Compiled.MapObject> { Structure("euro.castle", 0, 2, 2) };
        for (int i = 0; i < 8; i++)
        {
            objects.Add(Unit("euro.peon", 0, 8 + (i % 2), 8 + (i / 2)));
        }

        using var d = new SimDriver(TestMap(40, 24, [.. objects]), ruleset: Classic);
        EntityId[] peons = [.. OfType(d.World, "euro.peon")];
        d.Order(0, CommandKind.Build, [peons[0]], 20 * Tile, 10 * Tile, arg0: Id("euro.house"));
        d.Step();
        EntityId house = OfType(d.World, "euro.house").Single();
        d.Order(0, CommandKind.Build, peons, target: house);
        var building = new Dictionary<EntityId, Position>();
        d.RunUntil(() =>
        {
            foreach (EntityId p in peons.Where(p => d.World.Workers.Get(p).Phase == WorkerPhase.Building && !d.IsMoving(p)))
            {
                // Builders at work stay where they stand while the others of the site come (the fifth play-test).
                Position at = d.PositionOf(p);
                if (!building.TryAdd(p, at))
                {
                    Assert.True(Math.Abs(building[p].X - at.X) + Math.Abs(building[p].Y - at.Y) <= 64, "a builder was shoved");
                }
            }

            return building.Count == peons.Length;
        }, 600);

        HashSet<(int, int)> tiles = [.. peons.Select(p => TileOf(d, p))];
        Assert.Equal(peons.Length, tiles.Count); // one to a tile, none pushed onto another's
        (int fx, int fy, int fw, int fh) = d.World.FootprintRect(house);
        Assert.All(tiles, t => Assert.True(t.Item1 >= fx - 1 && t.Item1 <= fx + fw && t.Item2 >= fy - 1 && t.Item2 <= fy + fh, $"a builder stands at {t}, away from the site"));
        Assert.Contains(tiles, t => t.Item1 >= fx + fw / 2); // around it, not only on the side they came from
        d.RunUntil(() => d.Count(SimEventKind.ConstructionCompleted) == 1, 400);
        Assert.Equal(1, d.Count(SimEventKind.ConstructionCompleted));
    }

    private static bool Gathers(SimDriver d, EntityId peon, EntityId node)
    {
        Worker w = d.World.Workers.Get(peon);
        return w.Task == WorkerTask.Harvest && w.Phase == WorkerPhase.Gathering && w.Target == node.Value;
    }

    private static (int, int) TileOf(SimDriver d, EntityId e) => (d.PositionOf(e).X / Tile, d.PositionOf(e).Y / Tile);
}
