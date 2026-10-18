using System;
using System.Collections.Generic;
using System.IO;
using Tzar2.Core.Serialization;

namespace Tzar2.Scripting.Legacy;

/// <summary>A timer started by a Set Timer action (Appendix C.6: a millisecond variable named after the action).</summary>
public struct TimerState
{
    /// <summary>Whether Set Timer ran (the variable exists).</summary>
    public bool Started;

    /// <summary>Whether it is still counting (Stop Timer freezes it).</summary>
    public bool Running;

    /// <summary>Counting down (stops at 0) instead of up.</summary>
    public bool CountDown;

    /// <summary>The value in milliseconds.</summary>
    public long ValueMs;
}

/// <summary>The sizes of a scenario that bound and validate its runtime state.</summary>
/// <param name="Variables">User variables.</param>
/// <param name="Events">Event instances.</param>
/// <param name="Actions">Actions.</param>
/// <param name="Rules">Rules.</param>
/// <param name="Conversations">Phrases per conversation.</param>
/// <param name="Objects">Map objects.</param>
/// <param name="Players">Kingdoms of the match.</param>
/// <param name="EventMemory">Memory cells per event (named objects or players; 0 for stateless events).</param>
public sealed record TriggerShape(int Variables, int Events, int Actions, int Rules, int[] Conversations, int Objects, int Players, int[] EventMemory);

/// <summary>
/// The complete runtime state of a legacy scenario (REQ-SIM-13 "script variables and trigger states"; ADR 0130): the
/// snapshot block <c>triggers.legacy</c>. Everything the engine decides on lives here, written canonically and read back
/// with every count and index range-checked against the scenario's <see cref="TriggerShape"/> (snapshots are untrusted).
/// </summary>
public sealed class TriggerState
{
    /// <summary>No conversation runs.</summary>
    public const int NoConversation = -1;

    /// <summary>Most conversations waiting behind the running one.</summary>
    public const int MaxQueuedConversations = 64;

    /// <summary>Most candidate phrases offered at once.</summary>
    public const int MaxCandidates = 64;

    /// <summary>Most locked entities.</summary>
    public const int MaxLocked = 1 << 16;

    internal TriggerState(TriggerShape shape)
    {
        Shape = shape;
        Variables = new int[shape.Variables];
        EventValues = new bool[shape.Events];
        EventMemory = new int[shape.Events][];
        for (int i = 0; i < shape.Events; i++)
        {
            EventMemory[i] = new int[shape.EventMemory[i]];
        }

        RuleDisabled = new bool[shape.Rules];
        Timers = new TimerState[shape.Actions];
        MapEntities = new uint[shape.Objects];
        Hidden = new bool[shape.Objects];
        PlayerStatus = new byte[shape.Players];
    }

    /// <summary>The sizes.</summary>
    public TriggerShape Shape { get; }

    /// <summary>User variable values.</summary>
    public int[] Variables { get; }

    /// <summary>Event values of the current (or last) pass.</summary>
    public bool[] EventValues { get; }

    /// <summary>Per event: what it remembers between passes (deaths seen, units that left a holder).</summary>
    public int[][] EventMemory { get; }

    /// <summary>Rules that fired without Keep Rule.</summary>
    public bool[] RuleDisabled { get; }

    /// <summary>Timers, by Set Timer action index (other entries stay unused).</summary>
    public TimerState[] Timers { get; }

    /// <summary>The entity (<c>EntityId.Value</c>, 0 = none) of each map object.</summary>
    public uint[] MapEntities { get; }

    /// <summary>Map objects flagged Not Appear that have not appeared yet.</summary>
    public bool[] Hidden { get; }

    /// <summary>Per kingdom: bit 0 cannot win, bit 1 cannot lose (Change Player Status).</summary>
    public byte[] PlayerStatus { get; }

    /// <summary>Active quests (Add Quest action indices) in the order they were added.</summary>
    public List<int> Quests { get; } = [];

    /// <summary>Locked entities (<c>EntityId.Value</c>), ascending.</summary>
    public List<uint> Locked { get; } = [];

    /// <summary>Milliseconds accumulated toward the next rule pass.</summary>
    public long ClockMs { get; set; }

    /// <summary>The next rule of a pass in progress, or -1 between passes.</summary>
    public int PassRule { get; set; } = -1;

    /// <summary>Passes started so far.</summary>
    public int Passes { get; set; }

    /// <summary>The running conversation, or <see cref="NoConversation"/>.</summary>
    public int Conversation { get; set; } = NoConversation;

    /// <summary>Serial of the running conversation session (replies name it).</summary>
    public int ConversationSerial { get; set; }

    /// <summary>Phrases said in the running session (guard, REQ-SCR-02).</summary>
    public int ConversationSteps { get; set; }

    /// <summary>The phrases offered now (indices in the conversation).</summary>
    public List<int> Candidates { get; } = [];

    /// <summary>A reply received in this tick's command step: the candidate position, or -1.</summary>
    public int PendingReply { get; set; } = -1;

    /// <summary>Conversations started while another ran, in order.</summary>
    public List<int> QueuedConversations { get; } = [];

