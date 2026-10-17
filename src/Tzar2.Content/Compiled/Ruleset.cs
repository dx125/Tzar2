using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Tzar2.Core.Hashing;

namespace Tzar2.Content.Compiled;

/// <summary>
/// A compiled ruleset as the simulation consumes it (TDD §7.2: Sim references Content for compiled ruleset/map types
/// only; REQ-ARCH-01). Immutable. The rules compiler (<c>Tzar2.Content.Rules.RulesetCompiler</c>, §11.2) produces it
/// from YAML and the imported tables; the M2 movement tests build small ones in code.
/// </summary>
public sealed class Ruleset
{
    /// <summary>Format version of <see cref="ContentHash"/>'s encoding; bump when fields are added.</summary>
    public const int HashVersion = 3;

    /// <summary>Most resource types a ruleset may define (players store one amount per type).</summary>
    public const int MaxResourceTypes = 8;

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
    private readonly string[] _resourceTypes;
    private readonly DamageBonus[] _bonuses;
    private readonly int[] _bonusStart;
    private readonly string[] _sortedKeys;
    private readonly ushort[] _sortedIds;

    /// <summary>Creates a ruleset; ids must equal list positions.</summary>
    /// <param name="name">Ruleset name.</param>
    /// <param name="terrainTypes">Terrain types by id.</param>
    /// <param name="entityTypes">Entity types by id.</param>
    /// <param name="occupancyCellsPerTileAxis">Occupancy cells per tile axis (1, 2 or 4).</param>
    /// <param name="maxPlayers">Most kingdoms.</param>
    /// <param name="corpseTtlMilliseconds">Corpse decal lifetime.</param>
    /// <param name="decalCap">Most decals.</param>
    /// <param name="resourceTypes">Resource type keys by id (GAME-01), at most <see cref="MaxResourceTypes"/>.</param>
    /// <param name="gameplay">Gameplay constants (null = <see cref="GameplaySettings.Default"/>).</param>
    /// <param name="damageBonuses">Attack bonuses per (attacker type, target type) pair (<c>BONUS.INI</c>); pairs must be unique.</param>
    /// <exception cref="ArgumentException">The lists are empty, ids do not match positions, keys repeat, or a trait refers to an unknown id.</exception>
    public Ruleset(
        string name,
        IReadOnlyList<TerrainTypeDefinition> terrainTypes,
        IReadOnlyList<EntityTypeDefinition> entityTypes,
        int occupancyCellsPerTileAxis = DefaultOccupancyCellsPerTileAxis,
        int maxPlayers = DefaultMaxPlayers,
        int corpseTtlMilliseconds = DefaultCorpseTtlMilliseconds,
        int decalCap = DefaultDecalCap,
        IReadOnlyList<string>? resourceTypes = null,
        GameplaySettings? gameplay = null,
        IReadOnlyList<DamageBonus>? damageBonuses = null)
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

        resourceTypes ??= [];
        if (resourceTypes.Count > MaxResourceTypes)
        {
            throw new ArgumentException($"At most {MaxResourceTypes} resource types.", nameof(resourceTypes));
        }

        _resourceTypes = new string[resourceTypes.Count];
        keys.Clear();
        for (int i = 0; i < resourceTypes.Count; i++)
        {
            string key = resourceTypes[i];
            if (string.IsNullOrEmpty(key) || !keys.Add(key))
            {
                throw new ArgumentException("Resource type keys must be unique and non-empty.", nameof(resourceTypes));
            }

            _resourceTypes[i] = key;
        }

        Gameplay = gameplay ?? GameplaySettings.Default;
        Gameplay.Validate(_resourceTypes.Length);
        foreach (EntityTypeDefinition e in _entityTypes)
        {
            ValidateTraits(e);
        }

        (_bonuses, _bonusStart) = IndexBonuses(damageBonuses ?? [], _entityTypes.Length);
        _sortedKeys = new string[_entityTypes.Length];
        _sortedIds = new ushort[_entityTypes.Length];
        for (int i = 0; i < _entityTypes.Length; i++)
        {
            _sortedKeys[i] = _entityTypes[i].Key;
            _sortedIds[i] = (ushort)i;
        }

        Array.Sort(_sortedKeys, _sortedIds, StringComparer.Ordinal);

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

    /// <summary>Resource type keys by id (GAME-01: food, wood, stone, gold, iron in classic).</summary>
    public IReadOnlyList<string> ResourceTypes => _resourceTypes;

    /// <summary>Gameplay constants.</summary>
    public GameplaySettings Gameplay { get; }

    /// <summary>All attack bonuses, sorted by attacker then target type id.</summary>
    public IReadOnlyList<DamageBonus> DamageBonuses => _bonuses;

