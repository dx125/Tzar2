using System;
using Tzar2.Core.Ids;
using Xunit;

namespace Tzar2.Core.Tests.Ids;

public class IdTests
{
    [Fact]
    public void EntityId_packs_index_and_generation()
    {
        EntityId id = EntityId.Create(EntityId.MaxIndex, EntityId.MaxGeneration);
        Assert.Equal(EntityId.MaxIndex, id.Index);
        Assert.Equal(EntityId.MaxGeneration, id.Generation);
        Assert.Equal(uint.MaxValue, id.Value);
        Assert.Equal(id, EntityId.FromValue(id.Value));
        Assert.False(id.IsNone);
        Assert.Equal("Entity(1048575:4095)", id.ToString());
    }

    [Fact]
    public void EntityId_default_is_none()
    {
        Assert.True(default(EntityId).IsNone);
        Assert.Equal(EntityId.None, default);
        Assert.Equal("Entity(None)", EntityId.None.ToString());
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(EntityId.MaxIndex + 1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, EntityId.MaxGeneration + 1)]
    public void EntityId_rejects_out_of_range(int index, int generation)
        => Assert.Throws<ArgumentOutOfRangeException>(() => EntityId.Create(index, generation));

    [Fact]
    public void EntityId_orders_by_index_then_generation()
    {
        EntityId a = EntityId.Create(1, 4000);
        EntityId b = EntityId.Create(2, 1);
        EntityId c = EntityId.Create(2, 2);
        Assert.True(a < b && b < c && c > a && a <= b && c >= b);
        Assert.Equal(1, EntityId.NextGeneration(EntityId.MaxGeneration));
        Assert.Equal(3, EntityId.NextGeneration(2));
    }

    [Fact]
    public void PlayerId_and_Tick()
    {
        var p = new PlayerId(3);
        Assert.Equal(3, p.Value);
        Assert.True(new PlayerId(2) < p);
        Assert.Equal("Player(3)", p.ToString());

        Tick t = Tick.Zero + 20;
        Assert.Equal(20, t.Value);
        Assert.Equal(15, t - new Tick(5));
        Assert.True(t > Tick.Zero);
        Assert.Equal("Tick(20)", t.ToString());
        Assert.Equal(50, Tick.DefaultLengthMilliseconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Tick(-1));
        Assert.Throws<OverflowException>(() => new Tick(int.MaxValue) + 1);
    }
}
