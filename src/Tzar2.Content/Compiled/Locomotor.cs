using System;

namespace Tzar2.Content.Compiled;

/// <summary>Movement class of a mobile entity (TDD §9.1; the set is data-defined later, these are the built-in ones).</summary>
public enum Locomotor : byte
{
    /// <summary>Infantry, peasants.</summary>
    Foot = 0,

    /// <summary>Cavalry.</summary>
    Mounted = 1,

    /// <summary>Siege engines, chariots.</summary>
    Large = 2,

    /// <summary>Ships and boats.</summary>
    Ship = 3,

    /// <summary>Flying units (dragons, carpets, birds).</summary>
    Air = 4,

    /// <summary>
    /// Infantry that climbs cliffs: the ninja (WORLD-018, ADR 0220). Everywhere else it is <see cref="Foot"/>: data and
    /// effects that name "foot" include it.
    /// </summary>
    Climber = 5,
}

/// <summary>A set of <see cref="Locomotor"/>s, one bit each (bit = <c>1 &lt;&lt; (int)locomotor</c>).</summary>
[Flags]
public enum LocomotorMask : byte
{
    /// <summary>Nothing may enter.</summary>
    None = 0,

    /// <summary><see cref="Locomotor.Foot"/>.</summary>
    Foot = 1 << Locomotor.Foot,

    /// <summary><see cref="Locomotor.Mounted"/>.</summary>
    Mounted = 1 << Locomotor.Mounted,

    /// <summary><see cref="Locomotor.Large"/>.</summary>
    Large = 1 << Locomotor.Large,

    /// <summary><see cref="Locomotor.Ship"/>.</summary>
    Ship = 1 << Locomotor.Ship,

    /// <summary><see cref="Locomotor.Air"/>.</summary>
    Air = 1 << Locomotor.Air,

    /// <summary><see cref="Locomotor.Climber"/>.</summary>
    Climber = 1 << Locomotor.Climber,

    /// <summary>All land locomotors.</summary>
    Land = Foot | Mounted | Large | Climber,

    /// <summary>Every locomotor.</summary>
    All = Land | Ship | Air,
}

/// <summary>Helpers for <see cref="Locomotor"/> and <see cref="LocomotorMask"/>.</summary>
public static class LocomotorExtensions
{
    /// <summary>Number of built-in locomotors.</summary>
    public const int Count = 6;

    /// <summary>The mask bit of <paramref name="locomotor"/>.</summary>
    public static LocomotorMask ToMask(this Locomotor locomotor) => (LocomotorMask)(1 << (int)locomotor);

    /// <summary>
    /// The locomotors a data keyword names: "foot" also covers the cliff-climbing <see cref="Locomotor.Climber"/>, which
    /// is infantry in every rule but cliffs (ADR 0220).
    /// </summary>
    public static LocomotorMask Family(this Locomotor locomotor)
        => locomotor == Locomotor.Foot ? LocomotorMask.Foot | LocomotorMask.Climber : locomotor.ToMask();

    /// <summary>Whether a unit moving as <paramref name="unit"/> belongs to what <paramref name="named"/> names in data.</summary>
    public static bool IsA(this Locomotor unit, Locomotor named) => named.Family().Allows(unit);

    /// <summary>Whether <paramref name="mask"/> contains <paramref name="locomotor"/>.</summary>
    public static bool Allows(this LocomotorMask mask, Locomotor locomotor) => (mask & locomotor.ToMask()) != 0;
}
