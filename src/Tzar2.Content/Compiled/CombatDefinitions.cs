using System;
using System.Collections.Immutable;

namespace Tzar2.Content.Compiled;

/// <summary>
/// How a holder shares the damage of a hit of one damage class with the units inside (OBJECTS <c>[damage]</c>:
/// <c>00 = 90,10</c> — "% to take, % to distribute if holder"; the default is 100,0; GAME-07/09, ADR 0093).
/// </summary>
/// <param name="TakePercent">Share of the damage the holder itself takes.</param>
/// <param name="DistributePercent">Share of the damage spread over the units inside.</param>
public readonly record struct DamageShare(int TakePercent, int DistributePercent)
{
    /// <summary>All damage on the holder (the original's default).</summary>
    public static DamageShare Default => new(100, 0);
}

/// <summary>
/// Trait <c>Holder</c> (GAME-09; ADR 0093): the entity carries units — castles and outposts (<c>max_obj_in_holder = 50</c>,
/// <c>[accept] allunits</c>), ships (<c>earthunits</c>), carpets and caravans (<c>infantryunits</c>, <c>mountedunits</c>).
/// </summary>
/// <param name="Capacity">Most units inside (<c>max_obj_in_holder</c>; the <c>holderCapacity</c> modifier adds to it).</param>
/// <param name="Accepts">Unit type ids that may enter, ascending.</param>
/// <param name="Damage">Damage share per <see cref="DamageClass"/> (3 entries).</param>
/// <param name="HealPerMinute">Hit points every unit inside regains per minute (castles, outposts; UNK-24); 0 = none.</param>
public sealed record HolderDefinition(int Capacity, ImmutableArray<ushort> Accepts, ImmutableArray<DamageShare> Damage, int HealPerMinute)
{
    /// <summary>Most units any holder may carry (bounds snapshots and per-tick work).</summary>
    public const int MaxCapacity = 256;

    /// <summary>Whether a unit of <paramref name="typeId"/> may enter.</summary>
    public bool CanHold(ushort typeId) => !Accepts.IsDefault && Accepts.BinarySearch(typeId) >= 0;

    /// <summary>The damage share for a damage class.</summary>
    public DamageShare ShareFor(DamageClass damage) => (int)damage < Damage.Length ? Damage[(int)damage] : DamageShare.Default;

    internal void Validate(int typeCount, string where)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Capacity, 1, where);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Capacity, MaxCapacity, where);
        ArgumentOutOfRangeException.ThrowIfNegative(HealPerMinute, where);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(HealPerMinute, 1_000_000, where);
        Ruleset.CheckAscendingIds(Accepts, typeCount, $"{where}: holder accepts");
        if (Damage.IsDefault || Damage.Length != 3)
        {
            throw new ArgumentException($"{where}: a holder needs one damage share per damage class.");
        }

        foreach (DamageShare s in Damage)
        {
            if (s.TakePercent is < 0 or > 1000 || s.DistributePercent is < 0 or > 1000)
            {
                throw new ArgumentException($"{where}: damage shares must be 0-1000%.");
            }
        }
    }
}

/// <summary>Kinds of items (manual ch. 5 §6, editor manual §6.11, OBJECTS <c>ITEM*.INI</c>; GAME-10, ADR 0094).</summary>
public enum ItemKind : byte
{
    /// <summary>Raises the holder's attack by the item's power while carried.</summary>
    Weapon = 0,

    /// <summary>Raises the holder's defence by the item's power while carried.</summary>
    Armor = 1,

    /// <summary>Lets the holder cast its spell (charges, mana, cooldown).</summary>
    SpellBook = 2,

    /// <summary>Story text (an event for the presentation when picked up).</summary>
    Scroll = 3,

    /// <summary>Used once: adds level, attack, defence, health or mana to the user.</summary>
    Potion = 4,

    /// <summary>Its amount of a resource goes to the stockpile of whoever picks it up.</summary>
    Resource = 5,

    /// <summary>Carried by peasants; no effect (scenario objectives).</summary>
    Flag = 6,

    /// <summary>A soul: the orders <c>item_kill</c> (kill a unit) and <c>item_berserk</c>.</summary>
    Soul = 7,

    /// <summary>"PlusPlus": used once, raises the user's level (⚠ HYPOTHESIS).</summary>
    PlusPlus = 8,
}

