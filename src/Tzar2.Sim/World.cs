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
    /// <exception cref="InvalidOperationException">The world is locked.</exception>
    public bool Destroy(EntityId entity)
    {
        RequireUnlocked();
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
            if ((player.Flags & ~(PlayerFlags.Active | PlayerFlags.Defeated)) != 0)
            {
                throw Invalid($"invalid player flags {player.Flags}");
            }
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
            if (identity.TypeId >= Ruleset.EntityTypes.Count || !HasPlayer(identity.Owner) || identity.Facing >= Core.Numerics.Angle.DirectionCount)
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

        foreach (Motion m in Motions.Components)
        {
            if (m.State is < MotionState.Idle or > MotionState.Blocked || m.SpeedPerTick < 0 || !Map.ContainsPoint(m.TargetX, m.TargetY))
            {
                throw Invalid("invalid motion");
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

        static System.IO.InvalidDataException Invalid(string what) => new($"Snapshot violates a world invariant: {what}.");
    }

    private void RequireUnlocked()
    {
        if (IsLocked)
        {
            throw new InvalidOperationException("The world is locked between ticks; change it through commands (REQ-ARCH-03).");
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
