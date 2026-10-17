using System;
using System.Collections.Generic;
using System.IO;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Core.Serialization;
using Tzar2.Sim;
using Tzar2.Sim.Commands;

namespace Tzar2.Replay;

/// <summary>The header of a <c>.t2rep</c> replay (REQ-NET-09): what is needed to re-simulate and verify it.</summary>
public sealed record ReplayHeader
{
    /// <summary>Engine version that recorded the replay (compatibility: same <c>major.minor</c>, §19.4).</summary>
    public string EngineVersion { get; init; } = "0.0.0";

    /// <summary>Ruleset content hash.</summary>
    public ulong RulesetHash { get; init; }

    /// <summary>Map content hash.</summary>
    public ulong MapHash { get; init; }

    /// <summary>The match configuration.</summary>
    public SimulationConfig Config { get; init; } = new();

    /// <summary>Overflow policy of the recording build (ADR 0014).</summary>
    public OverflowMode OverflowMode { get; init; } = OverflowPolicy.Mode;

    /// <summary>Ticks between stored per-tick hashes (REQ-NET-04 uses 10).</summary>
    public int HashIntervalTicks { get; init; } = 10;

    /// <summary>Ticks between keyframe snapshots (REQ-NET-09: every 60 s of game time = 1,200 ticks at 20 Hz).</summary>
    public int KeyframeIntervalTicks { get; init; } = 1200;

    /// <summary>Creates the header for a simulation at tick 0.</summary>
    public static ReplayHeader For(Simulation simulation, string engineVersion)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        return new ReplayHeader
        {
            EngineVersion = engineVersion,
            RulesetHash = simulation.RulesetHash,
            MapHash = simulation.MapHash,
            Config = simulation.World.Config,
        };
    }
}

/// <summary>One recorded tick: its closed command set and, on checkpoint ticks, the hashes after it.</summary>
public sealed class ReplayTick
{
    internal ReplayTick(Tick tick, CommandList commands, ulong? stateHash, ulong? fullHash)
    {
        Tick = tick;
        Commands = commands;
        StateHash = stateHash;
        FullHash = fullHash;
    }

    /// <summary>The tick.</summary>
    public Tick Tick { get; }

    /// <summary>The stamped command set.</summary>
    public CommandList Commands { get; }

    /// <summary>Per-tick hash after the tick, if stored.</summary>
    public ulong? StateHash { get; }

    /// <summary>Full-state hash after the tick, if stored.</summary>
    public ulong? FullHash { get; }
}

/// <summary>
/// Records a match as a <c>.t2rep</c> byte stream (REQ-NET-09, ADR 0016): header, then per tick the command set and
/// the hashes on checkpoint ticks, plus a keyframe snapshot every <see cref="ReplayHeader.KeyframeIntervalTicks"/>.
/// </summary>
/// <remarks>
/// Layout: magic <c>T2RP</c>, format version, header, then records <c>tag</c> byte + body: 1 = tick (command set via
/// <see cref="CommandCodec"/>, flags byte, optional state hash, optional full hash), 2 = keyframe (tick, length,
/// snapshot bytes), 0 = end (tick count). Recording allocates only when the internal buffer grows.
/// </remarks>
public sealed class ReplayRecorder
{
    /// <summary>"T2RP" little-endian.</summary>
    public const uint Magic = 0x50523254;

    /// <summary>Current format version.</summary>
    public const int FormatVersion = 1;

    internal const byte TagEnd = 0;
    internal const byte TagTick = 1;
    internal const byte TagKeyframe = 2;

    private readonly BinaryBufferWriter _writer = new(64 * 1024);
    private int _ticks;
    private bool _finished;

