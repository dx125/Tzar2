using System.Collections.Generic;
using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>The rules the owner's third play-test asked for (2026-10-20; ADR 0180).</summary>
public class PlayTestRulesTests
{
    private const int Tile = MapState.UnitsPerTile;

    [Fact]
    [Trait("Rule", "PROD-001")]
    public void A_queue_takes_any_number_of_any_units_one_entry_per_type_and_cancels_one_of_an_item()
    {
        // No population room: nothing leaves, so the queue only grows.
        using var d = new SimDriver(TestMap(40, 24, Structure("euro.castle", 0, 2, 2), Structure("euro.barrack", 0, 14, 4)), ruleset: Classic, configure: c => c with { PopulationLimit = 0 });
        for (int r = 0; r < Classic.ResourceTypes.Count; r++)
        {
            d.World.Players[0].Resources[r] = 1_000_000;
        }

        for (int t = 0; t < Classic.Technologies.Count; t++)
        {
            d.World.Players[0].AddTechnology(t);
        }

        d.World.Stats.Rebuild(0, d.World.Players[0]);

        EntityId barracks = OfType(d.World, "euro.barrack").Single();
        ushort footman = Id("euro.footman"), pikeman = Id("euro.pikeman");
        d.Order(0, CommandKind.Train, [barracks], arg0: footman, arg1: 24);
        d.Order(0, CommandKind.Train, [barracks], arg0: pikeman, arg1: 3);
        d.Order(0, CommandKind.Train, [barracks], arg0: footman, arg1: 24);
        d.Order(0, CommandKind.Train, [barracks], arg0: footman, arg1: 24);
        d.Step();
        Assert.Equal(string.Empty, string.Join(",", d.Rejections()));
        Production q = d.World.Productions.Get(barracks);
        Assert.Equal(2, q.Count);
        Assert.Equal((footman, (ushort)72), (q.Queue[0].Item, q.Queue[0].Count));
        Assert.Equal((pikeman, (ushort)3), (q.Queue[1].Item, q.Queue[1].Count));
        Assert.Equal(75, QueueRuns.Items(q));

        // A right click on the pikeman's button: one pikeman leaves the queue, refunded.
        int food = Resource("food");
        int before = d.World.Players[0].Resources[food];
        d.Order(0, CommandKind.Cancel, [barracks], arg0: -1, arg1: pikeman + 1);
        d.Step();
        q = d.World.Productions.Get(barracks);
        Assert.Equal((ushort)2, q.Queue[1].Count);
        Assert.Equal((ushort)72, q.Queue[0].Count);
        Assert.True(d.World.Players[0].Resources[food] > before);
    }

    [Fact]
    [Trait("Rule", "BLD-001")]
    public void Peasants_of_another_civilization_help_build_a_site()
    {
        using var d = new SimDriver(TestMap(40, 24, Structure("euro.castle", 0, 2, 2), Unit("arab.peon", 0, 12, 10), Unit("arab.peon", 0, 13, 10)), ruleset: Classic);
        EntityId site = d.World.PlaceStructure(Id("euro.house"), 0, 16, 8, underConstruction: true);
        EntityId[] helpers = [.. OfType(d.World, "arab.peon")];
        d.Order(0, CommandKind.Build, helpers, target: site);
        d.Step();
        Assert.Empty(d.Rejections());
        Assert.All(helpers, h => Assert.Equal(WorkerTask.Build, d.World.Workers.Get(h).Task));
        d.RunUntil(() => d.Count(SimEventKind.ConstructionCompleted) == 1, 4000);
        Assert.False(d.World.Constructions.Has(site));
    }

    [Fact]
    [Trait("Rule", "PROD-004")]
    [Trait("Rule", "PROD-005")]
    public void Units_appear_around_the_building_on_free_tiles_with_a_way_out()
    {
        // The castle's south side (tiles 2..6 of row 7) is a pocket shut in by cliffs: the original could put units there.
        string[] rows =
        [
            "....................",
            "....................",
            "....................",
            "....................",
            "....................",
            "....................",
            ".#.....#............",
            ".#.....#............",
            ".#######............",
            "....................",
            "....................",
            "....................",
        ];
        using var d = new SimDriver(AsciiMap(rows, Structure("euro.castle", 0, 2, 2)), ruleset: Classic);
        EntityId castle = OfType(d.World, "euro.castle").Single();
        d.Order(0, CommandKind.Train, [castle], arg0: Id("euro.peon"), arg1: 4);
        d.RunUntil(() => d.Count(SimEventKind.UnitTrained) == 4, 4000);
        var tiles = new HashSet<(int, int)>();
        foreach (EntityId peon in OfType(d.World, "euro.peon"))
        {
            Position p = d.World.Positions.Get(peon);
            (int tx, int ty) = (p.X / Tile, p.Y / Tile);
            Assert.False(ty == 7 && tx is >= 2 and <= 6, $"a peasant appeared in the pocket at {tx},{ty}");
            Assert.True(tiles.Add((tx, ty)), $"two peasants appeared on tile {tx},{ty}");
        }
    }

