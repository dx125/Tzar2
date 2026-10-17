using System;
using System.Numerics;
using FsCheck.Xunit;
using Tzar2.Core.Numerics;
using Xunit;

namespace Tzar2.Core.Tests.Numerics;

public class IntMathTests
{
    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(1UL, 1UL)]
    [InlineData(2UL, 1UL)]
    [InlineData(3UL, 1UL)]
    [InlineData(4UL, 2UL)]
    [InlineData(99UL, 9UL)]
    [InlineData(100UL, 10UL)]
    [InlineData(ulong.MaxValue, 4294967295UL)]
    public void ISqrt_ulong(ulong value, ulong expected) => Assert.Equal(expected, IntMath.ISqrt(value));

    [Fact]
    public void ISqrt_negative_long_throws() => Assert.Throws<ArgumentOutOfRangeException>(() => IntMath.ISqrt(-1L));

    [Fact]
    public void ISqrt_uint128_max() => Assert.Equal(ulong.MaxValue, IntMath.ISqrt(UInt128.MaxValue));

    [Property(MaxTest = 5000)]
    public bool ISqrt_is_floor_root(ulong value)
    {
        ulong root = IntMath.ISqrt(value);
        return (UInt128)root * root <= value && ((UInt128)root + 1) * ((UInt128)root + 1) > value;
    }

    [Property(MaxTest = 2000)]
    public bool ISqrt_uint128_is_floor_root(ulong high, ulong low)
    {
        var value = new UInt128(high, low);
        BigInteger root = IntMath.ISqrt(value);
        BigInteger exact = (BigInteger)value;
        return root * root <= exact && (root + 1) * (root + 1) > exact;
    }

    [Theory]
    [InlineData(1000L, 2500, 250L)]
    [InlineData(-1000L, 2500, -250L)]
    [InlineData(7L, 5000, 3L)] // 3.5 truncated
    [InlineData(-7L, 5000, -3L)] // truncated toward zero
    [InlineData(123L, 10000, 123L)]
    [InlineData(5L, 15000, 7L)]
    public void MulBasisPoints_truncates_toward_zero(long value, int basisPoints, long expected)
        => Assert.Equal(expected, IntMath.MulBasisPoints(value, basisPoints));

    [Fact]
    public void MulBasisPoints_overflow_throws()
        => Assert.Throws<OverflowException>(() => IntMath.MulBasisPoints(long.MaxValue, 20_000));

    [Fact]
    public void DistanceSquared_handles_map_scale_coordinates()
    {
        const int MapSpan = 1024 * 2048; // 1,024 tiles of 2,048 world units (TDD §8.2)
        Assert.Equal(2L * MapSpan * MapSpan, IntMath.DistanceSquared(0, 0, MapSpan, MapSpan));
        Assert.Equal(25L, IntMath.DistanceSquared(1, 1, -2, -3));
        Assert.Throws<OverflowException>(() => IntMath.DistanceSquared(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue));
    }
}
