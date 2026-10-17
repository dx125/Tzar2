using System;
using System.IO;
using Tzar2.Core.Serialization;
using Tzar2.Spikes.S3.Sim;

namespace Tzar2.Spikes.S3.Lockstep;

/// <summary>First byte of every message.</summary>
public enum MessageType : byte
{
    /// <summary>Client to relay, as LiteNetLib connect data: create, join or rejoin a lobby.</summary>
    Hello = 1,

    /// <summary>Relay to client: slot, session token and match parameters.</summary>
    Welcome = 2,

    /// <summary>Client to relay: loaded; hash of the initial state (loading barrier, REQ-NET-05).</summary>
    Ready = 3,

    /// <summary>Relay to clients: every client is ready with the same initial hash; ticks start closing.</summary>
    Start = 4,

    /// <summary>
    /// Client to relay, once per tick: commands for a target tick, ack, state hashes (empty = heartbeat). In redundant
    /// mode one message repeats every batch the relay has not acknowledged yet.
    /// </summary>
    Batch = 5,

    /// <summary>Relay to clients: one or more closed ticks with their ordered commands ("tick T complete").</summary>
    Closures = 6,

    /// <summary>Relay to one client: a batch missed its tick and was moved to the next open tick.</summary>
    Late = 7,

    /// <summary>Relay to clients: state hashes disagree (REQ-NET-04).</summary>
    Desync = 8,

    /// <summary>Client to relay: RTT probe.</summary>
    Ping = 9,

    /// <summary>Relay to client: RTT probe echo.</summary>
    Pong = 10,

    /// <summary>Relay to a healthy client: send a snapshot for a rejoining peer (REQ-NET-06).</summary>
    SnapshotRequest = 11,

    /// <summary>Healthy client to relay: snapshot at its last executed tick.</summary>
    Snapshot = 12,

    /// <summary>Relay to a rejoining client: snapshot plus every closed tick after it.</summary>
    Resume = 13,

    /// <summary>Relay to clients: the match ends after the given tick.</summary>
    End = 14,
}

/// <summary>What a <see cref="Hello"/> asks for.</summary>
public enum HelloKind : byte
{
    /// <summary>Create a lobby; the relay answers with its join code.</summary>
    Create = 1,

    /// <summary>Join a lobby by code.</summary>
    Join = 2,

    /// <summary>Re-enter a running match in a known slot with its session token.</summary>
    Rejoin = 3,
}

/// <summary>How the relay delivers closed ticks.</summary>
public enum ClosureDelivery : byte
{
    /// <summary>One closure per tick on LiteNetLib ReliableOrdered.</summary>
    Reliable = 1,

    /// <summary>Unreliable packets carrying every closure the client has not acknowledged yet.</summary>
    Redundant = 2,
}

/// <summary>Connect request payload.</summary>
public sealed record Hello(HelloKind Kind, string Code, byte PlayerCount, byte Slot, ulong Token, string Name);

/// <summary>Accepted into a lobby.</summary>
public sealed record Welcome(
    string Code, byte Slot, ulong Token, byte PlayerCount, ulong Seed, int TickMicroseconds, int HashInterval, ClosureDelivery Delivery, int EndTick);

/// <summary>A state hash of one executed tick.</summary>
public readonly record struct TickHash(int Tick, ulong Hash);

/// <summary>Per-tick upstream message.</summary>
public sealed record Batch(uint Seq, int TargetTick, int AckTick, SimCommand[] Commands, TickHash[] Hashes);

/// <summary>A closed tick: the relay-ordered commands every peer executes for it.</summary>
public sealed record TickClosure(int Tick, SimCommand[] Commands);

/// <summary>Desync report: the first tick whose hashes disagree, and the slots outside the majority.</summary>
public sealed record DesyncReport(int Tick, ulong MajorityHash, byte[] OddSlots);

/// <summary>Snapshot sent by a healthy peer, or forwarded to a rejoining one with the backlog.</summary>
public sealed record SnapshotMessage(int Tick, ulong Hash, byte[] Data, TickClosure[] Backlog);

