using System;
using System.IO.Hashing;
using System.Text;
using Tzar2.Core.Hashing;
using Tzar2.Core.Numerics;
using Xunit;

namespace Tzar2.Core.Tests.Hashing;

public class StateHasherTests
{
    [Fact]
    public void Empty_input_matches_xxhash64_reference() => Assert.Equal(0xEF46DB3751D8E999UL, new StateHasher().GetCurrentHash());

    [Fact]
    public void Typed_adds_hash_little_endian_bytes()
    {
        var hasher = new StateHasher();
        hasher.Add(0x01020304).Add(-1L).Add((byte)7).Add(true).Add(Fixed.One).Add(5u).Add(6UL);
        byte[] expected =
        [
            0x04, 0x03, 0x02, 0x01,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0x07,
            0x01,
            0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x05, 0x00, 0x00, 0x00,
            0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];
        Assert.Equal(XxHash64.HashToUInt64(expected), hasher.GetCurrentHash());
    }

    [Fact]
    public void AddSpan_equals_element_wise_adds()
    {
        int[] values = [1, -2, 300000, int.MinValue];
        var bulk = new StateHasher().AddSpan<int>(values);
        var single = new StateHasher();
        foreach (int v in values)
        {
            single.Add(v);
        }

        Assert.Equal(single.GetCurrentHash(), bulk.GetCurrentHash());
    }

    [Fact]
    public void Incremental_equals_one_shot_and_reset_restarts()
    {
        byte[] data = Encoding.ASCII.GetBytes("Tzar2 deterministic state");
        var hasher = new StateHasher(seed: 123);
        hasher.Add(data.AsSpan(0, 5)).Add(data.AsSpan(5));
        Assert.Equal(StateHasher.Hash(data, 123), hasher.GetHashAndReset());
        Assert.Equal(StateHasher.Hash([], 123), hasher.GetCurrentHash());
        hasher.Add(data);
        hasher.Reset();
        Assert.Equal(StateHasher.Hash([], 123), hasher.GetCurrentHash());
    }
}
