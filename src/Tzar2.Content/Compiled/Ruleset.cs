using System;
using System.Collections.Generic;
using System.Text;
using Tzar2.Core.Hashing;

namespace Tzar2.Content.Compiled;

/// <summary>
/// A compiled ruleset as the simulation consumes it (TDD §7.2: Sim references Content for compiled ruleset/map types
/// only; REQ-ARCH-01). Immutable. This is the minimal M2 contract; the rules compiler (§11.2) produces it from YAML
/// and adds sections without changing what is here.
/// </summary>
public sealed class Ruleset
{
    /// <summary>Format version of <see cref="ContentHash"/>'s encoding; bump when fields are added.</summary>
    public const int HashVersion = 2;

    /// <summary>Default occupancy sub-cells per tile axis (TDD §9.1: 2×2 cells per tile; UNK-11).</summary>
    public const int DefaultOccupancyCellsPerTileAxis = 2;

    /// <summary>Default maximum kingdoms (REQ-PATH-00: 8 in classic; rules may raise it to 16).</summary>
    public const int DefaultMaxPlayers = 8;

    /// <summary>Default corpse lifetime (REQ-PATH-08: e.g. corpse 120 s).</summary>
    public const int DefaultCorpseTtlMilliseconds = 120_000;

    /// <summary>Default global decal cap (REQ-PATH-08: e.g. 4,000, oldest first).</summary>
    public const int DefaultDecalCap = 4000;

    private readonly TerrainTypeDefinition[] _terrainTypes;
    private readonly EntityTypeDefinition[] _entityTypes;

    /// <summary>Creates a ruleset; ids must equal list positions.</summary>
    /// <exception cref="ArgumentException">The lists are empty, ids do not match positions, or keys repeat.</exception>
    public Ruleset(
        string name,
        IReadOnlyList<TerrainTypeDefinition> terrainTypes,
        IReadOnlyList<EntityTypeDefinition> entityTypes,
        int occupancyCellsPerTileAxis = DefaultOccupancyCellsPerTileAxis,
        int maxPlayers = DefaultMaxPlayers,
        int corpseTtlMilliseconds = DefaultCorpseTtlMilliseconds,
        int decalCap = DefaultDecalCap)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(terrainTypes);
        ArgumentNullException.ThrowIfNull(entityTypes);
        if (occupancyCellsPerTileAxis is not (1 or 2 or 4))
        {
            // Cells must tile a 2,048-unit tile exactly (TDD §8.2), or points of one tile map into the next tile's cells.
            throw new ArgumentOutOfRangeException(nameof(occupancyCellsPerTileAxis), "Occupancy cells per tile axis must be 1, 2 or 4.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPlayers, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPlayers, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(corpseTtlMilliseconds, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(decalCap, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decalCap, 1 << 20);
        if (terrainTypes.Count is 0 or > 256)
        {
            throw new ArgumentException("A ruleset needs 1-256 terrain types.", nameof(terrainTypes));
        }

        if (entityTypes.Count > ushort.MaxValue)
        {
            throw new ArgumentException("Too many entity types.", nameof(entityTypes));
        }

        _terrainTypes = new TerrainTypeDefinition[terrainTypes.Count];
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < terrainTypes.Count; i++)
        {
            TerrainTypeDefinition t = terrainTypes[i] ?? throw new ArgumentException("Null terrain type.", nameof(terrainTypes));
            if (t.Id != i || !keys.Add(t.Key))
            {
                throw new ArgumentException($"Terrain type '{t.Key}' must have id {i} and a unique key.", nameof(terrainTypes));
            }

            _terrainTypes[i] = t;
        }

        _entityTypes = new EntityTypeDefinition[entityTypes.Count];
        keys.Clear();
        for (int i = 0; i < entityTypes.Count; i++)
        {
            EntityTypeDefinition e = entityTypes[i] ?? throw new ArgumentException("Null entity type.", nameof(entityTypes));
            if (e.Id != i || !keys.Add(e.Key))
            {
                throw new ArgumentException($"Entity type '{e.Key}' must have id {i} and a unique key.", nameof(entityTypes));
            }

            _entityTypes[i] = e;
        }

        Name = name;
        OccupancyCellsPerTileAxis = occupancyCellsPerTileAxis;
        MaxPlayers = maxPlayers;
        CorpseTtlMilliseconds = corpseTtlMilliseconds;
        DecalCap = decalCap;
        ContentHash = ComputeHash();
    }

    /// <summary>Ruleset name, e.g. <c>classic</c>.</summary>
    public string Name { get; }

    /// <summary>Terrain types by id.</summary>
    public IReadOnlyList<TerrainTypeDefinition> TerrainTypes => _terrainTypes;

    /// <summary>Entity types by id.</summary>
    public IReadOnlyList<EntityTypeDefinition> EntityTypes => _entityTypes;

    /// <summary>Occupancy grid resolution: cells per tile along each axis (TDD §9.1): 1, 2 or 4, a divisor of the 2,048-unit tile.</summary>
    public int OccupancyCellsPerTileAxis { get; }

    /// <summary>Maximum number of kingdoms in a match.</summary>
    public int MaxPlayers { get; }

    /// <summary>How long a corpse decal lives (REQ-PATH-08); 0 = corpses are not recorded.</summary>
    public int CorpseTtlMilliseconds { get; }

    /// <summary>Most decals alive at once; the oldest is dropped first (REQ-PATH-08).</summary>
    public int DecalCap { get; }

    /// <summary>xxHash64 of every field, in declaration order; replays and lobbies compare it (§19.4).</summary>
    public ulong ContentHash { get; }

    private ulong ComputeHash()
    {
        var hasher = new StateHasher();
        hasher.Add(HashVersion).Add(Encoding.UTF8.GetBytes(Name)).Add(OccupancyCellsPerTileAxis).Add(MaxPlayers)
            .Add(CorpseTtlMilliseconds).Add(DecalCap);
        hasher.Add(_terrainTypes.Length);
        foreach (TerrainTypeDefinition t in _terrainTypes)
        {
            hasher.Add(t.Id).Add(Encoding.UTF8.GetBytes(t.Key)).Add((byte)t.Passable).Add(t.IsWater);
        }

        hasher.Add(_entityTypes.Length);
        foreach (EntityTypeDefinition e in _entityTypes)
        {
            hasher.Add((int)e.Id).Add(Encoding.UTF8.GetBytes(e.Key)).Add((byte)e.Locomotor).Add(e.SpeedUnitsPerSecond).Add(e.MaxHealth)
                .Add(e.Radius).Add(e.ClearanceTiles).Add(e.FootprintWidth).Add(e.FootprintHeight).Add(e.Footprint).Add(e.LeavesCorpse);
        }

        return hasher.GetCurrentHash();
    }
}