/// <summary>Encoding and bounded decoding of all messages (REQ-NET-11: every count and size is limited).</summary>
public static class Wire
{
    /// <summary>Protocol magic + version, checked in <see cref="Hello"/>.</summary>
    public const uint Magic = 0x54325333; // "T2S3"

    /// <summary>Most commands one batch may carry.</summary>
    public const int MaxCommandsPerBatch = 64;

    /// <summary>Most commands one closed tick may carry.</summary>
    public const int MaxCommandsPerClosure = 1024;

    /// <summary>Most hashes one batch may carry.</summary>
    public const int MaxHashesPerBatch = 64;

    /// <summary>Most batches in one message (redundant upstream).</summary>
    public const int MaxBatchesPerMessage = 32;

    /// <summary>Largest snapshot accepted.</summary>
    public const int MaxSnapshotBytes = 1 << 20;

    /// <summary>Most closures in one message (a rejoin backlog).</summary>
    public const int MaxClosuresPerMessage = 100_000;

    private const int MaxCodeBytes = 16;
    private const int MaxNameBytes = 64;

    /// <summary>Writes a hello.</summary>
    public static void Write(BinaryBufferWriter w, Hello m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        w.WriteByte((byte)MessageType.Hello);
        w.WriteUInt32(Magic);
        w.WriteByte((byte)m.Kind);
        w.WriteString(m.Code);
        w.WriteByte(m.PlayerCount);
        w.WriteByte(m.Slot);
        w.WriteUInt64(m.Token);
        w.WriteString(m.Name);
    }

    /// <summary>Reads a hello (type byte included).</summary>
    public static Hello ReadHello(ReadOnlySpan<byte> data)
    {
        var r = new BinaryBufferReader(data);
        Expect(ref r, MessageType.Hello);
        if (r.ReadUInt32() != Magic)
        {
            throw new InvalidDataException("Protocol magic/version mismatch.");
        }

        var kind = (HelloKind)r.ReadByte();
        string code = r.ReadString(MaxCodeBytes);
        byte players = r.ReadByte();
        byte slot = r.ReadByte();
        ulong token = r.ReadUInt64();
        string name = r.ReadString(MaxNameBytes);
        return new Hello(kind, code, players, slot, token, name);
    }

    /// <summary>Writes a welcome.</summary>
    public static void Write(BinaryBufferWriter w, Welcome m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        w.WriteByte((byte)MessageType.Welcome);
        w.WriteString(m.Code);
        w.WriteByte(m.Slot);
        w.WriteUInt64(m.Token);
        w.WriteByte(m.PlayerCount);
        w.WriteUInt64(m.Seed);
        w.WriteInt32(m.TickMicroseconds);
        w.WriteInt32(m.HashInterval);
        w.WriteByte((byte)m.Delivery);
        w.WriteInt32(m.EndTick);
    }

    /// <summary>Reads a welcome (after the type byte).</summary>
    public static Welcome ReadWelcome(ref BinaryBufferReader r) => new(
        r.ReadString(MaxCodeBytes), r.ReadByte(), r.ReadUInt64(), r.ReadByte(), r.ReadUInt64(), r.ReadInt32(), r.ReadInt32(),
        (ClosureDelivery)r.ReadByte(), r.ReadInt32());

