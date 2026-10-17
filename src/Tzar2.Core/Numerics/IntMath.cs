using System;
using System.Numerics;

namespace Tzar2.Core.Numerics;

/// <summary>Integer-only math helpers used by the deterministic simulation (TDD §8.2).</summary>
public static class IntMath
{
    /// <summary>One basis point is 1/10,000.</summary>
    public const int BasisPointsPerUnit = 10_000;

    /// <summary>Floor of the square root of <paramref name="value"/>, by integer Newton iteration.</summary>
    public static ulong ISqrt(ulong value)
    {
        if (value < 2)
        {
            return value;
        }

        // Initial guess 2^ceil(bits/2) is always >= sqrt(value), so the iteration decreases monotonically.
        int shift = (BitOperations.Log2(value) >> 1) + 1;
        ulong x = 1UL << shift;
        while (true)
        {
            ulong y = (x + (value / x)) >> 1;
            if (y >= x)
            {
                return x;
            }

            x = y;
        }
    }

    /// <summary>Floor of the square root of a non-negative <paramref name="value"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    public static long ISqrt(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        return (long)ISqrt((ulong)value);
    }

    /// <summary>Floor of the square root of <paramref name="value"/> for 128-bit inputs.</summary>
    public static ulong ISqrt(UInt128 value)
    {
        if (value <= ulong.MaxValue)
        {
            return ISqrt((ulong)value);
        }

        int bits = 128 - (int)UInt128.LeadingZeroCount(value);
        UInt128 x = UInt128.One << ((bits >> 1) + 1);
        while (true)
        {
            UInt128 y = (x + (value / x)) >> 1;
            if (y >= x)
            {
                return (ulong)x;
            }

            x = y;
        }
    }

    /// <summary>
    /// Applies a basis-point factor: <c>value * basisPoints / 10,000</c>, truncated toward zero
    /// (the default rounding of TDD §8.2 until the original's rounding is known, UNK-08).
    /// </summary>
    /// <exception cref="OverflowException">The result does not fit in a <see cref="long"/>.</exception>
    public static long MulBasisPoints(long value, int basisPoints)
    {
        Int128 product = (Int128)value * basisPoints / BasisPointsPerUnit;
        return checked((long)product);
    }

    /// <summary>Squared Euclidean distance between two points in world units (TDD §8.2: squared distances use <c>long</c>).</summary>
    /// <exception cref="OverflowException">The result does not fit in a <see cref="long"/> (only possible far outside any map).</exception>
    public static long DistanceSquared(int x1, int y1, int x2, int y2)
    {
        long dx = (long)x2 - x1;
        long dy = (long)y2 - y1;
        return checked((dx * dx) + (dy * dy));
    }
}