    /// <summary>The last next-map request: the action index, or -1.</summary>
    public int MapRequestAction { get; set; } = -1;

    /// <summary>Whether the trigger engine stopped (a fatal scenario error).</summary>
    public bool Halted { get; set; }

    /// <summary>Whether <paramref name="value"/> is locked.</summary>
    public bool IsLocked(uint value) => Locked.Count != 0 && Locked.BinarySearch(value) >= 0;

    /// <summary>Locks or unlocks an entity.</summary>
    public void SetLocked(uint value, bool locked)
    {
        int at = Locked.BinarySearch(value);
        if (locked && at < 0 && Locked.Count < MaxLocked)
        {
            Locked.Insert(~at, value);
        }
        else if (!locked && at >= 0)
        {
            Locked.RemoveAt(at);
        }
    }

    private const byte Version = 1;

    /// <summary>Writes the state canonically.</summary>
    public void WriteTo(BinaryBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Version);
        writer.WriteVarUInt64((ulong)Variables.Length);
        foreach (int v in Variables)
        {
            writer.WriteVarInt64(v);
        }

        writer.WriteVarUInt64((ulong)EventValues.Length);
        for (int i = 0; i < EventValues.Length; i++)
        {
            writer.WriteBoolean(EventValues[i]);
            writer.WriteVarUInt64((ulong)EventMemory[i].Length);
            foreach (int m in EventMemory[i])
            {
                writer.WriteVarInt64(m);
            }
        }

        writer.WriteVarUInt64((ulong)RuleDisabled.Length);
        foreach (bool b in RuleDisabled)
        {
            writer.WriteBoolean(b);
        }

        writer.WriteVarUInt64((ulong)Timers.Length);
        foreach (TimerState t in Timers)
        {
            writer.WriteByte((byte)((t.Started ? 1 : 0) | (t.Running ? 2 : 0) | (t.CountDown ? 4 : 0)));
            if (t.Started)
            {
                writer.WriteVarInt64(t.ValueMs);
            }
        }

        writer.WriteVarUInt64((ulong)MapEntities.Length);
        for (int i = 0; i < MapEntities.Length; i++)
        {
            writer.WriteVarUInt64(MapEntities[i]);
            writer.WriteBoolean(Hidden[i]);
        }

        writer.WriteVarUInt64((ulong)PlayerStatus.Length);
        writer.WriteBytes(PlayerStatus);
        WriteList(writer, Quests);
        writer.WriteVarUInt64((ulong)Locked.Count);
        foreach (uint e in Locked)
        {
            writer.WriteVarUInt64(e);
        }

