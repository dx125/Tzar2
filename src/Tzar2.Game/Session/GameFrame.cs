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

    /// <summary>Digging at an enemy building to capture it (PRES-005).</summary>
    Capture = 8,

    /// <summary>Cutting meat from a carcass (PRES-005).</summary>
    Meat = 9,

    /// <summary>Casting a spell (PRES-005).</summary>
    Cast = 10,

    /// <summary>A ninja's murder (PRES-005, MAG-028).</summary>
    Murder = 11,
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

    /// <summary>Mana of a caster (whole points), or 0.</summary>
    public int Mana;

    /// <summary>The caster's mana pool, or 0 for entities that cast nothing.</summary>
    public int MaxMana;

    /// <summary>A building on fire (GAME-03).</summary>
    public bool Burning;

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

    /// <summary>Items a producer has queued in all (0 when none; per item: <see cref="GameFrame.QueueEntries"/>).</summary>
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

    /// <summary>Progress 0-1000 of the item a magic workshop is making, or -1 (GAME-10).</summary>
    public int CraftPermille;

    /// <summary>An item's image (IMAGES.INI name) when it wears one of its type's looks (ITEM-016), else null.</summary>
    public string? ItemLook;

    /// <summary>Units inside this holder of the frame's kingdom (castle, outpost, ship, carpet; GAME-09), else 0.</summary>
    public int Inside;

    /// <summary>How many units this holder of the frame's kingdom takes, 0 when it is none or another kingdom's.</summary>
    public int InsideMax;

    /// <summary>Experience level (GAME-08), 0 for what gains none.</summary>
    public int Level;

    /// <summary>A named hero (GAME-08).</summary>
    public bool Hero;

    /// <summary>It has a name of its own (a promoted hero, or one the scenario named): the kingdom's list of heroes shows it (HUD-033).</summary>
    public bool Named;

    /// <summary>Carries a spell book: yellow stars float above it (1.09, ITEM-012).</summary>
    public bool CarriesBook;

    /// <summary>The spells it learned from books (ITEM-019): bit n = spell n.</summary>
    public ulong LearnedSpells;

    /// <summary>What a worker is doing, which names a peasant (STRINGS.INI [peons], HUD-032).</summary>
    public FrameJob Job;

    /// <summary>Invisible (a ninja's cloak or the invisibility spell; GAME-11), as its owner and detectors see it.</summary>
    public bool Cloaked;

    /// <summary>
    /// For its own kingdom, the spy type a taken-over unit turns back into (its name in the selection view), or -1. Other
    /// kingdoms see the unit as its old kingdom's (<see cref="Owner"/>) and never learn the truth (MAG-012, ADR 0184).
    /// </summary>
    public int SpyType;

    /// <summary>For its own kingdom, the kingdom whose colours a taken-over unit wears (batch J: even to its owner), or -1.</summary>
    public int SeemsOwner;

    /// <summary>For its own kingdom, a spy's disguise meter 0-100 (MAG-021): 100 when it wears no disguise; -1 for others.</summary>
    public int DisguiseMastery;

    /// <summary>Upgrades the entity has or has queued (bit = upgrade id; towers, walls, gates, galleons).</summary>
    public uint Upgrades;

    /// <summary>Combat stance of an armed entity (0 aggressive, 1 stand ground, 2 hold fire; GAME-07).</summary>
    public byte Stance;

    /// <summary>A gate's state: <see cref="FrameGate"/> flags (0 = not a gate).</summary>
    public FrameGate Gate;

    /// <summary>A structure turned by 90° (a vertical bridge or gate; CTRL-024, BLD-019).</summary>
    public bool Rotated;

    /// <summary>A gate facing the other way along its axis (the original's facings 1 and 3; CTRL-024).</summary>
    public bool Mirrored;

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

    /// <summary>An item's power on the ground (a potion's health, a weapon's attack; the selection view's POWER, HUD-016), or 0.</summary>
    public int ItemPower;

    /// <summary>The node or site a worker works (a fishing boat's fish patch, HUD-025), or none; may be stale.</summary>
    public EntityId WorkTarget;

    /// <summary>A structure out of sight, drawn as the local kingdom last saw it (GAME-12); its state may be stale.</summary>
    public bool Remembered;
#pragma warning restore CA1051
}

