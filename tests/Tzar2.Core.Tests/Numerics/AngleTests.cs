using Tzar2.Core.Numerics;
using Xunit;

namespace Tzar2.Core.Tests.Numerics;

public class AngleTests
{
    [Fact]
    public void Arithmetic_wraps()
    {
        Assert.Equal(Angle.Zero, Angle.HalfTurn + Angle.HalfTurn);
        Assert.Equal(Angle.FromRaw(49152), -Angle.Quarter);
        Assert.Equal(Angle.FromRaw(65535), Angle.Zero - Angle.FromRaw(1));
        Assert.Equal(Angle.FromRaw(5), Angle.FromRaw(65536 + 5));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 16384)]
    [InlineData(180, 32768)]
    [InlineData(-90, 49152)]
    [InlineData(360, 0)]
    [InlineData(45, 8192)]
    public void FromDegrees(int degrees, int raw) => Assert.Equal(raw, Angle.FromDegrees(degrees).Raw);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4095, 0)]
    [InlineData(4096, 1)]
    [InlineData(8192, 1)]
    [InlineData(16384, 2)]
    [InlineData(61440, 0)] // within half a step of a full turn rounds back to east
    [InlineData(61439, 7)]
    public void ToDirection8_quantizes_to_nearest_facing(int raw, int direction)
        => Assert.Equal(direction, Angle.FromRaw(raw).ToDirection8());

    [Fact]
    public void FromDirection8_is_inverse_of_ToDirection8()
    {
        for (int d = 0; d < Angle.DirectionCount; d++)
        {
            Assert.Equal(d, Angle.FromDirection8(d).ToDirection8());
        }

        Assert.Throws<System.ArgumentOutOfRangeException>(() => Angle.FromDirection8(8));
    }
}