/// <summary>Trait <c>Item</c> (GAME-10; ADR 0094): an object that lies on the ground and can be carried.</summary>
/// <param name="Kind">What it does.</param>
/// <param name="Holders">Unit type ids that may carry it (OBJECTS <c>[enter]</c>), ascending.</param>
/// <param name="Power">Default magnitude: attack or defence of weapons/armor, the amount of a resource item.</param>
/// <param name="Resource">Resource type id of a resource item, else -1.</param>
/// <param name="Spell">Spell id of a spell book (or the effect a soul triggers), else -1.</param>
/// <param name="Charges">Uses of a spell book (0 = unlimited).</param>
/// <param name="CooldownMs">Least time between two uses of a spell book (editor "Time restriction").</param>
/// <param name="NeedsMana">Whether casting from the book costs the holder the spell's mana (editor "Needs manna to use").</param>
public sealed record ItemDefinition(ItemKind Kind, ImmutableArray<ushort> Holders, int Power, int Resource, int Spell, int Charges, int CooldownMs, bool NeedsMana)
{
    /// <summary>Levels a potion adds.</summary>
    public int PotionLevel { get; init; }

    /// <summary>Attack a potion adds for good.</summary>
    public int PotionAttack { get; init; }

    /// <summary>Defence a potion adds for good.</summary>
    public int PotionDefence { get; init; }

    /// <summary>Hit points a potion restores.</summary>
    public int PotionHealth { get; init; }

    /// <summary>Mana a potion restores.</summary>
    public int PotionMana { get; init; }

    /// <summary>Whether a unit of <paramref name="typeId"/> may carry it.</summary>
    public bool CanBeHeldBy(ushort typeId) => !Holders.IsDefault && Holders.BinarySearch(typeId) >= 0;

    internal void Validate(int typeCount, int resources, int spells, string where)
    {
        if (Kind > ItemKind.PlusPlus)
        {
            throw new ArgumentException($"{where}: unknown item kind.");
        }

        Ruleset.CheckAscendingIds(Holders, typeCount, $"{where}: item holders");
        if (Power is < 0 or > 1_000_000 || Charges is < 0 or > 1_000_000 || CooldownMs is < 0 or > 3_600_000
            || Resource < -1 || Resource >= resources || (Kind == ItemKind.Resource) != (Resource >= 0)
            || Spell < -1 || Spell >= spells || (Kind == ItemKind.SpellBook && Spell < 0)
            || PotionLevel is < 0 or > 1000 || PotionAttack is < 0 or > 100_000 || PotionDefence is < 0 or > 100_000
            || PotionHealth is < 0 or > 1_000_000 || PotionMana is < 0 or > 1_000_000)
        {
            throw new ArgumentException($"{where}: invalid item.");
        }
    }
}

/// <summary>What a spell does (GAME-11; ADR 0095). Values are hash format: append only.</summary>
public enum SpellEffect : byte
{
    /// <summary>Damages the target (lightning, flame bolt).</summary>
    Damage = 0,

    /// <summary>Restores hit points (priest heal).</summary>
    Heal = 1,

    /// <summary>Raises attack for a while (bless).</summary>
    Bless = 2,

    /// <summary>The target cannot move or act for a while (freeze).</summary>
    Freeze = 3,

    /// <summary>The target turns to stone: cannot act and takes no damage (turn to stone).</summary>
    Stone = 4,

    /// <summary>The target becomes invisible (invisibility, ninja cloak).</summary>
    Cloak = 5,

    /// <summary>The target attacks everyone near it, its own kingdom included (confusion / berserk).</summary>
    Confuse = 6,

    /// <summary>Spawns <see cref="SpellDefinition.Summon"/> next to the caster (bat, golem, dragon, carpet, jinn).</summary>
    Summon = 7,

    /// <summary>Raises <see cref="SpellDefinition.Summon"/> from corpses near the target point (raise dead).</summary>
    RaiseDead = 8,

    /// <summary>Puts a revealer (<see cref="SpellDefinition.Summon"/>, e.g. the fog revealer) at the point (magic vision).</summary>
    Reveal = 9,

    /// <summary>Turns the caster's mana into the target's experience (Mastery: spell_raiseexp).</summary>
    Experience = 10,

