using System;
using System.Runtime.InteropServices;
using FsCheck.Xunit;
using Tzar2.Core.Hashing;
using Tzar2.Core.Numerics;
using Xunit;

namespace Tzar2.Core.Tests.Numerics;

public class TrigTests
{
    // Hash-verification of the checked-in lookup tables (TDD §8.2). If tools/Tzar2.Tools.LutGen is changed on
    // purpose, regenerate the tables and update these values; any other change is a determinism break.
    private const ulong SinTableHash = 2175508696125189451UL;
    private const ulong AtanTableHash = 514395834046374973UL;

    private const double RadiansPerUnit = 2.0 * Math.PI / Angle.UnitsPerTurn;

    [Fact]
    public void Lookup_tables_match_checked_in_hashes()
    {
        Assert.Equal(1024, TrigTables.Sin.Length);
        Assert.Equal(1025, TrigTables.Atan.Length);
        Assert.Equal(SinTableHash, StateHasher.Hash(MemoryMarshal.AsBytes(TrigTables.Sin)));
        Assert.Equal(AtanTableHash, StateHasher.Hash(MemoryMarshal.AsBytes(TrigTables.Atan)));
    }

    [Fact]
    public void Lookup_tables_are_within_rounding_of_exact_values()
    {
        for (int i = 0; i < TrigTables.Sin.Length; i++)
        {
            double exact = Math.Sin(2.0 * Math.PI * i / TrigTables.Sin.Length) * Fixed.OneRaw;
            Assert.InRange(TrigTables.Sin[i] - exact, -0.5001, 0.5001);
        }

        for (int i = 0; i < TrigTables.Atan.Length; i++)
        {
            double exact = Math.Atan(i / 1024.0) / RadiansPerUnit;
            Assert.InRange(TrigTables.Atan[i] - exact, -0.5001, 0.5001);
        }
    }

    [Theory]
    [InlineData(0, 0, 65536)]
    [InlineData(16384, 65536, 0)]
    [InlineData(32768, 0, -65536)]
    [InlineData(49152, -65536, 0)]
    public void Cardinal_angles_are_exact(int angle, long sin, long cos)
    {
        Assert.Equal(sin, Trig.Sin(Angle.FromRaw(angle)).Raw);
        Assert.Equal(cos, Trig.Cos(Angle.FromRaw(angle)).Raw);
    }

    [Fact]
    public void Sin_and_cos_are_accurate_for_every_angle()
    {
        for (int raw = 0; raw < Angle.UnitsPerTurn; raw++)
        {
            double radians = raw * RadiansPerUnit;
            Assert.InRange(Trig.Sin(Angle.FromRaw(raw)).Raw - (Math.Sin(radians) * Fixed.OneRaw), -2.0, 2.0);
            Assert.InRange(Trig.Cos(Angle.FromRaw(raw)).Raw - (Math.Cos(radians) * Fixed.OneRaw), -2.0, 2.0);
        }
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, 1, 8192)]
    [InlineData(1, 0, 16384)]
    [InlineData(1, -1, 24576)]
    [InlineData(0, -1, 32768)]
    [InlineData(-1, -1, 40960)]
    [InlineData(-1, 0, 49152)]
    [InlineData(-1, 1, 57344)]
    [InlineData(0, 0, 0)]
    public void Atan2_octants(long y, long x, int expected) => Assert.Equal(expected, Trig.Atan2(y, x).Raw);

    [Fact]
    public void Atan2_handles_extreme_inputs()
    {
        Assert.Equal(49152, Trig.Atan2(long.MinValue, 0).Raw);
        Assert.Equal(40960, Trig.Atan2(long.MinValue, long.MinValue).Raw);
        Assert.Equal(8192, Trig.Atan2(long.MaxValue, long.MaxValue).Raw);
    }

    [Property(MaxTest = 5000)]
    public bool Atan2_is_accurate(int y, int x)
    {
        if (x == 0 && y == 0)
        {
            return true;
        }

        double expected = Math.Atan2(y, x) / RadiansPerUnit;
        double actual = Trig.Atan2(y, x).Raw;
        double error = Math.Abs(actual - ((expected + Angle.UnitsPerTurn) % Angle.UnitsPerTurn));
        return Math.Min(error, Angle.UnitsPerTurn - error) <= 2.0;
    }

    [Property(MaxTest = 2000)]
    public bool Atan2_inverts_sin_cos(ushort raw)
    {
        Angle angle = Angle.FromRaw(raw);
        Angle back = Trig.Atan2(Trig.Sin(angle).Raw, Trig.Cos(angle).Raw);
        int error = Math.Abs((short)(back.Raw - angle.Raw));
        return error <= 3;
    }
}
