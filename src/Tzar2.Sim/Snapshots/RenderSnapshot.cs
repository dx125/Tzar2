using System;
using System.Runtime.InteropServices;
using System.Threading;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;

namespace Tzar2.Sim.Snapshots;

/// <summary>What the presentation needs of one entity per tick (REQ-ARCH-03).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct RenderEntity
{
    internal RenderEntity(EntityId id, int x, int y, ushort typeId, byte owner, byte facing, int health, int maxHealth, bool moving = false)
    {
        Id = id;
        X = x;
        Y = y;
        TypeId = typeId;
        Owner = owner;
        Facing = facing;
        Health = health;
        MaxHealth = maxHealth;
        IsMoving = moving;
    }

    /// <summary>The entity.</summary>
    public EntityId Id { get; }

    /// <summary>X in world units.</summary>
    public int X { get; }

    /// <summary>Y in world units.</summary>
    public int Y { get; }

    /// <summary>Entity type id.</summary>
    public ushort TypeId { get; }

    /// <summary>Owner slot.</summary>
    public byte Owner { get; }

    /// <summary>8-direction facing.</summary>
    public byte Facing { get; }

    /// <summary>Hit points (0 if the entity has none).</summary>
    public int Health { get; }

    /// <summary>Maximum hit points (0 if the entity has none).</summary>
    public int MaxHealth { get; }

    /// <summary>Whether the unit is walking this tick (selects the walk vs. idle animation).</summary>
    public bool IsMoving { get; }
}

/// <summary>
/// An immutable view of the sim after one tick, for the presentation layer (REQ-ARCH-03): the visible entities in
/// ascending id order and the tick's events. Instances are recycled by <see cref="RenderSnapshotExchange"/>; a reader
/// may use one until it acquires the next.
/// </summary>
public sealed class RenderSnapshot
{
    private RenderEntity[] _entities = new RenderEntity[256];
    private SimEvent[] _events = new SimEvent[64];
    private byte[] _visibility = [];
    private int _entityCount;
    private int _eventCount;

    /// <summary>The tick this snapshot shows the result of.</summary>
    public Tick Tick { get; private set; }

    /// <summary>The per-tick state hash after that tick.</summary>
    public ulong StateHash { get; private set; }

    /// <summary>Entities with an identity and a position, ascending by id.</summary>
    public ReadOnlySpan<RenderEntity> Entities => _entities.AsSpan(0, _entityCount);

    /// <summary>The tick's events in emission order.</summary>
    public ReadOnlySpan<SimEvent> Events => _events.AsSpan(0, _eventCount);

    /// <summary>The kingdom whose view this is (GAME-12), or -1 when everything is shown.</summary>
    public int Viewer { get; private set; } = -1;

    /// <summary>
    /// The viewer's fog layer: one <c>TileVisibility</c> byte per tile, row-major (0 unexplored, 1 explored, 2 in sight); empty
    /// without a viewer. The sim computes it (ADR 0101): the presentation derives no fog of its own.
    /// </summary>
    public ReadOnlySpan<byte> Visibility => _visibility;

    /// <summary>Minute of the 24-hour day clock (GAME-16).</summary>
    public int DayMinute { get; private set; }

    /// <summary>Whether it is night (GAME-16).</summary>
    public bool IsNight { get; private set; }

    /// <summary>The weather (GAME-16).</summary>
    public WeatherKind Weather { get; private set; }

    internal void Fill(World world, Tick tick, ulong stateHash, int viewer)
    {
        Tick = tick;
        StateHash = stateHash;
        Viewer = world.HasPlayer(viewer) ? viewer : -1;
        DayMinute = world.DayMinute;
        IsNight = world.IsNight;
        Weather = world.Weather;
        int tiles = world.Map.Width * world.Map.Height;
        if (Viewer < 0)
        {
            _visibility = [];
        }
        else
        {
            if (_visibility.Length != tiles)
            {
                _visibility = new byte[tiles];
            }

            world.Visibility.WriteStates(Viewer, _visibility);
        }

        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        Span<Identity> identities = world.Identities.Components;
        if (_entities.Length < ids.Length)
        {
            _entities = new RenderEntity[Math.Max(ids.Length, _entities.Length * 2)];
        }

        int n = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            int p = world.Positions.IndexOf(ids[i]);
            if (p < 0)
            {
                continue;
            }

            if (Viewer >= 0 && !world.Visibility.CanSee(world, Viewer, ids[i]))
            {
                continue; // out of the viewer's sight (GAME-12)
            }

            ref Position position = ref world.Positions.At(p);
            int h = world.Healths.IndexOf(ids[i]);
            Health health = h >= 0 ? world.Healths.At(h) : default;
            Identity identity = identities[i];
            int m = world.Motions.IndexOf(ids[i]);
            bool moving = m >= 0 && (world.Motions.At(m).VelocityX != 0 || world.Motions.At(m).VelocityY != 0);
            _entities[n++] = new RenderEntity(ids[i], position.X, position.Y, identity.TypeId, identity.Owner, identity.Facing, health.Current, health.Max, moving);
        }

        _entityCount = n;
        ReadOnlySpan<SimEvent> events = world.Events.Events;
        if (_events.Length < events.Length)
        {
            _events = new SimEvent[Math.Max(events.Length, _events.Length * 2)];
        }

        events.CopyTo(_events);
        _eventCount = events.Length;
    }
}

/// <summary>
/// Lock-free triple buffer between the sim thread (writer) and the main thread (reader), TDD §7.3: the sim publishes
/// after every tick without waiting; the reader always gets the newest complete snapshot, never a half-written one.
/// </summary>
public sealed class RenderSnapshotExchange
{
    private const int IndexMask = 3;
    private const int FreshFlag = 4;

    private readonly RenderSnapshot[] _buffers = [new(), new(), new()];
    private int _back;
    private int _front = 1;
    private int _shared = 2;
    private bool _hasFront;

    /// <summary>The kingdom whose view is published (GAME-12: its fog layer, only what it sees), or -1 for everything.</summary>
    public int Viewer { get; set; } = -1;

    /// <summary>Fills the back buffer from <paramref name="world"/> and publishes it (sim thread only).</summary>
    internal void Publish(World world, Tick tick, ulong stateHash)
    {
        _buffers[_back].Fill(world, tick, stateHash, Viewer);
        _back = Interlocked.Exchange(ref _shared, _back | FreshFlag) & IndexMask;
    }

    /// <summary>
    /// The newest published snapshot (main thread only), or <c>null</c> before the first publish. The returned instance
    /// stays unchanged until the next call.
    /// </summary>
    public RenderSnapshot? AcquireLatest()
    {
        if ((Volatile.Read(ref _shared) & FreshFlag) != 0)
        {
            _front = Interlocked.Exchange(ref _shared, _front) & IndexMask;
            _hasFront = true;
        }

        return _hasFront ? _buffers[_front] : null;
    }
}
