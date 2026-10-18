using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Rng;
using Tzar2.Sim.Components;
using Tzar2.Sim.Ecs;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;
using Tzar2.Sim.Navigation;

namespace Tzar2.Sim;

/// <summary>
/// The complete simulation state of one match (TDD §8): entities, components, map layers, players, RNG streams and
/// the tick counter. Everything here is in snapshots (REQ-SIM-13) except <see cref="Events"/>, which is output.
/// </summary>
public sealed partial class World
{
    private readonly PlayerState[] _players;
    private readonly int[] _entityTypeSpeedPerTick;

    /// <summary>Creates an empty world at tick 0.</summary>
    public World(Ruleset ruleset, MapData map, SimulationConfig config)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(config);
        config.Validate(ruleset.MaxPlayers);
        Ruleset = ruleset;
        Config = config;
        Map = new MapState(map, ruleset);
        Rng = new RngStreams(config.MatchSeed, config.PlayerCount);
        _players = new PlayerState[config.PlayerCount];
        for (int i = 0; i < _players.Length; i++)
        {
            _players[i].Flags = PlayerFlags.Active;
        }

        _entityTypeSpeedPerTick = new int[ruleset.EntityTypes.Count];
        for (int i = 0; i < _entityTypeSpeedPerTick.Length; i++)
        {
            _entityTypeSpeedPerTick[i] = (int)((long)ruleset.EntityTypes[i].SpeedUnitsPerSecond * config.TickLengthMilliseconds / 1000);
        }

