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
    internal void Validate()
    {
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
/// <param name="QueueCapacity">Most queued items (UNK-20).</param>
public sealed record ProducerDefinition(ImmutableArray<ushort> Produces, int QueueCapacity)
{
    /// <summary>Largest queue the sim stores per building.</summary>
    public const int MaxQueueCapacity = 16;

    /// <summary>Whether the building trains <paramref name="typeId"/>.</summary>
    public bool CanProduce(ushort typeId) => Produces.BinarySearch(typeId) >= 0;
}

/// <summary>Trait <c>ResourceNode</c> (GAME-01/02): a tree, rock or field holding a resource.</summary>
/// <param name="Resource">Resource type id.</param>
/// <param name="Amount">Units it holds; <see cref="Unlimited"/> for fields (the manual: "fields produce unlimited amounts of food").</param>
public sealed record ResourceNodeDefinition(int Resource, int Amount)
{
    /// <summary><see cref="Amount"/> of a node that never depletes.</summary>
    public const int Unlimited = -1;
}

/// <summary>
/// The gameplay traits of a compiled entity type (REQ-SIM-09, TDD §11.2). Every array is immutable; ids refer to the
/// owning <see cref="Ruleset"/> (resource type ids, entity type ids). <see cref="None"/> = a type without gameplay
/// (scenery, the M2 movement test types).
/// </summary>
public sealed record EntityTraits
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

    /// <summary>Whether the type is a builder.</summary>
    public bool IsBuilder => !Builds.IsDefaultOrEmpty;

    /// <summary>Whether the type can construct <paramref name="typeId"/>.</summary>
    public bool CanBuild(ushort typeId) => IsBuilder && Builds.BinarySearch(typeId) >= 0;

    /// <summary>Whether the type accepts resource <paramref name="resource"/> as a drop-off.</summary>
    public bool AcceptsDropOff(int resource) => (uint)resource < 32 && (DropOffMask & (1 << resource)) != 0;
}
