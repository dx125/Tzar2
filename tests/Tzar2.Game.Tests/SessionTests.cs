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
        // GAME-12: the frame shows what the local kingdom sees (units in sight, structures as last seen).
        Assert.True(frame.EntityCount <= session.Simulation.World.Identities.Count + 64);
        Assert.All(frame.Entities.ToArray(), e => Assert.True(e.Owner == 0 || e.Remembered || session.Simulation.World.Visibility.CanSee(session.Simulation.World, 0, e.Id)));
        Assert.Equal(2, frame.Players.Length);
        Assert.Equal(64 * 64, frame.Visibility.Length);
        Assert.Contains(frame.Visibility, v => v == 2);
        Assert.Contains(frame.Visibility, v => v == 0);
        Assert.Equal(MatchOutcome.None, frame.Outcome);

        // The placeholder AI (Hard: every 20 ticks) sends its idle peasants harvesting.
        var events = new List<SimEvent>();
        session.DrainEvents(events);
        Assert.NotEmpty(events);
        Assert.Contains(session.Simulation.World.Motions.Components.ToArray(), m => m.State == Tzar2.Sim.Components.MotionState.Moving);
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
        using var session = new GameSession(setup, Rules.Value, fog: false);
        Assert.NotNull(session.Map.TerrainSet);
        session.Step(100);
        GameFrame frame = session.AcquireFrame(out _)!;
        Assert.Contains(frame.Entities.ToArray(), e => e.Owner == 0 && Rules.Value.EntityTypes[e.TypeId].Traits.LegacyClass == "castle");
        Assert.Contains(frame.Entities.ToArray(), e => e.Owner == 1 && Rules.Value.EntityTypes[e.TypeId].Traits.LegacyClass == "castle");
    }

    [Fact]
    public void A_right_click_on_an_enemy_unit_ends_in_a_kill()
    {
        // The client's order path end to end: frame entities -> EntityInfo -> OrderResolver -> session -> sim.
        using var session = new GameSession(Setup(), Rules.Value, _ => new IdleController(), fog: false);
        World world = session.Simulation.World;
        FrameEntity[] all = session.AcquireFrame(out _)!.Entities.ToArray();
        EntityInfo Info(FrameEntity e)
        {
            EntityTypeDefinition t = Rules.Value.EntityTypes[e.TypeId];
            return new EntityInfo(e.Id, e.TypeId, e.Owner, t.Traits.Category, t.Traits.Harvester is null && t.Traits.Armament is not null, 0, e.Health, e.MaxHealth,
                t.Traits.Harvester is not null, t.Traits.IsBuilder, t.Traits.Armament is not null, t.Traits.Producer is not null, t.Traits.ResourceNode is not null, false, t.IsMobile);
        }

        EntityInfo[] army = all.Where(e => e.Owner == 0 && Rules.Value.EntityTypes[e.TypeId].Traits.LegacyClass == "footman").Select(Info).ToArray();
        FrameEntity victim = all.First(e => e.Owner == 1 && Rules.Value.EntityTypes[e.TypeId].Traits.LegacyClass == "peon");
        Assert.NotEmpty(army);
        var events = new List<SimEvent>();
        int kills = 0;
        for (int round = 0; round < 40 && kills == 0; round++)
        {
            var batch = new CommandList();
            foreach (PlannedCommand c in OrderResolver.RightClick(army, Info(victim), victim.X, victim.Y, KeyMods.None, 0))
            {
                Assert.Equal(CommandKind.Attack, c.Kind);
                c.AddTo(batch);
            }

            session.Submit(batch);
            session.Step(100);
            session.DrainEvents(events);
            kills += events.Count(e => e.Kind == SimEventKind.Killed && e.Entity == victim.Id);
            events.Clear();
        }

        Assert.Equal(1, kills);
        Assert.False(world.Entities.IsAlive(victim.Id));
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
    public void A_kingdom_without_units_and_buildings_is_defeated_and_its_controller_stops()
    {
        // Three kingdoms: 1 deletes everything it owns; 0 (human) and 2 stay. 1 is defeated, the match goes on.
        var suicide = new SuicideController();
        MatchSetup setup = Setup(players: 3);
        using var session = new GameSession(setup, Rules.Value, p => p.Slot == 1 ? suicide : new IdleController());
        session.Step(40);
        GameFrame frame = session.AcquireFrame(out _)!;
        Assert.True(frame.Players[1].Defeated);
        Assert.False(frame.Players[0].Defeated);
        Assert.False(frame.Players[2].Defeated);
        Assert.Equal(MatchOutcome.None, frame.Outcome);
        int calls = suicide.Calls;
        session.Step(40);
        Assert.Equal(calls, suicide.Calls);

        // The human deletes everything: defeat.
        var batch = new CommandList();
        batch.Add(CommandKind.Delete, Own(session, 0), flags: CommandFlags.Queue); // Shift + Del: the castle too
        session.Submit(batch);
        session.Step(40);
        Assert.Equal(MatchOutcome.Defeat, session.AcquireFrame(out _)!.Outcome);
    }

    [Fact]
    public void The_last_kingdom_standing_wins()
    {
        using var session = new GameSession(Setup(), Rules.Value, _ => new SuicideController());
        session.Step(40);
        GameFrame frame = session.AcquireFrame(out _)!;
        Assert.Equal(MatchOutcome.Victory, frame.Outcome);
        Assert.True(frame.Players[1].Defeated);
    }

    [Fact]
    public void Controllers_only_read_the_world()
    {
        // The bundled computer player must change the world only through commands (replays and lockstep see those).
        using var session = new GameSession(Setup(), Rules.Value, _ => new IdleController());
        var ai = new PlaceholderAi(Difficulty.Hard);
        var batch = new CommandList();
        for (int tick = 0; tick < 2000; tick++)
        {
            ulong before = session.Simulation.ComputeFullHash();
            batch.Clear();
            ai.Think(session.Simulation.World, 0, batch);
            Assert.Equal(before, session.Simulation.ComputeFullHash());
            if (batch.Count > 0)
            {
                session.Submit(batch);
            }

            session.Step(1);
        }
    }

    [Fact]
    public void Frames_cross_threads_without_tearing()
    {
        // Writer fills the back buffer with one value per frame; the reader must never see a mix.
        var exchange = new FrameExchange();
        using var stop = new System.Threading.CancellationTokenSource();
        var writer = new System.Threading.Thread(() =>
        {
            for (int n = 1; !stop.IsCancellationRequested; n++)
            {
                GameFrame back = exchange.Back;
                FrameEntity[] buffer = back.EntityBuffer(64);
                for (int i = 0; i < 64; i++)
                {
                    buffer[i] = new FrameEntity { X = n, Y = n };
                }

                back.EntityCount = 64;
                back.Tick = new Tick(n);
                exchange.Publish();
            }
        });
        writer.Start();
        int seen = 0, last = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 500)
        {
            GameFrame? f = exchange.AcquireLatest(out bool fresh);
            if (f is null || !fresh)
            {
                continue;
            }

            int value = f.Entities[0].X;
            foreach (FrameEntity e in f.Entities)
            {
                Assert.Equal(value, e.X);
            }

            Assert.Equal(value, f.Tick.Value);
            Assert.True(value > last);
            last = value;
            seen++;
        }

        stop.Cancel();
        writer.Join();
        Assert.True(seen > 10);
    }

    private static EntityId[] Own(GameSession session, byte owner)
        => session.AcquireFrame(out _)!.Entities.ToArray().Where(e => e.Owner == owner).Select(e => e.Id).ToArray();

    private sealed class SuicideController : IPlayerController
    {
        public int Calls { get; private set; }

        public void Think(World world, byte player, CommandList batch)
        {
            Calls++;
            var own = new List<EntityId>();
            ReadOnlySpan<EntityId> ids = world.Identities.Entities;
            for (int i = 0; i < ids.Length; i++)
            {
                if (world.Identities.Components[i].Owner == player)
                {
                    own.Add(ids[i]);
                }
            }

            if (own.Count > 0)
            {
                batch.Add(CommandKind.Delete, own.ToArray(), flags: CommandFlags.Queue);
            }
        }
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
        Assert.Equal(1, interpolator.TickSpan);
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

    internal sealed class IdleController : IPlayerController
    {
        public void Think(World world, byte player, CommandList batch)
        {
        }
    }
}
