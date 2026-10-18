using System;
using System.IO;
using Tzar2.Core.Serialization;
using Tzar2.Net.Protocol;

namespace Tzar2.Net.Relay;

/// <summary>What one peer reported for a desync (REQ-NET-04).</summary>
/// <param name="Slot">The slot.</param>
/// <param name="Name">The player's name.</param>
/// <param name="ReportedHash">The hash the peer reported for the desync tick.</param>
/// <param name="BeforeTick">Tick of <paramref name="Before"/>: the peer's last keyframe whose hash every peer agreed on (-1: none).</param>
/// <param name="Before">That keyframe's snapshot (empty if none).</param>
/// <param name="AfterTick">Tick of <paramref name="After"/>: the peer's state when the report was requested.</param>
/// <param name="AfterHash">State hash of <paramref name="After"/>.</param>
/// <param name="After">The current snapshot.</param>
public sealed record DesyncSlotReport(byte Slot, string Name, ulong ReportedHash, int BeforeTick, byte[] Before, int AfterTick, ulong AfterHash, byte[] After);

/// <summary>
/// A desync report bundle (<c>.t2desync</c>; REQ-NET-04, ADR 0165): the desync tick, the majority hash and the odd slots,
/// the lobby settings (to rebuild the match), every peer's snapshots before and after, and the relay's log of closed
/// ticks covering them. <c>t2 desync diff</c> reads it. Parsing is bounded (REQ-NET-11).
/// </summary>
public sealed record DesyncBundle(
    string Lobby, ulong MatchId, int Tick, ulong MajorityHash, byte[] OddSlots, byte[] Settings, DesyncSlotReport[] Slots, TickClosure[] Log)
{
    /// <summary>"T2DS" little-endian.</summary>
    public const uint Magic = 0x53443254;

    /// <summary>Format version.</summary>
    public const int Version = 1;

    /// <summary>Largest bundle accepted.</summary>
    public const int MaxBytes = 1 << 30;

    /// <summary>The bundle as bytes.</summary>
    public byte[] ToBytes()
    {
        var w = new BinaryBufferWriter(64 * 1024);
        w.WriteUInt32(Magic);
        w.WriteVarUInt64(Version);
        w.WriteString(Lobby);
        w.WriteUInt64(MatchId);
        w.WriteVarInt64(Tick);
        w.WriteUInt64(MajorityHash);
        Blob(w, OddSlots);
        Blob(w, Settings);
        w.WriteVarUInt64((ulong)Slots.Length);
        foreach (DesyncSlotReport s in Slots)
        {
            w.WriteByte(s.Slot);
            w.WriteString(s.Name);
            w.WriteUInt64(s.ReportedHash);
            w.WriteVarInt64(s.BeforeTick);
            Blob(w, s.Before);
            w.WriteVarInt64(s.AfterTick);
            w.WriteUInt64(s.AfterHash);
            Blob(w, s.After);
        }

        w.WriteVarUInt64((ulong)Log.Length);
        foreach (TickClosure c in Log)
        {
            Wire.WriteClosure(w, c);
        }

        return w.ToArray();
    }

    /// <summary>Parses a bundle.</summary>
    /// <exception cref="InvalidDataException">Malformed or another version.</exception>
    public static DesyncBundle Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxBytes)
        {
            throw new InvalidDataException("Desync bundle too large.");
        }

        var r = new BinaryBufferReader(data);
        try
        {
            if (r.ReadUInt32() != Magic || r.ReadVarUInt64() != Version)
            {
                throw new InvalidDataException("Not a Tzar2 desync bundle of a supported version.");
            }

            string lobby = r.ReadString(Limits.MaxCodeBytes);
            ulong match = r.ReadUInt64();
            int tick = ReadTick(ref r);
            ulong majority = r.ReadUInt64();
            byte[] odd = ReadBlob(ref r, Limits.MaxSlots);
            byte[] settings = ReadBlob(ref r, Limits.MaxSettingsBytes);
            var slots = new DesyncSlotReport[Count(ref r, Limits.MaxSlots)];
            for (int i = 0; i < slots.Length; i++)
            {
                byte slot = r.ReadByte();
                if (slot >= Limits.MaxSlots)
                {
                    throw new InvalidDataException("Slot out of range.");
                }

                slots[i] = new DesyncSlotReport(
                    slot, r.ReadString(Limits.MaxNameBytes), r.ReadUInt64(), ReadTick(ref r), ReadBlob(ref r, Limits.MaxSnapshotBytes),
                    ReadTick(ref r), r.ReadUInt64(), ReadBlob(ref r, Limits.MaxSnapshotBytes));
            }

            var log = new TickClosure[Count(ref r, Limits.MaxBacklogClosures)];
            for (int i = 0; i < log.Length; i++)
            {
                log[i] = Wire.ReadClosure(ref r);
                if (i > 0 && log[i].Tick != log[i - 1].Tick + 1)
                {
                    throw new InvalidDataException("Log ticks are not consecutive.");
                }
            }

            if (!r.IsAtEnd)
            {
                throw new InvalidDataException("Trailing bytes.");
            }

            return new DesyncBundle(lobby, match, tick, majority, odd, settings, slots, log);
        }
        catch (OverflowException e)
        {
            throw new InvalidDataException("Integer out of range.", e);
        }
    }

    private static void Blob(BinaryBufferWriter w, ReadOnlySpan<byte> bytes)
    {
        w.WriteVarUInt64((ulong)bytes.Length);
        w.WriteBytes(bytes);
    }

    private static byte[] ReadBlob(ref BinaryBufferReader r, int max) => r.ReadBytes(Count(ref r, max)).ToArray();

    private static int Count(ref BinaryBufferReader r, int max)
    {
        ulong count = r.ReadVarUInt64();
        if (count > (ulong)max || count > (ulong)r.Remaining)
        {
            throw new InvalidDataException($"Count {count} exceeds the limit {max} or the remaining input.");
        }

        return (int)count;
    }

    private static int ReadTick(ref BinaryBufferReader r)
    {
        long tick = r.ReadVarInt64();
        return tick >= -1 && tick <= Limits.MaxTick ? (int)tick : throw new InvalidDataException("Tick out of range.");
    }
}