    /// <summary>Starts a recording.</summary>
    public ReplayRecorder(ReplayHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentOutOfRangeException.ThrowIfLessThan(header.HashIntervalTicks, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(header.KeyframeIntervalTicks, 1);
        Header = header;
        _writer.WriteUInt32(Magic);
        _writer.WriteVarUInt64(FormatVersion);
        _writer.WriteString(header.EngineVersion);
        _writer.WriteUInt64(header.RulesetHash);
        _writer.WriteUInt64(header.MapHash);
        SimulationConfig c = header.Config;
        _writer.WriteUInt64(c.MatchSeed);
        _writer.WriteVarUInt64((ulong)c.PlayerCount);
        _writer.WriteBoolean(c.CheatsAllowed);
        _writer.WriteVarUInt64((ulong)c.FullHashIntervalTicks);
        _writer.WriteVarUInt64((ulong)c.TickLengthMilliseconds);
        _writer.WriteByte((byte)header.OverflowMode);
        _writer.WriteVarUInt64((ulong)header.HashIntervalTicks);
        _writer.WriteVarUInt64((ulong)header.KeyframeIntervalTicks);
    }

    /// <summary>The header.</summary>
    public ReplayHeader Header { get; }

    /// <summary>Ticks recorded.</summary>
    public int TickCount => _ticks;

    /// <summary>Recorded bytes so far.</summary>
    public int Length => _writer.Length;

    /// <summary>Whether a keyframe is due before tick <paramref name="tick"/> runs.</summary>
    public bool IsKeyframeDue(Tick tick) => tick.Value % Header.KeyframeIntervalTicks == 0;

    /// <summary>Records a keyframe snapshot of the state before tick <paramref name="tick"/>.</summary>
    public void RecordKeyframe(Tick tick, ReadOnlySpan<byte> snapshot)
    {
        ThrowIfFinished();
        if (tick.Value != _ticks)
        {
            throw new InvalidOperationException($"Keyframe for tick {tick.Value}, but {_ticks} ticks are recorded.");
        }

        _writer.WriteByte(TagKeyframe);
        _writer.WriteVarUInt64((ulong)tick.Value);
        _writer.WriteVarUInt64((ulong)snapshot.Length);
        _writer.WriteBytes(snapshot);
    }

    /// <summary>Records the command set that ran in <paramref name="result"/>'s tick and its hashes.</summary>
    public void RecordTick(CommandList commands, TickResult result)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ThrowIfFinished();
        if (result.Tick.Value != _ticks)
        {
            throw new InvalidOperationException($"Expected tick {_ticks}, got {result.Tick.Value}.");
        }

        _writer.WriteByte(TagTick);
        CommandCodec.WriteTickSet(_writer, result.Tick, commands);
        bool checkpoint = (result.Tick.Value + 1) % Header.HashIntervalTicks == 0;
        byte flags = (byte)((checkpoint ? 1 : 0) | (result.FullHash.HasValue ? 2 : 0));
        _writer.WriteByte(flags);
        if (checkpoint)
        {
            _writer.WriteUInt64(result.StateHash);
        }

        if (result.FullHash is { } full)
        {
            _writer.WriteUInt64(full);
        }

        _ticks++;
    }

    /// <summary>Ends the recording and returns the replay bytes.</summary>
    public byte[] Finish()
    {
        if (!_finished)
        {
            _writer.WriteByte(TagEnd);
            _writer.WriteVarUInt64((ulong)_ticks);
            _finished = true;
        }

        return _writer.ToArray();
    }

    private void ThrowIfFinished()
    {
        if (_finished)
        {
            throw new InvalidOperationException("The recording is finished.");
        }
    }
}

/// <summary>A parsed replay.</summary>
public sealed class ReplayFile
{
    /// <summary>Largest accepted keyframe (REQ-NET-11: bounded parsing).</summary>
    public const int MaxKeyframeBytes = 256 << 20;

    private readonly List<ReplayTick> _ticks;
    private readonly List<(Tick Tick, byte[] Snapshot)> _keyframes;

    private ReplayFile(ReplayHeader header, List<ReplayTick> ticks, List<(Tick, byte[])> keyframes)
    {
        Header = header;
        _ticks = ticks;
        _keyframes = keyframes;
    }

    /// <summary>The header.</summary>
    public ReplayHeader Header { get; }

    /// <summary>All ticks in order (tick i at index i).</summary>
    public IReadOnlyList<ReplayTick> Ticks => _ticks;

    /// <summary>Keyframes in ascending tick order; each is the state before its tick.</summary>
    public IReadOnlyList<(Tick Tick, byte[] Snapshot)> Keyframes => _keyframes;

