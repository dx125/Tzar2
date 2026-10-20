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

    /// <summary>Milking a cow.</summary>
    Milk = 7,
}

/// <summary>What a worker carries, for its carrying sprite (the original's <c>_tree</c>, <c>_gold</c>, <c>_milk</c>... images).</summary>
public enum FrameCarry : byte
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Logs.</summary>
    Wood,

    /// <summary>Gold.</summary>
    Gold,

    /// <summary>Stone (and iron).</summary>
    Stone,

    /// <summary>Food from a field.</summary>
    Food,

    /// <summary>A pail of milk from a cow.</summary>
    Milk,

    /// <summary>Meat from a butchered animal.</summary>
    Meat,
}

/// <summary>The kind of a producer's queue item.</summary>
public enum FrameQueueKind : byte
{
    /// <summary>A unit in training.</summary>
    Unit = 0,

    /// <summary>A technology being researched.</summary>
    Research = 1,

    /// <summary>An upgrade of this entity.</summary>
    Upgrade = 2,
}

/// <summary>A gate's state in a frame.</summary>
[Flags]
public enum FrameGate : byte
{
    /// <summary>Not a gate.</summary>
    None = 0,

    /// <summary>A gate.</summary>
    Present = 1,

    /// <summary>It is open.</summary>
    Open = 2,

    /// <summary>It opens and closes by itself for its owner's units.</summary>
    Automatic = 4,
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

    /// <summary>What the load looks like (with <see cref="CarryAmount"/> &gt; 0).</summary>
    public FrameCarry Carry;

    /// <summary>Construction progress 0-1000, or -1 when the entity is not a construction site.</summary>
    public int ConstructionPermille;

    /// <summary>Queued units of a producer (0 when none).</summary>
    public int QueueCount;

    /// <summary>
    /// The first queue item's id, valid when <see cref="QueueCount"/> &gt; 0: a unit type, technology or upgrade id by
    /// <see cref="QueueKind"/>.
    /// </summary>
    public ushort QueueHead;

    /// <summary>What <see cref="QueueHead"/> is.</summary>
    public FrameQueueKind QueueKind;

    /// <summary>Progress of the first item 0-1000 (training, research or upgrade).</summary>
    public int QueuePermille;

    /// <summary>Upgrades the entity has or has queued (bit = upgrade id; towers, walls, gates, galleons).</summary>
    public uint Upgrades;

    /// <summary>Combat stance of an armed entity (0 aggressive, 1 stand ground, 2 hold fire; GAME-07).</summary>
    public byte Stance;

    /// <summary>A gate's state: <see cref="FrameGate"/> flags (0 = not a gate).</summary>
    public FrameGate Gate;

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

    /// <summary>A structure out of sight, drawn as the local kingdom last saw it (GAME-12); its state may be stale.</summary>
    public bool Remembered;
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

    /// <summary>Whether the kingdom won (GAME-17).</summary>
    public bool Victorious;

    /// <summary>Score (F4; GAME-17, UNK-29 placeholder formula).</summary>
    public long Score;

    /// <summary>Match statistics (F4 and the statistics file; GAME-17).</summary>
    public KingdomStatistics Statistics;

    /// <summary>Researched technologies (bit = technology id, GAME-06).</summary>
    public TechnologyRow Technologies;

    /// <summary>Technologies queued for research in one of the kingdom's buildings.</summary>
    public TechnologyRow Researching;
#pragma warning restore CA1051

    /// <summary>Whether technology <paramref name="technology"/> is researched.</summary>
    public readonly bool HasTechnology(int technology) => Has(Technologies, technology);

    /// <summary>Whether technology <paramref name="technology"/> is queued for research.</summary>
    public readonly bool IsResearching(int technology) => Has(Researching, technology);

    private static bool Has(in TechnologyRow row, int technology)
        => (uint)technology < TechnologyRow.Capacity && (row[technology >> 6] & (1UL << (technology & 63))) != 0;
}

