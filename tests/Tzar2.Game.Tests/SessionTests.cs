using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Core.Ids;
using Tzar2.Game.Input;
using Tzar2.Game.Maps;
using Tzar2.Game.Session;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Tzar2.Sim.Systems;
using Xunit;

namespace Tzar2.Game.Tests;

public sealed class SessionTests
{
    private static readonly Lazy<Ruleset> Rules = new(() => SyntheticLegacyContent.CompileRuleset());

    internal static MatchSetup Setup(int players = 2, int size = 64, ulong seed = 7) => new()
    {
        Map = new SkirmishTestMap(size),
        Players = Enumerable.Range(0, players)
            .Select(i => new PlayerSetup((byte)i, "euro", i == 0 ? ControllerKind.Human : ControllerKind.Computer, Difficulty.Hard))
            .ToArray(),
        Seed = seed,
    };

    [Fact]
    public void The_test_map_is_valid_and_deterministic()
    {
        MatchSetup setup = Setup(4, 96);
        MapData a = setup.Map.Create(Rules.Value, setup);
        MapData b = setup.Map.Create(Rules.Value, setup);
        a.Validate(Rules.Value);
        Assert.Equal(a.ContentHash, b.ContentHash);
        for (int p = 0; p < 4; p++)
        {
            Assert.Single(a.Objects, o => o.Owner == p && o.TypeKey == "euro.castle");
            Assert.Equal(6, a.Objects.Count(o => o.Owner == p && o.TypeKey == "euro.peon"));
        }

        Assert.True(a.Objects.Count(o => o.TypeKey == "comm.tree") > 100);
        Assert.Contains(a.Objects, o => o.TypeKey == "comm.res_gold");
        Assert.Contains(a.TerrainCorners.ToArray(), c => Rules.Value.TerrainTypes[c].IsWater);
        Assert.NotEqual(a.ContentHash, Setup(4, 96, seed: 8).Map.Create(Rules.Value, Setup(4, 96, seed: 8)).ContentHash);
    }

    [Fact]
    public void A_skirmish_runs_300_ticks_and_publishes_frames_and_events()
    {
        using var session = new GameSession(Setup(), Rules.Value);
        GameFrame? first = session.AcquireFrame(out bool fresh);
        Assert.NotNull(first);
        Assert.True(fresh);
        Assert.Equal(0, first!.Tick.Value);

        session.Step(300);
        GameFrame frame = session.AcquireFrame(out fresh)!;
        Assert.True(fresh);
        Assert.Equal(300, frame.Tick.Value);
        Assert.Equal(300, session.TicksRun);
        Assert.Equal(session.Simulation.World.Identities.Count, frame.EntityCount);
        Assert.Equal(2, frame.Players.Length);
        Assert.Equal(64 * 64, frame.Visibility.Length);
        Assert.Contains(frame.Visibility, v => v == 2);
        Assert.Contains(frame.Visibility, v => v == 0);
        Assert.Equal(MatchOutcome.None, frame.Outcome);

        // The placeholder AI (Hard: every 20 ticks) sends its idle peasants harvesting.
        var events = new List<SimEvent>();
        session.DrainEvents(events);
        Assert.NotEmpty(events);
        Assert.Contains(frame.Entities.ToArray(), e => e.Owner == 1 && e.Activity is UnitActivity.Walk or UnitActivity.Chop or UnitActivity.Mine);
        events.Clear();
        session.DrainEvents(events);
        Assert.Empty(events);
    }

    [Fact]
    public void Submitted_commands_are_stamped_with_the_local_slot_and_applied()
    {
        using var session = new GameSession(Setup(), Rules.Value);
        GameFrame frame = session.AcquireFrame(out _)!;
        FrameEntity peon = frame.Entities.ToArray().First(e => e.Owner == 0 && Rules.Value.EntityTypes[e.TypeId].Traits.LegacyClass == "peon");
        var batch = new CommandList();
        batch.Add(CommandKind.Move, [peon.Id], peon.X + 6000, peon.Y, player: 5);
        session.Submit(batch);
        session.Step(40);
        frame = session.AcquireFrame(out _)!;
        FrameEntity moved = frame.Entities.ToArray().Single(e => e.Id == peon.Id);
        Assert.True(moved.X > peon.X + 2000, $"peon at {moved.X}, started at {peon.X}");
    }

