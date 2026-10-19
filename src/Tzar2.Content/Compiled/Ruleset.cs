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
public sealed partial class Ruleset
{
    /// <summary>Format version of <see cref="ContentHash"/>'s encoding; bump when fields are added.</summary>
    public const int HashVersion = 6;

    /// <summary>Most resource types a ruleset may define (players store one amount per type).</summary>
    public const int MaxResourceTypes = 8;

    /// <summary>Most technologies (players keep one research bit each).</summary>
    public const int MaxTechnologies = 256;

    /// <summary>Most upgrades (entities keep one bit each for level upgrades).</summary>
    public const int MaxUpgrades = 32;

    /// <summary>Most entity types (production queues encode research and upgrades above them, ADR 0040).</summary>
    public const int MaxEntityTypes = 0x4000;

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
    private readonly TechnologyDefinition[] _technologies;
    private readonly UpgradeDefinition[] _upgrades;
    private readonly string[] _civilizations;
    private readonly OrderGate[] _orderGates;
    private readonly string[] _orderGateKeys;
    private readonly int[] _orderGateIndex;

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
    /// <param name="technologies">Technologies by id (GAME-06).</param>
    /// <param name="upgrades">Upgrades by id (GAME-06).</param>
    /// <param name="civilizations">Civilization keys (<c>euro</c>, <c>asia</c>, <c>arab</c>, <c>comm</c>).</param>
    /// <param name="orderGates">Orders that need a technology per civilization (<c>TECHTREE [orders]</c>).</param>
    /// <param name="world">World-system constants (null = <see cref="WorldSettings.Default"/>; ADR 0100).</param>
    /// <param name="hudResourceMask">Resource types the HUD shows (bit = id; -1 = all; GAME-01: iron is hidden).</param>
    /// <param name="combat">Combat, experience, holder, item and magic content (GAME-07…11; null = none).</param>
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
        IReadOnlyList<DamageBonus>? damageBonuses = null,
        IReadOnlyList<TechnologyDefinition>? technologies = null,
        IReadOnlyList<UpgradeDefinition>? upgrades = null,
        IReadOnlyList<string>? civilizations = null,
        IReadOnlyList<OrderGate>? orderGates = null,
        int hudResourceMask = -1,
        WorldSettings? world = null,
        CombatRules? combat = null)
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

        if (entityTypes.Count > MaxEntityTypes)
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
        World = world ?? WorldSettings.Default;
        World.Validate();
        _civilizations = [.. civilizations ?? []];
        keys.Clear();
        foreach (string civ in _civilizations)
        {
            if (string.IsNullOrEmpty(civ) || !keys.Add(civ))
            {
                throw new ArgumentException("Civilization keys must be unique and non-empty.", nameof(civilizations));
            }
        }

        _technologies = [.. technologies ?? []];
        _upgrades = [.. upgrades ?? []];
        if (_technologies.Length > MaxTechnologies || _upgrades.Length > MaxUpgrades)
        {
            throw new ArgumentException($"At most {MaxTechnologies} technologies and {MaxUpgrades} upgrades.", nameof(technologies));
        }

        _orderGates = [.. orderGates ?? []];
        _orderGateKeys = new string[_orderGates.Length];
        _orderGateIndex = new int[_orderGates.Length];
        for (int i = 0; i < _orderGates.Length; i++)
        {
            _orderGateKeys[i] = (_orderGates[i].Order ?? string.Empty) + "\n" + (_orderGates[i].Civilization ?? string.Empty);
            _orderGateIndex[i] = i;
        }

        Array.Sort(_orderGateKeys, _orderGateIndex, StringComparer.Ordinal);
        HudResourceMask = hudResourceMask & (int)((1u << _resourceTypes.Length) - 1);
        InitCombat(combat);
        foreach (EntityTypeDefinition e in _entityTypes)
        {
            ValidateTraits(e);
        }

        ValidateTechnologies(keys);
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

    /// <summary>Technologies by id (GAME-06).</summary>
    public IReadOnlyList<TechnologyDefinition> Technologies => _technologies;

    /// <summary>Upgrades by id (GAME-06).</summary>
    public IReadOnlyList<UpgradeDefinition> Upgrades => _upgrades;

    /// <summary>Civilization keys.</summary>
    public IReadOnlyList<string> Civilizations => _civilizations;

    /// <summary>Orders that need a technology per civilization (consumed by the systems that implement the orders).</summary>
    public IReadOnlyList<OrderGate> OrderGates => _orderGates;

    /// <summary>Resource types the HUD shows (bit = resource type id; GAME-01: iron exists in the data but not in the HUD).</summary>
    public int HudResourceMask { get; }

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

    /// <summary>The id of technology <paramref name="key"/>, or -1.</summary>
    public int TechnologyId(string key)
    {
        for (int i = 0; i < _technologies.Length; i++)
        {
            if (string.Equals(_technologies[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The technology <paramref name="civilization"/> needs for <paramref name="order"/> (<c>TECHTREE [orders]</c>: a spell,
    /// loan, bribe...), or -1 when the order is not gated for it (binary search; for the systems of GAME-11/14).
    /// </summary>
    public int OrderTechnology(string order, string civilization)
    {
        int i = Array.BinarySearch(_orderGateKeys, order + "\n" + civilization, StringComparer.Ordinal);
        return i >= 0 ? _orderGates[_orderGateIndex[i]].Technology : -1;
    }

    /// <summary>The id of upgrade <paramref name="key"/>, or -1.</summary>
    public int UpgradeId(string key)
    {
        for (int i = 0; i < _upgrades.Length; i++)
        {
            if (string.Equals(_upgrades[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
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
        ValidateWorldTraits(e, where);
        CheckTypeIds(t.Builds, where, "builds", requireFootprint: true);
        CheckTypeIds(t.RequiredTypes, where, "requires", requireFootprint: true);
        if (t.Producer is { } producer)
        {
            if (!e.HasFootprint && !producer.Produces.IsEmpty)
            {
                throw new ArgumentException($"{where}: only buildings can produce.");
            }

            if (producer.Researches.IsDefault || producer.Upgrades.IsDefault)
            {
                throw new ArgumentException($"{where}: researches and upgrades must not be default.");
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
            && ((uint)node.Resource >= (uint)resources || (node.Amount < 1 && node.Amount != ResourceNodeDefinition.Unlimited)
                || node.RegenMs < 0 || node.GatherMs < 0 || node.CarryLoad < 0 || (node.RegenMs > 0 && node.Amount < 1)))
        {
            throw new ArgumentException($"{where}: invalid resource node.");
        }

        if ((uint)t.DropOffMask >= (1u << resources) || (t.DropOffMask != 0 && (!e.HasFootprint || t.DropOffAccess == DropOffAccess.None))
            || t.DropOffAccess > (DropOffAccess.Land | DropOffAccess.Water) || t.Placement > PlacementKind.Bridge)
        {
            throw new ArgumentException($"{where}: invalid drop-off.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(t.ManaCost, where);
        ArgumentOutOfRangeException.ThrowIfNegative(t.LifetimeMs, where);
        if (t.ManaPool is { } mana && (mana.Max < 0 || mana.RegenPerMinute < 0 || mana.Max > 1_000_000 || mana.RegenPerMinute > 1_000_000))
        {
            throw new ArgumentException($"{where}: invalid mana pool.");
        }

        if (t.SpawnOnDeath >= _entityTypes.Length || t.SpawnOnDeath < -1
            || (t.SpawnOnDeath >= 0 && (_entityTypes[t.SpawnOnDeath].HasFootprint || _entityTypes[t.SpawnOnDeath].IsMobile)))
        {
            throw new ArgumentException($"{where}: spawn on death must name an immobile type without a footprint (a resource pile).");
        }

        if (t.Gate is { } gate && (!e.HasFootprint || gate.Passage.IsDefault || gate.Passage.Length != e.FootprintWidth * e.FootprintHeight))
        {
            throw new ArgumentException($"{where}: a gate needs one passage cell per footprint tile.");
        }

        if ((t.IsWall || t.IsBridge || t.Burnable || t.LeavesRuin || t.ProtectedDelete || t.Placement != PlacementKind.Land) && !e.HasFootprint)
        {
            throw new ArgumentException($"{where}: wall, bridge, burning, ruin, protected delete and placement rules need a footprint.");
        }

        if (t.Decay is { } decay && (decay.AfterMs < 0 || decay.HealthPerMinute < 0))
        {
            throw new ArgumentException($"{where}: invalid decay.");
        }

        if (t.Requirements.IsDefault)
        {
            throw new ArgumentException($"{where}: requirements must not be default.");
        }

        foreach (RequirementSet set in t.Requirements)
        {
            CheckTypeIds(set.Types, where, "requires", requireFootprint: true);
        }

        if ((t.Harvester is not null || t.IsBuilder) && !e.IsMobile)
        {
            throw new ArgumentException($"{where}: workers must be mobile.");
        }
    }

    /// <summary>Checks that <paramref name="ids"/> are ascending ids below <paramref name="count"/>.</summary>
    internal static void CheckAscendingIds(ImmutableArray<ushort> ids, int count, string what)
    {
        if (ids.IsDefault)
        {
            throw new ArgumentException($"{what} must not be default.");
        }

        for (int i = 0; i < ids.Length; i++)
        {
            if (ids[i] >= count || (i > 0 && ids[i] <= ids[i - 1]))
            {
                throw new ArgumentException($"{what} must list known ids in ascending order.");
            }
        }
    }

    // Technology, upgrade and order-gate cross-references, and the research lists of producers.
    private void ValidateTechnologies(HashSet<string> keys)
    {
        keys.Clear();
        for (int i = 0; i < _technologies.Length; i++)
        {
            TechnologyDefinition t = _technologies[i] ?? throw new ArgumentException("Null technology.");
            if (t.Id != i || string.IsNullOrEmpty(t.Key) || !keys.Add(t.Key))
            {
                throw new ArgumentException($"Technology '{t.Key}' must have id {i} and a unique key.");
            }

            ValidateResearch($"Technology '{t.Key}'", t.Cost, t.TimeMs, t.Requirements, t.Effects);
        }

        keys.Clear();
        for (int i = 0; i < _upgrades.Length; i++)
        {
            UpgradeDefinition u = _upgrades[i] ?? throw new ArgumentException("Null upgrade.");
            string where = $"Upgrade '{u.Key}'";
            if (u.Id != i || string.IsNullOrEmpty(u.Key) || !keys.Add(u.Key))
            {
                throw new ArgumentException($"{where} must have id {i} and a unique key.");
            }

            ValidateResearch(where, u.Cost, u.TimeMs, u.Requirements, u.Effects);
            CheckAscendingIds(u.After, _upgrades.Length, $"{where}: after");
            if (u.Conversions.IsDefault)
            {
                throw new ArgumentException($"{where}: conversions must not be default.");
            }

            for (int k = 0; k < u.Conversions.Length; k++)
            {
                (ushort from, ushort to) = u.Conversions[k];
                if (from >= _entityTypes.Length || to >= _entityTypes.Length || from == to || (k > 0 && from <= u.Conversions[k - 1].From))
                {
                    throw new ArgumentException($"{where}: conversions must name known, different types, sorted by the source type.");
                }

                EntityTypeDefinition a = _entityTypes[from];
                EntityTypeDefinition b = _entityTypes[to];
                if (a.FootprintWidth != b.FootprintWidth || a.FootprintHeight != b.FootprintHeight || a.IsMobile != b.IsMobile)
                {
                    throw new ArgumentException($"{where}: '{a.Key}' and '{b.Key}' must have the same footprint to convert in place.");
                }

                // The traits whose sim state lives in its own store must agree, so a converted entity's stores stay valid.
                EntityTraits ta = a.Traits;
                EntityTraits tb = b.Traits;
                if ((ta.Gate is null) != (tb.Gate is null) || ta.Burnable != tb.Burnable || (ta.Capturable is null) != (tb.Capturable is null)
                    || (ta.Decay is null) != (tb.Decay is null) || (ta.ManaPool is null) != (tb.ManaPool is null) || (ta.LifetimeMs > 0) != (tb.LifetimeMs > 0)
                    || ta.IsBridge != tb.IsBridge || (ta.ResourceNode is null) != (tb.ResourceNode is null) || ta.IsRepairable != tb.IsRepairable
                    || (ta.Harvester is null) != (tb.Harvester is null) || ta.IsBuilder != tb.IsBuilder || ta.IsCapturer != tb.IsCapturer)
                {
                    throw new ArgumentException($"{where}: '{a.Key}' and '{b.Key}' must agree on gate, fire, capture, decay, mana, lifetime, bridge, resource, repair and worker traits to convert in place.");
                }
            }
        }

        foreach (OrderGate g in _orderGates)
        {
            if (string.IsNullOrEmpty(g.Order) || g.Technology >= _technologies.Length || Array.IndexOf(_civilizations, g.Civilization) < 0)
            {
                throw new ArgumentException($"Order gate '{g.Order}' names an unknown technology or civilization.");
            }
        }

        foreach (EntityTypeDefinition e in _entityTypes)
        {
            if (e.Traits.Producer is { } p)
            {
                CheckAscendingIds(p.Researches, _technologies.Length, $"Entity type '{e.Key}': researches");
                CheckAscendingIds(p.Upgrades, _upgrades.Length, $"Entity type '{e.Key}': upgrades");
            }

            foreach (RequirementSet set in e.Traits.Requirements)
            {
                CheckAscendingIds(set.Technologies, _technologies.Length, $"Entity type '{e.Key}': required technologies");
            }

            if (e.Traits.Capturable is { } c && c.RequiredTechnology >= _technologies.Length)
            {
                throw new ArgumentException($"Entity type '{e.Key}': unknown capture technology.");
            }
        }
    }

    private void ValidateResearch(string where, ImmutableArray<int> cost, int timeMs, ImmutableArray<RequirementSet> requirements, ImmutableArray<ModifierDefinition> effects)
    {
        if (cost.IsDefault || (!cost.IsEmpty && cost.Length != _resourceTypes.Length) || requirements.IsDefault || effects.IsDefault)
        {
            throw new ArgumentException($"{where}: the cost needs one amount per resource type; requirements and effects must not be default.");
        }

        foreach (int c in cost)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(c, where);
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(timeMs, 1, where);
        foreach (RequirementSet set in requirements)
        {
            CheckAscendingIds(set.Technologies, _technologies.Length, $"{where}: required technologies");
            CheckTypeIds(set.Types, where, "requires", requireFootprint: true);
        }

        foreach (ModifierDefinition m in effects)
        {
            m.Validate(_entityTypes.Length);
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
            .Add(g.ConstructionRefundBasisPoints).Add(g.ProductionRefundBasisPoints).Add(g.ResearchRefundBasisPoints).Add(g.CapturePointsPerSecond)
            .Add(g.MinimumCaptureResistance).Add(g.CaptureResetMs).Add(g.InfluenceRadiusTiles).Add(g.RepairTimeBasisPoints)
            .Add(g.BurnThresholdBasisPoints).Add(g.BurnDamagePerMinute).Add(g.WallContinueRadiusTiles).Add(g.MaxWallSegments)
            .Add(g.GateOpenReachUnits).Add(g.GateCloseReachUnits).Add(g.GateChangeIntervalMs).Add(g.MaxGateChangesPerTick)
            .Add(g.RepairCostBasisPoints).Add(g.RepairerFalloffBasisPoints).Add(g.StartingResources.Length);
        foreach (StartingResources level in g.StartingResources)
        {
            hasher.Add(Encoding.UTF8.GetBytes(level.Key)).AddSpan<int>(level.Amounts.AsSpan());
        }

        hasher.Add(_bonuses.Length);
        foreach (DamageBonus b in _bonuses)
        {
            hasher.Add((int)b.Attacker).Add((int)b.Target).Add(b.BasisPoints);
        }

        hasher.Add(HudResourceMask).Add(_civilizations.Length);
        foreach (string civ in _civilizations)
        {
            hasher.Add(Encoding.UTF8.GetBytes(civ));
        }

        hasher.Add(_technologies.Length);
        foreach (TechnologyDefinition t in _technologies)
        {
            hasher.Add(Encoding.UTF8.GetBytes(t.Key)).Add(Encoding.UTF8.GetBytes(t.Name)).AddSpan<int>(t.Cost.AsSpan()).Add(t.TimeMs);
            AddRequirements(hasher, t.Requirements);
            AddModifiers(hasher, t.Effects);
        }

        hasher.Add(_upgrades.Length);
        foreach (UpgradeDefinition u in _upgrades)
        {
            hasher.Add(Encoding.UTF8.GetBytes(u.Key)).Add(Encoding.UTF8.GetBytes(u.Name)).AddSpan<int>(u.Cost.AsSpan()).Add(u.TimeMs)
                .AddSpan<ushort>(u.After.AsSpan()).Add(u.Conversions.Length);
            foreach ((ushort from, ushort to) in u.Conversions)
            {
                hasher.Add((int)from).Add((int)to);
            }

            AddRequirements(hasher, u.Requirements);
            AddModifiers(hasher, u.Effects);
        }

        hasher.Add(_orderGates.Length);
        foreach (OrderGate gate in _orderGates)
        {
            hasher.Add(Encoding.UTF8.GetBytes(gate.Order)).Add(Encoding.UTF8.GetBytes(gate.Civilization)).Add((int)gate.Technology);
        }

        AddWorld(hasher);
        AddCombat(hasher);
        return hasher.GetCurrentHash();
    }

    private static void AddRequirements(StateHasher hasher, ImmutableArray<RequirementSet> sets)
    {
        hasher.Add(sets.Length);
        foreach (RequirementSet s in sets)
        {
            hasher.Add(Encoding.UTF8.GetBytes(s.Civilization)).AddSpan<ushort>(s.Technologies.AsSpan()).AddSpan<ushort>(s.Types.AsSpan()).Add(s.Unsatisfiable);
        }
    }

    private static void AddModifiers(StateHasher hasher, ImmutableArray<ModifierDefinition> modifiers)
    {
        hasher.Add(modifiers.Length);
        foreach (ModifierDefinition m in modifiers)
        {
            hasher.Add((byte)m.Stat).Add(m.Add).Add(m.BasisPoints).AddSpan<ushort>(m.Types.AsSpan());
        }
    }

    private static void AddTraits(StateHasher hasher, EntityTraits t)
    {
        hasher.Add((byte)t.Category).Add(Encoding.UTF8.GetBytes(t.LegacyClass)).Add(Encoding.UTF8.GetBytes(t.Civilization))
            .AddSpan<int>(t.Cost.AsSpan()).Add(t.BuildTimeMs).Add(t.SightUnits).Add(t.Defence).Add(t.Resistance)
            .Add(t.DropOffMask).Add(t.PopulationCost).Add(t.PopulationProvided)
            .AddSpan<ushort>(t.Builds.AsSpan()).AddSpan<ushort>(t.RequiredTypes.AsSpan())
            .Add(Encoding.UTF8.GetBytes(t.RequiredTechnology)).Add(t.ManaCost).Add((byte)t.DropOffAccess).Add(t.AutoGather).Add((byte)t.Placement)
            .Add(t.IsCapturer).Add(t.IsRepairable).Add(t.Burnable).Add(t.LeavesRuin).Add(t.IsWall).Add(t.IsBridge).Add(t.ProtectedDelete)
            .Add(t.SpawnOnDeath).Add(t.LifetimeMs);
        AddRequirements(hasher, t.Requirements);
        hasher.Add(t.ManaPool is not null);
        if (t.ManaPool is { } mana)
        {
            hasher.Add(mana.Max).Add(mana.RegenPerMinute);
        }

        hasher.Add(t.Capturable is not null);
        if (t.Capturable is { } capture)
        {
            hasher.Add(capture.Instant).Add(capture.RequiredTechnology);
        }

        hasher.Add(t.Influence is not null);
        if (t.Influence is { } influence)
        {
            hasher.Add(influence.Own).Add(influence.Other);
        }

        hasher.Add(t.Gate is not null);
        if (t.Gate is { } gate)
        {
            hasher.AddSpan<byte>(gate.Passage.AsSpan());
        }

        hasher.Add(t.Decay is not null);
        if (t.Decay is { } decay)
        {
            hasher.Add(decay.AfterMs).Add(decay.HealthPerMinute);
        }
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
            hasher.Add(p.QueueCapacity).AddSpan<ushort>(p.Produces.AsSpan()).AddSpan<ushort>(p.Researches.AsSpan()).AddSpan<ushort>(p.Upgrades.AsSpan());
        }

        hasher.Add(t.ResourceNode is not null);
        if (t.ResourceNode is { } n)
        {
            hasher.Add(n.Resource).Add(n.Amount).Add(n.RegenMs).Add(n.GatherMs).Add(n.KeepWhenEmpty).Add(n.CarryLoad);
        }

        AddWorldTraits(hasher, t.World);
        AddCombatTraits(hasher, t);
    }
}

/// <summary>An attack bonus of one entity type against another (<c>BONUS.INI</c>, e.g. pikeman vs mounted +600%).</summary>
/// <param name="Attacker">Attacking type id.</param>
/// <param name="Target">Target type id.</param>
/// <param name="BasisPoints">Bonus in basis points (+600% = 60,000; -75% = -7,500).</param>
public readonly record struct DamageBonus(ushort Attacker, ushort Target, int BasisPoints);
