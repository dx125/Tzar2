using System;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>Placement, construction, cancellation, training queues, rally points, population (GAME-03/05; ADRs 0034, 0036, 0037).</summary>
public class BuildAndTrainTests
{
    private static int Food => Resource("food");

    private static int Stone => Resource("stone");

    private static int Wood => Resource("wood");

    [Fact]
    public void A_peasant_builds_barracks_which_train_a_footman_that_walks_to_the_rally_point()
    {
        using var d = new SimDriver(TestMap(40, 24, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 9, 10)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Build, [peon], 14 * Tile, 10 * Tile, arg0: Id("euro.barrack"));
        d.Step();
        EntityId barracks = OfType(d.World, "euro.barrack").Single();
        Assert.Equal(340, d.World.Players[0].Resources[Stone]); // paid at placement: 160 stone + 140 food
        Assert.Equal(360, d.World.Players[0].Resources[Food]);
        Assert.True(d.World.Constructions.Has(barracks));
        Assert.Equal(1, d.World.Healths.Get(barracks).Current);
        Assert.False(d.World.Map.IsPassable(Locomotor.Foot, 15, 11)); // the site blocks its tiles at once

        // Not trainable before it is complete.
        d.Order(0, CommandKind.Train, [barracks], arg0: Id("euro.footman"), arg1: 1);
        d.Step();
        Assert.Equal(CommandRejection.NoValidEntities, d.Rejections().Last());

        // TIME 42 x 1.25 s = 52.5 s of building for one builder (plus the walk).
        int ticks = d.RunUntil(() => d.Count(SimEventKind.ConstructionCompleted) == 1, 1500);
        Assert.InRange(ticks, 1050, 1350);
        Assert.False(d.World.Constructions.Has(barracks));
        Assert.Equal(1250, d.World.Healths.Get(barracks).Current);
        Assert.True(d.World.Productions.Has(barracks));
        d.Step();
        Assert.Equal(WorkerTask.None, d.World.Workers.Get(peon).Task);

        int rallyX = (30 * Tile) + (Tile / 2);
        int rallyY = (10 * Tile) + (Tile / 2);
        d.Order(0, CommandKind.SetRally, [barracks], rallyX, rallyY);
        d.Order(0, CommandKind.Train, [barracks], arg0: Id("euro.footman"), arg1: 1);
        d.Step();
        Assert.Equal(294, d.World.Players[0].Resources[Food]); // 66 food, paid when queued
        Assert.Equal(1, QueueRuns.Items(d.World.Productions.Get(barracks)));

        // TIME 11 x 1.25 s = 13.75 s of training, then the footman leaves toward the rally point and walks there.
        ticks = d.RunUntil(() => d.Count(SimEventKind.UnitTrained) == 1, 400);
        Assert.InRange(ticks, 273, 276);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        Assert.Equal(0, QueueRuns.Items(d.World.Productions.Get(barracks)));
        d.RunUntil(() => !d.IsMoving(footman), 600);
        Position at = d.PositionOf(footman);
        Assert.True(IntMath.DistanceSquared(at.X, at.Y, rallyX, rallyY) < (long)Tile * Tile, $"footman stopped at ({at.X}, {at.Y})");
        Assert.Equal(2, d.World.Players[0].Population);
    }

    [Fact]
    public void Several_builders_add_up_and_health_grows_with_progress()
    {
        using var d = new SimDriver(
            TestMap(32, 24, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 12, 8), Unit("euro.peon", 0, 12, 11), Unit("euro.peon", 0, 16, 8)),
            ruleset: Classic);
        EntityId[] peons = [.. OfType(d.World, "euro.peon")];
        d.Order(0, CommandKind.Build, [peons[0]], 13 * Tile, 9 * Tile, arg0: Id("euro.house"));
        d.Step();
        EntityId house = OfType(d.World, "euro.house").Single();
        d.Order(0, CommandKind.Build, peons.AsSpan(1), target: house);
        int previous = d.World.Healths.Get(house).Current;
        int ticks = d.RunUntil(() =>
        {
            int hp = d.World.Healths.Get(house).Current;
            Assert.True(hp >= previous);
            previous = hp;
            return d.Count(SimEventKind.ConstructionCompleted) == 1;
        }, 400);

