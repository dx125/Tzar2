using System;
using System.Collections.Immutable;

namespace Tzar2.Content.Compiled;

/// <summary>What kind of thing an entity type is (selection, targeting and placement rules differ).</summary>
public enum EntityCategory : byte
{
    /// <summary>Scenery, effects and test objects without gameplay role.</summary>
    Other = 0,

    /// <summary>A unit (peasant, soldier, ship...).</summary>
    Unit = 1,

    /// <summary>A building (castle, house, barracks, field...).</summary>
    Building = 2,

    /// <summary>A resource object (tree, gold or stone rock); never a combat target.</summary>
    Resource = 3,
}

/// <summary>Damage classes of the original (<c>[damage]</c> sections of the object INIs: 00 sword, 01 arrow, 02 cannon).</summary>
public enum DamageClass : byte
{
    /// <summary>Hand-to-hand (00).</summary>
    Sword = 0,

    /// <summary>Arrows and bolts (01).</summary>
    Arrow = 1,

    /// <summary>Siege and cannon (02).</summary>
    Cannon = 2,
}

/// <summary>Trait <c>Armament</c> (REQ-SIM-09): how an entity attacks.</summary>
/// <param name="Attack">Damage per hit before defence and bonuses (TSV <c>ATTACK</c>).</param>
/// <param name="RangeUnits">Reach in world units measured from the attacker's edge to the target's edge; 0 = melee (contact).</param>
/// <param name="CooldownMs">Time between two hits.</param>
/// <param name="HitDelayMs">Time from the start of a swing to the hit (the hit frame, UNK-07); at most <paramref name="CooldownMs"/>.</param>
/// <param name="DamageClass">Damage class (holders split damage by class).</param>
/// <param name="AutoAcquire">Whether an idle entity picks targets in sight by itself (workers do not).</param>
public sealed record ArmamentDefinition(int Attack, int RangeUnits, int CooldownMs, int HitDelayMs, DamageClass DamageClass, bool AutoAcquire)
{
    /// <summary>Least reach (edge to edge, world units): closer targets cannot be hit (OBJECTS <c>min_attack_range</c>; ADR 0091).</summary>
    public int MinRangeUnits { get; init; }

    /// <summary>Most damage of a hit before defence (OBJECTS <c>max_damage</c>, towers; ⚠ HYPOTHESIS: a hit rolls in [attack, max]); 0 = fixed.</summary>
    public int MaxAttack { get; init; }

    /// <summary>The projectile of a ranged attack (<see cref="Ruleset.Projectiles"/>), or -1 (the hit lands at the hit frame).</summary>
    public int Projectile { get; init; } = -1;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MinRangeUnits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MinRangeUnits, Math.Max(RangeUnits, 0));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxAttack);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxAttack, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(Projectile, -1);
        ArgumentOutOfRangeException.ThrowIfNegative(Attack);
        ArgumentOutOfRangeException.ThrowIfNegative(RangeUnits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RangeUnits, 64 * 2048);
        ArgumentOutOfRangeException.ThrowIfLessThan(CooldownMs, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(HitDelayMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(HitDelayMs, CooldownMs);
        if (DamageClass > DamageClass.Cannon)
        {
            throw new ArgumentOutOfRangeException(nameof(DamageClass));
        }
    }
}

/// <summary>Trait <c>Harvester</c> (GAME-02): gathers resources from nodes and carries them to a drop-off.</summary>
/// <param name="Capacity">Units carried per trip (UNK-17).</param>
/// <param name="GatherMs">Time to gather one unit, per resource type id; 0 = cannot gather that type.</param>
public sealed record HarvesterDefinition(int Capacity, ImmutableArray<int> GatherMs)
{
    /// <summary>Whether the harvester can gather resource type <paramref name="resource"/>.</summary>
    public bool CanGather(int resource) => (uint)resource < (uint)GatherMs.Length && GatherMs[resource] > 0;

    internal void Validate(int resourceTypes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Capacity, 1);
        if (GatherMs.IsDefault || GatherMs.Length != resourceTypes)
        {
            throw new ArgumentException($"Harvester needs one gather time per resource type ({resourceTypes}).");
        }

        foreach (int ms in GatherMs)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(ms);
        }
    }
}

/// <summary>Trait <c>Producer</c> (GAME-05): trains units in a queue.</summary>
/// <param name="Produces">Type ids this building trains, ascending.</param>
/// <param name="QueueCapacity">Most different items queued at once; any number of each (ADR 0180).</param>
public sealed record ProducerDefinition(ImmutableArray<ushort> Produces, int QueueCapacity)
{
    /// <summary>Most queue entries (different items) the sim stores per building.</summary>
    public const int MaxQueueCapacity = 24;