    /// <summary>xxHash64 of every field, in declaration order; replays and lobbies compare it (§19.4).</summary>
    public ulong ContentHash { get; }

    /// <summary>The attack bonus in basis points of <paramref name="attacker"/> against <paramref name="target"/> (0 = none).</summary>
    public int BonusBasisPoints(ushort attacker, ushort target)
    {
        if (attacker >= _entityTypes.Length)
        {
            return 0;
        }

        int lo = _bonusStart[attacker];
        int hi = _bonusStart[attacker + 1] - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            ushort t = _bonuses[mid].Target;
            if (t == target)
            {
                return _bonuses[mid].BasisPoints;
            }

            if (t < target)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return 0;
    }

    /// <summary>The id of the resource type <paramref name="key"/>, or -1.</summary>
    public int ResourceId(string key) => Array.IndexOf(_resourceTypes, key);

    /// <summary>The id of the entity type <paramref name="key"/>, or -1.</summary>
    public int EntityTypeId(string key)
    {
        int i = Array.BinarySearch(_sortedKeys, key, StringComparer.Ordinal);
        return i >= 0 ? _sortedIds[i] : -1;
    }

    private static (DamageBonus[] Bonuses, int[] Start) IndexBonuses(IReadOnlyList<DamageBonus> bonuses, int types)
    {
        var sorted = new DamageBonus[bonuses.Count];
        for (int i = 0; i < sorted.Length; i++)
        {
            DamageBonus b = bonuses[i];
            if (b.Attacker >= types || b.Target >= types)
            {
                throw new ArgumentException($"Damage bonus {b.Attacker} -> {b.Target} names an unknown type.", nameof(bonuses));
            }

            sorted[i] = b;
        }

        Array.Sort(sorted, static (a, b) => a.Attacker != b.Attacker ? a.Attacker.CompareTo(b.Attacker) : a.Target.CompareTo(b.Target));
        int[] start = new int[types + 1];
        for (int i = 0; i < sorted.Length; i++)
        {
            if (i > 0 && sorted[i].Attacker == sorted[i - 1].Attacker && sorted[i].Target == sorted[i - 1].Target)
            {
                throw new ArgumentException($"Damage bonus {sorted[i].Attacker} -> {sorted[i].Target} is defined twice.", nameof(bonuses));
            }

            start[sorted[i].Attacker + 1]++;
        }

        for (int t = 0; t < types; t++)
        {
            start[t + 1] += start[t];
        }

        return (sorted, start);
    }

    private void ValidateTraits(EntityTypeDefinition e)
    {
        EntityTraits t = e.Traits;
        int resources = _resourceTypes.Length;
        string where = $"Entity type '{e.Key}'";
        if (t.Category > EntityCategory.Resource)
        {
            throw new ArgumentException($"{where}: unknown category.");
        }

        if (t.Cost.IsDefault || (!t.Cost.IsEmpty && t.Cost.Length != resources))
        {
            throw new ArgumentException($"{where}: the cost needs one amount per resource type ({resources}).");
        }

        foreach (int c in t.Cost)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(c, where);
        }

        ArgumentOutOfRangeException.ThrowIfNegative(t.BuildTimeMs, where);
        ArgumentOutOfRangeException.ThrowIfNegative(t.Resistance, where);
        ArgumentOutOfRangeException.ThrowIfNegative(t.SightUnits, where);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(t.SightUnits, 64 * 2048, where);
        ArgumentOutOfRangeException.ThrowIfNegative(t.PopulationCost, where);
        ArgumentOutOfRangeException.ThrowIfNegative(t.PopulationProvided, where);
        t.Armament?.Validate();
        t.Harvester?.Validate(resources);
        CheckTypeIds(t.Builds, where, "builds", requireFootprint: true);
        CheckTypeIds(t.RequiredTypes, where, "requires", requireFootprint: true);
        if (t.Producer is { } producer)
        {
            if (!e.HasFootprint)
            {
                throw new ArgumentException($"{where}: only buildings can produce.");
            }

            ArgumentOutOfRangeException.ThrowIfLessThan(producer.QueueCapacity, 1, where);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(producer.QueueCapacity, ProducerDefinition.MaxQueueCapacity, where);
            CheckTypeIds(producer.Produces, where, "produces", requireFootprint: false);
            foreach (ushort id in producer.Produces)
            {
                if (!_entityTypes[id].IsMobile)
                {
                    throw new ArgumentException($"{where}: produces '{_entityTypes[id].Key}', which cannot move.");
                }
            }
        }

