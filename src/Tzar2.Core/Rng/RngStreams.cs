using System;
using Tzar2.Core.Hashing;
using Tzar2.Core.Serialization;

namespace Tzar2.Core.Rng;

/// <summary>The independent named random streams of REQ-SIM-06.</summary>
public enum RngStreamKind : byte
{
    /// <summary>Combat rolls.</summary>
    Combat = 1,

    /// <summary>Per-player AI decisions (one stream per player).</summary>
    Ai = 2,

    /// <summary>Random map generation.</summary>
    MapGen = 3,

    /// <summary>Triggers and Lua scripts.</summary>
    Scripts = 4,

    /// <summary>Anything else that affects the simulation.</summary>
    Misc = 5,
}

/// <summary>
/// The set of simulation random streams of one match, all derived from the match seed (REQ-SIM-06).
/// Drawing from one stream never changes another, so e.g. adding an AI player does not alter combat rolls.
/// Presentation-only randomness must not use these streams.
/// </summary>
/// <remarks>
/// <para>
/// <b>Copy trap.</b> <see cref="Xoshiro256StarStar"/> is a mutable struct and the stream accessors return it
/// <em>by reference</em>. Draw directly through the accessor or through a <c>ref</c> local:
/// </para>
/// <code>
/// int roll = streams.Combat.NextInt(100);        // advances the match stream
/// ref Xoshiro256StarStar rng = ref streams.Combat; // advances the match stream
/// rng.NextInt(100);
/// </code>
/// <para>
/// Assigning the result to a plain local or field (<c>var rng = streams.Combat;</c> or <c>Xoshiro256StarStar rng =
/// streams.Ai(p);</c>) or passing it by value <b>copies</b> the state: draws from the copy do not advance the match
/// stream, so the same numbers are produced again later and peers that copy at different points desync. Pass streams
/// to helpers as <c>ref Xoshiro256StarStar</c>.
/// </para>
/// </remarks>
public sealed class RngStreams
{
    private const int FixedStreamCount = 4; // Combat, MapGen, Scripts, Misc

    private readonly Xoshiro256StarStar[] _streams;

    /// <summary>Creates the streams for a match.</summary>
    /// <param name="matchSeed">The match seed.</param>
    /// <param name="playerCount">Number of players that get an <see cref="RngStreamKind.Ai"/> stream.</param>
    public RngStreams(ulong matchSeed, int playerCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(playerCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(playerCount, byte.MaxValue);
        MatchSeed = matchSeed;
        _streams = new Xoshiro256StarStar[FixedStreamCount + playerCount];
        _streams[0] = Derive(matchSeed, RngStreamKind.Combat, 0);
        _streams[1] = Derive(matchSeed, RngStreamKind.MapGen, 0);
        _streams[2] = Derive(matchSeed, RngStreamKind.Scripts, 0);
        _streams[3] = Derive(matchSeed, RngStreamKind.Misc, 0);
        for (int player = 0; player < playerCount; player++)
        {
            _streams[FixedStreamCount + player] = Derive(matchSeed, RngStreamKind.Ai, player);
        }
    }

    /// <summary>The seed all streams derive from.</summary>
    public ulong MatchSeed { get; }

    /// <summary>Number of per-player AI streams.</summary>
    public int PlayerCount => _streams.Length - FixedStreamCount;

    /// <summary>The <see cref="RngStreamKind.Combat"/> stream, by reference (do not copy it into a non-<c>ref</c> local; see remarks).</summary>
    public ref Xoshiro256StarStar Combat => ref _streams[0];

    /// <summary>The <see cref="RngStreamKind.MapGen"/> stream, by reference (do not copy it into a non-<c>ref</c> local; see remarks).</summary>
    public ref Xoshiro256StarStar MapGen => ref _streams[1];

    /// <summary>The <see cref="RngStreamKind.Scripts"/> stream, by reference (do not copy it into a non-<c>ref</c> local; see remarks).</summary>
    public ref Xoshiro256StarStar Scripts => ref _streams[2];

    /// <summary>The <see cref="RngStreamKind.Misc"/> stream, by reference (do not copy it into a non-<c>ref</c> local; see remarks).</summary>
    public ref Xoshiro256StarStar Misc => ref _streams[3];

    /// <summary>The <see cref="RngStreamKind.Ai"/> stream of <paramref name="player"/>, by reference (do not copy it into a non-<c>ref</c> local; see remarks).</summary>
    public ref Xoshiro256StarStar Ai(int player)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(player);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(player, PlayerCount);
        return ref _streams[FixedStreamCount + player];
    }

    /// <summary>The initial state of stream (<paramref name="kind"/>, <paramref name="index"/>) for <paramref name="matchSeed"/>.</summary>
    public static Xoshiro256StarStar Derive(ulong matchSeed, RngStreamKind kind, int index)
    {
        ulong key = ((ulong)kind << 32) | (uint)index;
        return Xoshiro256StarStar.FromSeed(SplitMix64.Mix(matchSeed ^ SplitMix64.Mix(key)));
    }

    /// <summary>Writes all stream states for snapshots.</summary>
    public void WriteTo(BinaryBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt64(MatchSeed);
        writer.WriteByte((byte)PlayerCount);
        foreach (Xoshiro256StarStar stream in _streams)
        {
            stream.WriteTo(writer);
        }
    }

    /// <summary>Restores streams written by <see cref="WriteTo"/>.</summary>
    public static RngStreams ReadFrom(ref BinaryBufferReader reader)
    {
        ulong seed = reader.ReadUInt64();
        int playerCount = reader.ReadByte();
        var streams = new RngStreams(seed, playerCount);
        for (int i = 0; i < streams._streams.Length; i++)
        {
            streams._streams[i] = Xoshiro256StarStar.ReadFrom(ref reader);
        }

        return streams;
    }

    /// <summary>Adds all stream states to a state hash.</summary>
    public void AppendTo(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        foreach (Xoshiro256StarStar stream in _streams)
        {
            stream.AppendTo(hasher);
        }
    }
}