    /// <summary>Technology ids researched here, ascending (GAME-06: the building's command card, ADR 0040).</summary>
    public ImmutableArray<ushort> Researches { get; init; } = [];

    /// <summary>Upgrade ids the entity can buy for itself, ascending (GAME-06).</summary>
    public ImmutableArray<ushort> Upgrades { get; init; } = [];

    /// <summary>Whether the building trains <paramref name="typeId"/>.</summary>
    public bool CanProduce(ushort typeId) => Produces.BinarySearch(typeId) >= 0;

    /// <summary>Whether technology <paramref name="technology"/> is researched here.</summary>
    public bool CanResearch(ushort technology) => !Researches.IsDefault && Researches.BinarySearch(technology) >= 0;

    /// <summary>Whether the entity can buy upgrade <paramref name="upgrade"/>.</summary>
    public bool CanUpgrade(ushort upgrade) => !Upgrades.IsDefault && Upgrades.BinarySearch(upgrade) >= 0;
}

/// <summary>
/// Trait <c>ResourceNode</c> (GAME-01/02): a tree, rock, field, fish patch, cow (milk) or meat pile holding a resource.
/// Nodes with a footprint are reached at their footprint; footprint-less nodes (fish, meat, cows) at their position.
/// </summary>
/// <param name="Resource">Resource type id.</param>
/// <param name="Amount">Units it holds; <see cref="Unlimited"/> for fields (the manual: "fields produce unlimited amounts of food").</param>
public sealed record ResourceNodeDefinition(int Resource, int Amount)
{
    /// <summary><see cref="Amount"/> of a node that never depletes.</summary>
    public const int Unlimited = -1;

    /// <summary>Time to regrow one unit up to <see cref="Amount"/> (cows' milk); 0 = never regrows.</summary>
    public int RegenMs { get; init; }

    /// <summary>Gather time per unit from this node, overriding the harvester's time for the resource; 0 = the harvester's.</summary>
    public int GatherMs { get; init; }

    /// <summary>
    /// Units a harvester carries per trip from this node, overriding the harvester's capacity (before technology
    /// effects); 0 = the harvester's. Cows: 50 (ADR 0170).
    /// </summary>
    public int CarryLoad { get; init; }

    /// <summary>Whether the node stays when empty (cows wait for their milk to regrow) instead of being removed.</summary>
    public bool KeepWhenEmpty { get; init; }

    /// <summary>How many workers may work the node at once; 0 = any number. Fields: 1 (ECO-021).</summary>
    public int MaxWorkers { get; init; }
}

/// <summary>Which workers may deliver to a drop-off (trait <c>DropOff.access</c>; harbors take fish from boats).</summary>
[Flags]
public enum DropOffAccess : byte
{
    /// <summary>Nobody.</summary>
    None = 0,

    /// <summary>Land workers (peasants).</summary>
    Land = 1,

    /// <summary>Ships (fishing boats).</summary>
    Water = 2,
}

/// <summary>Where a building may be placed (trait <c>Building.placement</c>; GAME-02/03).</summary>
public enum PlacementKind : byte
{
    /// <summary>Every blocked tile on walkable land (default).</summary>
    Land = 0,

    /// <summary>On land with water next to the footprint (harbors): ships dock at the water side.</summary>
    Coast = 1,

    /// <summary>Across water (bridges): the end tiles on land, the deck over water (ADR 0043).</summary>
    Bridge = 2,
}

/// <summary>Trait <c>Capturable</c> (GAME-04): peasants can take the entity over (OBJECTS <c>type = building, capture</c>).</summary>
/// <param name="Instant">Taken over at once when a capturer reaches it (cows under Cow Poaching) instead of by digging.</param>
/// <param name="RequiredTechnology">Technology the capturing player needs (-1 = none).</param>
public sealed record CapturableDefinition(bool Instant, int RequiredTechnology);

/// <summary>
/// Trait <c>InfluenceEmitter</c> (GAME-04, buildings TSV <c>EMIT (own/other)</c>; ⚠ HYPOTHESIS UNK-19, ADR 0044): the
/// building adds <see cref="Own"/> to the capture resistance of its owner's buildings nearby and <see cref="Other"/>
/// (usually negative) to other kingdoms' buildings nearby, fading with distance.
/// </summary>
/// <param name="Own">Influence on its owner's buildings.</param>
/// <param name="Other">Influence on other owners' buildings.</param>
public sealed record InfluenceDefinition(int Own, int Other);

/// <summary>Trait <c>ManaPool</c> (GAME-01: mana per caster; OBJECTS <c>max_manna</c>).</summary>
/// <param name="Max">Most mana.</param>
/// <param name="RegenPerMinute">Mana regrown per minute of game time (UNK-26).</param>
public sealed record ManaPoolDefinition(int Max, int RegenPerMinute);

