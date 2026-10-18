using System;
using System.Threading;
using Tzar2.Core.Ids;

namespace Tzar2.Game.Session;

/// <summary>What a unit is doing, for the presentation's choice of animation (walk, attack, chop...).</summary>
public enum UnitActivity : byte
{
    /// <summary>Standing.</summary>
    Idle = 0,

    /// <summary>Walking.</summary>
    Walk = 1,

    /// <summary>Swinging at a target.</summary>
    Attack = 2,

    /// <summary>Gathering wood.</summary>
    Chop = 3,

    /// <summary>Gathering gold, stone or iron.</summary>
    Mine = 4,

    /// <summary>Gathering food.</summary>
    Farm = 5,

    /// <summary>Building or repairing.</summary>
    Build = 6,
}

/// <summary>Match outcome for the local player.</summary>
public enum MatchOutcome : byte
{
    /// <summary>The match goes on.</summary>
    None = 0,

    /// <summary>Every other kingdom is defeated.</summary>
    Victory = 1,

    /// <summary>The local kingdom is defeated.</summary>
    Defeat = 2,
}

/// <summary>
/// One entity of a <see cref="GameFrame"/>: the render-snapshot fields (REQ-ARCH-03) plus what the HUD and the animation
/// choice need (activity, carried load, construction and production progress). Built on the sim thread.
/// </summary>
public struct FrameEntity
{
#pragma warning disable CA1051 // Plain data copied in bulk between threads.
    /// <summary>The entity.</summary>
    public EntityId Id;

    /// <summary>X in world units.</summary>
    public int X;

    /// <summary>Y in world units.</summary>
    public int Y;

    /// <summary>Entity type id.</summary>
    public ushort TypeId;

    /// <summary>Owner slot (0xFF = nature).</summary>
    public byte Owner;

    /// <summary>8-direction facing (0 = east, clockwise).</summary>
    public byte Facing;

    /// <summary>Hit points.</summary>
    public int Health;

    /// <summary>Maximum hit points.</summary>
    public int MaxHealth;

    /// <summary>Animation choice.</summary>
    public UnitActivity Activity;

    /// <summary>Milliseconds since the current swing started (attack animation phase), or -1.</summary>
    public int SwingMs;

    /// <summary>Carried resource type id, or 0xFF.</summary>
    public byte CarryResource;

    /// <summary>Carried amount.</summary>
    public int CarryAmount;

    /// <summary>Construction progress 0-1000, or -1 when the entity is not a construction site.</summary>
    public int ConstructionPermille;

    /// <summary>Queued units of a producer (0 when none).</summary>
    public int QueueCount;

    /// <summary>The type in training (first queue item), valid when <see cref="QueueCount"/> &gt; 0.</summary>
    public ushort QueueHead;

    /// <summary>Training progress of the first item 0-1000.</summary>
    public int QueuePermille;

    /// <summary>Footprint top-left tile X of a structure, or -1.</summary>
    public int TileX;

    /// <summary>Footprint top-left tile Y of a structure, or -1.</summary>
    public int TileY;

    /// <summary>Rally point of a producer, valid when <see cref="HasRally"/>.</summary>
    public int RallyX;

    /// <summary>Rally point Y.</summary>
    public int RallyY;

    /// <summary>Whether a rally point is set.</summary>
    public bool HasRally;

    /// <summary>Units left in a resource node, -1 for unlimited or not a node.</summary>
    public int ResourceLeft;

    /// <summary>Attack target of an armed entity, or none.</summary>
    public EntityId Target;
#pragma warning restore CA1051
}

/// <summary>A corpse on the ground (decal).</summary>
/// <param name="X">X in world units.</param>
/// <param name="Y">Y in world units.</param>
/// <param name="TypeId">Type of the dead unit.</param>
/// <param name="Owner">Its owner.</param>
/// <param name="AgeMs">Time since death.</param>
public readonly record struct FrameCorpse(int X, int Y, ushort TypeId, byte Owner, int AgeMs);

/// <summary>A kingdom's stockpile and population.</summary>
public struct FramePlayer
{
#pragma warning disable CA1051
    /// <summary>Food, wood, stone, gold, iron... by resource type id (at most 8).</summary>
    public ResourceRow Resources;

    /// <summary>Population used.</summary>
    public int Population;

    /// <summary>Population room.</summary>
    public int PopulationCap;