        // Registration order fixes the hash and snapshot layout (ADR 0016); append new stores at the end.
        Positions = Components.RegisterHot<Position>("core.position");
        Identities = Components.RegisterHot<Identity>("core.identity");
        Healths = Components.RegisterHot<Health>("core.health");
        Motions = Components.RegisterHot<Motion>("core.motion");
        OccupancyCells = Components.RegisterHot<OccupancyCell>("core.occupancy");
        Names = Components.RegisterCold<EntityName>("core.name");
        Footprints = Components.RegisterHot<Footprint>("core.footprint");
        Combats = Components.RegisterHot<Combat>("game.combat");
        Workers = Components.RegisterHot<Worker>("game.worker");
        ResourceStocks = Components.RegisterHot<ResourceStock>("game.resource");
        Constructions = Components.RegisterHot<Construction>("game.construction");
        Productions = Components.RegisterHot<Production>("game.production");
        UpgradeLevels = Components.RegisterHot<UpgradeLevels>("game.upgrades");
        CaptureStates = Components.RegisterHot<CaptureState>("game.capture");
        Gates = Components.RegisterHot<GateState>("game.gate");
        Burnings = Components.RegisterHot<Burning>("game.burning");
        Neglects = Components.RegisterHot<Neglect>("game.neglect");
        Manas = Components.RegisterHot<ManaState>("game.mana");
        Lifetimes = Components.RegisterHot<Lifetime>("game.lifetime");
        RepairProgresses = Components.RegisterHot<RepairProgress>("game.repair");
        RegisterCombatMagicStores();
        RegisterNavalStores();
        Stats = new PlayerStats(ruleset, config.PlayerCount);
        Decals = new DecalIndex(ruleset.DecalCap);
        Navigation = new NavigationGrid(Map, ruleset);
        CorpseTtlTicks = (ruleset.CorpseTtlMilliseconds + config.TickLengthMilliseconds - 1) / config.TickLengthMilliseconds;
        InitializeKingdoms();
    }

    /// <summary>The compiled ruleset.</summary>
    public Ruleset Ruleset { get; }

    /// <summary>The match settings.</summary>
    public SimulationConfig Config { get; }

    /// <summary>The tick that runs next (after <c>Simulation.Tick</c> returns, the tick just completed + 1).</summary>
    public Tick Tick { get; internal set; }

    /// <summary>Entity handles.</summary>
    public EntityRegistry Entities { get; } = new();

    /// <summary>Component stores.</summary>
    public ComponentRegistry Components { get; } = new();

    /// <summary>Map layers.</summary>
    public MapState Map { get; }

    /// <summary>The match's RNG streams.</summary>
    public RngStreams Rng { get; internal set; }

    /// <summary>Per-player state, by slot.</summary>
    public Span<PlayerState> Players => _players;

    /// <summary>Events of the current tick (output, not state).</summary>
    public SimEventStream Events { get; } = new();

    /// <summary>Positions (hot).</summary>
    public ComponentStore<Position> Positions { get; }

    /// <summary>Type and owner (hot).</summary>
    public ComponentStore<Identity> Identities { get; }

    /// <summary>Hit points (hot).</summary>
    public ComponentStore<Health> Healths { get; }

    /// <summary>Movement orders (hot).</summary>
    public ComponentStore<Motion> Motions { get; }

    /// <summary>Occupancy bookkeeping (hot).</summary>
    public ComponentStore<OccupancyCell> OccupancyCells { get; }

    /// <summary>Names (cold).</summary>
    public ColdComponentStore<EntityName> Names { get; }

    /// <summary>Structure footprints (hot).</summary>
    public ComponentStore<Footprint> Footprints { get; }

    /// <summary>Attack state of armed entities (hot).</summary>
    public ComponentStore<Combat> Combats { get; }

    /// <summary>Task state of workers (hot).</summary>
    public ComponentStore<Worker> Workers { get; }

    /// <summary>What is left in resource nodes (hot).</summary>
    public ComponentStore<ResourceStock> ResourceStocks { get; }

    /// <summary>Buildings under construction (hot).</summary>
    public ComponentStore<Construction> Constructions { get; }

    /// <summary>Training queues of completed producers (hot).</summary>
    public ComponentStore<Production> Productions { get; }

    /// <summary>Level upgrades bought by entities (hot; GAME-06).</summary>
    public ComponentStore<UpgradeLevels> UpgradeLevels { get; }

    /// <summary>Capture progress of buildings being captured (hot; GAME-04).</summary>
    public ComponentStore<CaptureState> CaptureStates { get; }

    /// <summary>Open/closed state of completed gates (hot; GAME-03).</summary>
    public ComponentStore<GateState> Gates { get; }

    /// <summary>Burning buildings (hot; GAME-03).</summary>
    public ComponentStore<Burning> Burnings { get; }

    /// <summary>Neglect of decaying entities (fields; hot; GAME-02).</summary>
    public ComponentStore<Neglect> Neglects { get; }

    /// <summary>Mana of casters (hot; GAME-01).</summary>
    public ComponentStore<ManaState> Manas { get; }

    /// <summary>Entities that expire (meat; hot).</summary>
    public ComponentStore<Lifetime> Lifetimes { get; }

    /// <summary>Repair bookkeeping of entities being repaired (hot; GAME-03).</summary>
    public ComponentStore<RepairProgress> RepairProgresses { get; }

    /// <summary>Technology effects per player and type (derived from <see cref="PlayerState.Technologies"/>; not state).</summary>
    public PlayerStats Stats { get; }

    /// <summary>Corpses and other footprint-less leftovers (ORIG-01, REQ-PATH-08).</summary>
    public DecalIndex Decals { get; }

    /// <summary>Navigation layers (derived from the map; not state, ADR 0021).</summary>
    public NavigationGrid Navigation { get; }

    /// <summary>Flow-field cache (derived, not state).</summary>
    public FlowFieldCache FlowFields { get; } = new();

    /// <summary>Planned waypoints per unit (state).</summary>
    public PathStore Paths { get; } = new();

    /// <summary>Pending planning requests (state).</summary>
    public PathRequestQueue PathQueue { get; } = new();

    /// <summary>The id the next group move order gets (state).</summary>
    public int NextGroupId { get; internal set; } = 1;

    /// <summary>State of in-sim controllers outside Tzar2.Sim (AI, scripts), in snapshots and the full hash (ADR 0051).</summary>
    public Snapshots.WorldStateExtensions Extensions { get; } = new();

    /// <summary>Corpse lifetime in ticks.</summary>
    public int CorpseTtlTicks { get; }

    /// <summary>
    /// The scenario controller (the legacy trigger engine, ADR 0130), or null. Not state: the controller keeps its state
    /// in <see cref="Extensions"/> and sets this hook again when it attaches to a restored match.
    /// </summary>
    public Systems.IScenarioController? Scenario { get; set; }

    /// <summary>
    /// The entity created for each of <see cref="MapData.Objects"/> at match start, by object index (ADR 0130: scenario
    /// triggers name map objects by index). Filled by the match setup only; empty in a restored world, whose scenario
    /// controller keeps its own copy in its snapshot state. Entries may since have died (check <see cref="Entities"/>).
    /// </summary>
    public ReadOnlySpan<EntityId> MapObjectEntities => _mapObjectEntities;

    private EntityId[] _mapObjectEntities = [];

    /// <summary>Speed of an entity type in world units per tick.</summary>
    public int SpeedPerTick(ushort typeId) => _entityTypeSpeedPerTick[typeId];

    /// <summary>Whether <paramref name="player"/> is a slot in this match.</summary>
    public bool HasPlayer(int player) => (uint)player < (uint)_players.Length;

    /// <summary>Whether <paramref name="owner"/> may own an entity: a player slot or <see cref="MapObject.Nature"/>.</summary>
    public bool IsValidOwner(int owner) => HasPlayer(owner) || owner == MapObject.Nature;

    /// <summary>The type of a live entity.</summary>
    public EntityTypeDefinition TypeOf(EntityId entity) => Ruleset.EntityTypes[Identities.Get(entity).TypeId];

    /// <summary>
    /// The civilization whose technology tree applies to what a live entity does (ADR 0040): its type's civilization
    /// (<c>euro.blacksmith</c> → <c>euro</c>), or, for common types (towers, walls, ships: <c>comm</c>), its owner's.
    /// </summary>
    public string CivilizationOf(EntityId entity)
    {
        Identity identity = Identities.Get(entity);
        string civ = Ruleset.EntityTypes[identity.TypeId].Traits.Civilization;
        return civ.Length > 0 && civ != "comm" ? civ : PlayerCivilization(identity.Owner);
    }

    /// <summary>The civilization key of a player (empty for Nature or a ruleset without civilizations).</summary>
    public string PlayerCivilization(int player)
        => HasPlayer(player) && (uint)_players[player].Civilization < (uint)Ruleset.Civilizations.Count ? Ruleset.Civilizations[_players[player].Civilization] : string.Empty;

    /// <summary>Sets a player's civilization (match setup, before the first tick: the lobby's choice).</summary>
    /// <exception cref="ArgumentException">The civilization is not in the ruleset.</exception>
    public void SetCivilization(int player, string civilization)
    {
        RequireUnlocked();
        int index = -1;
        for (int i = 0; i < Ruleset.Civilizations.Count; i++)
        {
            if (string.Equals(Ruleset.Civilizations[i], civilization, StringComparison.Ordinal))
            {
                index = i;
            }
        }

        if (!HasPlayer(player) || index < 0)
        {
            throw new ArgumentException($"Unknown player {player} or civilization '{civilization}'.", nameof(civilization));
        }

        _players[player].Civilization = index;
    }

    /// <summary>Maximum hit points of <paramref name="type"/> for <paramref name="owner"/> (technology effects, ADR 0040).</summary>
    public int MaxHealthFor(int owner, EntityTypeDefinition type) => Math.Max(1, Stats.Apply(owner, ModifierStat.MaxHealth, type.Id, type.MaxHealth));

    /// <summary>
    /// Whether structural mutations are refused: <c>true</c> between ticks of a <see cref="Simulation"/> (after the first
    /// tick, and after a restore). REQ-ARCH-03: outside a tick only the match setup may change the world; the
    /// presentation never does (ADR 0019).
    /// </summary>
    public bool IsLocked { get; internal set; }

    /// <summary>Adds or replaces a hot component on a live entity.</summary>
    /// <exception cref="InvalidOperationException">The entity is not alive, or the world is locked.</exception>
    public void Set<T>(EntityId entity, T value)
        where T : unmanaged, IComponent
    {
        RequireUnlocked();
        RequireAlive(entity);
        Components.Hot<T>().Set(entity, value);
    }

    /// <summary>Adds or replaces a cold component on a live entity.</summary>
    /// <exception cref="InvalidOperationException">The entity is not alive, or the world is locked.</exception>
    public void SetCold<T>(EntityId entity, T value)
        where T : IColdComponent<T>
    {
        RequireUnlocked();
        RequireAlive(entity);
        Components.Cold<T>().SetValue(entity, value);
    }

    /// <summary>Removes a hot component.</summary>
    /// <exception cref="InvalidOperationException">The world is locked.</exception>
    public bool Remove<T>(EntityId entity)
        where T : unmanaged, IComponent
    {
        RequireUnlocked();
        return Components.Hot<T>().Remove(entity);
    }

    /// <summary>
    /// Creates a unit of <paramref name="typeId"/> for <paramref name="owner"/> at a world point on the map
    /// (minimal M2 "prefab": identity, position, health, motion if mobile, occupancy).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The type, owner or point is invalid.</exception>
    /// <exception cref="InvalidOperationException">The world is locked, or no entity slot is left (commands check
    /// <see cref="EntityRegistry.RemainingCapacity"/> first, so this never happens inside a tick).</exception>
    public EntityId Spawn(ushort typeId, int owner, int x, int y)
    {
        RequireUnlocked();
        if (typeId >= Ruleset.EntityTypes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(typeId));
        }

        if (!IsValidOwner(owner))
        {
            throw new ArgumentOutOfRangeException(nameof(owner));
        }

        if (!Map.ContainsPoint(x, y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Spawn point is off the map.");
        }

        EntityTypeDefinition type = Ruleset.EntityTypes[typeId];
        if (type.HasFootprint)
        {
            return PlaceStructure(typeId, owner, x / MapState.UnitsPerTile, y / MapState.UnitsPerTile);
        }

        EntityId entity = Entities.Create();
        Positions.Set(entity, new Position(x, y));
        Identities.Set(entity, new Identity { TypeId = typeId, Owner = (byte)owner });
        int maxHealth = MaxHealthFor(owner, type);
        Healths.Set(entity, new Health { Current = maxHealth, Max = maxHealth });
        if (type.SpeedUnitsPerSecond > 0)
        {
            Motions.Set(entity, new Motion
            {
                TargetX = x,
                TargetY = y,
                SteerX = x,
                SteerY = y,
                OrderX = x,
                OrderY = y,
                SpeedPerTick = SpeedPerTick(typeId),
                Radius = type.Radius,
                GoalTile = -1,
                Layer = Navigation.LayerOfType(typeId),
                State = MotionState.Idle,
            });
        }

        // Immobile entities without a footprint (fish patches, meat) take no room: they never crowd movers (ORIG-01).
        if (type.IsMobile)
        {
            int cell = Map.CellIndexAt(x, y);
            OccupancyCells.Set(entity, new OccupancyCell { Cell = cell });
            Map.AddOccupant(cell);
        }

        AddTraitComponents(entity, type, underConstruction: false);
        Events.Emit(new SimEvent(SimEventKind.Spawned, (byte)owner, entity, x, y, typeId));
        return entity;
    }

    /// <summary>
    /// Places a structure (a type with a footprint: building, tree, rock) with its footprint's top-left at tile
    /// (<paramref name="tileX"/>, <paramref name="tileY"/>). Its blocked tiles become impassable for every locomotor
    /// except air; navigation updates incrementally (REQ-PATH-01).
    /// </summary>
    /// <param name="typeId">A type with a footprint.</param>
    /// <param name="owner">Player slot or <see cref="MapObject.Nature"/>.</param>
    /// <param name="tileX">Footprint left column.</param>
    /// <param name="tileY">Footprint top row.</param>
    /// <param name="underConstruction">
    /// Place a construction site (GAME-03): it blocks its tiles at once, starts with 1 hit point and grows to full health
    /// as builders add progress; it neither produces nor accepts drop-offs until completed.
    /// </param>
    /// <param name="rotated">Turn the footprint by 90° (gates and bridges built the other way, GAME-03).</param>
    /// <exception cref="ArgumentOutOfRangeException">The type has no footprint, or the footprint leaves the map.</exception>
    public EntityId PlaceStructure(ushort typeId, int owner, int tileX, int tileY, bool underConstruction = false, bool rotated = false)
    {
        RequireUnlocked();
        if (typeId >= Ruleset.EntityTypes.Count || !Ruleset.EntityTypes[typeId].HasFootprint)
        {
            throw new ArgumentOutOfRangeException(nameof(typeId), "Not a structure type.");
        }

        if (!IsValidOwner(owner))
        {
            throw new ArgumentOutOfRangeException(nameof(owner));
        }

        EntityTypeDefinition type = Ruleset.EntityTypes[typeId];
        var footprint = new Footprint { TileX = tileX, TileY = tileY, Rotated = rotated && type.FootprintWidth != type.FootprintHeight ? (byte)1 : (byte)0 };
        int w = footprint.Width(type.FootprintWidth, type.FootprintHeight);
        int h = footprint.Height(type.FootprintWidth, type.FootprintHeight);
        if (!Map.ContainsTile(tileX, tileY) || !Map.ContainsTile(tileX + w - 1, tileY + h - 1))
        {
            throw new ArgumentOutOfRangeException(nameof(tileX), "The footprint leaves the map.");
        }

        EntityId entity = Entities.Create();
        int x = (tileX * MapState.UnitsPerTile) + (w * MapState.UnitsPerTile / 2);
        int y = (tileY * MapState.UnitsPerTile) + (h * MapState.UnitsPerTile / 2);
        Positions.Set(entity, new Position(x, y));
        Identities.Set(entity, new Identity { TypeId = typeId, Owner = (byte)owner });
        bool site = underConstruction && type.Traits.BuildTimeMs > 0;
        int maxHealth = MaxHealthFor(owner, type);
        Healths.Set(entity, new Health { Current = site ? 1 : maxHealth, Max = maxHealth });
        Footprints.Set(entity, footprint);
        AddTraitComponents(entity, type, site);
        ApplyStructure(entity, add: true);
        Events.Emit(new SimEvent(SimEventKind.Spawned, (byte)owner, entity, x, y, typeId));
        return entity;
    }

    /// <summary>The footprint rectangle of a structure in tiles (rotation applied).</summary>
    public (int TileX, int TileY, int Width, int Height) FootprintRect(EntityId structure)
    {
        Footprint f = Footprints.Get(structure);
        EntityTypeDefinition type = TypeOf(structure);
        return (f.TileX, f.TileY, f.Width(type.FootprintWidth, type.FootprintHeight), f.Height(type.FootprintWidth, type.FootprintHeight));
    }

    /// <summary>
    /// Changes the owner of a live entity (GAME-04 capture, cow poaching): its queue is lost, its fight ends, its hit
    /// points follow the new owner's technologies (damage taken stays taken), capture progress on it is dropped.
    /// </summary>
    public void ChangeOwner(EntityId entity, byte newOwner)
    {
        RequireUnlocked();
        RequireAlive(entity);
        if (!HasPlayer(newOwner))
        {
            throw new ArgumentOutOfRangeException(nameof(newOwner));
        }

        ref Identity identity = ref Identities.Get(entity);
        identity.Owner = newOwner;
        EntityTypeDefinition type = Ruleset.EntityTypes[identity.TypeId];
        Productions.Remove(entity);
        if (type.Traits.Producer is not null && !Constructions.Has(entity))
        {
            Productions.Set(entity, default);
        }

        if (Combats.IndexOf(entity) is >= 0 and var c)
        {
            Combats.At(c) = new Combat { HitInMs = -1, ChaseTick = -1, CooldownMs = Combats.At(c).CooldownMs };
        }

        if (Workers.IndexOf(entity) is >= 0 and var w)
        {
            Workers.At(w) = new Worker { CarryResource = Worker.NoResource, NodeResource = Worker.NoResource };
        }

        CaptureStates.Remove(entity);
        RefreshMaxHealth(entity);
        AfterChangeOwnerCombatMagic(entity);
        AfterChangeNaval(entity);
    }

    /// <summary>
    /// Converts a live entity into another type in place (GAME-06 conversion upgrades: wood tower → guard tower). The
    /// footprint size is the same (checked by the ruleset); the blocked tiles, armament, queue and hit points follow the
    /// new type (damage taken stays taken).
    /// </summary>
    public void ConvertType(EntityId entity, ushort newType)
    {
        RequireUnlocked();
        RequireAlive(entity);
        EntityTypeDefinition next = Ruleset.EntityTypes[newType];
        bool structure = Footprints.Has(entity);
        if (structure)
        {
            ApplyStructure(entity, add: false);
        }

        ref Identity identity = ref Identities.Get(entity);
        identity.TypeId = newType;
        if (structure)
        {
            ApplyStructure(entity, add: true);
        }

        if (next.Traits.Armament is null)
        {
            Combats.Remove(entity);
        }
        else if (!Combats.Has(entity) && !Constructions.Has(entity))
        {
            Combats.Set(entity, new Combat { HitInMs = -1, ChaseTick = -1 });
        }

        if (next.Traits.Producer is null)
        {
            Productions.Remove(entity);
            UpgradeLevels.Remove(entity);
        }
        else if (!Productions.Has(entity) && !Constructions.Has(entity) && HasPlayer(identity.Owner))
        {
            Productions.Set(entity, default);
        }

        if (UpgradeLevels.IndexOf(entity) is >= 0 and var levels)
        {
            // Keep only the levels the new type can still hold.
            uint keep = 0;
            for (int u = 0; u < Ruleset.Upgrades.Count; u++)
            {
                keep |= next.Traits.Producer!.CanUpgrade((ushort)u) && !Ruleset.Upgrades[u].IsConversion ? 1u << u : 0;
            }

            UpgradeLevels.At(levels).Mask &= keep;
        }

        if (Motions.IndexOf(entity) is >= 0 and var m)
        {
            ref Motion motion = ref Motions.At(m);
            motion.SpeedPerTick = SpeedPerTick(newType);
            motion.Radius = next.Radius;
            motion.Layer = Navigation.LayerOfType(newType);
        }

        RefreshMaxHealth(entity);
        AfterConvertTypeCombatMagic(entity, next);
        AfterChangeNaval(entity);
    }

    /// <summary>Recomputes a live entity's maximum hit points (owner's technologies); the damage it took stays taken.</summary>
    public void RefreshMaxHealth(EntityId entity)
    {
        ref Health health = ref Healths.Get(entity);
        int owner = Identities.Get(entity).Owner;
        int max = MaxHealthFor(owner, TypeOf(entity)) + LevelHealth(entity, owner, TypeOf(entity));
        if (Constructions.Has(entity))
        {
            // A site grows toward its maximum as it is built.
            health.Max = max;
            health.Current = Math.Clamp(health.Current, 1, max);
            return;
        }

        int damage = health.Max - health.Current;
        health.Max = max;
        health.Current = Math.Clamp(max - damage, 1, max);
    }

    /// <summary>Opens or closes a gate's passage tiles (GAME-03).</summary>
    internal void SetGateOpen(EntityId gate, bool open)
    {
        ref GateState state = ref Gates.Get(gate);
        if ((state.Open != 0) == open)
        {
            return;
        }

        state.Open = open ? (byte)1 : (byte)0;
        state.Desired = state.Open;
        state.LastChangeTick = Tick.Value;

        // Only the passage tiles change (the posts stay blocked): the least navigation work per change.
        Footprint f = Footprints.Get(gate);
        EntityTypeDefinition type = TypeOf(gate);
        int w = f.Width(type.FootprintWidth, type.FootprintHeight);
        int h = f.Height(type.FootprintWidth, type.FootprintHeight);
        for (int dy = 0; dy < h; dy++)
        {
            for (int dx = 0; dx < w; dx++)
            {
                int k = f.MaskIndex(dx, dy, type.FootprintWidth);
                if (type.Footprint[k] == 0 || type.Traits.Gate!.Passage[k] == 0)
                {
                    continue;
                }

                if (open)
                {
                    Map.RemoveBlocker(f.TileX + dx, f.TileY + dy);
                }
                else
                {
                    Map.AddBlocker(f.TileX + dx, f.TileY + dy);
                }
            }
        }
    }

    /// <summary>
    /// Turns a completed construction site into a working building: drops the <see cref="Construction"/> component and
    /// adds the components of its traits that only completed buildings have (production queue).
    /// </summary>
    public void CompleteConstruction(EntityId entity)
    {
        RequireUnlocked();
        RequireAlive(entity);
        EntityTypeDefinition type = TypeOf(entity);
        bool reshape = type.Traits.IsBridge || type.Traits.Gate is not null;
        if (reshape)
        {
            ApplyStructure(entity, add: false); // a bridge deck and a gate passage exist only once completed
        }

        Constructions.Remove(entity);
        if (type.Traits.Gate is not null && !Gates.Has(entity))
        {
            Gates.Set(entity, new GateState { Mode = GateMode.Automatic, LastChangeTick = int.MinValue / 2 });
        }

        if (reshape)
        {
            ApplyStructure(entity, add: true);
        }

        AfterCompleteCombatMagic(entity, type);
        if (type.Traits.Decay is not null && !Neglects.Has(entity))
        {
            Neglects.Set(entity, default);
        }
        if (type.Traits.Producer is not null && !Productions.Has(entity) && HasPlayer(Identities.Get(entity).Owner))
        {
            Productions.Set(entity, default);
        }

        if (type.Traits.Armament is not null && !Combats.Has(entity))
        {
            Combats.Set(entity, new Combat { HitInMs = -1, ChaseTick = -1 });
        }

        if (type.Traits.ResourceNode is { } node && !ResourceStocks.Has(entity))
        {
            ResourceStocks.Set(entity, new ResourceStock { Amount = node.Amount });
        }
    }

    /// <summary>
    /// Places the map's objects and gives every player the starting resources and the population limit (match start;
    /// called by the <see cref="Simulation"/> constructor, never on restore).
    /// </summary>
    /// <exception cref="ArgumentException">An object names an unknown type or owner, or does not fit on the map.</exception>
    internal void LoadMap()
    {
        RequireUnlocked();
        MapData data = Map.Data;
        data.Validate(Ruleset);
        _mapObjectEntities = new EntityId[data.Objects.Length];
        int objectIndex = 0;
        foreach (MapObject o in data.Objects)
        {
            ushort typeId = (ushort)Ruleset.EntityTypeId(o.TypeKey);
            if (!IsValidOwner(o.Owner))
            {
                throw new ArgumentException($"Map object '{o.TypeKey}' has owner {o.Owner}, but the match has {_players.Length} players.", nameof(data));
            }

            EntityTraits traits = Ruleset.EntityTypes[typeId].Traits;
            if (o.Owner == MapObject.Nature && (traits.Harvester is not null || traits.IsBuilder || traits.IsCapturer || (traits.Producer is { Produces.IsEmpty: false } && traits.World.Claim is null) || traits.DropOffMask != 0))
            {
                throw new ArgumentException($"Map object '{o.TypeKey}' works for a kingdom (worker, producer or drop-off) and cannot belong to Nature.", nameof(data));
            }

            EntityId placed = Ruleset.EntityTypes[typeId].HasFootprint
                ? PlaceStructure(typeId, o.Owner, o.X / MapState.UnitsPerTile, o.Y / MapState.UnitsPerTile, rotated: (o.Flags & MapObjectFlags.Rotated) != 0)
                : Spawn(typeId, o.Owner, o.X, o.Y);
            _mapObjectEntities[objectIndex++] = placed;
            if (o.HasExtras)
            {
                ApplyMapObjectExtras(placed, o);
            }
        }

        AssignCivilizations();
        GameplaySettings g = Ruleset.Gameplay;
        int level = Config.StartingResources >= 0 ? Config.StartingResources : g.DefaultStartingResources;
        int limit = Config.PopulationLimit >= 0 ? Config.PopulationLimit : g.PopulationLimit;
        for (int p = 0; p < _players.Length; p++)
        {
            ref PlayerState player = ref _players[p];
            player.PopulationLimit = limit;
            if (g.StartingResources.Length > 0)
            {
                if (level >= g.StartingResources.Length)
                {
                    throw new ArgumentException($"Starting-resource level {level} does not exist in ruleset '{Ruleset.Name}'.", nameof(data));
                }

                for (int r = 0; r < g.StartingResources[level].Amounts.Length; r++)
                {
                    player.Resources[r] = g.StartingResources[level].Amounts[r];
                }
            }
        }
    }

    // Editor placement extras of a map object (ADR 0152): facing, a gate placed open (it then stays open until ordered,
    // manual mode), and a resource amount other than the type's default.
    private void ApplyMapObjectExtras(EntityId entity, MapObject o)
    {
        Identities.Get(entity).Facing = (byte)(o.Facing & 7);
        if ((o.Flags & MapObjectFlags.Open) != 0 && Gates.Has(entity))
        {
            Gates.Get(entity).Mode = GateMode.Manual;
            SetGateOpen(entity, true);
        }

        if (o.Amount > 0 && ResourceStocks.Has(entity))
        {
            ResourceStocks.Get(entity).Amount = o.Amount;
        }
    }

    // A kingdom's civilization at match start: that of its first own object of a civilization (castle, peasants), else
    // the first civilization of the ruleset that is not the common one (ADR 0040; the lobby can set it).
    private void AssignCivilizations()
    {
        int fallback = 0;
        for (int i = 0; i < Ruleset.Civilizations.Count; i++)
        {
            if (Ruleset.Civilizations[i] != "comm")
            {
                fallback = i;
                break;
            }
        }

        var assigned = new bool[_players.Length];
        for (int p = 0; p < _players.Length; p++)
        {
            _players[p].Civilization = fallback;
        }

        ReadOnlySpan<Identity> identities = Identities.Components;
        for (int i = 0; i < identities.Length; i++)
        {
            int owner = identities[i].Owner;
            string civ = Ruleset.EntityTypes[identities[i].TypeId].Traits.Civilization;
            if (!HasPlayer(owner) || assigned[owner] || civ.Length == 0 || civ == "comm")
            {
                continue;
            }

            for (int c = 0; c < Ruleset.Civilizations.Count; c++)
            {
                if (Ruleset.Civilizations[c] == civ)
                {
                    _players[owner].Civilization = c;
                    assigned[owner] = true;
                }
            }
        }
    }

    /// <summary>
    /// Kills an entity: records a corpse decal if its type leaves one (it never blocks movement, ORIG-01), then
    /// destroys it.
    /// </summary>
    /// <returns><c>false</c> if it was not alive.</returns>
    public bool Kill(EntityId entity)
    {
        RequireUnlocked();
        if (!Entities.IsAlive(entity))
        {
            return false;
        }

        int p = Identities.IndexOf(entity);
        int q = Positions.IndexOf(entity);
        if (p >= 0 && q >= 0)
        {
            Identity identity = Identities.At(p);
            EntityTypeDefinition type = Ruleset.EntityTypes[identity.TypeId];
            Position position = Positions.At(q);
            bool ruin = type.HasFootprint && type.Traits.LeavesRuin && !Constructions.Has(entity);
            if (CorpseTtlTicks > 0 && ((type.LeavesCorpse && !type.HasFootprint) || ruin))
            {
                // Corpses and ruins never block (ORIG-01, REQ-PATH-08); they share the decal lifetime.
                Decals.Add(new Decal
                {
                    X = position.X,
                    Y = position.Y,
                    ExpiresTick = Tick.Value + CorpseTtlTicks,
                    TypeId = identity.TypeId,
                    Owner = identity.Owner,
                    Kind = ruin ? DecalKind.Ruin : DecalKind.Corpse,
                });
            }

            if (type.Traits.SpawnOnDeath >= 0 && Entities.RemainingCapacity >= 1)
            {
                // A slaughtered cow leaves meat (GAME-02): a footprint-less node owned by Nature that anyone may harvest.
                Destroy(entity);
                Spawn((ushort)type.Traits.SpawnOnDeath, MapObject.Nature, position.X, position.Y);
                return true;
            }
        }

        return Destroy(entity);
    }

    /// <summary>Destroys an entity: removes all its components now; its slot is recycled in the cleanup step.</summary>
    /// <returns><c>false</c> if it was not alive.</returns>
    /// <exception cref="InvalidOperationException">The world is locked.</exception>
    public bool Destroy(EntityId entity)
    {
        RequireUnlocked();
        if (!Entities.IsAlive(entity))
        {
            return false;
        }

        BeforeDestroyCombatMagic(entity);
        int owner = 0;
        int typeId = 0;
        int position = Identities.IndexOf(entity);
        if (position >= 0)
        {
            owner = Identities.At(position).Owner;
            typeId = Identities.At(position).TypeId;
        }

        int cellPosition = OccupancyCells.IndexOf(entity);
        if (cellPosition >= 0 && OccupancyCells.At(cellPosition).Cell >= 0)
        {
            Map.RemoveOccupant(OccupancyCells.At(cellPosition).Cell);
        }

        if (Footprints.Has(entity))
        {
            ApplyStructure(entity, add: false);
        }

        foreach (IComponentStore store in Components.All)
        {
            store.RemoveEntity(entity);
        }

        Paths.Clear(entity.Index);
        Entities.Destroy(entity);
        Events.Emit(new SimEvent(SimEventKind.Destroyed, (byte)owner, entity, 0, 0, typeId));
        return true;
    }

    /// <summary>
    /// Checks the semantic invariants of a restored world (snapshots from peers are untrusted, ADR 0019) and rebuilds the
    /// occupancy grid from the entities' cells. Structural invariants (sorted stores, live entities, registry lists)
    /// are checked while reading.
    /// </summary>
    /// <exception cref="System.IO.InvalidDataException">An invariant does not hold.</exception>
    internal void ValidateRestoredState()
    {
        foreach (PlayerState player in _players)
        {
            if ((player.Flags & ~(PlayerFlags.Active | PlayerFlags.Defeated | PlayerFlags.Victorious | PlayerFlags.Resigned | PlayerFlags.AiControlled)) != 0
                || (player.Flags & (PlayerFlags.Defeated | PlayerFlags.Victorious)) == (PlayerFlags.Defeated | PlayerFlags.Victorious))
            {
                throw Invalid($"invalid player flags {player.Flags}");
            }

            if (player.Population < 0 || player.PopulationCap < 0 || player.PopulationLimit < 0)
            {
                throw Invalid("invalid population");
            }

            for (int r = 0; r < Ruleset.MaxResourceTypes; r++)
            {
                if (player.Resources[r] < 0 || (r >= Ruleset.ResourceTypes.Count && player.Resources[r] != 0))
                {
                    throw Invalid("invalid stockpile");
                }
            }

            if (player.Reserved != 0 || player.Civilization < 0 || (player.Civilization >= Ruleset.Civilizations.Count && player.Civilization != 0))
            {
                throw Invalid("invalid civilization");
            }

            for (int t = Ruleset.Technologies.Count; t < Ruleset.MaxTechnologies; t++)
            {
                if (player.HasTechnology(t))
                {
                    throw Invalid("a technology the ruleset does not have is researched");
                }
            }
        }

        for (int p = 0; p < _players.Length; p++)
        {
            Stats.Rebuild(p, _players[p]);
        }

        foreach (Position p in Positions.Components)
        {
            if (!Map.ContainsPoint(p.X, p.Y))
            {
                throw Invalid($"position ({p.X}, {p.Y}) is off the map");
            }
        }

        ReadOnlySpan<EntityId> ids = Identities.Entities;
        Span<Identity> identities = Identities.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            Identity identity = identities[i];
            if (identity.TypeId >= Ruleset.EntityTypes.Count || !IsValidOwner(identity.Owner) || identity.Facing >= Core.Numerics.Angle.DirectionCount)
            {
                throw Invalid($"{ids[i]} has an invalid identity");
            }
        }

        foreach (Health h in Healths.Components)
        {
            if (h.Max < 0 || h.Current > h.Max)
            {
                throw Invalid($"invalid health {h.Current}/{h.Max}");
            }
        }

        ReadOnlySpan<EntityId> movers = Motions.Entities;
        Span<Motion> motions = Motions.Components;
        for (int i = 0; i < movers.Length; i++)
        {
            Motion m = motions[i];
            int identity = Identities.IndexOf(movers[i]);
            if (m.State is < MotionState.Idle or > MotionState.Blocked || m.SpeedPerTick < 0 || !Map.ContainsPoint(m.TargetX, m.TargetY)
                || m.Mode is < MotionMode.None or > MotionMode.Route || m.Order is < MotionOrder.None or > MotionOrder.Follow
                || identity < 0 || m.Layer != Navigation.LayerOfType(Identities.At(identity).TypeId)
                || m.Radius is < 1 or > EntityTypeDefinition.MaxRadius
                || (m.Mode == MotionMode.Flow && (m.GoalTile < 0 || m.GoalTile >= Map.Width * Map.Height))
                || ((m.Mode is MotionMode.Route or MotionMode.Path) && (m.PathCursor < 0 || m.PathCursor > Paths.Count(movers[i].Index)))
                || (m.Mode == MotionMode.Path && (Paths.Count(movers[i].Index) < 2 || Paths.Count(movers[i].Index) % 2 != 0))
                || Positions.IndexOf(movers[i]) < 0)
            {
                throw Invalid("invalid motion");
            }

            if (m.Mode == MotionMode.Route && !RouteIsValid(Paths.Get(movers[i].Index), m.Layer, m.PathCursor, m.PlanVersion))
            {
                throw Invalid("invalid route");
            }
        }

        // Queued path requests (copied raw from the snapshot).
        const PathRequestFlags KnownFlags = PathRequestFlags.Prepared | PathRequestFlags.Flow | PathRequestFlags.FlowHead;
        for (int i = 0; i < PathQueue.Count; i++)
        {
            PathRequest r = PathQueue[i];
            bool prepared = (r.Flags & PathRequestFlags.Prepared) != 0;
            bool flow = (r.Flags & (PathRequestFlags.Flow | PathRequestFlags.FlowHead)) != 0;
            if ((r.Flags & ~KnownFlags) != 0 || (flow && !prepared) || r.PrepCost < 0 || r.Group < 0 || r.Group >= NextGroupId
                || (prepared && r.TargetX != int.MinValue && (!Map.ContainsPoint(r.StartX, r.StartY) || !Map.ContainsPoint(r.TargetX, r.TargetY))))
            {
                throw Invalid("invalid path request");
            }

            // A live flow request needs the unit's flow goal.
            int mp = Motions.IndexOf(EntityId.FromValue(r.Entity));
            if (flow && mp >= 0 && Motions.At(mp).RequestSerial == r.Serial
                && (Motions.At(mp).GoalTile < 0 || Motions.At(mp).GoalTile >= Map.Width * Map.Height || Motions.At(mp).Layer == NavigationGrid.NoLayer))
            {
                throw Invalid("flow request without a flow goal");
            }
        }

        ReadOnlySpan<EntityId> occupants = OccupancyCells.Entities;
        Span<OccupancyCell> cells = OccupancyCells.Components;
        for (int i = 0; i < occupants.Length; i++)
        {
            int p = Positions.IndexOf(occupants[i]);
            if (p < 0 || cells[i].Cell != Map.CellIndexAt(Positions.At(p).X, Positions.At(p).Y))
            {
                throw Invalid($"{occupants[i]}'s occupancy cell does not match its position");
            }
        }

        Map.RebuildOccupancy(cells);
        ValidateGameplayComponents();
        ValidateM3Components();
        ValidateKingdomComponents();
        ValidateCombatMagicComponents();
        ValidateNavalComponents();

        static System.IO.InvalidDataException Invalid(string what) => new($"Snapshot violates a world invariant: {what}.");
    }

    // Gameplay stores: enum ranges, timers, and that each entity's type has the matching trait (ADR 0019: snapshots are
    // untrusted). Target handles may be stale (their entity died); systems re-validate them every tick.
    private void ValidateGameplayComponents()
    {
        // The systems read these components of every entity in a gameplay store without checking.
        RequireCore(Combats.Entities, mobile: false, structure: false);
        RequireCore(Workers.Entities, mobile: true, structure: false);
        RequireCore(ResourceStocks.Entities, mobile: false, structure: false); // fish patches and meat have no footprint
        RequireCore(Constructions.Entities, mobile: false, structure: true);
        RequireCore(Productions.Entities, mobile: false, structure: true); // structures, or mobile units buying level upgrades
        RequirePlayerOwner(Workers.Entities);
        RequirePlayerOwner(Constructions.Entities);
        RequirePlayerOwner(Productions.Entities);

        ReadOnlySpan<EntityId> ids = Combats.Entities;
        Span<Combat> combats = Combats.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            Combat c = combats[i];
            ArmamentDefinition? arm = TypeOf(ids[i]).Traits.Armament;
            if (arm is null || c.Mode > CombatMode.Scripted || c.HitInMs < -1 || c.HitInMs > arm.HitDelayMs
                || c.CooldownMs < -Config.TickLengthMilliseconds || c.CooldownMs > arm.CooldownMs
                || c.ChaseTick < -1 || c.ChaseTick > Tick.Value || c.Resume > 1 || c.Reserved0 != 0 || c.Reserved1 != 0)
            {
                throw Invalid("invalid combat state");
            }
        }

        ids = Workers.Entities;
        Span<Worker> workers = Workers.Components;
        int resources = Ruleset.ResourceTypes.Count;
        for (int i = 0; i < ids.Length; i++)
        {
            Worker w = workers[i];
            EntityTraits t = TypeOf(ids[i]).Traits;
            int capacity = t.Harvester?.Capacity ?? 0;
            if ((t.Harvester is null && !t.IsBuilder && !t.IsCapturer) || w.Task > WorkerTask.Capture
                || (w.Task == WorkerTask.Harvest && t.Harvester is null) || (w.Task is WorkerTask.Build or WorkerTask.Repair && !t.IsBuilder)
                || (w.Task == WorkerTask.Capture && !t.IsCapturer)
                || w.Phase != w.Task switch
                {
                    WorkerTask.None => WorkerPhase.None,
                    WorkerTask.Harvest when w.Phase is WorkerPhase.ToNode or WorkerPhase.Gathering or WorkerPhase.ToDropOff => w.Phase,
                    WorkerTask.Build when w.Phase is WorkerPhase.ToSite or WorkerPhase.Building => w.Phase,
                    WorkerTask.Repair when w.Phase is WorkerPhase.ToTarget or WorkerPhase.Repairing => w.Phase,
                    WorkerTask.Capture when w.Phase is WorkerPhase.ToTarget or WorkerPhase.Capturing => w.Phase,
                    _ => (WorkerPhase)0xFF,
                } || w.Phase > WorkerPhase.Capturing || w.CarryAmount < 0
                || w.CarryAmount > Math.Max(capacity, Stats.Apply(Identities.Get(ids[i]).Owner, ModifierStat.CarryCapacity, Identities.Get(ids[i]).TypeId, capacity))
                || w.TimerMs < 0 || w.Retries < 0 || (w.CarryAmount > 0 && w.CarryResource == Worker.NoResource)
                || (w.CarryResource != Worker.NoResource && w.CarryResource >= resources)
                || (w.NodeResource != Worker.NoResource && w.NodeResource >= resources))
            {
                throw Invalid("invalid worker state");
            }
        }

        ids = ResourceStocks.Entities;
        Span<ResourceStock> stocks = ResourceStocks.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            if (TypeOf(ids[i]).Traits.ResourceNode is not { } node || stocks[i].Amount < -1 || (stocks[i].Amount == -1) != (node.Amount == -1)
                || (node.Amount >= 0 && stocks[i].Amount > node.Amount) || stocks[i].RegenMs < 0 || stocks[i].RegenMs > Math.Max(node.RegenMs, 0))
            {
                throw Invalid("invalid resource stock");
            }
        }

        ids = Constructions.Entities;
        Span<Construction> sites = Constructions.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            Construction c = sites[i];
            if (!Footprints.Has(ids[i]) || c.TotalMs != TypeOf(ids[i]).Traits.BuildTimeMs || c.ProgressMs < 0 || c.ProgressMs > c.TotalMs
                || c.CarryBasisPoints is < 0 or >= 10_000 || !ValidPaid(c.Paid))
            {
                throw Invalid("invalid construction site");
            }
        }

        ids = Productions.Entities;
        Span<Production> queues = Productions.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            Production q = queues[i];
            ProducerDefinition? producer = TypeOf(ids[i]).Traits.Producer;
            if (producer is null || Constructions.Has(ids[i]) || q.Count > producer.QueueCapacity || q.ProgressMs < 0 || q.HasRally > 1
                || q.Stall > (byte)ProductionStall.EntityLimit || q.RallyMode > RallyMode.MoveAdvance
                || (q.HasRally == 1 && !Map.ContainsPoint(q.RallyX, q.RallyY)) || (q.HasRally == 0 && (q.RallyTarget != 0 || q.RallyMode != 0))
                || (q.RallyTarget != 0 && q.RallyMode != RallyMode.DefaultAction))
            {
                throw Invalid("invalid production queue");
            }

            for (int k = 0; k < ProducerDefinition.MaxQueueCapacity; k++)
            {
                PaidModifier paid = q.Paid[k];
                bool unitItem = k < q.Count && !QueueItems.IsResearch(q.Queue[k]) && !QueueItems.IsUpgrade(q.Queue[k]);
                if ((k < q.Count ? !Systems.ResearchRules.IsValidQueueItem(this, producer, q.Queue[k]) : q.Queue[k] != 0)
                    || (!unitItem && (paid.Add != 0 || paid.BasisPoints != 0)) || !ValidPaid(paid))
                {
                    throw Invalid("invalid production queue item");
                }
            }
        }

        static System.IO.InvalidDataException Invalid(string what) => new($"Snapshot violates a world invariant: {what}.");
    }

    // M3 stores (ADRs 0040-0044): each entity's type must have the matching trait, values in range, padding zero.
    private void ValidateM3Components()
    {
        RequireCore(UpgradeLevels.Entities, mobile: false, structure: false);
        RequireCore(CaptureStates.Entities, mobile: false, structure: false);
        RequireCore(Gates.Entities, mobile: false, structure: true);
        RequireCore(Burnings.Entities, mobile: false, structure: true);
        RequireCore(Neglects.Entities, mobile: false, structure: true);
        RequireCore(Manas.Entities, mobile: false, structure: false);
        RequireCore(Lifetimes.Entities, mobile: false, structure: false);
        RequireCore(RepairProgresses.Entities, mobile: false, structure: false);
        int upgrades = Ruleset.Upgrades.Count;
        uint knownUpgrades = upgrades >= 32 ? uint.MaxValue : (1u << upgrades) - 1;
        ReadOnlySpan<EntityId> ids = UpgradeLevels.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            uint mask = UpgradeLevels.Components[i].Mask;
            ProducerDefinition? producer = TypeOf(ids[i]).Traits.Producer;
            if ((mask & ~knownUpgrades) != 0 || producer is null)
            {
                throw Invalid("invalid upgrade levels");
            }

            for (int u = 0; u < upgrades; u++)
            {
                if ((mask & (1u << u)) != 0 && (!producer.CanUpgrade((ushort)u) || Ruleset.Upgrades[u].IsConversion))
                {
                    throw Invalid("a level upgrade the type cannot buy");
                }
            }
        }

        ids = CaptureStates.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            CaptureState c = CaptureStates.Components[i];
            if (TypeOf(ids[i]).Traits.Capturable is null || !HasPlayer(c.Player) || c.Player == Identities.Get(ids[i]).Owner || c.MilliPoints < 0
                || c.IdleMs < 0 || c.Reserved0 != 0 || c.Reserved1 != 0 || c.Reserved2 != 0 || Constructions.Has(ids[i]))
            {
                throw Invalid("invalid capture state");
            }
        }

        ids = Gates.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            GateState g = Gates.Components[i];
            if (TypeOf(ids[i]).Traits.Gate is null || Constructions.Has(ids[i]) || g.Open > 1 || g.Mode > GateMode.Automatic || g.Desired > 1 || g.Reserved != 0 || g.LastChangeTick > Tick.Value)
            {
                throw Invalid("invalid gate state");
            }
        }

        ids = Burnings.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            if (!TypeOf(ids[i]).Traits.Burnable || Burnings.Components[i].AccumMs < 0 || Burnings.Components[i].AccumMs >= 60_000)
            {
                throw Invalid("invalid fire state");
            }
        }

        ids = Neglects.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            if (TypeOf(ids[i]).Traits.Decay is null || Neglects.Components[i].UntendedMs < 0 || Neglects.Components[i].AccumMs is < 0 or >= 60_000)
            {
                throw Invalid("invalid neglect state");
            }
        }

        ids = Manas.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            Identity identity = Identities.Get(ids[i]);
            if (TypeOf(ids[i]).Traits.ManaPool is not { } pool || Manas.Components[i].MilliMana < 0
                || Manas.Components[i].MilliMana > (long)Stats.Apply(identity.Owner, ModifierStat.ManaMax, identity.TypeId, pool.Max) * 1000)
            {
                throw Invalid("invalid mana");
            }
        }

        ids = Lifetimes.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            int lifetime = TypeOf(ids[i]).Traits.LifetimeMs;
            long latest = (long)Tick.Value + ((lifetime + Config.TickLengthMilliseconds - 1) / Config.TickLengthMilliseconds);
            if (lifetime <= 0 || Lifetimes.Components[i].ExpiresTick < Tick.Value || Lifetimes.Components[i].ExpiresTick > latest)
            {
                throw Invalid("invalid lifetime");
            }
        }

        ids = RepairProgresses.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            if (!TypeOf(ids[i]).Traits.IsRepairable || RepairProgresses.Components[i].HealedHp < 0 || RepairProgresses.Components[i].Carry < 0)
            {
                throw Invalid("invalid repair state");
            }
        }

        ValidateObstacleLayers();

        foreach (Footprint f in Footprints.Components)
        {
            if (f.Rotated > 1 || f.Reserved0 != 0 || f.Reserved1 != 0 || f.Reserved2 != 0)
            {
                throw Invalid("invalid footprint");
            }
        }

        static System.IO.InvalidDataException Invalid(string what) => new($"Snapshot violates a world invariant: {what}.");
    }

    private static bool ValidPaid(PaidModifier paid) => paid.Add is >= -1_000_000 and <= 1_000_000 && paid.BasisPoints is >= -10_000 and <= int.MaxValue / 2;

    // The blocker and bridge layers follow from the structures (ADR 0043): recompute them from the footprints, rotations,
    // sites and gate states; the snapshot must hold at least the structures' blockers, exactly their decks and exactly
    // their gate passages, so a restored world can never hold a phantom deck, a missing blocker or an open flag on a
    // blocked gate passage (each would fail later, mid-tick).
    private void ValidateObstacleLayers()
    {
        int w = Map.Width;
        var blockers = new int[w * Map.Height];
        var bridges = new int[blockers.Length];
        var passage = new bool[blockers.Length];
        ReadOnlySpan<EntityId> ids = Footprints.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            Footprint f = Footprints.Components[i];
            EntityTypeDefinition type = TypeOf(ids[i]);
            int fw = f.Width(type.FootprintWidth, type.FootprintHeight);
            int fh = f.Height(type.FootprintWidth, type.FootprintHeight);
            if (!Map.ContainsTile(f.TileX, f.TileY) || !Map.ContainsTile(f.TileX + fw - 1, f.TileY + fh - 1))
            {
                throw new System.IO.InvalidDataException("Snapshot violates a world invariant: a footprint leaves the map.");
            }

            bool site = Constructions.Has(ids[i]);
            bool open = !site && Gates.IndexOf(ids[i]) is >= 0 and var g && Gates.At(g).Open != 0;
            for (int dy = 0; dy < fh; dy++)
            {
                for (int dx = 0; dx < fw; dx++)
                {
                    int tile = ((f.TileY + dy) * w) + f.TileX + dx;
                    int k = f.MaskIndex(dx, dy, type.FootprintWidth);
                    if (type.Traits.IsBridge)
                    {
                        bridges[tile] += site ? 0 : 1;
                    }
                    else if (type.Traits.Gate is { } gate && gate.Passage[k] != 0)
                    {
                        passage[tile] = true;
                    }

                    if (!type.Traits.IsBridge && type.Footprint[k] != 0 && !(open && type.Traits.Gate!.Passage[k] != 0))
                    {
                        blockers[tile]++;
                    }
                }
            }
        }

        ReadOnlySpan<ushort> actualBlockers = Map.Blockers;
        ReadOnlySpan<byte> actualBridges = Map.Bridges;
        for (int t = 0; t < blockers.Length; t++)
        {
            // Structures' blockers must be there (plain map obstacles may add more); decks and gate passages exactly.
            if (actualBlockers[t] < blockers[t] || bridges[t] != actualBridges[t] || (passage[t] && blockers[t] != actualBlockers[t]))
            {
                throw new System.IO.InvalidDataException($"Snapshot violates a world invariant: the obstacle layers at tile ({t % w}, {t / w}) do not match the structures.");
            }
        }
    }

    // Workers, sites and queues spend or earn a player's stockpile: their owner must be a player slot (never Nature).
    private void RequirePlayerOwner(ReadOnlySpan<EntityId> ids)
    {
        foreach (EntityId e in ids)
        {
            if (!HasPlayer(Identities.Get(e).Owner))
            {
                throw new System.IO.InvalidDataException($"Snapshot violates a world invariant: {e} has worker, site or queue state but no player owner.");
            }
        }
    }

    private void RequireCore(ReadOnlySpan<EntityId> ids, bool mobile, bool structure)
    {
        foreach (EntityId e in ids)
        {
            if (!Identities.Has(e) || !Positions.Has(e) || !Healths.Has(e) || (mobile && !Motions.Has(e) && !IsHeld(e)) || (structure && !Footprints.Has(e) && !Motions.Has(e)))
            {
                throw new System.IO.InvalidDataException($"Snapshot violates a world invariant: {e} lacks a core component of its gameplay state.");
            }
        }
    }

    // Adds the components a type's traits call for (combat, worker, resource stock, construction or production).
    // Only player-owned entities work, produce or build: they spend and earn a player's stockpile (Nature has none).
    private void AddTraitComponents(EntityId entity, EntityTypeDefinition type, bool underConstruction)
    {
        EntityTraits t = type.Traits;
        byte owner = Identities.Get(entity).Owner;
        bool player = HasPlayer(owner);
        AddCombatMagicComponents(entity, type, underConstruction);
        if (player && (t.Harvester is not null || t.IsBuilder || t.IsCapturer))
        {
            Workers.Set(entity, new Worker { CarryResource = Worker.NoResource, NodeResource = Worker.NoResource });
        }

        if (t.LifetimeMs > 0)
        {
            Lifetimes.Set(entity, new Lifetime { ExpiresTick = Tick.Value + ((t.LifetimeMs + Config.TickLengthMilliseconds - 1) / Config.TickLengthMilliseconds) });
        }

        if (underConstruction)
        {
            Constructions.Set(entity, new Construction { TotalMs = t.BuildTimeMs });
            return; // a site neither fights nor produces until completed
        }

        if (t.ManaPool is { } mana)
        {
            Manas.Set(entity, new ManaState { MilliMana = (int)Math.Min((long)Stats.Apply(owner, ModifierStat.ManaMax, type.Id, mana.Max) * 1000, int.MaxValue) });
        }

        if (t.Gate is not null)
        {
            Gates.Set(entity, new GateState { Mode = GateMode.Automatic, LastChangeTick = int.MinValue / 2 });
        }

        if (t.Decay is not null)
        {
            Neglects.Set(entity, default);
        }

        if (t.Armament is not null)
        {
            Combats.Set(entity, new Combat { HitInMs = -1, ChaseTick = -1 });
        }

        if (t.ResourceNode is { } node)
        {
            ResourceStocks.Set(entity, new ResourceStock { Amount = node.Amount });
        }

        if (player && t.Producer is not null)
        {
            Productions.Set(entity, default);
        }
    }

    // Route nodes must name existing portals of the unit's layer (navigation is rebuilt from the restored map).
    // A route's node ids must name existing portals of the unit's layer (navigation is rebuilt from the restored map);
    // a plan made for the current passability version must match the layer exactly (it is used without re-checking).
    private bool RouteIsValid(ReadOnlySpan<int> plan, byte layerIndex, int cursor, int planVersion)
    {
        Navigation.EnsureUpToDate();
        if (layerIndex >= Navigation.LayerCount || plan.IsEmpty || (plan.Length & 1) != 0 || cursor > plan.Length / 2)
        {
            return false;
        }

        NavLayer layer = Navigation.Layers[layerIndex];
        if (planVersion == Navigation.Version)
        {
            return PathSearch.RouteValid(layer, plan, 0);
        }

        foreach (int node in plan[..(plan.Length / 2)])
        {
            if ((uint)node >= (uint)layer.NodeCapacity)
            {
                return false;
            }
        }

        return true;
    }

    private void RequireUnlocked()
    {
        if (IsLocked)
        {
            throw new InvalidOperationException("The world is locked between ticks; change it through commands (REQ-ARCH-03).");
        }
    }

    // Adds or removes what a structure puts on the map: blockers on its mask tiles (an open gate's passage excepted), or a
    // completed bridge's deck on every tile (a bridge site puts nothing, ADR 0043). Rotation applies.
    private void ApplyStructure(EntityId entity, bool add)
    {
        Footprint f = Footprints.Get(entity);
        EntityTypeDefinition type = TypeOf(entity);
        int w = f.Width(type.FootprintWidth, type.FootprintHeight);
        int h = f.Height(type.FootprintWidth, type.FootprintHeight);
        bool site = Constructions.Has(entity);
        if (type.Traits.IsBridge)
        {
            if (site)
            {
                return;
            }

            for (int dy = 0; dy < h; dy++)
            {
                for (int dx = 0; dx < w; dx++)
                {
                    if (add)
                    {
                        Map.AddBridge(f.TileX + dx, f.TileY + dy);
                    }
                    else
                    {
                        Map.RemoveBridge(f.TileX + dx, f.TileY + dy);
                    }
                }
            }

            return;
        }

        ReadOnlySpan<byte> mask = type.Footprint;
        bool open = !site && Gates.IndexOf(entity) is >= 0 and var g && Gates.At(g).Open != 0;
        for (int dy = 0; dy < h; dy++)
        {
            for (int dx = 0; dx < w; dx++)
            {
                int k = f.MaskIndex(dx, dy, type.FootprintWidth);
                if (mask[k] == 0 || (open && type.Traits.Gate!.Passage[k] != 0))
                {
                    continue;
                }

                if (add)
                {
                    Map.AddBlocker(f.TileX + dx, f.TileY + dy);
                }
                else
                {
                    Map.RemoveBlocker(f.TileX + dx, f.TileY + dy);
                }
            }
        }
    }

    private void RequireAlive(EntityId entity)
    {
        if (!Entities.IsAlive(entity))
        {
            throw new InvalidOperationException($"{entity} is not alive.");
        }
    }
}
