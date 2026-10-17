using System;
using System.Globalization;

namespace Tzar2.Core.Ids;

/// <summary>
/// A player slot in a match (kingdoms, plus the Nature and Rescue pseudo-players whose slot numbers the
/// ruleset assigns; REQ-PATH-00).
/// </summary>
public readonly struct PlayerId : IEquatable<PlayerId>, IComparable<PlayerId>
{
    private readonly byte _value;

    /// <summary>Creates a player id from its slot number.</summary>
    public PlayerId(byte value) => _value = value;

    /// <summary>The slot number.</summary>
    public byte Value => _value;

    /// <summary>Equality.</summary>
    public static bool operator ==(PlayerId a, PlayerId b) => a._value == b._value;

    /// <summary>Inequality.</summary>
    public static bool operator !=(PlayerId a, PlayerId b) => a._value != b._value;

    /// <summary>Ordering by slot number.</summary>
    public static bool operator <(PlayerId a, PlayerId b) => a._value < b._value;

    /// <summary>Ordering by slot number.</summary>
    public static bool operator >(PlayerId a, PlayerId b) => a._value > b._value;

    /// <summary>Ordering by slot number.</summary>
    public static bool operator <=(PlayerId a, PlayerId b) => a._value <= b._value;

    /// <summary>Ordering by slot number.</summary>
    public static bool operator >=(PlayerId a, PlayerId b) => a._value >= b._value;

    /// <inheritdoc />
    public bool Equals(PlayerId other) => _value == other._value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PlayerId other && _value == other._value;

    /// <inheritdoc />
    public override int GetHashCode() => _value;

    /// <inheritdoc />
    public int CompareTo(PlayerId other) => _value.CompareTo(other._value);

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Player({_value})");
}
