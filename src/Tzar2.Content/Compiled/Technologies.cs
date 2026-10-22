using System;
using System.Collections.Immutable;

namespace Tzar2.Content.Compiled;

/// <summary>
/// A quantity a technology or upgrade can change (GAME-06; ADR 0040). Values are wire/hash format: append only. Each
/// stat names the entity types it applies to through <see cref="ModifierDefinition.Types"/>: the types of the
/// researching player's entities (attack of footmen, cost of peasants...), except <see cref="GatherRate"/>, whose types
/// are the resource nodes gathered from (fields, trees, rocks...).
/// </summary>
public enum ModifierStat : byte
{
    /// <summary><c>Armament.attack</c> of the types.</summary>
    Attack = 0,

    /// <summary><c>Armor.defence</c> of the types.</summary>
    Defence = 1,

    /// <summary><c>Health.max</c> of the types (existing entities keep the damage they took).</summary>
    MaxHealth = 2,

    /// <summary>
    /// <c>Armor.resistance</c> of buildings: reduces damage (ADR 0038) and is the base of the capture resistance
    /// (GAME-04, ADR 0044).
    /// </summary>
    Resistance = 3,

    /// <summary>Sight radius (world units) of the types.</summary>
    Sight = 4,

    /// <summary><c>Armament.range</c> (world units) of ranged types.</summary>
    Range = 5,

    /// <summary>Gathering speed from the listed <b>node</b> types: gather time × 10,000 / (10,000 + bonus).</summary>
    GatherRate = 6,

    /// <summary>Carry capacity of harvester types.</summary>
    CarryCapacity = 7,

    /// <summary>Construction speed of the listed building types (and repair speed of them).</summary>
    BuildRate = 8,

    /// <summary>Cost (every resource) of the types: training, construction, research is not affected.</summary>
    Cost = 9,

    /// <summary><c>PopulationProvider.amount</c> of building types (houses: Supply Storage, Seafood).</summary>
    PopulationProvided = 10,

    /// <summary>Mana regeneration of the types (Concentration; GAME-11 casters).</summary>
    ManaRegen = 11,

    /// <summary>Maximum mana of the types.</summary>
    ManaMax = 12,

    /// <summary>Holder capacity (Landing Tactics; consumed by GAME-09).</summary>
    HolderCapacity = 13,

    /// <summary>Experience gain (Learning, Warfare, Tactics, Sun Tzu; consumed by GAME-08).</summary>
    ExperienceGain = 14,

    /// <summary>Experience level cap (Learning, Heroism; consumed by GAME-08).</summary>
    ExperienceCap = 15,

    /// <summary>Starting experience level of newly trained units (Philosophy; consumed by GAME-08).</summary>
    StartingLevel = 16,

    /// <summary>Hit points gained per experience level (Body Building; consumed by GAME-08).</summary>
    HealthPerLevel = 17,

    /// <summary>Spell or ability strength (Inquisition, Kung-Fu, Honor...; consumed by GAME-08/11).</summary>
    AbilityPower = 18,

    /// <summary>Spell or ability range (Medicine; consumed by GAME-11).</summary>
    AbilityRange = 19,

    /// <summary>Trade, loan and treasury yields (Trade Taxes, Banking, Usury...; consumed by GAME-14).</summary>
    Income = 20,

    /// <summary>How often a weapon fires, in percent of its own rate (ship cannon levels, help HT_UPGRADE_SHIPS; TECH-036).</summary>
    FireRate = 21,
}

/// <summary>The number of <see cref="ModifierStat"/> values.</summary>
public static class ModifierStats
{
    /// <summary>How many stats exist (the last value + 1).</summary>
    public const int Count = (int)ModifierStat.FireRate + 1;

    private static readonly string[] Keys =
    [
        "attack", "defence", "maxHealth", "resistance", "sight", "range", "gatherRate", "carryCapacity", "buildRate", "cost",
        "populationProvided", "manaRegen", "manaMax", "holderCapacity", "experienceGain", "experienceCap", "startingLevel",
        "healthPerLevel", "abilityPower", "abilityRange", "income", "fireRate",
    ];

    /// <summary>Parses a rules key (<c>attack</c>, <c>gatherRate</c>...), or returns false.</summary>
    public static bool TryParse(string key, out ModifierStat stat)
    {
        int i = Array.IndexOf(Keys, key);
        stat = i >= 0 ? (ModifierStat)i : default;
        return i >= 0;
    }

    /// <summary>The rules key of a stat.</summary>
    public static string Key(ModifierStat stat) => Keys[(int)stat];

    /// <summary>All rules keys, in stat order.</summary>
    public static string KnownKeys => string.Join(", ", Keys);
}

