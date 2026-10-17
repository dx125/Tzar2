using System;
using System.IO;
using System.Linq;
using FsCheck.Xunit;
using Tzar2.Core.Ids;
using Tzar2.Core.Serialization;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Xunit;

namespace Tzar2.Sim.Tests.Commands;

public class CommandTests
{
    [Fact]
    public void Entity_sets_are_sorted_and_deduplicated()
    {
        var list = new CommandList();
        EntityId a = EntityId.Create(5, 1);
        EntityId b = EntityId.Create(2, 3);
        list.Add(CommandKind.Move, [a, b, EntityId.None, a, EntityId.Create(5, 2)], 10, 20);
        Assert.Equal([b, EntityId.Create(5, 2)], list.EntitiesOf(list[0]).ToArray()); // the newer handle of slot 5 wins
    }

    [Property(MaxTest = 500)]
    public bool Tick_sets_round_trip(int[] values, ushort tick)
    {
        values ??= [];
        CommandList original = BuildList(values, stampPlayers: true);
        var writer = new BinaryBufferWriter();
        CommandCodec.WriteTickSet(writer, new Tick(tick), original);
        var reader = new BinaryBufferReader(writer.WrittenSpan);
        var decoded = new CommandList();
        Tick decodedTick = CommandCodec.ReadTickSet(ref reader, decoded);
        return reader.IsAtEnd && decodedTick.Value == tick && decoded.ContentEquals(original);
    }

    [Property(MaxTest = 300)]
    public bool Batches_round_trip_without_players_and_stamping_sets_the_slot(int[] values, byte slot)
    {
        values ??= [];
        CommandList batch = BuildList(values, stampPlayers: false);
        var writer = new BinaryBufferWriter();
        CommandCodec.WriteBatch(writer, batch);
        var reader = new BinaryBufferReader(writer.WrittenSpan);
        var decoded = new CommandList();
        CommandCodec.ReadBatch(ref reader, decoded);
        var stamped = new CommandList();
        stamped.AppendStamped(slot, decoded);
        bool allStamped = stamped.Commands.ToArray().All(c => c.Player == slot);
        return reader.IsAtEnd && decoded.ContentEquals(batch) && allStamped && stamped.Count == batch.Count;
    }

    [Property(MaxTest = 500)]
    public void Decoding_garbage_never_throws_anything_but_InvalidDataException(byte[] data)
    {
        data ??= [];
        var list = new CommandList();
        try
        {
            var reader = new BinaryBufferReader(data);
            CommandCodec.ReadTickSet(ref reader, list);
        }
        catch (InvalidDataException)
        {
        }
    }

