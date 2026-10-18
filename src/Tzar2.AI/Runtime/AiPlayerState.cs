using System;
using System.Collections.Generic;
using System.IO;
using Tzar2.Core.Serialization;

namespace Tzar2.AI.Runtime;

/// <summary>What a goal asks for.</summary>
public enum AiGoalKind : byte
{
    /// <summary>N objects of a class, or N % of the own units.</summary>
    Object = 1,

    /// <summary>N of a resource in the stockpile (gatherers favour it).</summary>
    Resource = 2,

    /// <summary>A technology researched.</summary>
    Technology = 3,

    /// <summary>Tavern conversion of food (GAME-14; no executor yet).</summary>
    Convert = 4,
}

/// <summary>A goal collected by a script pass, in priority (script) order.</summary>
/// <param name="Kind">What it asks for.</param>
/// <param name="Symbol">The program symbol.</param>
/// <param name="Number">The number as written (-1 = omitted).</param>
/// <param name="Percent">Whether the number is a share of the own units.</param>
/// <param name="LockedResources">Resources this goal may not spend: bit r is set while a resource goal for r before an
/// active <c>needs_locked</c> in the pass is unmet (ADR 0050).</param>
/// <param name="Rule">The rule that asked for it.</param>
public readonly record struct AiGoal(AiGoalKind Kind, int Symbol, int Number, bool Percent, byte LockedResources, int Rule);

/// <summary>The attack-wave state machine of the military executor (ADR 0052).</summary>
public enum AiWavePhase : byte
{
    /// <summary>The army gathers at home.</summary>
    Gathering = 0,

    /// <summary>A wave is on its way or fighting.</summary>
    Attacking = 1,
}

/// <summary>What the naval executor does with the current wave (ADR 0144).</summary>
public enum AiNavalPhase : byte
{
    /// <summary>Nothing: the wave walks (or there is none).</summary>
    None = 0,

    /// <summary>The wave boards the transports.</summary>
    Embarking = 1,

    /// <summary>The transports sail to the enemy's shore and unload.</summary>
    Sailing = 2,
}

/// <summary>
/// The persistent state of one AI player (REQ-SIM-13: in snapshots via the AI's world extension; ADR 0051): script
/// flags, forgotten rules, the position of a pass in progress and its goals, the goals of the last complete pass, and
/// the military executor's wave.
/// </summary>
public sealed class AiPlayerState
{
    internal const int MaxWaveUnits = 512;
    internal const int MaxTransports = 16;
    private const byte Version = 2;

    internal AiPlayerState(AiPlayerSettings settings, int flagCount, int ruleCount)
    {
        Settings = settings;
        Flags = new ulong[Words(flagCount)];
        Forgotten = new ulong[Words(ruleCount)];
        Latched = new ulong[Words(ruleCount)];
    }

    /// <summary>The settings.</summary>
    public AiPlayerSettings Settings { get; }

    internal ulong[] Flags { get; }

    internal ulong[] Forgotten { get; }

    // Rules that were active when last visited (a 'say' speaks when its rule becomes active).
    internal ulong[] Latched { get; }

    /// <summary>The next rule of a pass in progress.</summary>
    public int Pc { get; internal set; }

    /// <summary>Whether a pass is in progress.</summary>
    public bool InPass { get; internal set; }

    /// <summary>The tick at which the next pass may start.</summary>
    public int NextPassTick { get; internal set; }

    /// <summary>Completed passes.</summary>
    public int PassCount { get; internal set; }

    internal byte UnmetResources { get; set; }

    internal byte LockedResources { get; set; }

    /// <summary>Whether the script asked to resign (and the lobby allows it); executed once the sim has a resign command.</summary>
    public bool ResignRequested { get; internal set; }

    internal List<AiGoal> Pending { get; } = [];

    internal List<AiGoal> Current { get; } = [];

    /// <summary>The goals of the last complete pass, in priority order.</summary>
    public IReadOnlyList<AiGoal> Goals => Current;

    internal AiWavePhase WavePhase { get; set; }

    internal int WaveCount { get; set; }

    /// <summary>Attack waves launched so far.</summary>
    public int Waves => WaveCount;

    internal int WaveLaunchSize { get; set; }

    internal int LastWaveTick { get; set; } = int.MinValue / 2;

    internal int TargetPlayer { get; set; } = -1;

    internal int TargetX { get; set; }

    internal int TargetY { get; set; }

    internal List<uint> WaveUnits { get; } = [];

    internal AiNavalPhase NavalPhase { get; set; }

    internal int NavalTick { get; set; }

    internal List<uint> Transports { get; } = [];