/// <summary>Researched-technology bits of a kingdom (the sim's <c>TechnologyBits</c>, copied for the presentation).</summary>
[System.Runtime.CompilerServices.InlineArray(Capacity / 64)]
public struct TechnologyRow
{
    /// <summary>Technologies the row holds (<c>Ruleset.MaxTechnologies</c>).</summary>
    public const int Capacity = Tzar2.Content.Compiled.Ruleset.MaxTechnologies;

    private ulong _bits;
}

/// <summary>
/// A kingdom's match statistics for the presentation (F4, results screen; GAME-17): the counters of the sim's kingdom
/// state, copied so the client does not depend on sim state types (§7.2, ADR 0073).
/// </summary>
/// <param name="UnitsTrained">Units trained.</param>
/// <param name="UnitsKilled">Enemy units killed.</param>
/// <param name="UnitsLost">Own units lost.</param>
/// <param name="BuildingsBuilt">Buildings completed.</param>
/// <param name="BuildingsLost">Own buildings lost.</param>
/// <param name="BuildingsDestroyed">Enemy buildings destroyed.</param>
/// <param name="BuildingsCaptured">Buildings captured.</param>
/// <param name="TechnologiesResearched">Technologies researched.</param>
/// <param name="GatheredTotal">Resources gathered in total.</param>
/// <param name="TradeIncome">Trade income.</param>
/// <param name="TributeSent">Tribute sent.</param>
/// <param name="TributeReceived">Tribute received.</param>
public readonly record struct KingdomStatistics(
    int UnitsTrained,
    int UnitsKilled,
    int UnitsLost,
    int BuildingsBuilt,
    int BuildingsLost,
    int BuildingsDestroyed,
    int BuildingsCaptured,
    int TechnologiesResearched,
    long GatheredTotal,
    long TradeIncome,
    long TributeSent,
    long TributeReceived)
{
    /// <summary>The statistics of a sim kingdom state.</summary>
    public static KingdomStatistics From(in Tzar2.Sim.Kingdoms.KingdomState k) => new(
        k.UnitsTrained, k.UnitsKilled, k.UnitsLost, k.BuildingsBuilt, k.BuildingsLost, k.BuildingsDestroyed, k.BuildingsCaptured,
        k.TechnologiesResearched, k.GatheredTotal, k.TradeIncome, k.TributeSent, k.TributeReceived);
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

    /// <summary>The fields version (<c>World.WalkableFootprintsVersion</c>) <see cref="Buildable"/> was computed for.</summary>
    public int BuildableFieldsVersion { get; internal set; } = -1;

    /// <summary>
    /// For the local player, per entity type id: 1 where its requirements are met (buildings and
    /// technologies by the kingdom's tech tree, ADR 0040), the same check the sim's Build and Train commands make; empty
    /// without a player (replays of all kingdoms). The command card greys out what the player cannot make yet and says why.
    /// </summary>
    public byte[] Makeable { get; internal set; } = [];

    /// <summary>
    /// For the local player, per technology id: 1 where its prerequisites are met for the kingdom's civilization (the
    /// sim's research check before cost and queue room); empty without a player.
    /// </summary>
    public byte[] Researchable { get; internal set; } = [];

    /// <summary>Local outcome.</summary>
    public MatchOutcome Outcome { get; internal set; }

    /// <summary>Game speed in percent of the classic tick rate.</summary>
    public int SpeedPercent { get; internal set; }

    /// <summary>Whether the session is paused.</summary>
    public bool Paused { get; internal set; }

    /// <summary>Minute of the sim's 24-hour day clock (GAME-16; noon when day/night is off).</summary>
    public int DayMinute { get; internal set; } = 720;

    /// <summary>Whether it is night (GAME-16).</summary>
    public bool IsNight { get; internal set; }

    /// <summary>The sim's weather (GAME-16).</summary>
    public Tzar2.Content.Compiled.WeatherKind Weather { get; internal set; }

    /// <summary>The quest log and the conversation waiting for a choice (immutable), or null without a scenario.</summary>
    public Scenario.ScenarioView? Scenario { get; internal set; }

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