    [Fact]
    [Trait("Rule", "ECO-001")]
    [Trait("Rule", "ECO-003")]
    public void A_load_appears_at_once_but_milk_fills_the_pail()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Tree(12, 4), Unit("euro.peon", 0, 10, 4), Unit("comm.cow", 0, 12, 10), Unit("euro.peon", 0, 10, 10)), ruleset: Classic);
        EntityId[] peons = [.. OfType(d.World, "euro.peon")];
        EntityId chopper = peons.First(p => d.World.Positions.Get(p).Y < 8 * Tile), milker = peons.First(p => p != chopper);
        d.Order(0, CommandKind.Harvest, [chopper], target: OfType(d.World, "comm.tree").Single());
        d.Order(0, CommandKind.Harvest, [milker], target: OfType(d.World, "comm.cow").Single());
        var wood = new SortedSet<int>();
        var milk = new SortedSet<int>();
        for (int t = 0; t < 600; t++)
        {
            d.Step();
            wood.Add(d.World.Workers.Get(chopper).CarryAmount);
            milk.Add(d.World.Workers.Get(milker).CarryAmount);
        }

        int capacity = Classic.EntityTypes[Id("euro.peon")].Traits.Harvester!.Capacity;
        Assert.Equal([0, capacity], wood.Where(n => n is 0 || n == capacity));
        Assert.DoesNotContain(wood, n => n > 0 && n < capacity);
        Assert.Contains(milk, n => n > 0 && n < capacity);
    }

    [Fact]
    [Trait("Rule", "ECO-004")]
    public void After_a_tree_falls_the_next_one_is_on_the_way_to_the_drop_off_and_reachable()
    {
        // Tree A falls first. B touches A but lies away from the castle; C is two tiles from A toward the castle; D, right
        // next to A, stands in a cliff ring nobody can enter.
        string[] rows =
        [
            "..............................",
            "..............................",
            "..............................",
            "..............................",
            "..............................",
            "..............................",
            "..............................",
            "..............................",
            "..............................",
            "................###...........",
            "................#.#...........",
            "................###...........",
            "..............................",
        ];
        using var d = new SimDriver(AsciiMap(rows, Structure("euro.castle", 0, 2, 2), Tree(16, 6), Tree(17, 6), Tree(14, 6), Tree(17, 10), Unit("euro.peon", 0, 15, 7)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        EntityId[] trees = [.. OfType(d.World, "comm.tree")];
        EntityId TreeAt(int tx, int ty) => trees.Single(t => d.World.Entities.IsAlive(t) && d.World.Positions.Get(t) is var p && p.X / Tile == tx && p.Y / Tile == ty);
        EntityId a = TreeAt(16, 6), c = TreeAt(14, 6);
        d.World.ResourceStocks.Get(a).Amount = 1;
        d.Order(0, CommandKind.Harvest, [peon], target: a);
        d.RunUntil(() => !d.World.Entities.IsAlive(a) && d.World.Workers.Get(peon).Target != a.Value && d.World.Workers.Get(peon).Phase == WorkerPhase.ToNode, 3000);
        Assert.Equal(c.Value, d.World.Workers.Get(peon).Target);

        // With C gone too, the walled tree D (closer to A than any other but B) is never chosen.
        d.World.ResourceStocks.Get(c).Amount = 0;
        d.World.ResourceStocks.Get(TreeAt(17, 6)).Amount = 0;
        d.Step();
        d.RunUntil(() => d.World.Workers.Get(peon).Task != WorkerTask.Harvest || d.World.Workers.Get(peon).Target != c.Value, 200);
        Assert.NotEqual(TreeAt(17, 10).Value, d.World.Workers.Get(peon).Target);
    }

    [Fact]
    [Trait("Rule", "ECO-005")]
    public void An_idle_peasant_finds_work_by_itself_after_a_while()
    {
        using var d = new SimDriver(TestMap(40, 24, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 14, 10), Unit("euro.peon", 0, 30, 12), Tree(32, 12)), ruleset: Classic);
        EntityId[] peons = [.. OfType(d.World, "euro.peon")];
        EntityId builder = peons.First(p => d.World.Positions.Get(p).X < 20 * Tile), chopper = peons.First(p => p != builder);
        EntityId site = d.World.PlaceStructure(Id("euro.house"), 0, 16, 9, underConstruction: true);
        int after = Classic.Gameplay.IdleWorkAfterMs / d.World.Config.TickLengthMilliseconds;
        d.RunUntil(() => false, after - 2);
        Assert.Equal(WorkerTask.None, d.World.Workers.Get(builder).Task);
        d.RunUntil(() => false, 4);
        Assert.Equal((WorkerTask.Build, site.Value), (d.World.Workers.Get(builder).Task, d.World.Workers.Get(builder).Target));

        // The other one has no site near it, but a tree and the castle that takes the wood (within the radius of 12 tiles
        // from the tree's side only for the search; the castle is far: it still chops and carries the wood home).
        Assert.Equal(WorkerTask.Harvest, d.World.Workers.Get(chopper).Task);
    }
}
