using System;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Snapshots;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>Regression tests for the PR #14 review (adversarial cases R1-R11, blockers B1-B3, should-fix S1-S7).</summary>
public class GameplayReviewRegressionTests
{
    [Fact]
    [Trait("Rule", "BLD-015")]
    public void R1_building_on_top_of_units_pushes_them_out()
    {
        using var d = new SimDriver(TestMap(40, 24, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 9, 10), Unit("euro.peon", 1, 15, 11), Unit("euro.peon", 0, 16, 12)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon", 0).First();
        EntityId enemy = OfType(d.World, "euro.peon", 1).Single(); // peasants do not start fights
        EntityId other = OfType(d.World, "euro.peon", 0).Last();
        d.Order(0, CommandKind.Build, [peon], 14 * Tile, 10 * Tile, arg0: Id("euro.barrack"));
        d.Step();
        Assert.Empty(d.Rejections());
        Assert.Single(OfType(d.World, "euro.barrack"));
        d.RunUntil(() => false, 200);
        foreach (EntityId e in new[] { enemy, other })
        {
            Position p = d.PositionOf(e);
            Assert.True(d.World.Map.IsPassable(Locomotor.Foot, p.X / Tile, p.Y / Tile), $"{e} is still inside the barracks");
        }
    }

    [Fact]
    public void R2_another_kingdoms_field_cannot_be_harvested()
    {
        using var d = new SimDriver(TestMap(40, 24, Structure("euro.castle", 0, 2, 2), Structure("comm.field", 0, 12, 4), Structure("euro.castle", 1, 20, 2), Unit("euro.peon", 1, 16, 5)), ruleset: Classic);
        EntityId field = OfType(d.World, "comm.field").Single();
        EntityId peon = OfType(d.World, "euro.peon", 1).Single();
        int food = Resource("food");
        d.Order(1, CommandKind.Harvest, [peon], target: field);
        d.RunUntil(() => false, 600);
        Assert.Equal([CommandRejection.InvalidArgument], d.Rejections());
        Assert.Equal(500, d.World.Players[1].Resources[food]);

        // Its owner may.
        using var own = new SimDriver(TestMap(40, 24, Structure("euro.castle", 0, 2, 2), Structure("comm.field", 0, 9, 4), Unit("euro.peon", 0, 8, 8)), ruleset: Classic);
        own.Order(0, CommandKind.Harvest, [OfType(own.World, "euro.peon").Single()], target: OfType(own.World, "comm.field").Single());
        own.RunUntil(() => own.Count(SimEventKind.ResourceDelivered) > 0, 1200);
        Assert.True(own.World.Players[0].Resources[food] > 500);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void R3_a_symmetric_duel_is_a_mutual_kill_whatever_the_entity_order(bool p0First)
    {
        // Review S2: hits landing in the same tick are applied together, so the lower index does not always win.
        MapObject a = Unit("euro.footman", 0, 10, 10);
        MapObject b = new("euro.footman", 1, (10 * Tile) + (Tile / 2) + 900, (10 * Tile) + (Tile / 2));
        using var d = new SimDriver(TestMap(30, 20, p0First ? [a, b] : [b, a]), ruleset: Classic);
        EntityId f0 = OfType(d.World, "euro.footman", 0).Single();
        EntityId f1 = OfType(d.World, "euro.footman", 1).Single();
        d.Order(0, CommandKind.Attack, [f0], target: f1);
        d.Order(1, CommandKind.Attack, [f1], target: f0);
        d.RunUntil(() => d.Count(SimEventKind.Killed) > 0, 2000);
        Assert.False(d.World.Entities.IsAlive(f0));
        Assert.False(d.World.Entities.IsAlive(f1));
        int[] killTicks = [.. d.Events.Where(e => e.Event.Kind == SimEventKind.Killed).Select(e => e.Tick)];
        Assert.Equal(2, killTicks.Length);
        Assert.Equal(killTicks[0], killTicks[1]);
    }

    [Fact]
    public void R4_cancelling_one_site_three_times_refunds_once()
    {
        using var d = new SimDriver(TestMap(40, 24, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 9, 10)), ruleset: Classic);
        d.Order(0, CommandKind.Build, [OfType(d.World, "euro.peon").Single()], 14 * Tile, 10 * Tile, arg0: Id("euro.barrack"));
        d.Step();
        EntityId site = OfType(d.World, "euro.barrack").Single();
        d.Order(0, CommandKind.Cancel, [site, site, site]);
        d.Step();
        Assert.Equal(500, d.World.Players[0].Resources[Resource("stone")]);
    }

    [Fact]
    public void R5_R6_gameplay_state_owned_by_nature_is_rejected_on_restore()
    {
        // Review B1: Nature has no stockpile; a crafted snapshot with Nature-owned workers or queues must not load.
        foreach (string corruption in new[] { "worker", "queue" })
        {
            MapData map = TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 7, 4));
            var world = new World(Classic, map, new SimulationConfig { PlayerCount = 2 });
            world.LoadMap();
            EntityId peon = OfType(world, "euro.peon").Single();
            EntityId castle = OfType(world, "euro.castle").Single();
            if (corruption == "worker")
            {
                world.Identities.Get(peon).Owner = MapObject.Nature;
                ref Worker w = ref world.Workers.Get(peon);
                w.Task = WorkerTask.Harvest;
                w.Phase = WorkerPhase.ToDropOff;
                w.CarryAmount = 5;
                w.CarryResource = 0;
                w.DropTarget = castle.Value;
            }
            else
            {
                world.Identities.Get(castle).Owner = MapObject.Nature;
                world.Productions.Get(castle).Count = 1;
                world.Productions.Get(castle).Queue[0] = new QueueRun { Item = Id("euro.peon"), Count = 1 };
                world.Productions.Get(castle).ProgressMs = 999_999;
            }

            byte[] snapshot = WorldSnapshot.Capture(world);
            Assert.Throws<InvalidDataException>(() => Simulation.Restore(snapshot, Classic, map));
        }

