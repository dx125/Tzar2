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
    public void AddSpan_is_the_byte_length_then_the_element_bytes()
    {
        int[] values = [1, -2, 300000, int.MinValue];
        var bulk = new StateHasher().AddSpan<int>(values);
        var single = new StateHasher().Add((long)(values.Length * sizeof(int)));
        foreach (int v in values)
        {
            single.Add(v);
        }

        Assert.Equal(single.GetCurrentHash(), bulk.GetCurrentHash());
    }

    [Fact]
    public void Byte_spans_are_length_prefixed()
    {
        byte[] expected = [3, 0, 0, 0, 0, 0, 0, 0, 0xAA, 0xBB, 0xCC, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(XxHash64.HashToUInt64(expected), new StateHasher().Add([0xAA, 0xBB, 0xCC]).Add([]).GetCurrentHash());
    }

    [Fact]
    public void Different_splits_of_the_same_bytes_hash_differently()
    {
        ulong a = new StateHasher().Add([1, 2]).Add([3]).GetCurrentHash();
        ulong b = new StateHasher().Add([1]).Add([2, 3]).GetCurrentHash();
        ulong c = new StateHasher().AddSpan<int>([1, 2]).AddSpan<int>([]).GetCurrentHash();
        ulong d = new StateHasher().AddSpan<int>([1]).AddSpan<int>([2]).GetCurrentHash();
        Assert.NotEqual(a, b);
        Assert.NotEqual(c, d);
    }

    [Fact]
    public void Incremental_framing_and_reset_restarts()
    {
        byte[] data = Encoding.ASCII.GetBytes("Tzar2 deterministic state");
        var hasher = new StateHasher(seed: 123);
        hasher.Add(data.AsSpan(0, 5)).Add(data.AsSpan(5));
        byte[] framed = [5, 0, 0, 0, 0, 0, 0, 0, .. data.AsSpan(0, 5), (byte)(data.Length - 5), 0, 0, 0, 0, 0, 0, 0, .. data.AsSpan(5)];
        Assert.Equal(StateHasher.Hash(framed, 123), hasher.GetHashAndReset());
        Assert.Equal(StateHasher.Hash([], 123), hasher.GetCurrentHash());
        hasher.Add(data);
        hasher.Reset();
        Assert.Equal(StateHasher.Hash([], 123), hasher.GetCurrentHash());
    }
}