    /// <summary>Damages everything in the radius around the point, in pulses (rain of fire, whirlpool).</summary>
    AreaDamage = 11,

    /// <summary>Lowers attack and defence for a while (curse; spell books only).</summary>
    Curse = 12,

    /// <summary>Kills the target at once (ninja murder, soul item_kill; heroes immune).</summary>
    Kill = 13,
}

/// <summary>What a spell is aimed at.</summary>
public enum SpellTarget : byte
{
    /// <summary>A unit.</summary>
    Unit = 0,

    /// <summary>A point on the map.</summary>
    Ground = 1,

    /// <summary>The caster itself.</summary>
    Self = 2,
}

/// <summary>Whose units a spell may affect.</summary>
[Flags]
public enum SpellAffects : byte
{
    /// <summary>Nobody.</summary>
    None = 0,

    /// <summary>The caster's own kingdom.</summary>
    Own = 1,

    /// <summary>Other kingdoms and Nature.</summary>
    Enemy = 2,

    /// <summary>Everyone.</summary>
    All = Own | Enemy,
}

/// <summary>
/// A spell (GAME-11; ADR 0095): an order <c>ACTION = spell spell_x</c> of <c>ORDERS.INI</c> (its key), on the command cards
/// of casters (<c>MENU.INI</c>), gated per civilization by <c>TECHTREE [orders]</c>. The magnitudes are not in any
/// table (UNK-26): rules YAML, UNVERIFIED.
/// </summary>
/// <param name="Id">Spell id (position in <see cref="Ruleset.Spells"/>).</param>
/// <param name="Key">The order key (<c>spell_lightning</c>, <c>raisedead</c>).</param>
/// <param name="Effect">What it does.</param>
/// <param name="Target">What it is aimed at.</param>
/// <param name="Affects">Whose units it may affect.</param>
/// <param name="ManaCost">Mana the caster pays (summons add the summoned type's mana cost).</param>
/// <param name="RangeUnits">Cast range, edge to edge (world units).</param>
/// <param name="CooldownMs">Least time between two casts by one caster.</param>
/// <param name="CastMs">Time from the start of the cast to the effect.</param>
/// <param name="Amount">Damage, healing, bonus percent or mana converted, by effect.</param>
/// <param name="DurationMs">Duration of a lasting effect.</param>
/// <param name="RadiusUnits">Radius of an area effect (world units).</param>
/// <param name="Summon">Type summoned, raised or placed, or -1.</param>
/// <param name="Projectile">Projectile carrying the effect, or -1 (instant).</param>
/// <param name="TargetTypes">Types it may affect (empty = every unit), ascending.</param>
public sealed record SpellDefinition(
    ushort Id,
    string Key,
    SpellEffect Effect,
    SpellTarget Target,
    SpellAffects Affects,
    int ManaCost,
    int RangeUnits,
    int CooldownMs,
    int CastMs,
    int Amount,
    int DurationMs,
    int RadiusUnits,
    int Summon,
    int Projectile,
    ImmutableArray<ushort> TargetTypes)
{
    /// <summary>Time between two pulses of an area effect.</summary>
    public int PulseMs { get; init; } = 1000;

    /// <summary>How many summons one cast creates (or most corpses raised).</summary>
    public int Count { get; init; } = 1;

    /// <summary>Whether <paramref name="typeId"/> may be affected (by its type).</summary>
    public bool AffectsType(ushort typeId) => TargetTypes.IsDefaultOrEmpty || TargetTypes.BinarySearch(typeId) >= 0;
}

/// <summary>
/// A projectile (TDD §10 GAME-07: arrow, bolt, cannon, rocket arrow, stone; spell bolts; ADR 0091): flight speed, whether it
/// follows its target or lands where the target was, and its splash.
/// </summary>
/// <param name="Id">Projectile id (position in <see cref="Ruleset.Projectiles"/>).</param>
/// <param name="Key">Key (<c>arrow</c>, <c>stone</c>...; the original's projectile objects).</param>
/// <param name="SpeedUnitsPerSecond">Flight speed (world units per second).</param>
/// <param name="Homing">Follows the target and hits it unless it died; otherwise it lands at the target's position at launch.</param>
/// <param name="HitRadiusUnits">A ground-aimed projectile hits its target if the target is this close to the landing point.</param>
/// <param name="SplashRadiusUnits">Units and buildings this close to the impact take splash damage (0 = none).</param>
/// <param name="SplashPercent">Splash damage as a share of the hit's damage.</param>
/// <param name="FriendlyFire">Whether splash hurts the attacker's own kingdom.</param>
public sealed record ProjectileDefinition(ushort Id, string Key, int SpeedUnitsPerSecond, bool Homing, int HitRadiusUnits, int SplashRadiusUnits, int SplashPercent, bool FriendlyFire);