/// <summary>A tavern offer of the card's kingdom (GAME-14, ADR 0108): what <c>Hire</c> with its slot would bring now.</summary>
/// <param name="Slot">The offer slot (the <c>Hire</c> argument).</param>
/// <param name="TypeId">The unit type.</param>
/// <param name="Count">Units in it.</param>
/// <param name="Level">Their experience level.</param>
/// <param name="IsHero">A hero.</param>
/// <param name="Taken">Already hired this period.</param>
public readonly record struct FrameTavernOffer(int Slot, ushort TypeId, int Count, int Level, bool IsHero, bool Taken);

/// <summary>A worker's job (STRINGS.INI <c>[peons]</c>; HUD-032).</summary>
public enum FrameJob : byte
{
    /// <summary>No job: the unit's own name.</summary>
    None = 0,

    /// <summary>Cutting wood (<c>chop</c>, Woodcutter).</summary>
    Chop,

    /// <summary>Farming a field (<c>dig</c>, Farmer).</summary>
    Dig,

    /// <summary>Mining gold, stone or iron (<c>mine</c>, Miner).</summary>
    Mine,

    /// <summary>Building (<c>build</c>, Builder).</summary>
    Build,

    /// <summary>Repairing (<c>repair</c>, Repairman).</summary>
    Repair,

    /// <summary>Milking cows (<c>tend cows</c>, Cowherd).</summary>
    TendCows,

    /// <summary>Exploring (<c>explore</c>, Explorer).</summary>
    Explore,

    /// <summary>Capturing a building (<c>capture</c>, Invader).</summary>
    Capture,

    /// <summary>Fighting (<c>attack</c>, Fighter).</summary>
    Attack,

    /// <summary>Gathering meat or items (<c>Gather Resource Items</c>, Collector).</summary>
    Collect,

    /// <summary>On the way into a holder (<c>Use transport</c>, Passenger).</summary>
    Transport,
}

/// <summary>The names of the jobs.</summary>
public static class FrameJobs
{
    /// <summary>The STRINGS.INI <c>[peons]</c> key naming a peasant at <paramref name="job"/> (HUD-032), or null.</summary>
    public static string? StringKey(FrameJob job) => job switch
    {
        FrameJob.Chop => "chop",
        FrameJob.Dig => "dig",
        FrameJob.Mine => "mine",
        FrameJob.Build => "build",
        FrameJob.Repair => "repair",
        FrameJob.TendCows => "tend cows",
        FrameJob.Explore => "explore",
        FrameJob.Capture => "capture",
        FrameJob.Attack => "attack",
        FrameJob.Collect => "Gather Resource Items",
        FrameJob.Transport => "Use transport",
        _ => null,
    };
}

/// <summary>What an order of <see cref="FrameWaypoint"/> does.</summary>
public enum FrameOrderKind : byte
{
    /// <summary>Walk to the point.</summary>
    Move = 0,

    /// <summary>Attack the target.</summary>
    Attack = 1,

    /// <summary>Guard the target.</summary>
    Guard = 2,

    /// <summary>A point of a patrol.</summary>
    Patrol = 3,
}

/// <summary>
/// An order of one of the frame's kingdom's units, the one it carries out now first and then those queued with Shift
/// (CTRL-025: the waypoints show while Shift is held).
/// </summary>
/// <param name="Unit">The unit (<c>EntityId.Value</c>).</param>
/// <param name="Kind">What the order does.</param>
/// <param name="X">The point, or where the target stood (world units).</param>
/// <param name="Y">The point's Y.</param>
/// <param name="Target">The attacked or guarded entity (<c>EntityId.Value</c>), or 0.</param>
public readonly record struct FrameWaypoint(uint Unit, FrameOrderKind Kind, int X, int Y, uint Target);

/// <summary>An entry of a producer's queue (ADR 0180): how many of one item it has queued, in queue order.</summary>
/// <param name="Entity">The producer (<c>EntityId.Value</c>).</param>
/// <param name="Kind">What the item is.</param>
/// <param name="Id">The unit type, technology or upgrade id.</param>
/// <param name="Count">How many are queued.</param>
public readonly record struct FrameQueueEntry(uint Entity, FrameQueueKind Kind, ushort Id, int Count);