        // Maps cannot give Nature workers, producers or drop-offs either.
        Assert.Throws<ArgumentException>(() => new Simulation(Classic, TestMap(32, 16, Unit("euro.peon", MapObject.Nature, 7, 4)), new SimulationConfig()));
        Assert.Throws<ArgumentException>(() => new Simulation(Classic, TestMap(32, 16, Structure("euro.castle", MapObject.Nature, 2, 2)), new SimulationConfig()));
    }

    [Fact]
    public void R8_cancelling_a_stalled_first_item_refunds_and_clears_the_stall()
    {
        using var d = new SimDriver(TestMap(40, 24, Structure("euro.castle", 0, 2, 2)), ruleset: Classic, configure: c => c with { PopulationLimit = 0 });
        EntityId castle = OfType(d.World, "euro.castle").Single();
        int food = Resource("food");
        d.Order(0, CommandKind.Train, [castle], arg0: Id("euro.peon"), arg1: 3);
        d.RunUntil(() => false, 400);
        Assert.Equal(3, QueueRuns.Items(d.World.Productions.Get(castle)));
        Assert.Equal((byte)ProductionStall.Population, d.World.Productions.Get(castle).Stall);
        Assert.Equal(500 - (3 * 45), d.World.Players[0].Resources[food]);

        // One of the entry's waiting items goes first (ADR 0180): the finished one still waits for room.
        d.Order(0, CommandKind.Cancel, [castle], arg0: 0);
        d.Step();
        Assert.Equal(2, QueueRuns.Items(d.World.Productions.Get(castle)));
        Assert.Equal((byte)ProductionStall.Population, d.World.Productions.Get(castle).Stall);
        Assert.Equal(500 - (2 * 45), d.World.Players[0].Resources[food]);

        // The last of the entry clears the stall.
        d.Order(0, CommandKind.Cancel, [castle], arg0: 0);
        d.Order(0, CommandKind.Cancel, [castle], arg0: 0);
        d.Step();
        Assert.Equal(0, d.World.Productions.Get(castle).Count);
        Assert.Equal(0, d.World.Productions.Get(castle).Stall);
        Assert.Equal(500, d.World.Players[0].Resources[food]);
    }

    [Fact]
    public void R11_nature_units_are_hostile_and_can_be_fought()
    {
        // Review S4: a wild armed unit attacks every kingdom, so every kingdom may attack it.
        using var d = new SimDriver(TestMap(30, 20, Unit("euro.footman", MapObject.Nature, 10, 10), Unit("euro.footman", 0, 11, 10)), ruleset: Classic);
        EntityId wild = OfType(d.World, "euro.footman", MapObject.Nature).Single();
        EntityId mine = OfType(d.World, "euro.footman", 0).Single();
        d.Order(0, CommandKind.Attack, [mine], target: wild);
        d.RunUntil(() => d.Count(SimEventKind.Killed) > 0, 2000);
        Assert.Empty(d.Rejections());
        Assert.True(d.Events.Any(e => e.Event.Kind == SimEventKind.Damaged && e.Event.Entity == wild), "the wild unit was never hit");
        Assert.False(d.World.Entities.IsAlive(wild) && d.World.Entities.IsAlive(mine));
    }

    [Fact]
    public void S6_out_of_range_combat_timers_and_worker_phases_are_rejected_on_restore()
    {
        foreach (string corruption in new[] { "cooldown", "chase", "phase" })
        {
            MapData map = TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 7, 4), Unit("euro.footman", 1, 20, 8));
            var world = new World(Classic, map, new SimulationConfig { PlayerCount = 2 });
            world.LoadMap();
            EntityId footman = OfType(world, "euro.footman").Single();
            EntityId peon = OfType(world, "euro.peon").Single();
            switch (corruption)
            {
                case "cooldown": world.Combats.Get(footman).CooldownMs = int.MinValue; break;
                case "chase": world.Combats.Get(footman).ChaseTick = 1_000_000; break;
                case "phase":
                    world.Workers.Get(peon).Task = WorkerTask.Harvest;
                    world.Workers.Get(peon).Phase = WorkerPhase.Building;
                    break;
            }

            byte[] snapshot = WorldSnapshot.Capture(world);
            Assert.Throws<InvalidDataException>(() => Simulation.Restore(snapshot, Classic, map));
        }
    }

    [Fact]
    [Trait("Rule", "WAR-008")]
    public void N1_an_armed_building_only_fights_once_completed()
    {
        Ruleset ruleset = ClassicWith("euro.house: { Armament: { attack: 4, range: 5, rate: 3 } }");
        using var d = new SimDriver(TestMap(32, 16, Unit("euro.peon", 0, 6, 6)), ruleset: ruleset);
        d.Order(0, CommandKind.Build, [OfType(d.World, "euro.peon").Single()], 8 * Tile, 8 * Tile, arg0: Id(ruleset, "euro.house"));
        d.Step();
        EntityId site = OfType(d.World, "euro.house").Single();
        Assert.False(d.World.Combats.Has(site));
        d.RunUntil(() => d.Count(SimEventKind.ConstructionCompleted) == 1, 1000);
        Assert.True(d.World.Combats.Has(site));
    }
}