        if (t.ResourceNode is { } node
            && ((uint)node.Resource >= (uint)resources || (node.Amount < 1 && node.Amount != ResourceNodeDefinition.Unlimited) || !e.HasFootprint))
        {
            throw new ArgumentException($"{where}: invalid resource node.");
        }

        if ((uint)t.DropOffMask >= (1u << resources) || (t.DropOffMask != 0 && !e.HasFootprint))
        {
            throw new ArgumentException($"{where}: invalid drop-off.");
        }

        if ((t.Harvester is not null || t.IsBuilder) && !e.IsMobile)
        {
            throw new ArgumentException($"{where}: workers must be mobile.");
        }
    }

    private void CheckTypeIds(ImmutableArray<ushort> ids, string where, string what, bool requireFootprint)
    {
        if (ids.IsDefault)
        {
            throw new ArgumentException($"{where}: {what} must not be default.");
        }

        for (int i = 0; i < ids.Length; i++)
        {
            if (ids[i] >= _entityTypes.Length || (i > 0 && ids[i] <= ids[i - 1]))
            {
                throw new ArgumentException($"{where}: {what} must list known type ids in ascending order.");
            }

            if (requireFootprint && !_entityTypes[ids[i]].HasFootprint)
            {
                throw new ArgumentException($"{where}: {what} lists '{_entityTypes[ids[i]].Key}', which is not a building.");
            }
        }
    }

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
            AddTraits(hasher, e.Traits);
        }

        hasher.Add(_resourceTypes.Length);
        foreach (string r in _resourceTypes)
        {
            hasher.Add(Encoding.UTF8.GetBytes(r));
        }

        GameplaySettings g = Gameplay;
        hasher.Add((byte)g.GroupMoveSpeed).Add(g.PopulationLimit).Add(g.DefaultStartingResources).Add(g.MinimumDamage).Add(g.ResistanceScale)
            .Add(g.InteractionReachUnits).Add(g.MeleeReachUnits).Add(g.AcquireIntervalMs).Add(g.NodeSearchRadiusTiles)
            .Add(g.ConstructionRefundBasisPoints).Add(g.ProductionRefundBasisPoints).Add(g.StartingResources.Length);
        foreach (StartingResources level in g.StartingResources)
        {
            hasher.Add(Encoding.UTF8.GetBytes(level.Key)).AddSpan<int>(level.Amounts.AsSpan());
        }

        hasher.Add(_bonuses.Length);
        foreach (DamageBonus b in _bonuses)
        {
            hasher.Add((int)b.Attacker).Add((int)b.Target).Add(b.BasisPoints);
        }

        return hasher.GetCurrentHash();
    }

    private static void AddTraits(StateHasher hasher, EntityTraits t)
    {
        hasher.Add((byte)t.Category).Add(Encoding.UTF8.GetBytes(t.LegacyClass)).Add(Encoding.UTF8.GetBytes(t.Civilization))
            .AddSpan<int>(t.Cost.AsSpan()).Add(t.BuildTimeMs).Add(t.SightUnits).Add(t.Defence).Add(t.Resistance)
            .Add(t.DropOffMask).Add(t.PopulationCost).Add(t.PopulationProvided)
            .AddSpan<ushort>(t.Builds.AsSpan()).AddSpan<ushort>(t.RequiredTypes.AsSpan())
            .Add(Encoding.UTF8.GetBytes(t.RequiredTechnology));
        hasher.Add(t.Armament is not null);
        if (t.Armament is { } a)
        {
            hasher.Add(a.Attack).Add(a.RangeUnits).Add(a.CooldownMs).Add(a.HitDelayMs).Add((byte)a.DamageClass).Add(a.AutoAcquire);
        }

        hasher.Add(t.Harvester is not null);
        if (t.Harvester is { } h)
        {
            hasher.Add(h.Capacity).AddSpan<int>(h.GatherMs.AsSpan());
        }

        hasher.Add(t.Producer is not null);
        if (t.Producer is { } p)
        {
            hasher.Add(p.QueueCapacity).AddSpan<ushort>(p.Produces.AsSpan());
        }

        hasher.Add(t.ResourceNode is not null);
        if (t.ResourceNode is { } n)
        {
            hasher.Add(n.Resource).Add(n.Amount);
        }
    }
}

/// <summary>An attack bonus of one entity type against another (<c>BONUS.INI</c>, e.g. pikeman vs mounted +600%).</summary>
/// <param name="Attacker">Attacking type id.</param>
/// <param name="Target">Target type id.</param>
/// <param name="BasisPoints">Bonus in basis points (+600% = 60,000; -75% = -7,500).</param>
public readonly record struct DamageBonus(ushort Attacker, ushort Target, int BasisPoints);