    /// <summary>Whether an enemy town was overseas at the last look (<see cref="OverseasTick"/>).</summary>
    internal bool Overseas { get; set; }

    internal int OverseasTick { get; set; } = int.MinValue / 2;

    /// <summary>Waves carried across the water so far (ADR 0144).</summary>
    public int Landings { get; internal set; }

    /// <summary>Whether flag <paramref name="flag"/> (program flag index) is set.</summary>
    public bool IsFlagSet(int flag) => Get(Flags, flag);

    /// <summary>Whether rule <paramref name="rule"/> was forgotten.</summary>
    public bool IsForgotten(int rule) => Get(Forgotten, rule);

    internal static bool Get(ulong[] bits, int i) => (bits[i >> 6] & (1UL << (i & 63))) != 0;

    internal static void Set(ulong[] bits, int i, bool value)
    {
        if (value)
        {
            bits[i >> 6] |= 1UL << (i & 63);
        }
        else
        {
            bits[i >> 6] &= ~(1UL << (i & 63));
        }
    }

    // Clears bits [from, to).
    internal static void ClearRange(ulong[] bits, int from, int to)
    {
        for (int i = from; i < to;)
        {
            if ((i & 63) == 0 && i + 64 <= to)
            {
                bits[i >> 6] = 0;
                i += 64;
            }
            else
            {
                bits[i >> 6] &= ~(1UL << (i & 63));
                i++;
            }
        }
    }

    internal void WriteTo(BinaryBufferWriter w)
    {
        w.WriteByte(Version);
        w.WriteByte(Settings.Slot);
        w.WriteUInt64(Settings.Program.ContentHash);
        w.WriteByte((byte)Settings.Difficulty);
        w.WriteString(Settings.Civilization);
        w.WriteByte((byte)((Settings.EconomyAi ? 1 : 0) | (Settings.WarAi ? 2 : 0) | (Settings.MayResign ? 4 : 0)));
        WriteBits(w, Flags);
        WriteBits(w, Forgotten);
        WriteBits(w, Latched);
        w.WriteVarInt64(Pc);
        w.WriteBoolean(InPass);
        w.WriteVarInt64(NextPassTick);
        w.WriteVarInt64(PassCount);
        w.WriteByte(UnmetResources);
        w.WriteByte(LockedResources);
        w.WriteBoolean(ResignRequested);
        WriteGoals(w, Pending);
        WriteGoals(w, Current);
        w.WriteByte((byte)WavePhase);
        w.WriteVarInt64(WaveCount);
        w.WriteVarInt64(WaveLaunchSize);
        w.WriteVarInt64(LastWaveTick);
        w.WriteVarInt64(TargetPlayer);
        w.WriteVarInt64(TargetX);
        w.WriteVarInt64(TargetY);
        w.WriteVarUInt64((ulong)WaveUnits.Count);
        foreach (uint unit in WaveUnits)
        {
            w.WriteUInt32(unit);
        }

        w.WriteByte((byte)NavalPhase);
        w.WriteVarInt64(NavalTick);
        w.WriteBoolean(Overseas);
        w.WriteVarInt64(OverseasTick);
        w.WriteVarInt64(Landings);
        w.WriteVarUInt64((ulong)Transports.Count);
        foreach (uint ship in Transports)
        {
            w.WriteUInt32(ship);
        }
    }

