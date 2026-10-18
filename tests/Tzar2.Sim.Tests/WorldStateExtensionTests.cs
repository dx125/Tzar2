using System;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Serialization;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Snapshots;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// The hooks for in-sim controllers outside Tzar2.Sim (ADR 0051): extension state in snapshots and the full hash,
/// commands issued during a tick through the normal validation, and the read-only gameplay queries.
/// </summary>
public class WorldStateExtensionTests
{
    private static MapData Base() => TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Unit("euro.peon", 0, 9, 8), Unit("euro.footman", 1, 20, 8));

    // Counts ticks in its own state, as a controller system would.
    private sealed class Counter(string key) : ISimSystem, IWorldStateExtension
    {
        public long Value { get; set; }

        public string Key => key;

        public SimPhase Phase => SimPhase.AI;

        public string Name => "counter";

        public void Execute(SimContext context) => Value++;

        public void WriteTo(BinaryBufferWriter writer) => writer.WriteVarInt64(Value);

        public void ReadFrom(ReadOnlySpan<byte> data)
        {
            var reader = new BinaryBufferReader(data);
            Value = reader.ReadVarInt64();
            if (!reader.IsAtEnd || Value < 0)
            {
                throw new InvalidDataException("bad counter");
            }
        }
    }

    private static (Simulation Sim, Counter Counter) WithCounter(string key = "test.counter")
    {
        var sim = new Simulation(Classic, Base(), new SimulationConfig { PlayerCount = 2, MatchSeed = 3 });
        var counter = new Counter(key);
        sim.Systems.Add(counter);
        sim.World.Extensions.Register(counter);
        return (sim, counter);
    }

    [Fact]
    public void Extension_state_is_in_snapshots_and_the_full_hash()
    {
        (Simulation sim, Counter counter) = WithCounter();
        using (sim)
        {
            var none = new CommandList();
            for (int t = 0; t < 7; t++)
            {
                sim.Tick(none);
            }

            ulong hash = sim.ComputeFullHash();
            byte[] snapshot = sim.CaptureSnapshot();
            counter.Value++;
            Assert.NotEqual(hash, sim.ComputeFullHash());
            counter.Value--;

            using Simulation restored = Simulation.Restore(snapshot, Classic, sim.World.Map.Data);
            Assert.True(restored.World.Extensions.HasPending("test.counter"));
            Assert.Equal(hash, restored.ComputeFullHash()); // pending bytes hash like the registered state
            var again = new Counter("test.counter");
            restored.Systems.Add(again);
            restored.World.Extensions.Register(again);
            Assert.Equal(7, again.Value);
            Assert.Equal(snapshot, restored.CaptureSnapshot());
            restored.Tick(none);
            sim.Tick(none);
            Assert.Equal(sim.ComputeFullHash(), restored.ComputeFullHash());
        }
    }

    [Fact]
    public void An_unclaimed_block_is_carried_through_unchanged()
    {
        (Simulation sim, _) = WithCounter("z.last");
        using (sim)
        {
            sim.Tick(new CommandList());
            byte[] snapshot = sim.CaptureSnapshot();
            using Simulation restored = Simulation.Restore(snapshot, Classic, sim.World.Map.Data);
            Assert.Equal(snapshot, restored.CaptureSnapshot());
        }
    }

    [Fact]
    public void Worlds_without_extensions_hash_as_before()
    {
        using var plain = new Simulation(Classic, Base(), new SimulationConfig { PlayerCount = 2, MatchSeed = 3 });
        Assert.True(plain.World.Extensions.IsEmpty);
        (Simulation sim, Counter counter) = WithCounter();
        using (sim)
        {
            Assert.NotEqual(plain.ComputeFullHash(), sim.ComputeFullHash());
            Assert.Equal(plain.ComputeStateHash(), sim.ComputeStateHash()); // the per-tick hash covers the world only
            _ = counter;
        }
    }

    [Fact]
    public void Invalid_extension_state_is_rejected()
    {
        (Simulation sim, Counter counter) = WithCounter();
        using (sim)
        {
            counter.Value = 5;
            byte[] snapshot = sim.CaptureSnapshot();
            using Simulation restored = Simulation.Restore(snapshot, Classic, sim.World.Map.Data);
            var bad = new BadReader("test.counter");
            Assert.Throws<InvalidDataException>(() => restored.World.Extensions.Register(bad));
            Assert.Throws<ArgumentException>(() => sim.World.Extensions.Register(new Counter("test.counter")));
        }
    }

    [Fact]
    public void Corrupted_extension_blocks_are_rejected()
    {
        (Simulation sim, _) = WithCounter();
        using (sim)
        {
            byte[] snapshot = sim.CaptureSnapshot();

            // Every single-byte change inside the extension section must be caught (structure or hash check).
            int keyAt = IndexOf(snapshot, "test.counter"u8.ToArray());
            Assert.True(keyAt > 0);
            for (int i = keyAt - 2; i < snapshot.Length - 8; i++)
            {
                byte[] corrupt = (byte[])snapshot.Clone();
                corrupt[i] ^= 0x5A;
                Assert.Throws<InvalidDataException>(() => Simulation.Restore(corrupt, Classic, sim.World.Map.Data));
            }
        }
    }

    [Fact]
    public void In_tick_commands_use_the_normal_validation()
    {
        using var sim = new Simulation(Classic, Base(), new SimulationConfig { PlayerCount = 2, MatchSeed = 3 });
        EntityId peon = OfType(sim.World, "euro.peon").Single();
        EntityId footman = OfType(sim.World, "euro.footman").Single();
        var commands = new CommandList();
        commands.Add(CommandKind.Move, [peon], 12 * Tile, 8 * Tile, player: 0);
        commands.Add(CommandKind.Move, [footman], 12 * Tile, 8 * Tile, player: 0); // not player 0's unit
        sim.Systems.Add(new Issuer(commands));
        sim.Tick(new CommandList());
        Assert.Throws<InvalidOperationException>(() => CommandSystem.ExecuteInTick(sim.World, commands)); // between ticks
        Assert.Equal(Components.MotionState.Moving, sim.World.Motions.Get(peon).State);
        Assert.NotEqual(Components.MotionState.Moving, sim.World.Motions.Get(footman).State);
        Assert.Contains(sim.World.Events.Events.ToArray(), e => e.Kind == Events.SimEventKind.CommandRejected && e.X == 1);
    }

    [Fact]
    public void Gameplay_queries_answer_like_the_commands()
    {
        var world = new World(Classic, Base(), new SimulationConfig { PlayerCount = 2 });
        world.LoadMap();
        ushort house = Id("euro.house");
        Assert.True(PlacementQueries.CanPlace(world, house, 12, 2));
        Assert.False(PlacementQueries.CanPlace(world, house, 3, 3)); // on the castle
        Assert.False(PlacementQueries.CanPlace(world, house, 31, 15)); // leaves the map
        Assert.False(PlacementQueries.CanPlace(world, Id("euro.peon"), 12, 2)); // not a structure
        Assert.True(PlacementQueries.RequirementsMet(world, 0, house));
        Assert.False(PlacementQueries.RequirementsMet(world, 0, Id("euro.knight"))); // needs a technology
        Assert.False(GameplayQueries.MayHarvest(world, 0, EntityId.None));
    }

    private sealed class BadReader(string key) : IWorldStateExtension
    {
        public string Key => key;

        public void WriteTo(BinaryBufferWriter writer)
        {
        }

        public void ReadFrom(ReadOnlySpan<byte> data) => throw new InvalidDataException("no");
    }

    private sealed class Issuer(CommandList commands) : ISimSystem
    {
        public SimPhase Phase => SimPhase.AI;

        public string Name => "issuer";

        public void Execute(SimContext context) => CommandSystem.ExecuteInTick(context.World, commands);
    }

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        for (int i = 0; i + pattern.Length <= data.Length; i++)
        {
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern))
            {
                return i;
            }
        }

        return -1;
    }
}