        // 16 s of work shared by three builders (linear, UNVERIFIED UNK-18): well under the single-builder 320 ticks.
        Assert.InRange(ticks, 100, 200);
        Assert.Equal(420, d.World.Healths.Get(house).Current);
        ProductionSystem.UpdatePopulation(d.World);
        Assert.Equal(36, d.World.Players[0].PopulationCap); // castle 24 + house 12
    }

    [Fact]
    public void Cancelling_a_site_refunds_it_and_frees_its_tiles_and_cancelling_a_queue_item_refunds_it()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 10, 10)), ruleset: Classic);
        EntityId peon = OfType(d.World, "euro.peon").Single();
        EntityId castle = OfType(d.World, "euro.castle").Single();
        d.Order(0, CommandKind.Build, [peon], 14 * Tile, 4 * Tile, arg0: Id("euro.house"));
        d.Order(0, CommandKind.Train, [castle], arg0: Id("euro.peon"), arg1: 3);
        d.Step();
        Assert.Equal(445, d.World.Players[0].Resources[Wood]);
        Assert.Equal(365, d.World.Players[0].Resources[Food]);
        EntityId house = OfType(d.World, "euro.house").Single();
        int version = d.World.Map.ObstacleVersion;

        d.Order(0, CommandKind.Cancel, [house]);
        d.Order(0, CommandKind.Cancel, [castle], arg0: -1);
        d.Step();
        Assert.Equal(500, d.World.Players[0].Resources[Wood]);
        Assert.Equal(410, d.World.Players[0].Resources[Food]);
        Assert.False(d.World.Entities.IsAlive(house));
        Assert.True(d.World.Map.IsPassable(Locomotor.Foot, 14, 4));
        Assert.NotEqual(version, d.World.Map.ObstacleVersion);
        Assert.Equal(2, QueueRuns.Items(d.World.Productions.Get(castle)));
        Assert.Equal(1, d.Count(SimEventKind.ConstructionCancelled));
        d.Step();
        Assert.Equal(WorkerTask.None, d.World.Workers.Get(peon).Task);
    }

    [Fact]
    public void Placement_affordability_requirements_and_ownership_are_validated()
    {
        string[] rows =
        [
            "................................",
            "................................",
            "................................",
            "................................",
            "................................",
            "................................",
            "................................",
            "................................",
            ".....................~~~~.......",
            ".....................~~~~.......",
            "................................",
            "................................",
        ];
        using var d = new SimDriver(
            AsciiMap(rows, Structure("euro.castle", 0, 2, 2), Structure("euro.barrack", 0, 12, 2), Unit("euro.peon", 0, 10, 10), Unit("euro.footman", 0, 11, 10), Unit("euro.peon", 1, 20, 4)),
            ruleset: Classic,
            configure: c => c with { StartingResources = 0 }); // low: 210 food, 220 wood, 20 stone
        EntityId peon = OfType(d.World, "euro.peon", 0).Single();
        EntityId enemyPeon = OfType(d.World, "euro.peon", 1).Single();
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId barracks = OfType(d.World, "euro.barrack").Single();
        ushort house = Id("euro.house");

        d.Order(0, CommandKind.Build, [peon], 21 * Tile, 8 * Tile, arg0: house);         // on water
        d.Order(0, CommandKind.Build, [peon], 3 * Tile, 3 * Tile, arg0: house);          // on the castle
        d.Order(0, CommandKind.Build, [peon], 31 * Tile, 3 * Tile, arg0: house);         // footprint leaves the map
        d.Order(0, CommandKind.Build, [peon], 14 * Tile, 8 * Tile, arg0: Id("euro.barrack")); // needs stone
        d.Order(0, CommandKind.Build, [footman], 14 * Tile, 8 * Tile, arg0: house);      // not a builder
        d.Order(0, CommandKind.Build, [enemyPeon], 14 * Tile, 8 * Tile, arg0: house);    // not owned
        d.Order(0, CommandKind.Build, [peon], 14 * Tile, 8 * Tile, arg0: Id("euro.footman")); // not a building
        d.Order(0, CommandKind.Train, [barracks], arg0: Id("euro.knight"), arg1: 1);     // te_platearmor
        d.Order(0, CommandKind.Train, [barracks], arg0: Id("euro.peon"), arg1: 1);       // the castle trains peasants
        d.Order(0, CommandKind.Harvest, [footman], target: peon);                        // not a resource
        d.Order(0, CommandKind.Attack, [footman], target: peon);                         // own unit
        d.Order(1, CommandKind.Cancel, [barracks]);                                      // not owned
        d.Step();
        Assert.Equal(
            [
                CommandRejection.IllegalPlacement, CommandRejection.IllegalPlacement, CommandRejection.IllegalPlacement,
                CommandRejection.NotAffordable, CommandRejection.NoValidEntities, CommandRejection.NoValidEntities,
                CommandRejection.InvalidArgument, CommandRejection.RequirementNotMet, CommandRejection.NoValidEntities,
                CommandRejection.InvalidArgument, CommandRejection.InvalidArgument, CommandRejection.NoValidEntities,
            ],
            d.Rejections());
        Assert.Equal(220, d.World.Players[0].Resources[Wood]);
        Assert.Empty(OfType(d.World, "euro.house"));

        // Training more than the player can pay stops there (the queue holds any number: ADR 0180).
        d.Order(0, CommandKind.Train, [barracks], arg0: Id("euro.footman"), arg1: 16);
        d.Step();
        Assert.Equal(3, QueueRuns.Items(d.World.Productions.Get(barracks))); // 210 food / 66
        Assert.Equal(12, d.World.Players[0].Resources[Food]);
    }

    [Fact]
    public void A_full_population_stalls_production_until_there_is_room()
    {
        using var d = new SimDriver(
            TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 10, 10), Unit("euro.peon", 0, 11, 10)),
            ruleset: Classic,
            configure: c => c with { PopulationLimit = 2 });
        EntityId castle = OfType(d.World, "euro.castle").Single();
        Assert.Equal(2, d.World.Players[0].PopulationCap);
        d.Order(0, CommandKind.Train, [castle], arg0: Id("euro.peon"), arg1: 1);
        d.RunUntil(() => d.Count(SimEventKind.ProductionStalled) == 1, 400);
        Assert.Equal(1, d.Count(SimEventKind.ProductionStalled));
        Assert.Equal((byte)ProductionStall.Population, d.World.Productions.Get(castle).Stall);
        Assert.Equal(1, QueueRuns.Items(d.World.Productions.Get(castle)));

        // One peasant less: the finished one leaves.
        d.Order(0, CommandKind.Delete, [OfType(d.World, "euro.peon").First()]);
        d.RunUntil(() => d.Count(SimEventKind.UnitTrained) == 1, 10);
        Assert.Equal(1, d.Count(SimEventKind.UnitTrained));
        Assert.Equal(0, QueueRuns.Items(d.World.Productions.Get(castle)));
    }

    [Fact]
    public void Group_move_speed_is_a_ruleset_option()
    {
        foreach ((string yaml, bool slowest) in new[] { ("gameplay: { groupMoveSpeed: slowest }", true), ("gameplay: { groupMoveSpeed: own }", false) })
        {
            Ruleset ruleset = ClassicWith(yaml);
            using var d = new SimDriver(TestMap(32, 16, Unit("euro.footman", 0, 4, 4), Unit("euro.maceman", 0, 4, 6)), ruleset: ruleset);
            EntityId[] group = [.. OfType(d.World, "euro.footman"), .. OfType(d.World, "euro.maceman")];
            d.MoveToTile(0, group, 24, 5);
            d.Step();
            int maceSpeed = d.World.SpeedPerTick(Id(ruleset, "euro.maceman"));
            Assert.Equal(slowest ? maceSpeed : 0, d.MotionOf(group[0]).GroupSpeed);
        }
    }
}
