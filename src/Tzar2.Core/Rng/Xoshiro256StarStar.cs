using System;
using System.Numerics;
using Tzar2.Core.Hashing;
using Tzar2.Core.Numerics;
using Tzar2.Core.Serialization;

namespace Tzar2.Core.Rng;

/// <summary>
/// xoshiro256** 1.0 (Blackman and Vigna), the simulation's deterministic random generator (REQ-SIM-06).
/// This is a mutable struct: keep it in a field or array and call methods on it by reference.
/// </summary>
public struct Xoshiro256StarStar
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    /// <summary>Creates a generator from an explicit state.</summary>
    /// <exception cref="ArgumentException">All four state words are zero.</exception>
    public Xoshiro256StarStar(ulong s0, ulong s1, ulong s2, ulong s3)
    {
        if ((s0 | s1 | s2 | s3) == 0)
        {
            throw new ArgumentException("The xoshiro256** state must not be all zero.");
        }

        _s0 = s0;
        _s1 = s1;
        _s2 = s2;
        _s3 = s3;
    }

    /// <summary>Creates a generator whose state is four consecutive SplitMix64 outputs of <paramref name="seed"/> (the reference seeding procedure).</summary>
    public static Xoshiro256StarStar FromSeed(ulong seed)
    {
        var splitMix = new SplitMix64(seed);
        // SplitMix64 is a bijection over a counter, so four consecutive outputs are never all zero.
        return new Xoshiro256StarStar(splitMix.Next(), splitMix.Next(), splitMix.Next(), splitMix.Next());
    }

    /// <summary>The four state words, for snapshots and diagnostics.</summary>
    public readonly (ulong S0, ulong S1, ulong S2, ulong S3) State => (_s0, _s1, _s2, _s3);

    /// <summary>Returns the next 64 random bits.</summary>
    public ulong NextUInt64()
    {
        unchecked
        {
            ulong result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
            ulong t = _s1 << 17;
            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = BitOperations.RotateLeft(_s3, 45);
            return result;
        }
    }

    /// <summary>Returns the next 32 random bits (the high half of <see cref="NextUInt64"/>).</summary>
    public uint NextUInt32() => (uint)(NextUInt64() >> 32);

    /// <summary>Uniform integer in [0, <paramref name="maxExclusive"/>), unbiased (Lemire's method).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxExclusive"/> is not positive.</exception>
    public int NextInt(int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);
        return (int)NextBounded((uint)maxExclusive);
    }

    /// <summary>Uniform integer in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>), unbiased.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is empty.</exception>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "maxExclusive must be greater than minInclusive.");
        }

        uint range = (uint)((long)maxExclusive - minInclusive);
        return (int)(minInclusive + NextBounded(range));
    }

    /// <summary>A fair coin flip.</summary>
    public bool NextBool() => (NextUInt64() >> 63) != 0;

    /// <summary>True with probability <paramref name="basisPoints"/>/10,000 (values outside [0, 10,000] saturate).</summary>
    public bool NextChance(int basisPoints) => NextInt(IntMath.BasisPointsPerUnit) < basisPoints;

    /// <summary>Uniform <see cref="Fixed"/> in [0, 1).</summary>
    public Fixed NextFixed() => Fixed.FromRaw((long)(NextUInt64() >> (64 - Fixed.FractionBits)));

    /// <summary>Advances the state by 2^128 steps (reference jump function), giving a non-overlapping subsequence.</summary>
    public void Jump()
    {
        ReadOnlySpan<ulong> jump = [0x180EC6D33CFD0ABAUL, 0xD5A61266F0C9392CUL, 0xA9582618E03FC9AAUL, 0x39ABDC4529B1661CUL];
        ulong s0 = 0, s1 = 0, s2 = 0, s3 = 0;
        foreach (ulong word in jump)
        {
            for (int bit = 0; bit < 64; bit++)
            {
                if ((word & (1UL << bit)) != 0)
                {
                    s0 ^= _s0;
                    s1 ^= _s1;
                    s2 ^= _s2;
                    s3 ^= _s3;
                }

                NextUInt64();
            }
        }

        _s0 = s0;
        _s1 = s1;
        _s2 = s2;
        _s3 = s3;
    }

    /// <summary>Writes the state (32 bytes, little-endian) for snapshots.</summary>
    public readonly void WriteTo(BinaryBufferWriter writer)
    {
        writer.WriteUInt64(_s0);
        writer.WriteUInt64(_s1);
        writer.WriteUInt64(_s2);
        writer.WriteUInt64(_s3);
    }

    /// <summary>Reads a state written by <see cref="WriteTo"/>.</summary>
    /// <exception cref="ArgumentException">The stored state is all zero.</exception>
    public static Xoshiro256StarStar ReadFrom(ref BinaryBufferReader reader)
        => new(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64());

    /// <summary>Adds the state to a state hash.</summary>
    public readonly void AppendTo(StateHasher hasher)
    {
        hasher.Add(_s0);
        hasher.Add(_s1);
        hasher.Add(_s2);
        hasher.Add(_s3);
    }

    private uint NextBounded(uint range)
    {
        uint x = NextUInt32();
        ulong m = (ulong)x * range;
        uint low = (uint)m;
        if (low < range)
        {
            uint threshold = unchecked(0u - range) % range;
            while (low < threshold)
            {
                x = NextUInt32();
                m = (ulong)x * range;
                low = (uint)m;
            }
        }

        return (uint)(m >> 32);
    }
}
