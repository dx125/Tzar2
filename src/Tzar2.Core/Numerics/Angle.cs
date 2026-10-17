using System;
using System.Globalization;

namespace Tzar2.Core.Numerics;

/// <summary>
/// 16-bit binary angle (TDD §8.2): a full turn is 65,536 units and arithmetic wraps naturally.
/// Convention: 0 points along +X (east); angles grow toward +Y. World Y points down the map (south),
/// so on screen positive angles turn clockwise.
/// </summary>
public readonly struct Angle : IEquatable<Angle>
{
    /// <summary>Units in a full turn.</summary>
    public const int UnitsPerTurn = 65536;

    /// <summary>Number of sprite facings derived by <see cref="ToDirection8"/>.</summary>
    public const int DirectionCount = 8;

    private const int UnitsPerDirection = UnitsPerTurn / DirectionCount;

    private readonly ushort _raw;

    private Angle(ushort raw) => _raw = raw;

    /// <summary>0 (east).</summary>
    public static Angle Zero => default;

    /// <summary>90 degrees (south).</summary>
    public static Angle Quarter => new(UnitsPerTurn / 4);

    /// <summary>180 degrees (west).</summary>
    public static Angle HalfTurn => new(UnitsPerTurn / 2);

    /// <summary>The raw binary angle.</summary>
    public ushort Raw => _raw;

    /// <summary>Creates an angle from raw units; any integer wraps modulo a full turn.</summary>
    public static Angle FromRaw(int raw) => new(unchecked((ushort)raw));

    /// <summary>Creates an angle from whole degrees, truncated toward zero to binary units.</summary>
    public static Angle FromDegrees(int degrees) => FromRaw((int)((long)degrees * UnitsPerTurn / 360));

    /// <summary>
    /// Quantizes to one of 8 facings: 0 = east, then one step per 45 degrees in the direction of increasing
    /// angle. Mapping facings to sprite-sheet columns is a rules/presentation concern (RE-02).
    /// </summary>
    public int ToDirection8() => ((_raw + (UnitsPerDirection / 2)) & 0xFFFF) / UnitsPerDirection;

    /// <summary>The angle of facing <paramref name="direction"/> (0..7), the inverse of <see cref="ToDirection8"/>.</summary>
    public static Angle FromDirection8(int direction)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(direction);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(direction, DirectionCount);
        return new((ushort)(direction * UnitsPerDirection));
    }

    /// <summary>Wrapping addition.</summary>
    public static Angle operator +(Angle a, Angle b) => FromRaw(a._raw + b._raw);

    /// <summary>Wrapping subtraction.</summary>
    public static Angle operator -(Angle a, Angle b) => FromRaw(a._raw - b._raw);

    /// <summary>Wrapping negation.</summary>
    public static Angle operator -(Angle a) => FromRaw(-a._raw);

    /// <summary>Equality.</summary>
    public static bool operator ==(Angle a, Angle b) => a._raw == b._raw;

    /// <summary>Inequality.</summary>
    public static bool operator !=(Angle a, Angle b) => a._raw != b._raw;

    /// <inheritdoc />
    public bool Equals(Angle other) => _raw == other._raw;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Angle other && _raw == other._raw;

    /// <inheritdoc />
    public override int GetHashCode() => _raw;

    /// <inheritdoc />
    public override string ToString() => _raw.ToString(CultureInfo.InvariantCulture);
}
