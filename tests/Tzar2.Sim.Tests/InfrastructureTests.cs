using System;
using System.Linq;
using System.Threading;
using Tzar2.Content.Compiled;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Map;
using Tzar2.Sim.Scenarios;
using Tzar2.Sim.Snapshots;
using Tzar2.Sim.Systems;
using Tzar2.Sim.Threading;
using Xunit;

namespace Tzar2.Sim.Tests;

public class InfrastructureTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Worker_pool_runs_every_job_exactly_once(int workers)
    {
        using var pool = new DeterministicWorkerPool(workers);
        var job = new CountingJob(1000);
        for (int round = 0; round < 50; round++)
        {
            pool.Run(job, round == 0 ? 0 : 1000);
        }

        Assert.All(job.Counts, c => Assert.Equal(49, c));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Worker_pool_rethrows_the_lowest_failing_job_after_running_all(int workers)
    {
        using var pool = new DeterministicWorkerPool(workers);
        var job = new FailingJob(64, failing: [40, 7, 51]);
        var ex = Assert.Throws<InvalidOperationException>(() => pool.Run(job, 64));
        Assert.Equal("job 7", ex.Message);
        Assert.Equal(64, job.Executed);
        pool.Run(new CountingJob(4), 4); // still usable
    }

    [Fact]
    public void Scheduler_orders_by_phase_then_registration()
    {
        var scheduler = new SystemScheduler();
        scheduler.Add(new NamedSystem("cleanup", SimPhase.Cleanup));
        scheduler.Add(new NamedSystem("move-a", SimPhase.Movement));
        scheduler.Add(new NamedSystem("cmd", SimPhase.Commands));
        scheduler.Add(new NamedSystem("move-b", SimPhase.Movement));
        scheduler.Seal();
        Assert.Equal(["cmd", "move-a", "move-b", "cleanup"], scheduler.Ordered.Select(s => s.Name));
    }

    [Fact]
    [Trait("Rule", "WORLD-003")]
    public void Map_passability_combines_corners_cliffs_and_blockers()
    {
        // 2x2 tiles; corner (2,0) is water, tile (0,1) is a cliff.
        byte[] corners = new byte[9];
        corners[2] = SyntheticScenario.Water;
        var map = new MapData("t", 2, 2, corners, [], [0, 0, 1, 0]);
        var state = new MapState(map, TestWorlds.Ruleset);
        Assert.Equal(LocomotorMask.Land | LocomotorMask.Air, state.PassabilityAt(0, 0));
        Assert.Equal(LocomotorMask.Air, state.PassabilityAt(1, 0)); // mixed land/water corners: only what both allow
        Assert.Equal(LocomotorMask.Air, state.PassabilityAt(0, 1));
        Assert.Equal(LocomotorMask.None, state.PassabilityAt(2, 0));
        Assert.True(state.IsPassableAt(Locomotor.Foot, 100, 100));
        Assert.False(state.IsPassableAt(Locomotor.Foot, -1, 100));

        int version = state.ObstacleVersion;
        state.AddBlocker(1, 1);
        Assert.Equal(LocomotorMask.Air, state.PassabilityAt(1, 1));
        state.RemoveBlocker(1, 1);
        Assert.Equal(LocomotorMask.Land | LocomotorMask.Air, state.PassabilityAt(1, 1));
        Assert.Equal(version + 2, state.ObstacleVersion);
        Assert.Throws<InvalidOperationException>(() => state.RemoveBlocker(1, 1));
    }

    [Theory]
    [Trait("Rule", "WORLD-018")]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void Only_a_climber_crosses_a_cliff_band_and_only_one_or_two_tiles_thick(int thick, bool climbs)
    {
        // A cliff band from edge to edge across a 9 x 4 map, `thick` columns wide from column 3.
        byte[] cliffs = new byte[9 * 4];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 3; x < 3 + thick; x++)
            {
                cliffs[(y * 9) + x] = 1;
            }
        }

        var state = new MapState(new MapData("band", 9, 4, new byte[10 * 5], [], cliffs), TestWorlds.Ruleset);
        Assert.False(state.IsPassable(Locomotor.Foot, 3, 1));
        Assert.False(state.IsPassable(Locomotor.Mounted, 3, 1));
        Assert.Equal(climbs, state.IsPassable(Locomotor.Climber, 3, 1));
        Assert.True(state.IsPassable(Locomotor.Climber, 1, 1)); // everywhere else the climber walks as infantry
        Assert.Equal(LocomotorMask.Foot | LocomotorMask.Climber, Locomotor.Foot.Family()); // data's "foot" includes it
        Assert.True(Locomotor.Climber.IsA(Locomotor.Foot));
        Assert.False(Locomotor.Foot.IsA(Locomotor.Climber));
    }

    [Fact]
    public void Map_layers_scale_to_1024_tiles()
    {
        var map = new MapData("big", 1024, 1024, new byte[1025 * 1025], [], []);
        var state = new MapState(map, TestWorlds.Ruleset);
        Assert.Equal(1024 * 2048, state.WorldWidth);
        Assert.Equal(2048 * 2048, state.Occupancy.Length);
        Assert.Equal(state.Occupancy.Length - 1, state.CellIndexAt(state.WorldWidth - 1, state.WorldHeight - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapData("too big", 1025, 4, new byte[1026 * 5], [], []));
    }

    [Fact]
    public void Map_rejects_unknown_terrain()
    {
        byte[] corners = new byte[4];
        corners[3] = 9;
        var map = new MapData("bad", 1, 1, corners, [], []);
        Assert.Throws<ArgumentException>(() => new MapState(map, TestWorlds.Ruleset));
    }

    [Fact]
    public void Render_exchange_hands_the_reader_the_newest_complete_snapshot()
    {
        using var sim = new Simulation(TestWorlds.Ruleset, TestWorlds.Flat(), new SimulationConfig { PlayerCount = 2 });
        var exchange = new RenderSnapshotExchange();
        Assert.Null(exchange.AcquireLatest());
        sim.RenderOutput = exchange;
        sim.World.Spawn(0, 0, 500, 600);
        var empty = new CommandList();
        sim.Tick(empty);
        RenderSnapshot first = exchange.AcquireLatest()!;
        Assert.Equal(0, first.Tick.Value);
        Assert.Equal(500, first.Entities[0].X);
        Assert.Same(first, exchange.AcquireLatest());
        sim.Tick(empty);
        sim.Tick(empty);
        RenderSnapshot latest = exchange.AcquireLatest()!;
        Assert.Equal(2, latest.Tick.Value);
        Assert.Equal(sim.ComputeStateHash(), latest.StateHash);
    }

    [Fact]
    public void Render_exchange_is_consistent_under_a_concurrent_reader()
    {
        using var sim = new Simulation(TestWorlds.Ruleset, TestWorlds.Flat(), new SimulationConfig { PlayerCount = 2 });
        var exchange = new RenderSnapshotExchange();
        sim.RenderOutput = exchange;
        sim.World.Spawn(0, 0, 500, 600);
        int lastSeen = -1;
        bool ordered = true;
        using var done = new ManualResetEventSlim();
        var reader = new Thread(() =>
        {
            while (!done.IsSet)
            {
                RenderSnapshot? s = exchange.AcquireLatest();
                if (s is not null)
                {
                    ordered &= s.Tick.Value >= lastSeen && s.Entities.Length == 1;
                    lastSeen = s.Tick.Value;
                }
            }
        });
        reader.Start();
        var empty = new CommandList();
        for (int i = 0; i < 2000; i++)
        {
            sim.Tick(empty);
        }

        done.Set();
        reader.Join();
        Assert.True(ordered);
        Assert.Equal(1999, exchange.AcquireLatest()!.Tick.Value);
    }

    [Fact]
    public void Ruleset_and_map_hashes_are_stable_and_content_sensitive()
    {
        Assert.Equal(SyntheticScenario.CreateRuleset().ContentHash, SyntheticScenario.CreateRuleset().ContentHash);
        Assert.Equal(SyntheticScenario.CreateMap(32, 5).ContentHash, SyntheticScenario.CreateMap(32, 5).ContentHash);
        Assert.NotEqual(SyntheticScenario.CreateMap(32, 5).ContentHash, SyntheticScenario.CreateMap(32, 6).ContentHash);
    }

    private sealed class CountingJob(int size) : IParallelJob
    {
        public int[] Counts { get; } = new int[size];

        public void Execute(int jobIndex) => Interlocked.Increment(ref Counts[jobIndex]);
    }

    private sealed class FailingJob(int size, int[] failing) : IParallelJob
    {
        private int _executed;

        public int Executed => _executed;

        public void Execute(int jobIndex)
        {
            Interlocked.Increment(ref _executed);
            if (failing.Contains(jobIndex))
            {
                throw new InvalidOperationException($"job {jobIndex}");
            }

            _ = size;
        }
    }

    private sealed class NamedSystem(string name, SimPhase phase) : ISimSystem
    {
        public SimPhase Phase => phase;

        public string Name => name;

        public void Execute(SimContext context)
        {
        }
    }
}