    internal void ReadFrom(ref BinaryBufferReader r, int maxGoals)
    {
        if (r.ReadByte() != Version || r.ReadByte() != Settings.Slot || r.ReadUInt64() != Settings.Program.ContentHash
            || r.ReadByte() != (byte)Settings.Difficulty || !string.Equals(r.ReadString(64), Settings.Civilization, StringComparison.Ordinal)
            || r.ReadByte() != (byte)((Settings.EconomyAi ? 1 : 0) | (Settings.WarAi ? 2 : 0) | (Settings.MayResign ? 4 : 0)))
        {
            throw new InvalidDataException($"The AI state of slot {Settings.Slot} was saved with another script or settings.");
        }

        int rules = Settings.Program.Rules.Length;
        ReadBits(ref r, Flags, Settings.Program.Flags.Length);
        ReadBits(ref r, Forgotten, rules);
        ReadBits(ref r, Latched, rules);
        Pc = ReadInt(ref r, 0, rules);
        InPass = r.ReadBoolean();
        NextPassTick = ReadInt(ref r, int.MinValue, int.MaxValue);
        PassCount = ReadInt(ref r, 0, int.MaxValue);
        UnmetResources = r.ReadByte();
        LockedResources = r.ReadByte();
        ResignRequested = r.ReadBoolean();
        ReadGoals(ref r, Pending, maxGoals);
        ReadGoals(ref r, Current, maxGoals);
        byte phase = r.ReadByte();
        if (phase > (byte)AiWavePhase.Attacking)
        {
            throw new InvalidDataException("Invalid AI wave phase.");
        }

        WavePhase = (AiWavePhase)phase;
        WaveCount = ReadInt(ref r, 0, int.MaxValue);
        WaveLaunchSize = ReadInt(ref r, 0, int.MaxValue);
        LastWaveTick = ReadInt(ref r, int.MinValue, int.MaxValue);
        TargetPlayer = ReadInt(ref r, -1, 255);
        TargetX = ReadInt(ref r, int.MinValue, int.MaxValue);
        TargetY = ReadInt(ref r, int.MinValue, int.MaxValue);
        ulong units = r.ReadVarUInt64();
        if (units > MaxWaveUnits)
        {
            throw new InvalidDataException("Too many AI wave units.");
        }

        WaveUnits.Clear();
        for (ulong i = 0; i < units; i++)
        {
            WaveUnits.Add(r.ReadUInt32());
        }

        byte naval = r.ReadByte();
        if (naval > (byte)AiNavalPhase.Sailing)
        {
            throw new InvalidDataException("Invalid AI naval phase.");
        }

        NavalPhase = (AiNavalPhase)naval;
        NavalTick = ReadInt(ref r, int.MinValue, int.MaxValue);
        Overseas = r.ReadBoolean();
        OverseasTick = ReadInt(ref r, int.MinValue, int.MaxValue);
        Landings = ReadInt(ref r, 0, int.MaxValue);
        ulong ships = r.ReadVarUInt64();
        if (ships > MaxTransports || (NavalPhase == AiNavalPhase.None && ships != 0))
        {
            throw new InvalidDataException("Invalid AI transports.");
        }

        Transports.Clear();
        for (ulong i = 0; i < ships; i++)
        {
            Transports.Add(r.ReadUInt32());
        }
    }

    private static int Words(int bits) => (bits + 63) >> 6;

    private static void WriteBits(BinaryBufferWriter w, ulong[] bits)
    {
        w.WriteVarUInt64((ulong)bits.Length);
        foreach (ulong word in bits)
        {
            w.WriteUInt64(word);
        }
    }

    private static void ReadBits(ref BinaryBufferReader r, ulong[] bits, int count)
    {
        if (r.ReadVarUInt64() != (ulong)bits.Length)
        {
            throw new InvalidDataException("AI state bit set has the wrong size.");
        }

        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = r.ReadUInt64();
        }

        // Bits past the count must be clear (canonical state: equal states give equal snapshots and hashes).
        if ((count & 63) != 0 && (bits[^1] >> (count & 63)) != 0)
        {
            throw new InvalidDataException("AI state bit set has bits beyond its size.");
        }
    }

    private void WriteGoals(BinaryBufferWriter w, List<AiGoal> goals)
    {
        w.WriteVarUInt64((ulong)goals.Count);
        foreach (AiGoal g in goals)
        {
            w.WriteByte((byte)g.Kind);
            w.WriteVarInt64(g.Symbol);
            w.WriteVarInt64(g.Number);
            w.WriteBoolean(g.Percent);
            w.WriteByte(g.LockedResources);
            w.WriteVarInt64(g.Rule);
        }
    }

    private void ReadGoals(ref BinaryBufferReader r, List<AiGoal> goals, int maxGoals)
    {
        ulong count = r.ReadVarUInt64();
        if (count > (ulong)maxGoals)
        {
            throw new InvalidDataException("Too many AI goals.");
        }

        goals.Clear();
        int symbols = Settings.Program.Symbols.Length;
        int rules = Settings.Program.Rules.Length;
        for (ulong i = 0; i < count; i++)
        {
            byte kind = r.ReadByte();
            if (kind is 0 or > (byte)AiGoalKind.Convert)
            {
                throw new InvalidDataException("Invalid AI goal kind.");
            }

            goals.Add(new AiGoal((AiGoalKind)kind, ReadInt(ref r, 0, symbols - 1), ReadInt(ref r, -1, int.MaxValue), r.ReadBoolean(), r.ReadByte(), ReadInt(ref r, 0, rules - 1)));
        }
    }

    private static int ReadInt(ref BinaryBufferReader r, int min, int max)
    {
        long value = r.ReadVarInt64();
        if (value < min || value > max)
        {
            throw new InvalidDataException("AI state value out of range.");
        }

        return (int)value;
    }
}