/// <summary>
/// One effect of a technology or upgrade: <c>value = (base + Add) × (10,000 + BasisPoints) / 10,000</c>, truncated
/// toward zero, never below 0 (ADR 0040). Several modifiers of one stat and type add up (adds and basis points
/// separately) before the formula applies.
/// </summary>
/// <param name="Stat">What changes.</param>
/// <param name="Add">Flat amount added to the base.</param>
/// <param name="BasisPoints">Percentage in basis points (+25% = 2,500).</param>
/// <param name="Types">The entity type ids it applies to, ascending (see <see cref="ModifierStat"/> for their role).</param>
public sealed record ModifierDefinition(ModifierStat Stat, int Add, int BasisPoints, ImmutableArray<ushort> Types)
{
    /// <summary>Whether the modifier applies to <paramref name="typeId"/>.</summary>
    public bool AppliesTo(ushort typeId) => Types.BinarySearch(typeId) >= 0;

    internal void Validate(int typeCount)
    {
        if ((int)Stat >= ModifierStats.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(Stat));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(Add, -1_000_000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Add, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(BasisPoints, -10_000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BasisPoints, 1_000_000);
        Ruleset.CheckAscendingIds(Types, typeCount, "modifier types");
    }
}

/// <summary>
/// Prerequisites that apply to one civilization (<c>TECHTREE &lt;civ&gt;.INI</c>; ADR 0040): the technologies the player
/// must have researched and the building types it must own completed. The civilization is that of the entity that acts
/// (the builder, the producer or the researching building).
/// </summary>
/// <param name="Civilization">Civilization key; empty = every civilization.</param>
/// <param name="Technologies">Technology ids, ascending.</param>
/// <param name="Types">Building type ids, ascending.</param>
/// <param name="Unsatisfiable">A prerequisite names something that does not exist (kept so it can never be met).</param>
public sealed record RequirementSet(string Civilization, ImmutableArray<ushort> Technologies, ImmutableArray<ushort> Types, bool Unsatisfiable = false)
{
    /// <summary>Whether the set applies to an actor of <paramref name="civilization"/>.</summary>
    public bool AppliesTo(string civilization) => Civilization.Length == 0 || string.Equals(Civilization, civilization, StringComparison.Ordinal);
}

/// <summary>A researchable technology (<c>TECHNOLOGIES.INI</c>, 87 in the original; GAME-06, ADR 0040).</summary>
/// <param name="Id">Technology id (position in <see cref="Ruleset.Technologies"/>).</param>
/// <param name="Key">Stable key, the original id (<c>te_halberd</c>, REQ-DATA-06).</param>
/// <param name="Name">Display name of the data (<c>displayname</c>), for tools and logs.</param>
/// <param name="Cost">Cost per resource type id.</param>
/// <param name="TimeMs">Research time.</param>
/// <param name="Requirements">Prerequisites by civilization (of the researching building).</param>
/// <param name="Effects">What it changes once researched.</param>
public sealed record TechnologyDefinition(ushort Id, string Key, string Name, ImmutableArray<int> Cost, int TimeMs, ImmutableArray<RequirementSet> Requirements, ImmutableArray<ModifierDefinition> Effects);

/// <summary>
/// An upgrade (<c>UPGRADES.INI</c>, 12 in the original; GAME-06, ADR 0040), bought per entity: either it
/// <b>converts</b> the entity into another type (wood tower → guard tower, wood wall → stone wall; <see cref="Conversions"/>)
/// or it is a <b>level</b> the entity keeps (ship cannons; <see cref="Effects"/> apply to that entity only).
/// </summary>
/// <param name="Id">Upgrade id (position in <see cref="Ruleset.Upgrades"/>, at most 32).</param>
/// <param name="Key">Stable key (<c>to_wtower2</c>).</param>
/// <param name="Name">Display name.</param>
/// <param name="Cost">Cost per resource type id.</param>
/// <param name="TimeMs">Upgrade time.</param>
/// <param name="Requirements">Prerequisites by civilization (of the upgraded entity).</param>
/// <param name="Conversions">Pairs (from type, to type), sorted by from type; empty for a level upgrade.</param>
/// <param name="After">Level upgrades the entity must already have (ids).</param>
/// <param name="Effects">Entity effects of a level upgrade.</param>
public sealed record UpgradeDefinition(
    ushort Id,
    string Key,
    string Name,
    ImmutableArray<int> Cost,
    int TimeMs,
    ImmutableArray<RequirementSet> Requirements,
    ImmutableArray<(ushort From, ushort To)> Conversions,
    ImmutableArray<ushort> After,
    ImmutableArray<ModifierDefinition> Effects)
{
    /// <summary>Whether this upgrade converts entities into another type.</summary>
    public bool IsConversion => !Conversions.IsDefaultOrEmpty;

    /// <summary>The type an entity of <paramref name="from"/> becomes, or -1.</summary>
    public int ConvertedType(ushort from)
    {
        foreach ((ushort f, ushort t) in Conversions)
        {
            if (f == from)
            {
                return t;
            }
        }

        return -1;
    }
}

/// <summary>An order that needs a technology for one civilization (<c>TECHTREE [orders]</c>: spells, loans, bribes...).</summary>
/// <param name="Order">The original order key (<c>spell_heal</c>, <c>loan</c>, <c>bribe</c>).</param>
/// <param name="Civilization">Civilization key.</param>
/// <param name="Technology">Technology id.</param>
public readonly record struct OrderGate(string Order, string Civilization, ushort Technology);
