using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Rng;
using Tzar2.Sim.Components;
using Tzar2.Sim.Ecs;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;

namespace Tzar2.Sim;

/// <summary>
/// The complete simulation state of one match (TDD §8): entities, components, map layers, players, RNG streams and
/// the tick counter. Everything here is in snapshots (REQ-SIM-13) except <see cref="Events"/>, which is output.
/// </summary>
public sealed class World
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

    /// <summary>Speed of an entity type in world units per tick.</summary>
    public int SpeedPerTick(ushort typeId) => _entityTypeSpeedPerTick[typeId];

    /// <summary>Whether <paramref name="player"/> is a slot in this match.</summary>
    public bool HasPlayer(int player) => (uint)player < (uint)_players.Length;

    /// <summary>Adds or replaces a hot component on a live entity.</summary>
    /// <exception cref="InvalidOperationException">The entity is not alive.</exception>
    public void Set<T>(EntityId entity, T value)
        where T : unmanaged, IComponent
    {
        RequireAlive(entity);
        Components.Hot<T>().Set(entity, value);
    }

    /// <summary>Adds or replaces a cold component on a live entity.</summary>
    /// <exception cref="InvalidOperationException">The entity is not alive.</exception>
    public void SetCold<T>(EntityId entity, T value)
        where T : IColdComponent<T>
    {
        RequireAlive(entity);
        Components.Cold<T>().SetValue(entity, value);
    }

    /// <summary>Removes a hot component.</summary>
    public bool Remove<T>(EntityId entity)
        where T : unmanaged, IComponent
        => Components.Hot<T>().Remove(entity);

    /// <summary>
    /// Creates a unit of <paramref name="typeId"/> for <paramref name="owner"/> at a world point on the map
    /// (minimal M2 "prefab": identity, position, health, motion if mobile, occupancy).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The type, owner or point is invalid.</exception>
    public EntityId Spawn(ushort typeId, int owner, int x, int y)
    {
        if (typeId >= Ruleset.EntityTypes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(typeId));
        }

        if (!HasPlayer(owner))
        {
            throw new ArgumentOutOfRangeException(nameof(owner));
        }

        if (!Map.ContainsPoint(x, y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Spawn point is off the map.");
        }

        EntityTypeDefinition type = Ruleset.EntityTypes[typeId];
        EntityId entity = Entities.Create();
        Positions.Set(entity, new Position(x, y));
        Identities.Set(entity, new Identity { TypeId = typeId, Owner = (byte)owner });
        Healths.Set(entity, new Health { Current = type.MaxHealth, Max = type.MaxHealth });
        if (type.SpeedUnitsPerSecond > 0)
        {
            Motions.Set(entity, new Motion { TargetX = x, TargetY = y, SpeedPerTick = SpeedPerTick(typeId), State = MotionState.Idle });
        }

        int cell = Map.CellIndexAt(x, y);
        OccupancyCells.Set(entity, new OccupancyCell { Cell = cell });
        Map.AddOccupant(cell);
        Events.Emit(new SimEvent(SimEventKind.Spawned, (byte)owner, entity, x, y, typeId));
        return entity;
    }

    /// <summary>Destroys an entity: removes all its components now; its slot is recycled in the cleanup step.</summary>
    /// <returns><c>false</c> if it was not alive.</returns>
    public bool Destroy(EntityId entity)
    {
        if (!Entities.IsAlive(entity))
        {
            return false;
        }

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

        foreach (IComponentStore store in Components.All)
        {
            store.RemoveEntity(entity);
        }

        Entities.Destroy(entity);
        Events.Emit(new SimEvent(SimEventKind.Destroyed, (byte)owner, entity, 0, 0, typeId));
        return true;
    }

    private void RequireAlive(EntityId entity)
    {
        if (!Entities.IsAlive(entity))
        {
            throw new InvalidOperationException($"{entity} is not alive.");
        }
    }
}
