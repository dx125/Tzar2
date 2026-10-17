using System;

namespace Tzar2.Content.Compiled;

/// <summary>
/// A compiled terrain type (TDD §9.1): the id painted on the map's corner grid, and which locomotors may cross it.
/// Visual data (transition strips, animation) lives in the terrain-set manifests, not here.
/// </summary>
public sealed class TerrainTypeDefinition
{
    /// <summary>Creates a terrain type.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty.</exception>
    public TerrainTypeDefinition(byte id, string key, LocomotorMask passable, bool isWater)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if ((passable & ~LocomotorMask.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(passable), "Unknown locomotor bits.");
        }

        Id = id;
        Key = key;
        Passable = passable;
        IsWater = isWater;
    }

    /// <summary>The id stored in <see cref="MapData.TerrainCorners"/>; equals the index in <see cref="Ruleset.TerrainTypes"/>.</summary>
    public byte Id { get; }

    /// <summary>Rules key, e.g. <c>grass</c>, <c>shallow_water</c>.</summary>
    public string Key { get; }

    /// <summary>Locomotors that may cross a tile whose four corners all allow them.</summary>
    public LocomotorMask Passable { get; }

    /// <summary>Whether the terrain counts as water (placement class <c>water</c>/<c>coast</c>).</summary>
    public bool IsWater { get; }
}
