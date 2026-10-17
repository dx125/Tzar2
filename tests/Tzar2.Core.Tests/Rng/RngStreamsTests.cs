using System;
using Tzar2.Core.Hashing;
using Tzar2.Core.Rng;
using Tzar2.Core.Serialization;
using Xunit;

namespace Tzar2.Core.Tests.Rng;

public class RngStreamsTests
{
    [Fact]
    public void Streams_are_reproducible_from_the_match_seed()
    {
        var a = new RngStreams(42, 8);
        var b = new RngStreams(42, 8);
        Assert.Equal(a.Combat.NextUInt64(), b.Combat.NextUInt64());
        Assert.Equal(a.Ai(7).NextUInt64(), b.Ai(7).NextUInt64());
    }

    [Fact]
    public void Streams_are_independent()
    {
        var reference = new RngStreams(42, 8);
        ulong combatFirst = reference.Combat.NextUInt64();

        // Drawing heavily from other streams must not change the combat stream.
        var streams = new RngStreams(42, 8);
        for (int i = 0; i < 1000; i++)
        {
            streams.Ai(3).NextUInt64();
            streams.MapGen.NextUInt64();
            streams.Scripts.NextUInt64();
            streams.Misc.NextUInt64();
        }

        Assert.Equal(combatFirst, streams.Combat.NextUInt64());
    }

    [Fact]
    public void Player_count_does_not_change_shared_streams()
    {
        var two = new RngStreams(42, 2);
        var eight = new RngStreams(42, 8);
        Assert.Equal(two.Combat.NextUInt64(), eight.Combat.NextUInt64());
        Assert.Equal(two.Ai(1).NextUInt64(), eight.Ai(1).NextUInt64());
    }

    [Fact]
    public void All_streams_differ()
    {
        var streams = new RngStreams(1, 4);
        ulong[] firsts =
        [
            streams.Combat.NextUInt64(), streams.MapGen.NextUInt64(), streams.Scripts.NextUInt64(), streams.Misc.NextUInt64(),
            streams.Ai(0).NextUInt64(), streams.Ai(1).NextUInt64(), streams.Ai(2).NextUInt64(), streams.Ai(3).NextUInt64(),
        ];
        Assert.Equal(firsts.Length, new System.Collections.Generic.HashSet<ulong>(firsts).Count);
    }

    [Fact]
    public void Ai_index_is_bounds_checked()
    {
        var streams = new RngStreams(1, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => streams.Ai(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => streams.Ai(-1));
    }

    [Fact]
    public void Snapshot_round_trip_preserves_every_stream()
    {
        var streams = new RngStreams(77, 3);
        streams.Combat.NextUInt64();
        streams.Ai(2).NextUInt64();

        var writer = new BinaryBufferWriter();
        streams.WriteTo(writer);
        var reader = new BinaryBufferReader(writer.WrittenSpan);
        RngStreams restored = RngStreams.ReadFrom(ref reader);
        Assert.True(reader.IsAtEnd);
        Assert.Equal(77UL, restored.MatchSeed);
        Assert.Equal(3, restored.PlayerCount);

        var h1 = new StateHasher();
        var h2 = new StateHasher();
        streams.AppendTo(h1);
        restored.AppendTo(h2);
        Assert.Equal(h1.GetCurrentHash(), h2.GetCurrentHash());
        Assert.Equal(streams.Ai(2).NextUInt64(), restored.Ai(2).NextUInt64());
    }
}