/// <summary>Per-unit special abilities (GAME-11; flags, append only).</summary>
[Flags]
public enum AbilityFlags : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Peasants bribe enemy units (order <c>bribe</c>, Bribery).</summary>
    Bribe = 1,

    /// <summary>Cannot be bribed (crusaders, peasants; TDD GAME-11).</summary>
    BribeImmune = 2,

    /// <summary>A cathedral preaches crusades (order <c>preach_crusade</c>).</summary>
    PreachCrusade = 4,

    /// <summary>A training dummy: attacks on it give experience and do no damage (Tactics; GAME-08).</summary>
    TrainingDummy = 8,

    /// <summary>A hero cannot be killed outright (murder, souls).</summary>
    KillImmune = 16,
}

/// <summary>Trait <c>ConvertsFrom</c>: a unit made from another in place (Jihad warriors from peasants, priests from monks).</summary>
/// <param name="From">Source unit type ids, ascending.</param>
/// <param name="Order">The order key (<c>become_jihadw</c>, <c>become_priest</c>; gated by <c>TECHTREE [orders]</c>).</param>
public sealed record ConversionDefinition(ImmutableArray<ushort> From, string Order);

/// <summary>Trait <c>ItemCrafter</c> (Arabian Magic Workshop, manual ch. 5 §6.1): crafts items; better ones cost more gold.</summary>
/// <param name="Items">Item type ids it crafts, ascending.</param>
public sealed record CrafterDefinition(ImmutableArray<ushort> Items);

/// <summary>Hero names of one civilization (<c>DATA/HEROES.INI</c>), in order.</summary>
/// <param name="Civilization">Civilization key.</param>
/// <param name="Names">Names in file order.</param>
public sealed record HeroNames(string Civilization, ImmutableArray<string> Names);

/// <summary>
/// Ruleset-wide constants of combat, experience, holders, items and magic (GAME-07…11; section <c>combat:</c>). Values
/// not in the original tables are UNVERIFIED placeholders (docs/mechanics/m3-placeholders.md, stream E).
/// </summary>
public sealed record CombatSettings
{
    /// <summary>Defaults (rulesets without a <c>combat:</c> section).</summary>
    public static CombatSettings Default { get; } = new();

    /// <summary>Experience a unit gains per point of damage it deals to an enemy (percent; UNK-23).</summary>
    public int ExperiencePerDamagePercent { get; init; } = 100;

    /// <summary>Extra experience for a kill (UNK-23).</summary>
    public int ExperiencePerKill { get; init; }

    /// <summary>Experience for level n is <c>LevelBaseXp × n × (n + 1) / 2</c>: each level costs more (strategy guide).</summary>
    public int LevelBaseXp { get; init; } = 100;

    /// <summary>Levels a unit may reach without technologies (<c>experienceCap</c> modifiers add; UNK-23).</summary>
    public int BaseLevelCap { get; init; } = 3;

    /// <summary>Attack per level (manual: one point per experience point).</summary>
    public int AttackPerLevel { get; init; } = 1;

    /// <summary>Defence per level in half points (manual: half a point; truncated).</summary>
    public int DefenceHalvesPerLevel { get; init; } = 1;

    /// <summary>Level at which a unit becomes a named hero (TDD GAME-08: level 12).</summary>
    public int HeroLevel { get; init; } = 12;

    /// <summary>Technology id that lets heroes share experience (Leadership), or -1.</summary>
    public int LeadershipTechnology { get; init; } = -1;

    /// <summary>Radius of Leadership (world units).</summary>
    public int LeadershipRadiusUnits { get; init; } = 4 * 2048;

    /// <summary>Share of a hero's experience gain the units around it get (percent).</summary>
    public int LeadershipSharePercent { get; init; } = 50;

    /// <summary>Technology id of Honor (heroes hit heroes harder), or -1.</summary>
    public int HonorTechnology { get; init; } = -1;

