using System;
using System.IO;
using Tzar2.Core.Ids;
using Tzar2.Core.Serialization;

namespace Tzar2.Sim.Commands;

/// <summary>
/// Binary encoding of commands for lockstep and replays (REQ-SIM-11, ADR 0016). Little-endian LEB128 varints via
/// <see cref="BinaryBufferWriter"/>; decoding is bounded and rejects malformed input with
/// <see cref="InvalidDataException"/> (REQ-NET-11).
/// </summary>
/// <remarks>
/// Command: <c>kind</c> varuint, <c>flags</c> byte, [<c>player</c> byte, tick sets only], <c>target</c> varuint
/// (packed <see cref="EntityId.Value"/>), <c>x y arg0 arg1</c> zigzag varints, <c>n</c> varuint, then the entity set
/// delta-encoded: per entity the index delta to the previous entity (the first relative to 0) as varuint and the
/// generation as varuint. Sets are sorted ascending without duplicates, so deltas are non-negative and a zero delta
/// is invalid after the first entity. A typical 12-unit move order is ~40 bytes.
/// </remarks>
public static class CommandCodec
{
    /// <summary>Writes a client batch (players are not sent: the relay stamps them).</summary>
    public static void WriteBatch(BinaryBufferWriter writer, CommandList batch) => WriteList(writer, batch, withPlayers: false);

    /// <summary>Reads a client batch into <paramref name="batch"/> (cleared first).</summary>
    public static void ReadBatch(ref BinaryBufferReader reader, CommandList batch) => ReadList(ref reader, batch, withPlayers: false);

    /// <summary>Writes the closed command set of <paramref name="tick"/> (players included).</summary>
    public static void WriteTickSet(BinaryBufferWriter writer, Tick tick, CommandList commands)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteVarUInt64((ulong)tick.Value);
        WriteList(writer, commands, withPlayers: true);
    }

    /// <summary>Reads a tick command set into <paramref name="commands"/> (cleared first) and returns its tick.</summary>
    public static Tick ReadTickSet(ref BinaryBufferReader reader, CommandList commands)
    {
        ulong tick = reader.ReadVarUInt64();
        if (tick > int.MaxValue)
        {
            throw new InvalidDataException($"Tick {tick} is out of range.");
        }

        ReadList(ref reader, commands, withPlayers: true);
        return new Tick((int)tick);
    }

    private static void WriteList(BinaryBufferWriter writer, CommandList list, bool withPlayers)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(list);
        writer.WriteVarUInt64((ulong)list.Count);
        foreach (ref readonly Command c in list.Commands)
        {
            writer.WriteVarUInt64((ushort)c.Kind);
            writer.WriteByte((byte)c.Flags);
            if (withPlayers)
            {
                writer.WriteByte(c.Player);
            }

            writer.WriteVarUInt64(c.Target.Value);
            writer.WriteVarInt64(c.X);
            writer.WriteVarInt64(c.Y);
            writer.WriteVarInt64(c.Arg0);
            writer.WriteVarInt64(c.Arg1);
            ReadOnlySpan<EntityId> entities = list.EntitiesOf(c);
            writer.WriteVarUInt64((ulong)entities.Length);
            int previous = 0;
            foreach (EntityId e in entities)
            {
                writer.WriteVarUInt64((ulong)(e.Index - previous));
                writer.WriteVarUInt64((ulong)e.Generation);
                previous = e.Index;
            }
        }
    }

    private static void ReadList(ref BinaryBufferReader reader, CommandList list, bool withPlayers)
    {
        ArgumentNullException.ThrowIfNull(list);
        list.Clear();
        int count = ReadBounded(ref reader, CommandList.MaxCommands, "command count");
        Span<EntityId> scratch = stackalloc EntityId[256];
        EntityId[]? rented = null;
        for (int i = 0; i < count; i++)
        {
            ulong kind = reader.ReadVarUInt64();
            if (kind is 0 or > ushort.MaxValue)
            {
                throw new InvalidDataException($"Invalid command kind {kind}.");
            }

            byte flags = reader.ReadByte();
            if ((flags & ~(int)(CommandFlags.Queue | CommandFlags.Ctrl | CommandFlags.Alt)) != 0)
            {
                throw new InvalidDataException($"Invalid command flags {flags}.");
            }

            byte player = withPlayers ? reader.ReadByte() : (byte)0;
            ulong target = reader.ReadVarUInt64();
            if (target > uint.MaxValue)
            {
                throw new InvalidDataException("Invalid target entity.");
            }

            int x = ReadInt32(ref reader);
            int y = ReadInt32(ref reader);
            int arg0 = ReadInt32(ref reader);
            int arg1 = ReadInt32(ref reader);
            int n = ReadBounded(ref reader, CommandList.MaxEntitiesPerCommand, "entity count");
            Span<EntityId> entities = scratch;
            if (n > scratch.Length)
            {
                rented ??= new EntityId[CommandList.MaxEntitiesPerCommand];
                entities = rented;
            }

            entities = entities[..n];
            int index = 0;
            for (int k = 0; k < n; k++)
            {
                ulong delta = reader.ReadVarUInt64();
                ulong generation = reader.ReadVarUInt64();
                if ((k > 0 && delta == 0) || delta > EntityId.MaxIndex || (ulong)index + delta > EntityId.MaxIndex
                    || generation is 0 or > EntityId.MaxGeneration)
                {
                    throw new InvalidDataException("Invalid entity set encoding.");
                }

                index += (int)delta;
                entities[k] = EntityId.Create(index, (int)generation);
            }

            var command = new Command((CommandKind)kind, player, (CommandFlags)flags, EntityId.FromValue((uint)target), x, y, arg0, arg1, 0, n);
            list.AddDecoded(command, entities);
        }
    }

    private static int ReadBounded(ref BinaryBufferReader reader, int max, string what)
    {
        ulong value = reader.ReadVarUInt64();
        if (value > (ulong)max)
        {
            throw new InvalidDataException($"The {what} {value} exceeds {max}.");
        }

        return (int)value;
    }

    private static int ReadInt32(ref BinaryBufferReader reader)
    {
        long value = reader.ReadVarInt64();
        if (value is < int.MinValue or > int.MaxValue)
        {
            throw new InvalidDataException($"Value {value} is out of the 32-bit range.");
        }

        return (int)value;
    }
}