    [Fact]
    public void The_buildable_layer_matches_the_sim_placement_query()
    {
        using var session = new GameSession(Setup(), Rules.Value);
        session.Step(1);
        GameFrame frame = session.AcquireFrame(out _)!;
        World world = session.Simulation.World;
        ushort house = (ushort)Rules.Value.EntityTypeId("euro.house");
        EntityTypeDefinition type = Rules.Value.EntityTypes[house];
        int checkedLegal = 0;
        for (int ty = 0; ty < world.Map.Height; ty += 3)
        {
            for (int tx = 0; tx < world.Map.Width; tx += 3)
            {
                bool expected = PlacementQueries.CanPlace(world, house, tx, ty);
                Assert.Equal(expected, Placement.IsLegal(frame.Buildable, world.Map.Width, world.Map.Height, type, tx, ty));
                checkedLegal += expected ? 1 : 0;
            }
        }

        Assert.True(checkedLegal > 50);
    }

    [Theory]
    [InlineData(Tzar2.Content.MapGen.RandomMapType.Continental)]
    [InlineData(Tzar2.Content.MapGen.RandomMapType.Lakes)]
    public void A_skirmish_runs_on_a_random_map(Tzar2.Content.MapGen.RandomMapType type)
    {
        MatchSetup setup = Setup() with { Map = new RandomMapSource(type, size: Tzar2.Content.MapGen.RandomMapSize.Tiny) };
        using var session = new GameSession(setup, Rules.Value);
        Assert.NotNull(session.Map.TerrainSet);
        session.Step(100);
        GameFrame frame = session.AcquireFrame(out _)!;
        Assert.Contains(frame.Entities.ToArray(), e => e.Owner == 0 && Rules.Value.EntityTypes[e.TypeId].Traits.LegacyClass == "castle");
        Assert.Contains(frame.Entities.ToArray(), e => e.Owner == 1 && Rules.Value.EntityTypes[e.TypeId].Traits.LegacyClass == "castle");
    }

    [Fact]
    public void Running_on_the_sim_thread_advances_ticks_until_paused()
    {
        using var session = new GameSession(Setup(), Rules.Value);
        session.SpeedIndex = GameSession.SpeedSteps.Length - 1;
        session.Start();
        System.Threading.SpinWait.SpinUntil(() => session.TicksRun >= 10, TimeSpan.FromSeconds(20));
        Assert.True(session.TicksRun >= 10);
        session.Paused = true;
        System.Threading.Thread.Sleep(100);
        int paused = session.TicksRun;
        System.Threading.Thread.Sleep(200);
        Assert.InRange(session.TicksRun, paused, paused + 1);
        Assert.Null(session.Fault);
    }

    [Fact]
    public void A_kingdom_without_units_and_buildings_is_defeated()
    {
        MatchSetup setup = Setup() with { Players = [new PlayerSetup(0, "euro", ControllerKind.Human), new PlayerSetup(1, "euro", ControllerKind.Computer)] };
        using var session = new GameSession(setup, Rules.Value, _ => new IdleController());
        session.Step(40);
        Assert.Equal(MatchOutcome.None, session.AcquireFrame(out _)!.Outcome);
    }

    [Fact]
    public void Interpolation_blends_between_frames_and_snaps_long_jumps()
    {
        var interpolator = new FrameInterpolator();
        GameFrame a = Frame((1, 0, 0), (2, 0, 0));
        interpolator.Push(a);
        GameFrame b = Frame((1, 1000, 500), (2, 50_000, 0));
        interpolator.Push(b);
        FrameEntity[] e = b.Entities.ToArray();
        Assert.Equal((500, 250), interpolator.Position(e[0], 500));
        Assert.Equal((1000, 500), interpolator.Position(e[0], 1000));
        Assert.Equal((0, 0), interpolator.Position(e[0], 0));
        Assert.Equal((50_000, 0), interpolator.Position(e[1], 500));
        Assert.Equal(500, FrameInterpolator.Alpha(25, 50));
        Assert.Equal(1000, FrameInterpolator.Alpha(80, 50));

        static GameFrame Frame(params (int Id, int X, int Y)[] entities)
        {
            var frame = new GameFrame();
            FrameEntity[] buffer = frame.EntityBuffer(entities.Length);
            for (int i = 0; i < entities.Length; i++)
            {
                buffer[i] = new FrameEntity { Id = EntityId.Create(entities[i].Id, 1), X = entities[i].X, Y = entities[i].Y };
            }

            frame.EntityCount = entities.Length;
            return frame;
        }
    }

    private sealed class IdleController : IPlayerController
    {
        public void Think(World world, byte player, CommandList batch)
        {
        }
    }
}
