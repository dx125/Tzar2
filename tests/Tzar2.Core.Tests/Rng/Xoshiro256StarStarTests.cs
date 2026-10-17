using System;
using FsCheck.Xunit;
using Tzar2.Core.Numerics;
using Tzar2.Core.Rng;
using Tzar2.Core.Serialization;
using Xunit;

namespace Tzar2.Core.Tests.Rng;

public class Xoshiro256StarStarTests
{
    // Reference outputs computed with Vigna's C reference implementation (xoshiro256starstar.c, splitmix64.c).

    [Fact]
    public void Matches_reference_for_state_1_2_3_4()
    {
        var rng = new Xoshiro256StarStar(1, 2, 3, 4);
        ulong[] expected = [11520UL, 0UL, 1509978240UL, 1215971899390074240UL, 1216172134540287360UL, 607988272756665600UL];
        foreach (ulong value in expected)
        {
            Assert.Equal(value, rng.NextUInt64());
        }
    }

    [Fact]
    public void SplitMix64_matches_reference()
    {
        var sm = new SplitMix64(0);
        ulong[] expected = [16294208416658607535UL, 7960286522194355700UL, 487617019471545679UL, 17909611376780542444UL, 1961750202426094747UL];
        foreach (ulong value in expected)
        {
            Assert.Equal(value, sm.Next());
        }
    }

    [Fact]
    public void FromSeed_matches_reference_seeding()
    {
        var rng = Xoshiro256StarStar.FromSeed(12345);
        Assert.Equal((2454886589211414944UL, 3778200017661327597UL, 2205171434679333405UL, 3248800117070709450UL), rng.State);
        ulong[] expected = [13720838825685603483UL, 2398916695208396998UL, 17770384849984869256UL, 891717726879801395UL, 10241316046318454344UL, 196975429884907396UL];
        foreach (ulong value in expected)
        {
            Assert.Equal(value, rng.NextUInt64());
        }
    }

    [Fact]
    public void All_zero_state_is_rejected() => Assert.Throws<ArgumentException>(() => new Xoshiro256StarStar(0, 0, 0, 0));

    [Fact]
    public void Jump_changes_state_deterministically()
    {
        var a = Xoshiro256StarStar.FromSeed(7);
        var b = Xoshiro256StarStar.FromSeed(7);
        a.Jump();
        b.Jump();
        Assert.Equal(a.State, b.State);
        Assert.NotEqual(Xoshiro256StarStar.FromSeed(7).State, a.State);
    }

    [Property(MaxTest = 500)]
    public bool NextInt_stays_in_range(ulong seed, int bound)
    {
        int max = bound == int.MinValue ? int.MaxValue : Math.Max(1, Math.Abs(bound));
        var rng = Xoshiro256StarStar.FromSeed(seed);
        for (int i = 0; i < 64; i++)
        {
            int value = rng.NextInt(max);
            if (value < 0 || value >= max)
            {
                return false;
            }
        }

        return true;
    }

    [Property(MaxTest = 500)]
    public bool NextInt_range_handles_full_int_span(ulong seed)
    {
        var rng = Xoshiro256StarStar.FromSeed(seed);
        int value = rng.NextInt(int.MinValue, int.MaxValue);
        return value < int.MaxValue;
    }

    [Fact]
    public void NextInt_is_roughly_uniform()
    {
        var rng = Xoshiro256StarStar.FromSeed(99);
        var counts = new int[6];
        const int Draws = 60_000;
        for (int i = 0; i < Draws; i++)
        {
            counts[rng.NextInt(6)]++;
        }

        foreach (int count in counts)
        {
            Assert.InRange(count, 9_500, 10_500);
        }
    }

    [Fact]
    public void Invalid_bounds_throw()
    {
        var rng = Xoshiro256StarStar.FromSeed(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(5, 5));
    }

    [Fact]
    public void NextChance_saturates_and_NextFixed_is_in_unit_interval()
    {
        var rng = Xoshiro256StarStar.FromSeed(3);
        for (int i = 0; i < 1000; i++)
        {
            Assert.False(rng.NextChance(0));
            Assert.True(rng.NextChance(10_000));
            Fixed f = rng.NextFixed();
            Assert.True(f >= Fixed.Zero && f < Fixed.One);
        }
    }

    [Fact]
    public void State_round_trips_through_binary()
    {
        var rng = Xoshiro256StarStar.FromSeed(5);
        rng.NextUInt64();
        var writer = new BinaryBufferWriter();
        rng.WriteTo(writer);
        Assert.Equal(32, writer.Length);
        var reader = new BinaryBufferReader(writer.WrittenSpan);
        var restored = Xoshiro256StarStar.ReadFrom(ref reader);
        Assert.Equal(rng.NextUInt64(), restored.NextUInt64());
    }
}