    /// <summary>The latest keyframe at or before <paramref name="tick"/>, or <c>null</c>.</summary>
    public (Tick Tick, byte[] Snapshot)? KeyframeAtOrBefore(Tick tick)
    {
        (Tick, byte[])? best = null;
        foreach ((Tick Tick, byte[] Snapshot) keyframe in _keyframes)
        {
            if (keyframe.Tick <= tick)
            {
                best = keyframe;
            }
        }

        return best;
    }

    /// <summary>Parses replay bytes.</summary>
    /// <exception cref="InvalidDataException">The data is malformed or of an unsupported version.</exception>
    public static ReplayFile Parse(ReadOnlySpan<byte> data)
    {
        var reader = new BinaryBufferReader(data);
        if (reader.ReadUInt32() != ReplayRecorder.Magic)
        {
            throw new InvalidDataException("Not a Tzar2 replay.");
        }

        if (reader.ReadVarUInt64() != ReplayRecorder.FormatVersion)
        {
            throw new InvalidDataException("Unsupported replay format version.");
        }

        string engine = reader.ReadString(256);
        ulong rulesetHash = reader.ReadUInt64();
        ulong mapHash = reader.ReadUInt64();
        ulong seed = reader.ReadUInt64();
        int players = ReadInt(ref reader, 16);
        bool cheats = reader.ReadBoolean();
        int fullInterval = ReadInt(ref reader, int.MaxValue);
        int tickLength = ReadInt(ref reader, 1000);
        byte overflow = reader.ReadByte();
        int hashInterval = ReadInt(ref reader, int.MaxValue);
        int keyframeInterval = ReadInt(ref reader, int.MaxValue);
        if (players < 1 || fullInterval < 1 || tickLength < 1 || hashInterval < 1 || keyframeInterval < 1 || overflow > 1)
        {
            throw new InvalidDataException("Invalid replay header.");
        }

        var header = new ReplayHeader
        {
            EngineVersion = engine,
            RulesetHash = rulesetHash,
            MapHash = mapHash,
            Config = new SimulationConfig
            {
                MatchSeed = seed,
                PlayerCount = players,
                CheatsAllowed = cheats,
                FullHashIntervalTicks = fullInterval,
                TickLengthMilliseconds = tickLength,
            },
            OverflowMode = (OverflowMode)overflow,
            HashIntervalTicks = hashInterval,
            KeyframeIntervalTicks = keyframeInterval,
        };

        var ticks = new List<ReplayTick>();
        var keyframes = new List<(Tick, byte[])>();
        while (true)
        {
            byte tag = reader.ReadByte();
            switch (tag)
            {
                case ReplayRecorder.TagTick:
                {
                    var commands = new CommandList();
                    Tick tick = CommandCodec.ReadTickSet(ref reader, commands);
                    if (tick.Value != ticks.Count)
                    {
                        throw new InvalidDataException($"Tick {tick.Value} out of order.");
                    }

                    byte flags = reader.ReadByte();
                    if (flags > 3)
                    {
                        throw new InvalidDataException("Invalid tick flags.");
                    }

                    ulong? state = (flags & 1) != 0 ? reader.ReadUInt64() : null;
                    ulong? full = (flags & 2) != 0 ? reader.ReadUInt64() : null;
                    ticks.Add(new ReplayTick(tick, commands, state, full));
                    break;
                }

                case ReplayRecorder.TagKeyframe:
                {
                    int tick = ReadInt(ref reader, int.MaxValue);
                    if (tick != ticks.Count)
                    {
                        throw new InvalidDataException($"Keyframe for tick {tick} out of order.");
                    }

                    int length = ReadInt(ref reader, MaxKeyframeBytes);
                    keyframes.Add((new Tick(tick), reader.ReadBytes(length).ToArray()));
                    break;
                }

                case ReplayRecorder.TagEnd:
                {
                    if (ReadInt(ref reader, int.MaxValue) != ticks.Count || !reader.IsAtEnd)
                    {
                        throw new InvalidDataException("Replay end record does not match its contents.");
                    }

                    return new ReplayFile(header, ticks, keyframes);
                }

                default:
                    throw new InvalidDataException($"Unknown replay record tag {tag}.");
            }
        }
    }

    private static int ReadInt(ref BinaryBufferReader reader, int max)
    {
        ulong value = reader.ReadVarUInt64();
        if (value > (ulong)max)
        {
            throw new InvalidDataException($"Value {value} exceeds {max}.");
        }

        return (int)value;
    }
}