    /// <summary>Writes a ready message.</summary>
    public static void WriteReady(BinaryBufferWriter w, ulong initialHash)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)MessageType.Ready);
        w.WriteUInt64(initialHash);
    }

    /// <summary>Writes a message that is only its type byte (Start, SnapshotRequest).</summary>
    public static void WriteEmpty(BinaryBufferWriter w, MessageType type)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)type);
    }

    /// <summary>Writes a message with one integer (End: last tick; Ping/Pong: timestamp).</summary>
    public static void WriteValue(BinaryBufferWriter w, MessageType type, long value)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)type);
        w.WriteVarInt64(value);
    }

    /// <summary>Writes a batches message header; follow with <paramref name="count"/> calls to <see cref="WriteBatch"/>.</summary>
    public static void WriteBatchesHeader(BinaryBufferWriter w, int count)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)MessageType.Batch);
        w.WriteVarUInt64((ulong)count);
    }

    /// <summary>Writes one batch (without a type byte).</summary>
    public static void WriteBatch(BinaryBufferWriter w, Batch m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        w.WriteVarUInt64(m.Seq);
        w.WriteVarInt64(m.TargetTick);
        w.WriteVarInt64(m.AckTick);
        w.WriteVarUInt64((ulong)m.Commands.Length);
        foreach (SimCommand c in m.Commands)
        {
            WriteCommand(w, c, includePlayer: false);
        }

        w.WriteVarUInt64((ulong)m.Hashes.Length);
        foreach (TickHash h in m.Hashes)
        {
            w.WriteVarInt64(h.Tick);
            w.WriteUInt64(h.Hash);
        }
    }

    /// <summary>Reads a batches message (after the type byte); the relay stamps <paramref name="player"/> on every command.</summary>
    public static Batch[] ReadBatches(ref BinaryBufferReader r, byte player)
    {
        var batches = new Batch[Count(ref r, MaxBatchesPerMessage)];
        for (int i = 0; i < batches.Length; i++)
        {
            batches[i] = ReadBatch(ref r, player);
        }

        return batches;
    }

    private static Batch ReadBatch(ref BinaryBufferReader r, byte player)
    {
        uint seq = checked((uint)r.ReadVarUInt64());
        int target = checked((int)r.ReadVarInt64());
        int ack = checked((int)r.ReadVarInt64());
        var commands = new SimCommand[Count(ref r, MaxCommandsPerBatch)];
        for (int i = 0; i < commands.Length; i++)
        {
            commands[i] = ReadCommand(ref r, player);
        }

        var hashes = new TickHash[Count(ref r, MaxHashesPerBatch)];
        for (int i = 0; i < hashes.Length; i++)
        {
            hashes[i] = new TickHash(checked((int)r.ReadVarInt64()), r.ReadUInt64());
        }

        return new Batch(seq, target, ack, commands, hashes);
    }

    /// <summary>
    /// Writes a closures message header: the highest batch sequence number the relay has processed from this client
    /// (acknowledges redundant upstream batches), then <paramref name="count"/> calls to <see cref="WriteClosure"/>.
    /// </summary>
    public static void WriteClosuresHeader(BinaryBufferWriter w, uint batchAck, int count)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)MessageType.Closures);
        w.WriteVarUInt64(batchAck);
        w.WriteVarUInt64((ulong)count);
    }

    /// <summary>Writes one closure.</summary>
    public static void WriteClosure(BinaryBufferWriter w, TickClosure c)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(c);
        w.WriteVarInt64(c.Tick);
        w.WriteVarUInt64((ulong)c.Commands.Length);
        foreach (SimCommand command in c.Commands)
        {
            WriteCommand(w, command, includePlayer: true);
        }
    }

    /// <summary>Reads a closures message (after the type byte).</summary>
    public static TickClosure[] ReadClosures(ref BinaryBufferReader r, out uint batchAck)
    {
        batchAck = checked((uint)r.ReadVarUInt64());
        var closures = new TickClosure[Count(ref r, MaxClosuresPerMessage)];
        for (int i = 0; i < closures.Length; i++)
        {
            closures[i] = ReadClosure(ref r);
        }

        return closures;
    }

    /// <summary>Writes a late notice.</summary>
    public static void WriteLate(BinaryBufferWriter w, uint seq, int targetTick, int assignedTick)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)MessageType.Late);
        w.WriteVarUInt64(seq);
        w.WriteVarInt64(targetTick);
        w.WriteVarInt64(assignedTick);
    }

    /// <summary>Writes a desync report.</summary>
    public static void Write(BinaryBufferWriter w, DesyncReport m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        w.WriteByte((byte)MessageType.Desync);
        w.WriteVarInt64(m.Tick);
        w.WriteUInt64(m.MajorityHash);
        w.WriteVarUInt64((ulong)m.OddSlots.Length);
        w.WriteBytes(m.OddSlots);
    }

    /// <summary>Reads a desync report (after the type byte).</summary>
    public static DesyncReport ReadDesync(ref BinaryBufferReader r)
    {
        int tick = checked((int)r.ReadVarInt64());
        ulong majority = r.ReadUInt64();
        byte[] odd = r.ReadBytes(Count(ref r, byte.MaxValue)).ToArray();
        return new DesyncReport(tick, majority, odd);
    }

    /// <summary>Writes a snapshot (healthy peer to relay) or a resume (relay to rejoining peer).</summary>
    public static void Write(BinaryBufferWriter w, MessageType type, SnapshotMessage m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        w.WriteByte((byte)type);
        w.WriteVarInt64(m.Tick);
        w.WriteUInt64(m.Hash);
        w.WriteVarUInt64((ulong)m.Data.Length);
        w.WriteBytes(m.Data);
        w.WriteVarUInt64((ulong)m.Backlog.Length);
        foreach (TickClosure c in m.Backlog)
        {
            WriteClosure(w, c);
        }
    }

    /// <summary>Reads a snapshot or resume (after the type byte).</summary>
    public static SnapshotMessage ReadSnapshot(ref BinaryBufferReader r)
    {
        int tick = checked((int)r.ReadVarInt64());
        ulong hash = r.ReadUInt64();
        byte[] data = r.ReadBytes(Count(ref r, MaxSnapshotBytes)).ToArray();
        var backlog = new TickClosure[Count(ref r, MaxClosuresPerMessage)];
        for (int i = 0; i < backlog.Length; i++)
        {
            backlog[i] = ReadClosure(ref r);
        }

        return new SnapshotMessage(tick, hash, data, backlog);
    }

    /// <summary>Reads and checks the type byte.</summary>
    public static MessageType ReadType(ref BinaryBufferReader r) => (MessageType)r.ReadByte();

    private static TickClosure ReadClosure(ref BinaryBufferReader r)
    {
        int tick = checked((int)r.ReadVarInt64());
        var commands = new SimCommand[Count(ref r, MaxCommandsPerClosure)];
        for (int i = 0; i < commands.Length; i++)
        {
            commands[i] = ReadCommand(ref r, null);
        }

        return new TickClosure(tick, commands);
    }

    private static void WriteCommand(BinaryBufferWriter w, SimCommand c, bool includePlayer)
    {
        if (includePlayer)
        {
            w.WriteByte(c.Player);
        }

        w.WriteByte((byte)c.Kind);
        w.WriteVarUInt64(c.FirstUnit);
        w.WriteVarUInt64(c.UnitCount);
        w.WriteVarInt64(c.X);
        w.WriteVarInt64(c.Y);
        w.WriteVarUInt64(c.Id);
    }

    private static SimCommand ReadCommand(ref BinaryBufferReader r, byte? player)
    {
        byte p = player ?? r.ReadByte();
        var kind = (SimCommandKind)r.ReadByte();
        ushort first = checked((ushort)r.ReadVarUInt64());
        ushort count = checked((ushort)r.ReadVarUInt64());
        int x = checked((int)r.ReadVarInt64());
        int y = checked((int)r.ReadVarInt64());
        uint id = checked((uint)r.ReadVarUInt64());
        return new SimCommand(p, kind, first, count, x, y, id);
    }

    private static int Count(ref BinaryBufferReader r, int max)
    {
        ulong count = r.ReadVarUInt64();
        if (count > (ulong)max)
        {
            throw new InvalidDataException($"Count {count} exceeds the limit {max}.");
        }

        return (int)count;
    }

    private static void Expect(ref BinaryBufferReader r, MessageType type)
    {
        if (ReadType(ref r) != type)
        {
            throw new InvalidDataException($"Expected {type}.");
        }
    }
}