    [Fact]
    public void Decoding_rejects_out_of_bounds_counts()
    {
        var writer = new BinaryBufferWriter();
        writer.WriteVarUInt64(7); // tick
        writer.WriteVarUInt64(CommandList.MaxCommands + 1);
        Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new BinaryBufferReader(writer.WrittenSpan);
            CommandCodec.ReadTickSet(ref reader, new CommandList());
        });
    }

    [Fact]
    public void A_typical_move_order_is_compact()
    {
        var list = new CommandList();
        EntityId[] units = Enumerable.Range(1000, 12).Select(i => EntityId.Create(i, 1)).ToArray();
        list.Add(CommandKind.Move, units, 512 * 2048 - 1, 300_000);
        var writer = new BinaryBufferWriter();
        CommandCodec.WriteBatch(writer, list);
        Assert.True(writer.Length <= 48, $"{writer.Length} bytes");
    }

    [Fact]
    public void Execution_validates_ownership_and_reports_rejections()
    {
        using var sim = new Simulation(TestWorlds.Ruleset, TestWorlds.Flat(), new SimulationConfig { PlayerCount = 2 });
        World world = sim.World;
        EntityId mine = world.Spawn(0, 0, 1000, 1000);
        EntityId theirs = world.Spawn(0, 1, 2000, 2000);

        var batch = new CommandList();
        batch.Add(CommandKind.Move, [mine, theirs], 5000, 5000);
        batch.Add(CommandKind.Move, [theirs], 5000, 5000);
        batch.Add(CommandKind.Move, [mine], -1, 5000);
        batch.Add(CommandKind.DebugSpawn, default, 100, 100, arg0: 0, arg1: 1);
        batch.Add((CommandKind)0x0FFE, [mine]);
        var set = new CommandList();
        set.AppendStamped(0, batch);
        var foreign = new CommandList();
        foreign.Add(CommandKind.Delete, [mine]);
        set.AppendStamped(1, foreign);
        set.AppendStamped(9, foreign);

        var output = new Tzar2.Sim.Snapshots.RenderSnapshotExchange();
        sim.RenderOutput = output;
        sim.Tick(set);

        Assert.Equal(MotionState.Moving, world.Motions.Get(mine).State);
        Assert.Equal(MotionState.Idle, world.Motions.Get(theirs).State);
        Assert.True(world.Entities.IsAlive(mine));
        SimEvent[] rejections = output.AcquireLatest()!.Events.ToArray().Where(e => e.Kind == SimEventKind.CommandRejected).ToArray();
        Assert.Equal(
            [
                (1, CommandRejection.NoValidEntities),
                (2, CommandRejection.OutOfMap),
                (3, CommandRejection.CheatsDisabled),
                (4, CommandRejection.UnknownKind),
                (5, CommandRejection.NoValidEntities),
                (6, CommandRejection.InvalidPlayer),
            ],
            rejections.Select(e => (e.X, (CommandRejection)e.Arg)).ToArray());
    }

    [Fact]
    public void Move_stop_delete_and_spawn_work()
    {
        using var sim = new Simulation(TestWorlds.Ruleset, TestWorlds.Flat(), new SimulationConfig { PlayerCount = 2, CheatsAllowed = true });
        World world = sim.World;
        EntityId unit = world.Spawn(1, 1, 1000, 1000);
        var batch = new CommandList();
        batch.Add(CommandKind.Move, [unit], 1000 + 20_000, 1000);
        var set = new CommandList();
        set.AppendStamped(1, batch);
        sim.Tick(set);
        int speed = world.SpeedPerTick(1);
        Assert.Equal(1000 + speed, world.Positions.Get(unit).X);
        Assert.Equal(0, world.Identities.Get(unit).Facing);

        int ticks = 0;
        var empty = new CommandList();
        while (world.Motions.Get(unit).State == MotionState.Moving && ticks++ < 100)
        {
            sim.Tick(empty);
        }

        Assert.Equal(new Position(21_000, 1000), world.Positions.Get(unit));

        batch.Clear();
        batch.Add(CommandKind.DebugSpawn, default, 3000, 3000, arg0: 2, arg1: 3);
        batch.Add(CommandKind.Delete, [unit]);
        set.Clear();
        set.AppendStamped(1, batch);
        sim.Tick(set);
        Assert.False(world.Entities.IsAlive(unit));
        Assert.Equal(3, world.Identities.Count);
        Assert.Equal(3, world.Map.Occupancy.ToArray().Sum(c => c));
    }

    private static CommandList BuildList(int[] values, bool stampPlayers)
    {
        var list = new CommandList();
        CommandKind[] kinds = [CommandKind.Move, CommandKind.Stop, CommandKind.Delete, CommandKind.DebugSpawn, (CommandKind)77];
        for (int i = 0; i + 6 < values.Length; i += 7)
        {
            int n = Math.Abs(values[i] % 40);
            EntityId[] entities = Enumerable.Range(0, n)
                .Select(k => EntityId.Create(Math.Abs((values[i + 1] * (k + 1)) % (EntityId.MaxIndex + 1)), 1 + Math.Abs((values[i + 2] + k) % EntityId.MaxGeneration)))
                .ToArray();
            list.Add(
                kinds[Math.Abs(values[i + 3] % kinds.Length)],
                entities,
                values[i + 4],
                values[i + 5],
                values[i + 6] % 3 == 0 ? EntityId.None : EntityId.FromValue((uint)values[i + 6] | 1u << 20),
                arg0: values[i + 1],
                arg1: -values[i + 2],
                flags: (CommandFlags)Math.Abs(values[i] % 8),
                player: stampPlayers ? (byte)(values[i + 3] & 0xFF) : (byte)0);
        }

        return list;
    }
}