/// <summary>Trait <c>Gate</c> (GAME-03): the footprint tiles that open (row-major, same size as the footprint).</summary>
/// <param name="Passage">1 = the tile is the gate's passage (blocked while closed), 0 = a post.</param>
public sealed record GateDefinition(ImmutableArray<byte> Passage)
{
    /// <summary>The passage of the gate turned (row-major over <see cref="EntityTypeDefinition.Turned"/>).</summary>
    public ImmutableArray<byte> TurnedPassage { get; init; } = [];

    /// <summary>The passage as placed, turned or not.</summary>
    public ReadOnlySpan<byte> PassageOf(bool turned) => turned && !TurnedPassage.IsDefaultOrEmpty ? TurnedPassage.AsSpan() : Passage.AsSpan();
}

/// <summary>Trait <c>Decay</c> (GAME-02: neglected fields decay and disappear, manual ch. 5; UNK-17).</summary>
/// <param name="AfterMs">Untended time before the decay starts.</param>
/// <param name="HealthPerMinute">Hit points lost per minute while neglected; at 0 the entity disappears.</param>
public sealed record DecayDefinition(int AfterMs, int HealthPerMinute);

/// <summary>
/// The gameplay traits of a compiled entity type (REQ-SIM-09, TDD §11.2). Every array is immutable; ids refer to the
/// owning <see cref="Ruleset"/> (resource type ids, entity type ids). <see cref="None"/> = a type without gameplay
/// (scenery, the M2 movement test types).
/// </summary>
public sealed partial record EntityTraits
{
    /// <summary>No traits.</summary>
    public static EntityTraits None { get; } = new();

    /// <summary>What the type is.</summary>
    public EntityCategory Category { get; init; }

    /// <summary>The original class name (<c>peon</c>, <c>barrack</c>...), a stable key (REQ-DATA-06); empty if none.</summary>
    public string LegacyClass { get; init; } = string.Empty;

    /// <summary>Civilization key (<c>euro</c>, <c>asia</c>, <c>arab</c>, <c>comm</c>); empty if none.</summary>
    public string Civilization { get; init; } = string.Empty;

    /// <summary>Cost per resource type id (empty = free).</summary>
    public ImmutableArray<int> Cost { get; init; } = [];

    /// <summary>Training or construction time (TSV <c>TIME</c>, seconds → ms).</summary>
    public int BuildTimeMs { get; init; }

    /// <summary>Sight radius in world units (trait <c>RevealsShroud</c>; also the auto-acquire radius).</summary>
    public int SightUnits { get; init; }

    /// <summary>Defence (TSV <c>DEFENCE</c>; trait <c>Armor</c>).</summary>
    public int Defence { get; init; }

    /// <summary>
    /// Building resistance (buildings TSV <c>RESISTANCE</c>; trait <c>Armor</c>): reduces the damage the building takes
    /// (manual ch. 4 "The Selection View") and, later, its capture resistance (GAME-04).
    /// </summary>
    public int Resistance { get; init; }

    /// <summary>How it attacks, or null.</summary>
    public ArmamentDefinition? Armament { get; init; }

    /// <summary>Gathering, or null.</summary>
    public HarvesterDefinition? Harvester { get; init; }

    /// <summary>Building types it can construct (ascending type ids); empty = not a builder.</summary>
    public ImmutableArray<ushort> Builds { get; init; } = [];

    /// <summary>Training, or null.</summary>
    public ProducerDefinition? Producer { get; init; }

    /// <summary>Resource held, or null.</summary>
    public ResourceNodeDefinition? ResourceNode { get; init; }

    /// <summary>Resource types accepted as a drop-off (bit = resource type id); 0 = not a drop-off.</summary>
    public int DropOffMask { get; init; }

    /// <summary>Population the entity uses (classic: 1 per unit, GAME-05).</summary>
    public int PopulationCost { get; init; }

    /// <summary>Population room a completed building provides (object INI <c>pop_count</c>).</summary>
    public int PopulationProvided { get; init; }

    /// <summary>Building types the owner must have completed before it can build or train this type (TSV <c>DEPENDS ON</c>).</summary>
    public ImmutableArray<ushort> RequiredTypes { get; init; } = [];

    /// <summary>Technology that must be researched first (TSV <c>DEPENDS ON</c>, e.g. <c>te_halberd</c>); empty = none.</summary>
    public string RequiredTechnology { get; init; } = string.Empty;