    /// <summary>Whether the kingdom is defeated.</summary>
    public bool Defeated;
#pragma warning restore CA1051
}

/// <summary>Eight resource amounts.</summary>
[System.Runtime.CompilerServices.InlineArray(8)]
public struct ResourceRow
{
    private int _amount;
}

/// <summary>
/// The presentation's view of the match after one tick (TDD §7.3 render snapshot, extended by the session on the sim
/// thread, ADR 0070): entities, corpses, stockpiles, the local player's fog layer and the buildable-tile layer for
/// placement ghosts. Instances are recycled by <see cref="FrameExchange"/>; a reader may use one until it acquires the next.
/// </summary>
public sealed class GameFrame
{
    private FrameEntity[] _entities = new FrameEntity[256];
    private FrameCorpse[] _corpses = new FrameCorpse[64];

    /// <summary>The tick the frame shows the result of.</summary>
    public Tick Tick { get; internal set; }

    /// <summary>Game time per tick.</summary>
    public int TickMs { get; internal set; }

    /// <summary>Entities, ascending by id.</summary>
    public ReadOnlySpan<FrameEntity> Entities => _entities.AsSpan(0, EntityCount);

    /// <summary>Number of entities.</summary>
    public int EntityCount { get; internal set; }

    /// <summary>Corpses.</summary>
    public ReadOnlySpan<FrameCorpse> Corpses => _corpses.AsSpan(0, CorpseCount);

    /// <summary>Number of corpses.</summary>
    public int CorpseCount { get; internal set; }

    /// <summary>Per-slot stockpiles.</summary>
    public FramePlayer[] Players { get; internal set; } = [];

    /// <summary>
    /// Local player's visibility per tile (row-major W×H): 0 unexplored, 1 explored, 2 visible. Empty when fog is off.
    /// </summary>
    public byte[] Visibility { get; internal set; } = [];

    /// <summary>1 where a footprint tile may be placed (walkable land without a structure), per tile.</summary>
    public byte[] Buildable { get; internal set; } = [];

    /// <summary>The map passability version <see cref="Buildable"/> was computed for.</summary>
    public int BuildableVersion { get; internal set; } = -1;

    /// <summary>Local outcome.</summary>
    public MatchOutcome Outcome { get; internal set; }

    /// <summary>Game speed in percent of the classic tick rate.</summary>
    public int SpeedPercent { get; internal set; }

    /// <summary>Whether the session is paused.</summary>
    public bool Paused { get; internal set; }

    internal FrameEntity[] EntityBuffer(int count)
    {
        if (_entities.Length < count)
        {
            _entities = new FrameEntity[Math.Max(count, _entities.Length * 2)];
        }

        return _entities;
    }

    internal FrameCorpse[] CorpseBuffer(int count)
    {
        if (_corpses.Length < count)
        {
            _corpses = new FrameCorpse[Math.Max(count, _corpses.Length * 2)];
        }

        return _corpses;
    }
}

/// <summary>
/// Lock-free triple buffer of <see cref="GameFrame"/>s (TDD §7.3), the same scheme as the sim's render-snapshot
/// exchange: the sim thread fills the back buffer and publishes without waiting; the main thread gets the newest frame.
/// </summary>
public sealed class FrameExchange
{
    private const int IndexMask = 3;
    private const int FreshFlag = 4;
    private readonly GameFrame[] _buffers = [new(), new(), new()];
    private int _back;
    private int _front = 1;
    private int _shared = 2;
    private bool _hasFront;

    /// <summary>The buffer to fill next (writer only).</summary>
    public GameFrame Back => _buffers[_back];

    /// <summary>Publishes <see cref="Back"/> (writer only).</summary>
    public void Publish() => _back = Interlocked.Exchange(ref _shared, _back | FreshFlag) & IndexMask;

    /// <summary>The newest published frame, or null before the first one (reader only).</summary>
    public GameFrame? AcquireLatest() => AcquireLatest(out _);

    /// <summary>The newest published frame; <paramref name="fresh"/> tells whether it is new since the last call.</summary>
    public GameFrame? AcquireLatest(out bool fresh)
    {
        fresh = false;
        if ((Volatile.Read(ref _shared) & FreshFlag) != 0)
        {
            _front = Interlocked.Exchange(ref _shared, _front) & IndexMask;
            _hasFront = true;
            fresh = true;
        }

        return _hasFront ? _buffers[_front] : null;
    }
}