/// <summary>
/// A unit inside a holder of the frame's kingdom (GAME-09; the selection view's list, ORD-015), or an item one of its
/// units carries (GAME-10; the kingdom's list of items, HUD-033).
/// </summary>
/// <param name="Holder">The holder or the carrier (<c>EntityId.Value</c>).</param>
/// <param name="Unit">The unit or the item.</param>
/// <param name="TypeId">Its type.</param>
/// <param name="Health">Its hit points.</param>
/// <param name="MaxHealth">Its most hit points.</param>
/// <param name="IsItem">An item in a unit's inventory.</param>
/// <param name="Slot">An item's inventory slot, or −1.</param>
/// <param name="IsDefault">Whether the item is its carrier's default item (Backspace).</param>
/// <param name="Spell">A book's spell id (its own or its type's), or −1.</param>
/// <param name="Look">An item's own image (ITEM-016), or null.</param>
/// <param name="Value">An item's worth on a market (TECH-030), or 0.</param>
/// <param name="Hero">A unit promoted to hero or with a name of its own: the kingdom's list of heroes shows it (HUD-033).</param>
/// <param name="Level">A unit's experience level, or 0.</param>
public readonly record struct FrameHeld(uint Holder, EntityId Unit, ushort TypeId, int Health, int MaxHealth, bool IsItem = false, int Slot = -1, bool IsDefault = false, int Spell = -1, string? Look = null, int Value = 0, bool Hero = false, int Level = 0);