    /// <summary>
    /// Prerequisites by civilization of the actor (builder or producer; <c>TECHTREE &lt;civ&gt;.INI</c> and the TSV
    /// <c>DEPENDS ON</c>; ADR 0040), in addition to <see cref="RequiredTypes"/>.
    /// </summary>
    public ImmutableArray<RequirementSet> Requirements { get; init; } = [];

    /// <summary>Mana the caster pays to summon the type (cost suffix <c>m</c>, GAME-01; spells are GAME-11).</summary>
    public int ManaCost { get; init; }

    /// <summary>Mana pool of casters (OBJECTS <c>max_manna</c>), or null.</summary>
    public ManaPoolDefinition? ManaPool { get; init; }

    /// <summary>Who may deliver to the drop-off (<see cref="DropOffMask"/>).</summary>
    public DropOffAccess DropOffAccess { get; init; } = DropOffAccess.Land;

    /// <summary>Whether the builders of a completed building start gathering nearby at once (1.05: storage, lumber mill).</summary>
    public bool AutoGather { get; init; }

    /// <summary>Placement rule of a building.</summary>
    public PlacementKind Placement { get; init; }

    /// <summary>Capture by peasants (GAME-04), or null.</summary>
    public CapturableDefinition? Capturable { get; init; }

    /// <summary>Whether the type captures (peasants).</summary>
    public bool IsCapturer { get; init; }

    /// <summary>Capture influence (EMIT own/other), or null.</summary>
    public InfluenceDefinition? Influence { get; init; }

    /// <summary>Whether builders can repair the type (buildings, ships, siege; GAME-03).</summary>
    public bool IsRepairable { get; init; }

    /// <summary>Whether the type burns while badly damaged (GAME-03: badly damaged buildings burn, manual ch. 5).</summary>
    public bool Burnable { get; init; }

    /// <summary>Whether a destroyed entity leaves a ruin decal (never blocking, ORIG-01).</summary>
    public bool LeavesRuin { get; init; }

    /// <summary>Whether the type is a wall segment that can be placed by dragging a line (GAME-03).</summary>
    public bool IsWall { get; init; }

    /// <summary>Gate passage, or null.</summary>
    public GateDefinition? Gate { get; init; }

    /// <summary>Whether the type is a bridge: its deck makes water passable for land units (GAME-03).</summary>
    public bool IsBridge { get; init; }

    /// <summary>Whether deleting it needs Shift + Delete (castle and guilds, GAME-04).</summary>
    public bool ProtectedDelete { get; init; }

    /// <summary>
    /// Whether units walk over the footprint (fields: their farmer works on them). Nothing can be built on it, and it
    /// blocks nobody's way.
    /// </summary>
    public bool WalkableFootprint { get; init; }

    /// <summary>Neglect decay (fields), or null.</summary>
    public DecayDefinition? Decay { get; init; }

    /// <summary>Type spawned where the entity dies (a cow's meat), or -1.</summary>
    public int SpawnOnDeath { get; init; } = -1;

    /// <summary>
    /// The meat a living animal grows and leaves as <see cref="SpawnOnDeath"/> items when it dies (NAT-004), or null for a
    /// single spawn.
    /// </summary>
    public MeatDefinition? Meat { get; init; }

    /// <summary>Lifetime of the entity (meat rots), 0 = unlimited.</summary>
    public int LifetimeMs { get; init; }

    /// <summary>World traits (trade, tavern, claims, animals, detection; ADRs 0101-0104).</summary>
    public WorldTraits World { get; init; } = WorldTraits.None;

    /// <summary>Whether the type is a builder.</summary>
    public bool IsBuilder => !Builds.IsDefaultOrEmpty;

    /// <summary>Whether the type can construct <paramref name="typeId"/>.</summary>
    public bool CanBuild(ushort typeId) => IsBuilder && Builds.BinarySearch(typeId) >= 0;

    /// <summary>Whether the type accepts resource <paramref name="resource"/> as a drop-off.</summary>
    public bool AcceptsDropOff(int resource) => (uint)resource < 32 && (DropOffMask & (1 << resource)) != 0;
}

/// <summary>
/// Meat an animal grows (NAT-004; ReTzar's reading of the original, the cow's +0x120): <paramref name="Start"/> when it
/// appears, <paramref name="PerSecond"/> more each second it can act, up to <paramref name="Max"/>; when it dies it leaves
/// items of at most <paramref name="Piece"/> each.
/// </summary>
/// <param name="Start">Meat at the start (300).</param>
/// <param name="PerSecond">Meat grown each second (2).</param>
/// <param name="Max">Most meat (600).</param>
/// <param name="Piece">Most meat of one item left (100).</param>
public sealed record MeatDefinition(int Start, int PerSecond, int Max, int Piece);