    /// <summary>Attack bonus of a hero against a hero with Honor (percent).</summary>
    public int HonorAttackPercent { get; init; } = 25;

    /// <summary>Highest level training dummies can teach.</summary>
    public int DummyMaxLevel { get; init; } = 3;

    /// <summary>Tiles around a holder searched for room when units leave it.</summary>
    public int ExitRadiusTiles { get; init; } = 3;

    /// <summary>Gold per point of power of a crafted item (UNK-25).</summary>
    public int CraftGoldPerPower { get; init; } = 50;

    /// <summary>Crafting time per point of power (UNK-25).</summary>
    public int CraftMsPerPower { get; init; } = 2000;

    /// <summary>Most power a crafted item may have.</summary>
    public int CraftMaxPower { get; init; } = 50;

    /// <summary>Bribe cost as a share of the unit's cost, in gold (percent; UNK-26).</summary>
    public int BribeCostPercent { get; init; } = 200;

    /// <summary>Crusade: the anchor type spawned (<c>CRUSADE_ANCHOR.INI</c>), or -1.</summary>
    public int CrusadeAnchor { get; init; } = -1;

    /// <summary>Crusade: the crusader type, or -1.</summary>
    public int Crusader { get; init; } = -1;

    /// <summary>Crusade: crusaders one crusade raises (⚠ HYPOTHESIS UNK-26).</summary>
    public int CrusadeSize { get; init; } = 5;

    /// <summary>Crusade: the anchor stands at most this far from the cathedral (TDD GAME-11: 4 tiles).</summary>
    public int CrusadeAnchorRadiusUnits { get; init; } = 4 * 2048;

    /// <summary>Crusade: least time after the last crusader died before the next crusade (1.09).</summary>
    public int CrusadeCooldownMs { get; init; } = 60_000;

    /// <summary>Hero names per civilization (<c>HEROES.INI</c>).</summary>
    public ImmutableArray<HeroNames> HeroNames { get; init; } = [];

    /// <summary>The hero names of <paramref name="civilization"/> (empty if none).</summary>
    public ImmutableArray<string> NamesOf(string civilization)
    {
        foreach (HeroNames h in HeroNames)
        {
            if (string.Equals(h.Civilization, civilization, StringComparison.Ordinal))
            {
                return h.Names;
            }
        }

        return [];
    }

    internal void Validate(int typeCount, int technologies)
    {
        Check(ExperiencePerDamagePercent, 0, 100_000);
        Check(ExperiencePerKill, 0, 1_000_000);
        Check(LevelBaseXp, 1, 1_000_000);
        Check(BaseLevelCap, 0, 10_000);
        Check(AttackPerLevel, 0, 10_000);
        Check(DefenceHalvesPerLevel, 0, 10_000);
        Check(HeroLevel, 1, 10_000);
        Check(LeadershipTechnology, -1, technologies - 1);
        Check(HonorTechnology, -1, technologies - 1);
        Check(LeadershipRadiusUnits, 0, 64 * 2048);
        Check(LeadershipSharePercent, 0, 1000);
        Check(HonorAttackPercent, 0, 10_000);
        Check(DummyMaxLevel, 0, 10_000);
        Check(ExitRadiusTiles, 1, 16);
        Check(CraftGoldPerPower, 1, 1_000_000);
        Check(CraftMsPerPower, 0, 600_000);
        Check(CraftMaxPower, 1, 10_000);
        Check(BribeCostPercent, 0, 100_000);
        Check(CrusadeAnchor, -1, typeCount - 1);
        Check(Crusader, -1, typeCount - 1);
        Check(CrusadeSize, 0, 256);
        Check(CrusadeAnchorRadiusUnits, 0, 64 * 2048);
        Check(CrusadeCooldownMs, 0, 3_600_000);
        if (HeroNames.IsDefault)
        {
            throw new ArgumentException("combat: hero names must not be default.");
        }

        foreach (HeroNames h in HeroNames)
        {
            if (h is null || string.IsNullOrEmpty(h.Civilization) || h.Names.IsDefault || h.Names.Length > 1024)
            {
                throw new ArgumentException("combat: invalid hero names.");
            }
        }

        static void Check(int value, int min, int max)
        {
            if (value < min || value > max)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"combat setting {value} is outside {min}..{max}.");
            }
        }
    }
}
