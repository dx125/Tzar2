using System;

namespace Tzar2.Content.Compiled;

/// <summary>
/// A compiled entity type as the sim consumes it: movement and footprint fields (M2 parts A/B) plus the gameplay
/// <see cref="Traits"/> the rules compiler produces (REQ-SIM-09, §11.2; ADR 0033). The id and key are stable.
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
    /// <param name="traits">Gameplay traits (null = <see cref="EntityTraits.None"/>); cross-references are checked by <see cref="Ruleset"/>.</param>
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
        bool leavesCorpse = true,
        EntityTraits? traits = null)
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
        Traits = traits ?? EntityTraits.None;
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

    /// <summary>Collision radius in world units (mobile units): the body other units keep off, and combat reach is
    /// measured from (WORLD-015).</summary>
    public int Radius { get; }

    /// <summary>
    /// The radius the unit slides along walls, trees and buildings with (world units; 0 = <see cref="Radius"/>). The
    /// original moves units from tile to tile, so a body never clips the terrain; the old placeholder footprint keeps
    /// narrow passages open while the body spaces the units (WORLD-015).
    /// </summary>
    public int TerrainRadius
    {
        get => _terrainRadius > 0 ? Math.Min(_terrainRadius, Radius) : Radius;
        init => _terrainRadius = value;
    }

    private readonly int _terrainRadius;

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

    /// <summary>
    /// The footprint turned by 90° (a gate or a bridge placed the other way; CTRL-024, BLD-019): the type's own where it
    /// gives one (the original's vertical gate is 1 × 6 while its horizontal one is 7 × 1), else the footprint transposed.
    /// </summary>
    public TurnedFootprint Turned
    {
        get => _turned ??= TurnedFootprint.Transposed(FootprintWidth, FootprintHeight, _footprint);
        init => _turned = value;
    }

    private TurnedFootprint? _turned;

    /// <summary>Whether a placement may turn it: its turned footprint differs from its own (gates, bridges, long buildings).</summary>
    public bool CanTurn => HasFootprint && (Turned.Width != FootprintWidth || Turned.Height != FootprintHeight || !Turned.Mask.AsSpan().SequenceEqual(_footprint) || Traits.Gate is { TurnedPassage.IsDefaultOrEmpty: false });

    /// <summary>The width and height of the footprint as placed, turned or not.</summary>
    public (int Width, int Height) FootprintSize(bool turned) => turned ? (Turned.Width, Turned.Height) : (FootprintWidth, FootprintHeight);

    /// <summary>The row-major mask of the footprint as placed, turned or not.</summary>
    public ReadOnlySpan<byte> FootprintMask(bool turned) => turned ? Turned.Mask.AsSpan() : _footprint;

    /// <summary>Whether a killed entity leaves a corpse decal (which never blocks movement, ORIG-01).</summary>
    public bool LeavesCorpse { get; }

    /// <summary>Gameplay traits (cost, armament, harvesting, production...).</summary>
    public EntityTraits Traits { get; }

    /// <summary>Whether the type moves.</summary>
    public bool IsMobile => SpeedUnitsPerSecond > 0;
}

/// <summary>A footprint turned by 90°: its width, height and row-major mask (1 = the tile is blocked).</summary>
/// <param name="Width">Width in tiles.</param>
/// <param name="Height">Height in tiles.</param>
/// <param name="Mask">Row-major mask, Width × Height.</param>
public sealed record TurnedFootprint(int Width, int Height, System.Collections.Immutable.ImmutableArray<byte> Mask)
{
    /// <summary>The footprint <paramref name="mask"/> of <paramref name="width"/> × <paramref name="height"/> read transposed.</summary>
    public static TurnedFootprint Transposed(int width, int height, ReadOnlySpan<byte> mask)
    {
        var cells = new byte[width * height];
        for (int y = 0; y < width; y++)
        {
            for (int x = 0; x < height; x++)
            {
                cells[(y * height) + x] = mask.Length == width * height ? mask[(x * width) + y] : (byte)1;
            }
        }

        return new TurnedFootprint(height, width, [.. cells]);
    }
}
