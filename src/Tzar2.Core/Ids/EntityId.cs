using System;
using System.Globalization;

namespace Tzar2.Core.Ids;

/// <summary>
/// 32-bit entity handle: a 20-bit slot index plus a 12-bit generation (REQ-SIM-07, ADR 0005).
/// The default value is <see cref="None"/>; valid handles have a generation in [1, <see cref="MaxGeneration"/>].
/// Ordering is by index, then generation, matching the simulation's ascending-index iteration.
/// </summary>
public readonly struct EntityId : IEquatable<EntityId>, IComparable<EntityId>
{
    /// <summary>Bits used for the slot index.</summary>
    public const int IndexBits = 20;

    /// <summary>Largest slot index (1,048,575).</summary>
    public const int MaxIndex = (1 << IndexBits) - 1;

    /// <summary>Largest generation (4,095); generations wrap from this value back to 1.</summary>
    public const int MaxGeneration = (1 << (32 - IndexBits)) - 1;

    private readonly uint _value;

    private EntityId(uint value) => _value = value;

    /// <summary>The null handle.</summary>
    public static EntityId None => default;

    /// <summary>Creates a handle.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index or generation is out of range.</exception>
    public static EntityId Create(int index, int generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, MaxIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(generation, MaxGeneration);
        return new(((uint)generation << IndexBits) | (uint)index);
    }

    /// <summary>Recreates a handle from <see cref="Value"/>.</summary>
    public static EntityId FromValue(uint value) => new(value);

    /// <summary>The packed 32-bit value.</summary>
    public uint Value => _value;

    /// <summary>The slot index.</summary>
    public int Index => (int)(_value & MaxIndex);

    /// <summary>The generation, 0 only for <see cref="None"/>.</summary>
    public int Generation => (int)(_value >> IndexBits);

    /// <summary>Whether this is <see cref="None"/>.</summary>
    public bool IsNone => _value == 0;

    /// <summary>The generation that follows <paramref name="generation"/> when a slot is reused.</summary>
    public static int NextGeneration(int generation) => generation >= MaxGeneration ? 1 : generation + 1;

    /// <summary>Equality.</summary>
    public static bool operator ==(EntityId a, EntityId b) => a._value == b._value;

    /// <summary>Inequality.</summary>
    public static bool operator !=(EntityId a, EntityId b) => a._value != b._value;

    /// <summary>Ordering by index, then generation.</summary>
    public static bool operator <(EntityId a, EntityId b) => a.CompareTo(b) < 0;

    /// <summary>Ordering by index, then generation.</summary>
    public static bool operator >(EntityId a, EntityId b) => a.CompareTo(b) > 0;

    /// <summary>Ordering by index, then generation.</summary>
    public static bool operator <=(EntityId a, EntityId b) => a.CompareTo(b) <= 0;

    /// <summary>Ordering by index, then generation.</summary>
    public static bool operator >=(EntityId a, EntityId b) => a.CompareTo(b) >= 0;

    /// <inheritdoc />
    public bool Equals(EntityId other) => _value == other._value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EntityId other && _value == other._value;

    /// <inheritdoc />
    public override int GetHashCode() => (int)_value;

    /// <inheritdoc />
    public int CompareTo(EntityId other)
    {
        int byIndex = Index.CompareTo(other.Index);
        return byIndex != 0 ? byIndex : Generation.CompareTo(other.Generation);
    }

    /// <inheritdoc />
    public override string ToString()
        => IsNone ? "Entity(None)" : string.Create(CultureInfo.InvariantCulture, $"Entity({Index}:{Generation})");
}
