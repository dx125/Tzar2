using System;

namespace Tzar2.Content.Compiled;

/// <summary>
/// The minimal compiled entity type the M2 sim core consumes. The trait list of REQ-SIM-09 (§11.2) replaces the
/// fixed fields here once the rules compiler exists; the id and key stay.
/// </summary>
/// <remarks>
/// A type is <b>mobile</b> when <see cref="SpeedUnitsPerSecond"/> &gt; 0 (trait <c>Mobile(locomotor, speed)</c>) and a
/// <b>structure</b> when it has a footprint (trait <c>Building(footprint)</c>; trees and rocks use the same footprint
/// model, docs/formats/dat.md §3). A type cannot be both.
/// </remarks>
public sealed class EntityTypeDefinition
{
    /// <summary>Default collision radius of mobile units: 7 px (⚠ until RE-06/UNK-11 measures unit sizes).</summary>
    public const int DefaultRadius = 7 * 64;

    /// <summary>Largest collision radius (two tiles).</summary>
    public const int MaxRadius = 2 * 2048;

    /// <summary>Largest clearance in tiles a unit may need (§9.1 "large units occupy several cells").</summary>
    public const int MaxClearanceTiles = 3;

    /// <summary>Largest footprint side in tiles.</summary>
    public const int MaxFootprintTiles = 16;

    private readonly byte[] _footprint;

    /// <summary>Creates an entity type.</summary>
    /// <param name="id">Type id (= position in the ruleset).</param>
    /// <param name="key">Rules key.</param>
    /// <param name="locomotor">How the entity moves.</param>
    /// <param name="speedUnitsPerSecond">Speed in world units per second; 0 = immobile.</param>
    /// <param name="maxHealth">Hit points at full health.</param>
    /// <param name="radius">Collision radius in world units of a mobile unit (local avoidance, §9.2 REQ-PATH-06).</param>
    /// <param name="clearanceTiles">
    /// Side of the square of passable tiles the unit needs (1 = fits through any one-tile gap; 2 = needs 2×2 tiles,
    /// e.g. siege engines). The pathfinder keeps one navigation layer per (locomotor, clearance).
    /// </param>
    /// <param name="footprintWidth">Footprint width in tiles of a structure (0 = no footprint).</param>
    /// <param name="footprintHeight">Footprint height in tiles of a structure (0 = no footprint).</param>
    /// <param name="footprint">
    /// Optional row-major W×H occupancy mask (non-zero = the tile is blocked); empty = the whole rectangle blocks.
    /// Imported from the DAT footprint grid (value 0 = occupied, 2 = free; docs/formats/dat.md §3).
    /// </param>
    /// <param name="leavesCorpse">Whether a killed entity leaves a corpse decal (trait <c>CorpseOnDeath</c>).</param>
    /// <exception cref="ArgumentException">A value is out of range.</exception>
    public EntityTypeDefinition(
        ushort id,
        string key,
        Locomotor locomotor,
        int speedUnitsPerSecond,
        int maxHealth,
        int radius = DefaultRadius,
        int clearanceTiles = 1,
        int footprintWidth = 0,
        int footprintHeight = 0,
        byte[]? footprint = null,
        bool leavesCorpse = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if ((int)locomotor >= LocomotorExtensions.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(locomotor));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(speedUnitsPerSecond);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHealth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(radius, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(radius, MaxRadius);
        ArgumentOutOfRangeException.ThrowIfLessThan(clearanceTiles, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(clearanceTiles, MaxClearanceTiles);
        ArgumentOutOfRangeException.ThrowIfNegative(footprintWidth);
        ArgumentOutOfRangeException.ThrowIfNegative(footprintHeight);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(footprintWidth, MaxFootprintTiles);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(footprintHeight, MaxFootprintTiles);
        if ((footprintWidth == 0) != (footprintHeight == 0))
        {
            throw new ArgumentException("A footprint needs both a width and a height.", nameof(footprintWidth));
        }

        if (footprintWidth > 0 && speedUnitsPerSecond > 0)
        {
            throw new ArgumentException("A mobile type cannot have a footprint.", nameof(footprintWidth));
        }

        footprint ??= [];
        if (footprint.Length != 0 && footprint.Length != footprintWidth * footprintHeight)
        {
            throw new ArgumentException($"Expected 0 or {footprintWidth * footprintHeight} footprint cells.", nameof(footprint));
        }

        Id = id;
        Key = key;
        Locomotor = locomotor;
        SpeedUnitsPerSecond = speedUnitsPerSecond;
        MaxHealth = maxHealth;
        Radius = radius;
        ClearanceTiles = clearanceTiles;
        FootprintWidth = footprintWidth;
        FootprintHeight = footprintHeight;
        _footprint = new byte[footprintWidth * footprintHeight];
        for (int i = 0; i < _footprint.Length; i++)
        {
            _footprint[i] = footprint.Length == 0 || footprint[i] != 0 ? (byte)1 : (byte)0;
        }

        LeavesCorpse = leavesCorpse;
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

    /// <summary>Collision radius in world units (mobile units).</summary>
    public int Radius { get; }

    /// <summary>Side in tiles of the passable square the unit needs.</summary>
    public int ClearanceTiles { get; }

    /// <summary>Footprint width in tiles (0 = none).</summary>
    public int FootprintWidth { get; }

    /// <summary>Footprint height in tiles (0 = none).</summary>
    public int FootprintHeight { get; }

    /// <summary>Whether the type has a footprint that blocks tiles.</summary>
    public bool HasFootprint => FootprintWidth > 0;

    /// <summary>Row-major footprint mask, W×H, 1 = the tile is blocked.</summary>
    public ReadOnlySpan<byte> Footprint => _footprint;

    /// <summary>Whether a killed entity leaves a corpse decal (which never blocks movement, ORIG-01).</summary>
    public bool LeavesCorpse { get; }
}
