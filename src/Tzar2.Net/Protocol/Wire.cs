using System;
using System.IO;
using Tzar2.Core.Serialization;

namespace Tzar2.Net.Protocol;

/// <summary>
/// Encoding and bounded decoding of every protocol message (ADR 0160; REQ-NET-11). Writers emit the type byte; readers
/// are called after <see cref="ReadType"/> and reject malformed input with <see cref="InvalidDataException"/> only:
/// every count, size, enum and tick is checked before anything is allocated, and every allocation is bounded by the
/// remaining input.
/// </summary>
public static class Wire
{
    /// <summary>"T2NP" little-endian, first field of a hello.</summary>
    public const uint Magic = 0x504E3254;

    /// <summary>Protocol version; peers of another version are refused with <see cref="RefusalReason.ProtocolVersion"/>.</summary>
    public const ushort ProtocolVersion = 1;

    /// <summary>Reads the type byte.</summary>
    public static MessageType ReadType(ref BinaryBufferReader r) => (MessageType)r.ReadByte();

    /// <summary>Writes a message that is only its type byte.</summary>
    public static void WriteEmpty(BinaryBufferWriter w, MessageType type)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)type);
    }

    // ---- connection ----

    /// <summary>Writes a hello.</summary>
    public static void Write(BinaryBufferWriter w, Hello m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        w.WriteByte((byte)MessageType.Hello);
        w.WriteUInt32(Magic);
        w.WriteUInt16(ProtocolVersion);
        w.WriteByte((byte)m.Kind);
        w.WriteString(Truncate(m.GameVersion, Limits.MaxVersionBytes));
        w.WriteString(m.Name);
        w.WriteString(m.Code);
        w.WriteByte(m.Slot);
        w.WriteUInt64(m.Token);
        w.WriteBoolean(m.Create is not null);
        if (m.Create is { } c)
        {
            w.WriteString(c.LobbyName);
            w.WriteByte(c.MaxPlayers);
            w.WriteBoolean(c.Public);
        }
    }

    /// <summary>
    /// Reads a hello, type byte included. A hello of another protocol version is returned with only
    /// <see cref="Hello.Protocol"/> set (the rest of its layout may differ), so the relay can refuse it clearly.
    /// </summary>
    public static Hello ReadHello(ReadOnlySpan<byte> data)
    {
        var r = new BinaryBufferReader(data);
        return Guard(ref r, static (ref BinaryBufferReader r) =>
        {
            Expect(ref r, MessageType.Hello);
            if (r.ReadUInt32() != Magic)
            {
                throw new InvalidDataException("Not a Tzar2 hello.");
            }

            ushort version = r.ReadUInt16();
            if (version != ProtocolVersion)
            {
                return new Hello(HelloKind.Join, string.Empty, string.Empty, string.Empty, 0, 0, null) { Protocol = version };
            }

            var kind = (HelloKind)r.ReadByte();
            if (kind is not (HelloKind.Create or HelloKind.Join or HelloKind.Rejoin))
            {
                throw new InvalidDataException("Unknown hello kind.");
            }

            string game = r.ReadString(Limits.MaxVersionBytes);
            string name = r.ReadString(Limits.MaxNameBytes);
            string code = r.ReadString(Limits.MaxCodeBytes);
            byte slot = r.ReadByte();
            ulong token = r.ReadUInt64();
            LobbyCreate? create = null;
            if (r.ReadBoolean())
            {
                create = new LobbyCreate(r.ReadString(Limits.MaxLobbyNameBytes), r.ReadByte(), r.ReadBoolean());
            }

            End(ref r);
            return new Hello(kind, game, name, code, slot, token, create);
        });
    }

    /// <summary>Writes a refusal.</summary>
    public static void Write(BinaryBufferWriter w, Refusal m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        w.WriteByte((byte)MessageType.Refusal);
        w.WriteByte((byte)m.Reason);
        w.WriteString(Truncate(m.Detail, Limits.MaxDetailBytes));
    }

    /// <summary>Reads a refusal, type byte included.</summary>
    public static Refusal ReadRefusal(ReadOnlySpan<byte> data)
    {
        var r = new BinaryBufferReader(data);
        return Guard(ref r, static (ref BinaryBufferReader r) =>
        {
            Expect(ref r, MessageType.Refusal);
            var reason = (RefusalReason)r.ReadByte();
            if (reason is < RefusalReason.Malformed or > RefusalReason.GameVersion)
            {
                throw new InvalidDataException("Unknown refusal reason.");
            }

            return new Refusal(reason, r.ReadString(Limits.MaxDetailBytes));
        });
    }

    /// <summary>Writes a browse query.</summary>
    public static void Write(BinaryBufferWriter w, BrowseQuery m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        w.WriteByte((byte)MessageType.BrowseQuery);
        w.WriteUInt32(Magic);
        w.WriteUInt16(ProtocolVersion);
        w.WriteUInt16(m.Page);
        w.WriteUInt32(m.Nonce);
    }

    /// <summary>Reads a browse query, type byte included.</summary>
    public static BrowseQuery ReadBrowseQuery(ReadOnlySpan<byte> data)
    {
        var r = new BinaryBufferReader(data);
        return Guard(ref r, static (ref BinaryBufferReader r) =>
        {
            Expect(ref r, MessageType.BrowseQuery);
            if (r.ReadUInt32() != Magic || r.ReadUInt16() != ProtocolVersion)
            {
                throw new InvalidDataException("Not a Tzar2 browse query of this protocol version.");
            }

            var query = new BrowseQuery(r.ReadUInt16(), r.ReadUInt32());
            End(ref r);
            return query;
        });
    }

    /// <summary>Writes a browse reply.</summary>
    public static void Write(BinaryBufferWriter w, BrowseReply m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        if (m.Lobbies.Length > Limits.BrowsePageSize)
        {
            throw new ArgumentException("Too many lobbies for one page.", nameof(m));
        }

        w.WriteByte((byte)MessageType.BrowseReply);
        w.WriteUInt32(m.Nonce);
        w.WriteVarUInt64((ulong)m.Total);
        w.WriteVarUInt64((ulong)m.Lobbies.Length);
        foreach (LobbySummary l in m.Lobbies)
        {
            w.WriteString(l.Code);
            w.WriteString(l.Name);
            w.WriteByte(l.Players);
            w.WriteByte(l.MaxPlayers);
            w.WriteByte((byte)l.Phase);
            w.WriteString(l.GameVersion);
        }
    }

    /// <summary>Reads a browse reply, type byte included.</summary>
    public static BrowseReply ReadBrowseReply(ReadOnlySpan<byte> data)
    {
        var r = new BinaryBufferReader(data);
        return Guard(ref r, static (ref BinaryBufferReader r) =>
        {
            Expect(ref r, MessageType.BrowseReply);
            uint nonce = r.ReadUInt32();
            int total = Count(ref r, 1 << 20, 0);
            var lobbies = new LobbySummary[Count(ref r, Limits.BrowsePageSize, 6)];
            for (int i = 0; i < lobbies.Length; i++)
            {
                lobbies[i] = new LobbySummary(
                    r.ReadString(Limits.MaxCodeBytes), r.ReadString(Limits.MaxLobbyNameBytes), Slots(r.ReadByte()), Slots(r.ReadByte()),
                    Phase(r.ReadByte()), r.ReadString(Limits.MaxVersionBytes));
            }

            return new BrowseReply(nonce, total, lobbies);
        });
    }

    // ---- lobby ----

    /// <summary>Writes a welcome.</summary>
    public static void Write(BinaryBufferWriter w, Welcome m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        w.WriteByte((byte)MessageType.Welcome);
        w.WriteString(m.Code);
        w.WriteByte(m.Slot);
        w.WriteUInt64(m.Token);
        w.WriteByte(m.MaxPlayers);
    }

    /// <summary>Reads a welcome (after the type byte).</summary>
    public static Welcome ReadWelcome(ref BinaryBufferReader r)
    {
        string code = r.ReadString(Limits.MaxCodeBytes);
        byte slot = r.ReadByte();
        ulong token = r.ReadUInt64();
        byte max = Slots(r.ReadByte());
        if (slot >= max)
        {
            throw new InvalidDataException("Welcome slot out of range.");
        }

        return new Welcome(code, slot, token, max);
    }

    /// <summary>Writes the lobby state.</summary>
    public static void Write(BinaryBufferWriter w, LobbyState m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        w.WriteByte((byte)MessageType.LobbyState);
        w.WriteString(m.Code);
        w.WriteString(m.Name);
        w.WriteByte(m.HostSlot);
        w.WriteByte((byte)m.Phase);
        w.WriteVarInt64(m.SettingsRevision);
        WriteBlob(w, m.Settings);
        w.WriteVarUInt64((ulong)m.Slots.Length);
        foreach (SlotInfo s in m.Slots)
        {
            w.WriteByte((byte)s.State);
            w.WriteString(s.Name);
            w.WriteByte((byte)((s.Ready ? 1 : 0) | (s.Connected ? 2 : 0)));
            w.WriteUInt16(s.PingMs);
            WriteBlob(w, s.Choice);
        }
    }

    /// <summary>Reads the lobby state (after the type byte).</summary>
    public static LobbyState ReadLobbyState(ref BinaryBufferReader r)
    {
        string code = r.ReadString(Limits.MaxCodeBytes);
        string name = r.ReadString(Limits.MaxLobbyNameBytes);
        byte host = r.ReadByte();
        LobbyPhase phase = Phase(r.ReadByte());
        int revision = Int(ref r, int.MinValue, int.MaxValue);
        byte[] settings = ReadBlob(ref r, Limits.MaxSettingsBytes);
        var slots = new SlotInfo[Count(ref r, Limits.MaxSlots, 5)];
        if (slots.Length == 0 || host >= slots.Length)
        {
            throw new InvalidDataException("Lobby without slots or with the host outside them.");
        }

        for (int i = 0; i < slots.Length; i++)
        {
            SlotState state = State(r.ReadByte());
            string player = r.ReadString(Limits.MaxNameBytes);
            byte flags = r.ReadByte();
            if (flags > 3)
            {
                throw new InvalidDataException("Invalid slot flags.");
            }

            slots[i] = new SlotInfo(state, player, (flags & 1) != 0, (flags & 2) != 0, r.ReadUInt16(), ReadBlob(ref r, Limits.MaxChoiceBytes));
        }

        return new LobbyState(code, name, host, phase, revision, settings, slots);
    }

    /// <summary>Writes a slot choice.</summary>
    public static void WriteSetChoice(BinaryBufferWriter w, byte slot, ReadOnlySpan<byte> choice)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)MessageType.SetChoice);
        w.WriteByte(slot);
        WriteBlob(w, choice);
    }

    /// <summary>Reads a slot choice (after the type byte).</summary>
    public static (byte Slot, byte[] Choice) ReadSetChoice(ref BinaryBufferReader r) => (r.ReadByte(), ReadBlob(ref r, Limits.MaxChoiceBytes));

    /// <summary>Writes the match settings.</summary>
    public static void WriteSetSettings(BinaryBufferWriter w, ReadOnlySpan<byte> settings)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)MessageType.SetSettings);
        WriteBlob(w, settings);
    }

    /// <summary>Reads the match settings (after the type byte).</summary>
    public static byte[] ReadSetSettings(ref BinaryBufferReader r) => ReadBlob(ref r, Limits.MaxSettingsBytes);

    /// <summary>Writes a host slot change.</summary>
    public static void WriteSetSlot(BinaryBufferWriter w, byte slot, SlotState state)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)MessageType.SetSlot);
        w.WriteByte(slot);
        w.WriteByte((byte)state);
    }

    /// <summary>Reads a host slot change (after the type byte).</summary>
    public static (byte Slot, SlotState State) ReadSetSlot(ref BinaryBufferReader r) => (r.ReadByte(), State(r.ReadByte()));

    /// <summary>Writes a message with one flag (SetReady, Pause).</summary>
    public static void WriteFlag(BinaryBufferWriter w, MessageType type, bool value)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)type);
        w.WriteBoolean(value);
    }

    /// <summary>Writes a chat line.</summary>
    public static void WriteChat(BinaryBufferWriter w, byte recipients, string text)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)MessageType.Chat);
        w.WriteByte(recipients);
        w.WriteString(Truncate(text, Limits.MaxChatBytes));
    }

    /// <summary>Reads a chat line (after the type byte).</summary>
    public static (byte Recipients, string Text) ReadChat(ref BinaryBufferReader r) => (r.ReadByte(), r.ReadString(Limits.MaxChatBytes));

    /// <summary>Writes a delivered chat line.</summary>
    public static void WriteChatDelivery(BinaryBufferWriter w, byte from, byte recipients, string text)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)MessageType.ChatDelivery);
        w.WriteByte(from);
        w.WriteByte(recipients);
        w.WriteString(Truncate(text, Limits.MaxChatBytes));
    }

    /// <summary>Reads a delivered chat line (after the type byte).</summary>
    public static (byte From, byte Recipients, string Text) ReadChatDelivery(ref BinaryBufferReader r)
    {
        byte from = r.ReadByte();
        if (from >= Limits.MaxSlots)
        {
            throw new InvalidDataException("Chat sender out of range.");
        }

        return (from, r.ReadByte(), r.ReadString(Limits.MaxChatBytes));
    }

    /// <summary>Writes a notice.</summary>
    public static void WriteNotice(BinaryBufferWriter w, NoticeKind kind, string text)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)MessageType.Notice);
        w.WriteByte((byte)kind);
        w.WriteString(Truncate(text, Limits.MaxDetailBytes));
    }

    /// <summary>Reads a notice (after the type byte).</summary>
    public static (NoticeKind Kind, string Text) ReadNotice(ref BinaryBufferReader r)
    {
        var kind = (NoticeKind)r.ReadByte();
        if (kind is < NoticeKind.ChatThrottled or > NoticeKind.NoPausesLeft)
        {
            throw new InvalidDataException("Unknown notice.");
        }

        return (kind, r.ReadString(Limits.MaxDetailBytes));
    }

    // ---- loading ----

    /// <summary>Writes a load order.</summary>
    public static void Write(BinaryBufferWriter w, MatchParameters m) => WriteParameters(w, MessageType.Load, m);

    /// <summary>Writes the host's start request with the wanted match parameters (the relay assigns the match id).</summary>
    public static void WriteStartRequest(BinaryBufferWriter w, MatchParameters wanted) => WriteParameters(w, MessageType.StartRequest, wanted);

    /// <summary>Reads a start request (after the type byte).</summary>
    public static MatchParameters ReadStartRequest(ref BinaryBufferReader r) => ReadLoad(ref r);

    private static void WriteParameters(BinaryBufferWriter w, MessageType type, MatchParameters m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        w.WriteByte((byte)type);
        w.WriteUInt64(m.MatchId);
        w.WriteVarInt64(m.TickMicroseconds);
        w.WriteVarInt64(m.HashInterval);
        w.WriteVarInt64(m.DropTimeoutMs);
        w.WriteByte((byte)m.Policy);
        w.WriteByte(m.MaxPauses);
    }

    /// <summary>Reads a load order (after the type byte).</summary>
    public static MatchParameters ReadLoad(ref BinaryBufferReader r)
    {
        ulong id = r.ReadUInt64();
        int tick = Int(ref r, 1_000, 2_000_000);
        int hashes = Int(ref r, 1, 1_000);
        int timeout = Int(ref r, 0, 3_600_000);
        var policy = (DropPolicy)r.ReadByte();
        if (policy > DropPolicy.Defeat)
        {
            throw new InvalidDataException("Unknown drop policy.");
        }

        return new MatchParameters(id, tick, hashes, timeout, policy, r.ReadByte());
    }

    /// <summary>Writes a loaded report.</summary>
    public static void WriteLoaded(BinaryBufferWriter w, ReadOnlySpan<ulong> fingerprint, ulong initialHash)
    {
        ArgumentNullException.ThrowIfNull(w);
        if (fingerprint.Length > Limits.MaxFingerprintParts)
        {
            throw new ArgumentException("Too many fingerprint parts.", nameof(fingerprint));
        }

        w.WriteByte((byte)MessageType.Loaded);
        w.WriteVarUInt64((ulong)fingerprint.Length);
        foreach (ulong part in fingerprint)
        {
            w.WriteUInt64(part);
        }

        w.WriteUInt64(initialHash);
    }

    /// <summary>Reads a loaded report (after the type byte).</summary>
    public static (ulong[] Fingerprint, ulong InitialHash) ReadLoaded(ref BinaryBufferReader r)
    {
        var parts = new ulong[Count(ref r, Limits.MaxFingerprintParts, 8)];
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = r.ReadUInt64();
        }

        return (parts, r.ReadUInt64());
    }

    /// <summary>Writes a start order.</summary>
    public static void WriteStart(BinaryBufferWriter w, int startDelayMs)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)MessageType.Start);
        w.WriteVarInt64(startDelayMs);
    }

    /// <summary>Reads a start order (after the type byte).</summary>
    public static int ReadStart(ref BinaryBufferReader r) => Int(ref r, 0, 60_000);

    // ---- lockstep ----

    /// <summary>Writes a batches header; follow with <paramref name="count"/> <see cref="WriteBatch"/> calls (or their bytes).</summary>
    public static void WriteBatchesHeader(BinaryBufferWriter w, int count)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Limits.MaxBatchesPerMessage);
        w.WriteByte((byte)MessageType.Batches);
        w.WriteVarUInt64((ulong)count);
    }

    /// <summary>Writes one batch (no type byte).</summary>
    public static void WriteBatch(BinaryBufferWriter w, Batch m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        if (m.Payload.Length > Limits.MaxPayloadBytes || m.Hashes.Length > Limits.MaxHashesPerBatch)
        {
            throw new ArgumentException("Batch exceeds the protocol limits.", nameof(m));
        }

        w.WriteVarUInt64(m.Seq);
        w.WriteVarInt64(m.TargetTick);
        w.WriteVarInt64(m.AckTick);
        WriteBlob(w, m.Payload);
        w.WriteVarUInt64((ulong)m.Hashes.Length);
        foreach (TickHash h in m.Hashes)
        {
            w.WriteVarInt64(h.Tick);
            w.WriteUInt64(h.Hash);
        }
    }

    /// <summary>Reads a batches message (after the type byte).</summary>
    public static Batch[] ReadBatches(ref BinaryBufferReader r)
    {
        var batches = new Batch[Count(ref r, Limits.MaxBatchesPerMessage, 5)];
        for (int i = 0; i < batches.Length; i++)
        {
            uint seq = U32(ref r);
            int target = TickValue(ref r);
            int ack = TickValue(ref r);
            byte[] payload = ReadBlob(ref r, Limits.MaxPayloadBytes);
            var hashes = new TickHash[Count(ref r, Limits.MaxHashesPerBatch, 9)];
            for (int j = 0; j < hashes.Length; j++)
            {
                hashes[j] = new TickHash(TickValue(ref r), r.ReadUInt64());
            }

            batches[i] = new Batch(seq, target, ack, payload, hashes);
        }

        return batches;
    }

    /// <summary>Writes a closures header; follow with <paramref name="count"/> <see cref="WriteClosure"/> calls.</summary>
    public static void WriteClosuresHeader(BinaryBufferWriter w, uint batchAck, int count)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Limits.MaxClosuresPerMessage);
        w.WriteByte((byte)MessageType.Closures);
        w.WriteVarUInt64(batchAck);
        w.WriteVarUInt64((ulong)count);
    }

    /// <summary>Writes one closure (no type byte).</summary>
    public static void WriteClosure(BinaryBufferWriter w, TickClosure c)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(c);
        w.WriteVarInt64(c.Tick);
        w.WriteVarUInt64((ulong)c.Entries.Length);
        foreach (ClosureEntry e in c.Entries)
        {
            w.WriteByte(e.Slot);
            WriteBlob(w, e.Payload);
        }

        w.WriteVarUInt64((ulong)c.Controls.Length);
        foreach (ControlEvent e in c.Controls)
        {
            w.WriteByte((byte)e.Kind);
            w.WriteByte(e.Slot);
            w.WriteVarInt64(e.Arg);
        }
    }

    /// <summary>Reads one closure (no type byte).</summary>
    public static TickClosure ReadClosure(ref BinaryBufferReader r)
    {
        int tick = TickValue(ref r);
        if (tick < 0)
        {
            throw new InvalidDataException("Closure for a negative tick.");
        }

        var entries = new ClosureEntry[Count(ref r, Limits.MaxEntriesPerClosure, 2)];
        int bytes = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            byte slot = r.ReadByte();
            if (slot >= Limits.MaxSlots)
            {
                throw new InvalidDataException("Closure entry slot out of range.");
            }

            byte[] payload = ReadBlob(ref r, Limits.MaxPayloadBytes);
            bytes += payload.Length;
            if (bytes > Limits.MaxClosureBytes)
            {
                throw new InvalidDataException("Closure exceeds its byte limit.");
            }

            entries[i] = new ClosureEntry(slot, payload);
        }

        var controls = new ControlEvent[Count(ref r, Limits.MaxControlsPerClosure, 3)];
        for (int i = 0; i < controls.Length; i++)
        {
            var kind = (ControlKind)r.ReadByte();
            byte slot = r.ReadByte();
            if (kind is < ControlKind.Takeover or > ControlKind.Save || slot >= Limits.MaxSlots)
            {
                throw new InvalidDataException("Invalid control event.");
            }

            controls[i] = new ControlEvent(kind, slot, Int(ref r, int.MinValue, int.MaxValue));
        }

        return new TickClosure(tick, entries, controls);
    }

    /// <summary>Reads a closures message (after the type byte).</summary>
    public static TickClosure[] ReadClosures(ref BinaryBufferReader r, out uint batchAck)
    {
        batchAck = U32(ref r);
        var closures = new TickClosure[Count(ref r, Limits.MaxClosuresPerMessage, 3)];
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

    /// <summary>Reads a late notice (after the type byte).</summary>
    public static (uint Seq, int Target, int Assigned) ReadLate(ref BinaryBufferReader r) =>
        (U32(ref r), TickValue(ref r), TickValue(ref r));

    /// <summary>Writes a message with one integer (Ping, Pong, WaitVote slot, SaveVote id).</summary>
    public static void WriteValue(BinaryBufferWriter w, MessageType type, long value)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)type);
        w.WriteVarInt64(value);
    }

    /// <summary>Reads a one-integer message's value (after the type byte).</summary>
    public static long ReadValue(ref BinaryBufferReader r) => r.ReadVarInt64();

    /// <summary>Writes the status of every slot.</summary>
    public static void WriteStatus(BinaryBufferWriter w, ReadOnlySpan<PeerStatus> slots)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)MessageType.Status);
        w.WriteVarUInt64((ulong)slots.Length);
        foreach (PeerStatus s in slots)
        {
            w.WriteByte((byte)s.Control);
            w.WriteBoolean(s.Connected);
            w.WriteUInt16(s.RttMs);
            w.WriteByte(s.InputDelay);
            w.WriteUInt16(s.DroppedSeconds);
        }
    }

    /// <summary>Reads the status of every slot (after the type byte).</summary>
    public static PeerStatus[] ReadStatus(ref BinaryBufferReader r)
    {
        var slots = new PeerStatus[Count(ref r, Limits.MaxSlots, 7)];
        for (int i = 0; i < slots.Length; i++)
        {
            var control = (SlotControl)r.ReadByte();
            if (control > SlotControl.Dropped)
            {
                throw new InvalidDataException("Unknown slot control.");
            }

            slots[i] = new PeerStatus(control, r.ReadBoolean(), r.ReadUInt16(), r.ReadByte(), r.ReadUInt16());
        }

        return slots;
    }

    // ---- desync, rejoin, saves, pause ----

    /// <summary>Writes a desync notice.</summary>
    public static void Write(BinaryBufferWriter w, DesyncNotice m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        w.WriteByte((byte)MessageType.Desync);
        w.WriteVarInt64(m.Tick);
        w.WriteUInt64(m.MajorityHash);
        WriteBlob(w, m.OddSlots);
    }

    /// <summary>Reads a desync notice (after the type byte).</summary>
    public static DesyncNotice ReadDesync(ref BinaryBufferReader r)
    {
        int tick = TickValue(ref r);
        ulong majority = r.ReadUInt64();
        byte[] odd = ReadBlob(ref r, Limits.MaxSlots);
        foreach (byte slot in odd)
        {
            if (slot >= Limits.MaxSlots)
            {
                throw new InvalidDataException("Odd slot out of range.");
            }
        }

        return new DesyncNotice(tick, majority, odd);
    }

    /// <summary>Writes a snapshot request.</summary>
    public static void WriteSnapshotRequest(BinaryBufferWriter w, SnapshotPurpose purpose, uint requestId)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)MessageType.SnapshotRequest);
        w.WriteByte((byte)purpose);
        w.WriteVarUInt64(requestId);
    }

    /// <summary>Reads a snapshot request (after the type byte).</summary>
    public static (SnapshotPurpose Purpose, uint RequestId) ReadSnapshotRequest(ref BinaryBufferReader r) =>
        (Purpose(r.ReadByte()), U32(ref r));

    /// <summary>Writes a snapshot.</summary>
    public static void Write(BinaryBufferWriter w, SnapshotMessage m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        w.WriteByte((byte)MessageType.Snapshot);
        w.WriteByte((byte)m.Purpose);
        w.WriteVarUInt64(m.RequestId);
        w.WriteVarInt64(m.Tick);
        w.WriteUInt64(m.Hash);
        WriteBlob(w, m.Data);
        w.WriteVarInt64(m.BeforeTick);
        WriteBlob(w, m.BeforeData);
    }

    /// <summary>Reads a snapshot (after the type byte).</summary>
    public static SnapshotMessage ReadSnapshot(ref BinaryBufferReader r)
    {
        SnapshotPurpose purpose = Purpose(r.ReadByte());
        uint id = U32(ref r);
        int tick = TickValue(ref r);
        ulong hash = r.ReadUInt64();
        byte[] data = ReadBlob(ref r, Limits.MaxSnapshotBytes);
        int beforeTick = TickValue(ref r);
        byte[] before = ReadBlob(ref r, Limits.MaxSnapshotBytes);
        return new SnapshotMessage(purpose, id, tick, hash, data, beforeTick, before);
    }

    /// <summary>Writes a resume.</summary>
    public static void Write(BinaryBufferWriter w, ResumeMessage m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        w.WriteByte((byte)MessageType.Resume);
        w.WriteVarInt64(m.Tick);
        w.WriteUInt64(m.Hash);
        WriteBlob(w, m.Data);
        w.WriteVarUInt64((ulong)m.Backlog.Length);
        foreach (TickClosure c in m.Backlog)
        {
            WriteClosure(w, c);
        }
    }

    /// <summary>Reads a resume (after the type byte).</summary>
    public static ResumeMessage ReadResume(ref BinaryBufferReader r)
    {
        int tick = TickValue(ref r);
        ulong hash = r.ReadUInt64();
        byte[] data = ReadBlob(ref r, Limits.MaxSnapshotBytes);
        var backlog = new TickClosure[Count(ref r, Limits.MaxBacklogClosures, 3)];
        for (int i = 0; i < backlog.Length; i++)
        {
            backlog[i] = ReadClosure(ref r);
            if (backlog[i].Tick != tick + 1 + i)
            {
                throw new InvalidDataException("Backlog closures are not consecutive after the snapshot tick.");
            }
        }

        return new ResumeMessage(tick, hash, data, backlog);
    }

    /// <summary>Writes a save request.</summary>
    public static void WriteSaveRequest(BinaryBufferWriter w, string label)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)MessageType.SaveRequest);
        w.WriteString(Truncate(label, Limits.MaxLabelBytes));
    }

    /// <summary>Reads a save request (after the type byte).</summary>
    public static string ReadSaveRequest(ref BinaryBufferReader r) => r.ReadString(Limits.MaxLabelBytes);

    /// <summary>Writes a save proposal.</summary>
    public static void Write(BinaryBufferWriter w, SaveProposal m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        w.WriteByte((byte)MessageType.SaveProposal);
        w.WriteVarUInt64(m.Id);
        w.WriteByte(m.By);
        w.WriteString(Truncate(m.Label, Limits.MaxLabelBytes));
    }

    /// <summary>Reads a save proposal (after the type byte).</summary>
    public static SaveProposal ReadSaveProposal(ref BinaryBufferReader r) =>
        new(U32(ref r), r.ReadByte(), r.ReadString(Limits.MaxLabelBytes));

    /// <summary>Writes a save vote.</summary>
    public static void WriteSaveVote(BinaryBufferWriter w, uint id, bool accept)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)MessageType.SaveVote);
        w.WriteVarUInt64(id);
        w.WriteBoolean(accept);
    }

    /// <summary>Reads a save vote (after the type byte).</summary>
    public static (uint Id, bool Accept) ReadSaveVote(ref BinaryBufferReader r) => (U32(ref r), r.ReadBoolean());

    /// <summary>Writes a save outcome.</summary>
    public static void Write(BinaryBufferWriter w, SaveOutcome m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        w.WriteByte((byte)MessageType.SaveOutcome);
        w.WriteVarUInt64(m.Id);
        w.WriteBoolean(m.Accepted);
        w.WriteByte(m.RefusedBy);
    }

    /// <summary>Reads a save outcome (after the type byte).</summary>
    public static SaveOutcome ReadSaveOutcome(ref BinaryBufferReader r) => new(U32(ref r), r.ReadBoolean(), r.ReadByte());

    /// <summary>Writes the pause state.</summary>
    public static void WritePauseState(BinaryBufferWriter w, bool paused, byte by, byte pausesLeft)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteByte((byte)MessageType.PauseState);
        w.WriteBoolean(paused);
        w.WriteByte(by);
        w.WriteByte(pausesLeft);
    }

    /// <summary>Reads the pause state (after the type byte).</summary>
    public static (bool Paused, byte By, byte PausesLeft) ReadPauseState(ref BinaryBufferReader r) => (r.ReadBoolean(), r.ReadByte(), r.ReadByte());

    /// <summary>Writes an end message.</summary>
    public static void Write(BinaryBufferWriter w, EndMessage m)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(m);
        w.WriteByte((byte)MessageType.End);
        w.WriteVarInt64(m.LastTick);
        w.WriteByte((byte)m.Reason);
        w.WriteByte(m.Slot);
        w.WriteString(Truncate(m.Detail, Limits.MaxDetailBytes));
    }

    /// <summary>Reads an end message (after the type byte).</summary>
    public static EndMessage ReadEnd(ref BinaryBufferReader r)
    {
        int tick = TickValue(ref r);
        var reason = (EndReason)r.ReadByte();
        if (reason is < EndReason.Closed or > EndReason.Shutdown)
        {
            throw new InvalidDataException("Unknown end reason.");
        }

        return new EndMessage(tick, reason, r.ReadByte(), r.ReadString(Limits.MaxDetailBytes));
    }

    // ---- helpers ----

    /// <summary>
    /// Cuts <paramref name="text"/> to at most <paramref name="maxBytes"/> UTF-8 bytes on a character boundary; lone
    /// surrogates become U+FFFD (the strict encoder of the writers would refuse them).
    /// </summary>
    public static string Truncate(string text, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] bytes = LenientUtf8.GetBytes(text);
        if (bytes.Length <= maxBytes)
        {
            return LenientUtf8.GetString(bytes);
        }

        int length = maxBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
        {
            length--; // do not cut inside a multi-byte sequence
        }

        return LenientUtf8.GetString(bytes, 0, length);
    }

    private static readonly System.Text.UTF8Encoding LenientUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private delegate T ReadFunc<T>(ref BinaryBufferReader r);

    // Integer overflows in checked conversions become InvalidDataException, the only failure readers report.
    private static T Guard<T>(ref BinaryBufferReader r, ReadFunc<T> read)
    {
        try
        {
            return read(ref r);
        }
        catch (OverflowException e)
        {
            throw new InvalidDataException("Integer out of range.", e);
        }
    }

    private static void Expect(ref BinaryBufferReader r, MessageType type)
    {
        if (ReadType(ref r) != type)
        {
            throw new InvalidDataException($"Expected {type}.");
        }
    }

    private static void End(ref BinaryBufferReader r)
    {
        if (!r.IsAtEnd)
        {
            throw new InvalidDataException("Trailing bytes.");
        }
    }

    private static void WriteBlob(BinaryBufferWriter w, ReadOnlySpan<byte> bytes)
    {
        w.WriteVarUInt64((ulong)bytes.Length);
        w.WriteBytes(bytes);
    }

    private static byte[] ReadBlob(ref BinaryBufferReader r, int max) => r.ReadBytes(Count(ref r, max, 1)).ToArray();

    /// <summary>A count of at most <paramref name="max"/> elements of at least <paramref name="minBytes"/> bytes each.</summary>
    private static int Count(ref BinaryBufferReader r, int max, int minBytes)
    {
        ulong count = r.ReadVarUInt64();
        if (count > (ulong)max || (minBytes > 0 && count > (ulong)(r.Remaining / minBytes)))
        {
            throw new InvalidDataException($"Count {count} exceeds the limit {max} or the remaining input.");
        }

        return (int)count;
    }

    private static int Int(ref BinaryBufferReader r, int min, int max)
    {
        long value = r.ReadVarInt64();
        if (value < min || value > max)
        {
            throw new InvalidDataException($"Value {value} is outside [{min}, {max}].");
        }

        return (int)value;
    }

    private static uint U32(ref BinaryBufferReader r)
    {
        ulong value = r.ReadVarUInt64();
        return value <= uint.MaxValue ? (uint)value : throw new InvalidDataException($"Value {value} exceeds 32 bits.");
    }

    private static int TickValue(ref BinaryBufferReader r) => Int(ref r, -1, Limits.MaxTick);

    private static byte Slots(byte value) => value is >= 1 and <= Limits.MaxSlots ? value : throw new InvalidDataException("Slot count out of range.");

    private static LobbyPhase Phase(byte value) => value <= (byte)LobbyPhase.Ended ? (LobbyPhase)value : throw new InvalidDataException("Unknown lobby phase.");

    private static SlotState State(byte value) => value <= (byte)SlotState.Computer ? (SlotState)value : throw new InvalidDataException("Unknown slot state.");

    private static SnapshotPurpose Purpose(byte value) => value is (byte)SnapshotPurpose.Rejoin or (byte)SnapshotPurpose.Desync
        ? (SnapshotPurpose)value
        : throw new InvalidDataException("Unknown snapshot purpose.");
}
