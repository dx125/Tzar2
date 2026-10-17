using System;

namespace Tzar2.Core.Numerics;

/// <summary>
/// Integer trigonometry (TDD §8.2): <c>Sin</c>/<c>Cos</c> from a 1,024-entry lookup table with linear
/// interpolation, <c>Atan2</c> from an arctangent table over one octant. Results are bit-identical on every
/// platform; accuracy is within ±2 raw units of the exact value.
/// </summary>
public static class Trig
{
    private const int SinIndexShift = 6; // 65,536 angle units / 1,024 entries = 64 units per entry
    private const int SinFractionMask = (1 << SinIndexShift) - 1;
    private const int SinIndexMask = 1023;
    private const int AtanRatioBits = 16; // Q16 ratio, 64 ratio units per table entry
    private const int AtanIndexShift = 6;
    private const int AtanFractionMask = (1 << AtanIndexShift) - 1;

    /// <summary>Sine as a <see cref="Fixed"/> in [-1, 1].</summary>
    public static Fixed Sin(Angle angle) => Fixed.FromRaw(SinRaw(angle.Raw));

    /// <summary>Cosine as a <see cref="Fixed"/> in [-1, 1].</summary>
    public static Fixed Cos(Angle angle) => Fixed.FromRaw(SinRaw(angle.Raw + (Angle.UnitsPerTurn / 4)));

    /// <summary>
    /// Angle of the vector (<paramref name="x"/>, <paramref name="y"/>) using the <see cref="Angle"/> convention.
    /// Returns <see cref="Angle.Zero"/> for the zero vector.
    /// </summary>
    public static Angle Atan2(long y, long x)
    {
        if (x == 0 && y == 0)
        {
            return Angle.Zero;
        }

        ulong ax = Magnitude(x);
        ulong ay = Magnitude(y);

        // First octant angle of the reduced vector, in [0, 8192].
        int angle = ax >= ay
            ? AtanOfRatio(ay, ax)
            : (Angle.UnitsPerTurn / 4) - AtanOfRatio(ax, ay);

        if (x < 0)
        {
            angle = (Angle.UnitsPerTurn / 2) - angle;
        }

        if (y < 0)
        {
            angle = -angle;
        }

        return Angle.FromRaw(angle);
    }

    private static long SinRaw(int angle)
    {
        ReadOnlySpan<int> table = TrigTables.Sin;
        int index = (angle >> SinIndexShift) & SinIndexMask;
        int fraction = angle & SinFractionMask;
        int a = table[index];
        int b = table[(index + 1) & SinIndexMask];
        return a + (((b - a) * fraction) >> SinIndexShift);
    }

    // atan(numerator / denominator) in angle units for 0 <= numerator <= denominator, denominator > 0.
    private static int AtanOfRatio(ulong numerator, ulong denominator)
    {
        int ratio = (int)(((UInt128)numerator << AtanRatioBits) / denominator);
        int index = ratio >> AtanIndexShift;
        int fraction = ratio & AtanFractionMask;
        ReadOnlySpan<int> table = TrigTables.Atan;
        if (fraction == 0)
        {
            return table[index];
        }

        int a = table[index];
        int b = table[index + 1];
        return a + (((b - a) * fraction) >> AtanIndexShift);
    }

    private static ulong Magnitude(long value) => value < 0 ? (ulong)(-(Int128)value) : (ulong)value;
}
