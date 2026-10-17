using System;
using System.Globalization;

namespace Tzar2.Core.Ids;

/// <summary>A simulation tick number (REQ-SIM-01). Tick 0 is the first tick of a match.</summary>
public readonly struct Tick : IEquatable<Tick>, IComparable<Tick>
{
    /// <summary>Game time per tick at game speed 1.0, in milliseconds (REQ-SIM-01).</summary>
    public const int DefaultLengthMilliseconds = 50;

    private readonly int _value;

    /// <summary>Creates a tick number.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    public Tick(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        _value = value;
    }

    /// <summary>The first tick.</summary>
    public static Tick Zero => default;

    /// <summary>The tick number.</summary>
    public int Value => _value;

    /// <summary>The tick <paramref name="ticks"/> later.</summary>
    public static Tick operator +(Tick tick, int ticks) => new(checked(tick._value + ticks));

    /// <summary>The number of ticks between two ticks.</summary>
    public static int operator -(Tick a, Tick b) => checked(a._value - b._value);

    /// <summary>Equality.</summary>
    public static bool operator ==(Tick a, Tick b) => a._value == b._value;

    /// <summary>Inequality.</summary>
    public static bool operator !=(Tick a, Tick b) => a._value != b._value;

    /// <summary>Ordering.</summary>
    public static bool operator <(Tick a, Tick b) => a._value < b._value;

    /// <summary>Ordering.</summary>
    public static bool operator >(Tick a, Tick b) => a._value > b._value;

    /// <summary>Ordering.</summary>
    public static bool operator <=(Tick a, Tick b) => a._value <= b._value;

    /// <summary>Ordering.</summary>
    public static bool operator >=(Tick a, Tick b) => a._value >= b._value;

    /// <inheritdoc />
    public bool Equals(Tick other) => _value == other._value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Tick other && _value == other._value;

    /// <inheritdoc />
    public override int GetHashCode() => _value;

    /// <inheritdoc />
    public int CompareTo(Tick other) => _value.CompareTo(other._value);

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Tick({_value})");
}