/// <summary>A projectile in flight (GAME-07; ADR 0091), as far as the viewer sees it.</summary>
/// <param name="Id">Its entity (<c>EntityId.Value</c>): the presentation follows one projectile across frames.</param>
/// <param name="X">X in world units.</param>
/// <param name="Y">Y in world units.</param>
/// <param name="TargetX">Where it flies (world units).</param>
/// <param name="TargetY">Where it flies.</param>
/// <param name="Kind">The projectile kind (<c>Ruleset.Projectiles</c>).</param>
/// <param name="Spell">The spell it carries, or 0xFFFF.</param>
/// <param name="ShooterType">The shooter's type (how the shot looks, PRES-002), or <see cref="ushort.MaxValue"/>.</param>
public readonly record struct FrameProjectile(uint Id, int X, int Y, int TargetX, int TargetY, byte Kind, ushort Spell, ushort ShooterType = ushort.MaxValue);

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

    /// <summary>AI assistance the kingdom has asked for: bit 1 economy, bit 2 war (ADR 0183).</summary>
    public byte AiAssist;

    /// <summary>What the tavern lends now (TRADE-008): the limit, a raise whose time has come included.</summary>
    public int LoanLimit;

    /// <summary>The loan to return, or 0.</summary>
    public int LoanDue;

    /// <summary>The resource of the loan taken (a resource type id), or -1 for none.</summary>
    public int LoanResource;

    /// <summary>The kingdom's civilization key (the diplomacy screen's race icon), or empty.</summary>
    public string? Civilization;

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
    internal static KingdomStatistics From(in Tzar2.Sim.Kingdoms.KingdomState k) => new(
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
    private FrameProjectile[] _projectiles = new FrameProjectile[64];

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

    /// <summary>Projectiles in flight the viewer sees.</summary>
    public ReadOnlySpan<FrameProjectile> Projectiles => _projectiles.AsSpan(0, ProjectileCount);

    /// <summary>Number of valid entries in <see cref="Projectiles"/>.</summary>
    public int ProjectileCount { get; internal set; }

    /// <summary>Number of corpses.</summary>
    public int CorpseCount { get; internal set; }

    /// <summary>Per-slot stockpiles.</summary>
    public FramePlayer[] Players { get; internal set; } = [];

    /// <summary>Stances between the kingdoms, <c>[from × Players.Length + to]</c> (GAME-13; the diplomacy screen).</summary>
    public byte[] Stances { get; internal set; } = [];

    /// <summary>What each kingdom offers each other one (<c>Sharing</c> flags), indexed as <see cref="Stances"/>.</summary>
    public byte[] Offers { get; internal set; } = [];

    /// <summary>The stance of <paramref name="from"/> towards <paramref name="to"/> (enemy when unknown).</summary>
    public Tzar2.Sim.Kingdoms.Stance StanceOf(int from, int to)
        => (uint)from < (uint)Players.Length && (uint)to < (uint)Players.Length && (from * Players.Length) + to < Stances.Length
            ? (Tzar2.Sim.Kingdoms.Stance)Stances[(from * Players.Length) + to]
            : Tzar2.Sim.Kingdoms.Stance.Enemy;

    /// <summary>What <paramref name="from"/> offers <paramref name="to"/>.</summary>
    public Tzar2.Sim.Kingdoms.Sharing OffersOf(int from, int to)
        => (uint)from < (uint)Players.Length && (uint)to < (uint)Players.Length && (from * Players.Length) + to < Offers.Length
            ? (Tzar2.Sim.Kingdoms.Sharing)Offers[(from * Players.Length) + to]
            : Tzar2.Sim.Kingdoms.Sharing.None;

    /// <summary>
    /// Local player's visibility per tile (row-major W×H): 0 unexplored, 1 explored, 2 visible. Empty when fog is off.
    /// </summary>
    public byte[] Visibility { get; internal set; } = [];

    /// <summary>1 where a footprint tile may be placed (walkable land without a structure), per tile.</summary>
    public byte[] Buildable { get; internal set; } = [];

    /// <summary>
    /// Per tile, what coast and bridge placements need (<see cref="Input.Placement.Water"/>, <see cref="Input.Placement.Land"/>,
    /// <see cref="Input.Placement.Free"/> bits), kept with <see cref="Buildable"/>.
    /// </summary>
    public byte[] Terrain { get; internal set; } = [];

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

    /// <summary>The tavern offers of the card's kingdom now (empty without one, or before Mercenaries / Heroes' Quest offers exist).</summary>
    public ReadOnlySpan<FrameTavernOffer> TavernOffers => _offers.AsSpan(0, TavernOfferCount);

    /// <summary>Number of valid entries in <see cref="TavernOffers"/>.</summary>
    public int TavernOfferCount { get; internal set; }

    /// <summary>The price of offer i, resource r at [i × <see cref="TavernCostStride"/> + r].</summary>
    public int[] TavernCosts { get; internal set; } = [];

    /// <summary>Resources per offer in <see cref="TavernCosts"/>.</summary>
    public int TavernCostStride { get; internal set; }

    private FrameTavernOffer[] _offers = [];
    private FrameWaypoint[] _waypoints = new FrameWaypoint[16];
    private FrameQueueEntry[] _queue = new FrameQueueEntry[16];

    /// <summary>The orders of the frame's kingdom's units, grouped by unit in the order they are carried out (CTRL-025).</summary>
    public ReadOnlySpan<FrameWaypoint> Waypoints => _waypoints.AsSpan(0, WaypointCount);

    /// <summary>Number of valid entries in <see cref="Waypoints"/>.</summary>
    public int WaypointCount { get; internal set; }

    internal void SetWaypoint(int index, FrameWaypoint entry)
    {
        if (_waypoints.Length <= index)
        {
            Array.Resize(ref _waypoints, Math.Max(index + 1, _waypoints.Length * 2));
        }

        _waypoints[index] = entry;
    }
    private FrameHeld[] _held = new FrameHeld[16];

    /// <summary>The units inside the frame's kingdom's holders and the items its units carry, grouped by holder in the order they are kept.</summary>
    public ReadOnlySpan<FrameHeld> Helds => _held.AsSpan(0, HeldCount);

    /// <summary>Number of valid entries in <see cref="Helds"/>.</summary>
    public int HeldCount { get; internal set; }

    /// <summary>The queue entries of every producer, grouped by producer in entity order (first entry = in progress).</summary>
    public ReadOnlySpan<FrameQueueEntry> QueueEntries => _queue.AsSpan(0, QueueEntryCount);

    /// <summary>Number of valid entries in <see cref="QueueEntries"/>.</summary>
    public int QueueEntryCount { get; internal set; }

    internal void SetQueueEntry(int index, FrameQueueEntry entry)
    {
        if (_queue.Length <= index)
        {
            Array.Resize(ref _queue, Math.Max(index + 1, _queue.Length * 2));
        }

        _queue[index] = entry;
    }

    internal void SetHeld(int index, FrameHeld entry)
    {
        if (_held.Length <= index)
        {
            Array.Resize(ref _held, Math.Max(index + 1, _held.Length * 2));
        }

        _held[index] = entry;
    }

    // Sorts the held units by holder, keeping each holder's own order (insertion sort: a handful of entries).
    internal void GroupHelds()
    {
        for (int i = 1; i < HeldCount; i++)
        {
            FrameHeld entry = _held[i];
            int j = i - 1;
            while (j >= 0 && _held[j].Holder > entry.Holder)
            {
                _held[j + 1] = _held[j];
                j--;
            }

            _held[j + 1] = entry;
        }
    }

    internal FrameTavernOffer[] OfferBuffer(int count)
    {
        if (_offers.Length < count)
        {
            _offers = new FrameTavernOffer[count];
        }

        return _offers;
    }

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

    internal FrameProjectile[] ProjectileBuffer(int count)
    {
        if (_projectiles.Length < count)
        {
            _projectiles = new FrameProjectile[Math.Max(count, _projectiles.Length * 2)];
        }

        return _projectiles;
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
