using System;

namespace Tzar2.Content.Compiled;

/// <summary>
/// The minimal compiled entity type the M2 sim core consumes. The trait list of REQ-SIM-09 (§11.2) replaces the
/// fixed fields here once the rules compiler exists; the id and key stay.
/// </summary>
public sealed class EntityTypeDefinition
{
    /// <summary>Creates an entity type.</summary>
    /// <exception cref="ArgumentException">A value is out of range.</exception>
    public EntityTypeDefinition(ushort id, string key, Locomotor locomotor, int speedUnitsPerSecond, int maxHealth)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if ((int)locomotor >= LocomotorExtensions.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(locomotor));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(speedUnitsPerSecond);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHealth, 1);
        Id = id;
        Key = key;
        Locomotor = locomotor;
        SpeedUnitsPerSecond = speedUnitsPerSecond;
        MaxHealth = maxHealth;
    }

    /// <summary>The type id used in sim components; equals the index in <see cref="Ruleset.EntityTypes"/>.</summary>
    public ushort Id { get; }

    /// <summary>Rules key, e.g. <c>european_swordsman</c>.</summary>
    public string Key { get; }

    /// <summary>How the entity moves.</summary>
    public Locomotor Locomotor { get; }

    /// <summary>Speed in world units (1 px = 64 units, TDD §8.2) per second of game time; 0 = immobile.</summary>
    public int SpeedUnitsPerSecond { get; }

    /// <summary>Hit points at full health.</summary>
    public int MaxHealth { get; }
}