        writer.WriteVarInt64(ClockMs);
        writer.WriteVarInt64(PassRule);
        writer.WriteVarInt64(Passes);
        writer.WriteVarInt64(Conversation);
        writer.WriteVarInt64(ConversationSerial);
        writer.WriteVarInt64(ConversationSteps);
        WriteList(writer, Candidates);
        writer.WriteVarInt64(PendingReply);
        WriteList(writer, QueuedConversations);
        writer.WriteVarInt64(MapRequestAction);
        writer.WriteBoolean(Halted);
    }

    /// <summary>Reads a state written by <see cref="WriteTo"/> for a scenario of <paramref name="shape"/>.</summary>
    /// <exception cref="InvalidDataException">The bytes are malformed or do not fit the scenario.</exception>
    public static TriggerState ReadFrom(ReadOnlySpan<byte> data, TriggerShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var s = new TriggerState(shape);
        var r = new BinaryBufferReader(data);
        try
        {
            if (r.ReadByte() != Version)
            {
                throw new InvalidDataException("Unsupported trigger state version.");
            }

            Expect(r.ReadVarUInt64(), shape.Variables, "variables");
            for (int i = 0; i < s.Variables.Length; i++)
            {
                s.Variables[i] = Int(r.ReadVarInt64(), int.MinValue, int.MaxValue, "variable");
            }

            Expect(r.ReadVarUInt64(), shape.Events, "events");
            for (int i = 0; i < s.EventValues.Length; i++)
            {
                s.EventValues[i] = r.ReadBoolean();
                Expect(r.ReadVarUInt64(), shape.EventMemory[i], "event memory");
                for (int k = 0; k < s.EventMemory[i].Length; k++)
                {
                    s.EventMemory[i][k] = Int(r.ReadVarInt64(), 0, int.MaxValue, "event memory");
                }
            }

            Expect(r.ReadVarUInt64(), shape.Rules, "rules");
            for (int i = 0; i < s.RuleDisabled.Length; i++)
            {
                s.RuleDisabled[i] = r.ReadBoolean();
            }

            Expect(r.ReadVarUInt64(), shape.Actions, "timers");
            for (int i = 0; i < s.Timers.Length; i++)
            {
                byte flags = r.ReadByte();
                if ((flags & ~7) != 0 || ((flags & 1) == 0 && flags != 0))
                {
                    throw new InvalidDataException("Invalid timer flags.");
                }

                s.Timers[i] = new TimerState { Started = (flags & 1) != 0, Running = (flags & 2) != 0, CountDown = (flags & 4) != 0 };
                if (s.Timers[i].Started)
                {
                    s.Timers[i].ValueMs = r.ReadVarInt64();
                    if (s.Timers[i].ValueMs < 0 || s.Timers[i].ValueMs > TimerLimitMs)
                    {
                        throw new InvalidDataException("Timer value out of range.");
                    }
                }
            }

            Expect(r.ReadVarUInt64(), shape.Objects, "map objects");
            for (int i = 0; i < s.MapEntities.Length; i++)
            {
                s.MapEntities[i] = UInt(r.ReadVarUInt64(), 0, "entity");
                s.Hidden[i] = r.ReadBoolean();
                if (s.Hidden[i] && s.MapEntities[i] != 0)
                {
                    throw new InvalidDataException("A hidden map object has an entity.");
                }
            }

            Expect(r.ReadVarUInt64(), shape.Players, "players");
            r.ReadBytes(shape.Players).CopyTo(s.PlayerStatus);
            foreach (byte b in s.PlayerStatus)
            {
                if (b > 3)
                {
                    throw new InvalidDataException("Invalid player status.");
                }
            }

            ReadList(ref r, s.Quests, 0, shape.Actions - 1, shape.Actions, distinct: true);
            ulong locked = r.ReadVarUInt64();
            if (locked > MaxLocked)
            {
                throw new InvalidDataException("Too many locked entities.");
            }

            for (ulong i = 0; i < locked; i++)
            {
                uint e = UInt(r.ReadVarUInt64(), 1, "locked entity");
                if (s.Locked.Count > 0 && s.Locked[^1] >= e)
                {
                    throw new InvalidDataException("Locked entities must be ascending.");
                }

                s.Locked.Add(e);
            }

            s.ClockMs = r.ReadVarInt64();
            if (s.ClockMs < 0 || s.ClockMs > TimerLimitMs)
            {
                throw new InvalidDataException("Trigger clock out of range.");
            }

            s.PassRule = Int(r.ReadVarInt64(), -1, shape.Rules - 1, "pass rule");
            s.Passes = Int(r.ReadVarInt64(), 0, int.MaxValue, "passes");
            s.Conversation = Int(r.ReadVarInt64(), NoConversation, shape.Conversations.Length - 1, "conversation");
            s.ConversationSerial = Int(r.ReadVarInt64(), 0, int.MaxValue, "conversation serial");
            s.ConversationSteps = Int(r.ReadVarInt64(), 0, int.MaxValue, "conversation steps");
            int phrases = s.Conversation >= 0 ? shape.Conversations[s.Conversation] : 0;
            ReadList(ref r, s.Candidates, 0, phrases - 1, MaxCandidates, distinct: true);
            s.PendingReply = Int(r.ReadVarInt64(), -1, s.Candidates.Count - 1, "pending reply");
            ReadList(ref r, s.QueuedConversations, 0, shape.Conversations.Length - 1, MaxQueuedConversations, distinct: false);
            s.MapRequestAction = Int(r.ReadVarInt64(), -1, shape.Actions - 1, "map request");
            s.Halted = r.ReadBoolean();
            if (s.Conversation < 0 && (s.Candidates.Count != 0 || s.PendingReply >= 0))
            {
                throw new InvalidDataException("Conversation candidates without a conversation.");
            }

            if (!r.IsAtEnd)
            {
                throw new InvalidDataException("Trailing bytes after the trigger state.");
            }
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException("Malformed trigger state.", e);
        }
        catch (IndexOutOfRangeException e)
        {
            throw new InvalidDataException("Truncated trigger state.", e);
        }

        return s;
    }

    /// <summary>Largest timer or clock value (about 292 million years of game time is far beyond any match).</summary>
    public const long TimerLimitMs = long.MaxValue / 4;

    private static void WriteList(BinaryBufferWriter writer, List<int> list)
    {
        writer.WriteVarUInt64((ulong)list.Count);
        foreach (int v in list)
        {
            writer.WriteVarInt64(v);
        }
    }

    private static void ReadList(ref BinaryBufferReader r, List<int> into, int min, int max, int maxCount, bool distinct)
    {
        ulong count = r.ReadVarUInt64();
        if (count > (ulong)Math.Max(0, maxCount))
        {
            throw new InvalidDataException("List too long in the trigger state.");
        }

        for (ulong i = 0; i < count; i++)
        {
            int v = Int(r.ReadVarInt64(), min, max, "list entry");
            if (distinct && into.Contains(v))
            {
                throw new InvalidDataException("Duplicate entry in the trigger state.");
            }

            into.Add(v);
        }
    }

    private static void Expect(ulong actual, int expected, string what)
    {
        if (actual != (ulong)expected)
        {
            throw new InvalidDataException($"The trigger state has {actual} {what}; the scenario has {expected}.");
        }
    }

    private static int Int(long value, int min, int max, string what)
        => value >= min && value <= max ? (int)value : throw new InvalidDataException($"Trigger state {what} {value} is out of range.");

    private static uint UInt(ulong value, uint min, string what)
        => value >= min && value <= uint.MaxValue ? (uint)value : throw new InvalidDataException($"Trigger state {what} {value} is out of range.");
}
