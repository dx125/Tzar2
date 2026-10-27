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

/// <summary>
/// What one item adds, set per item (ITEM-016, ADR 0186): the original's power items raise attack (swords, bows) or
/// defence (shields), its potions health, levels, mana, attack or defence. Values are hash format: append only.
/// </summary>
public enum ItemEffect : byte
{
    /// <summary>The type's own (<see cref="ItemDefinition.Kind"/> and potion values).</summary>
    Default = 0,

    /// <summary>Attack: while carried (a power item), or for good (a potion).</summary>
    Attack = 1,

    /// <summary>Defence: while carried (a power item), or for good (a potion).</summary>
    Defence = 2,

    /// <summary>Hit points restored (a potion).</summary>
    Health = 3,

    /// <summary>Experience levels gained (a potion).</summary>
    Levels = 4,

    /// <summary>Mana restored (a potion).</summary>
    Mana = 5,
}

/// <summary>One item a crafter can make of a type (ITEM-016): its effect or spell and its look.</summary>
/// <param name="Effect">What it adds (<see cref="ItemEffect.Default"/>: the type's).</param>
/// <param name="Spell">A book's spell id, or -1 for the type's.</param>
/// <param name="Look">1 + the index in <see cref="ItemDefinition.Looks"/>, or 0 for the type's own image.</param>
public readonly record struct CraftVariant(ItemEffect Effect, int Spell, int Look);

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

    /// <summary>Other images a single item of the type may wear (IMAGES.INI names; ITEM-016), in order.</summary>
    public ImmutableArray<string> Looks { get; init; } = [];

    /// <summary>The items a crafter makes of the type (ITEM-016); empty: one, the type as it is.</summary>
    public ImmutableArray<CraftVariant> Crafts { get; init; } = [];

    /// <summary>The variants a crafter offers: <see cref="Crafts"/>, or the type as it is.</summary>
    public int CraftVariantCount => Crafts.IsDefaultOrEmpty ? 1 : Crafts.Length;

    /// <summary>Variant <paramref name="index"/> (<see cref="CraftVariantCount"/>), or the type as it is.</summary>
    public CraftVariant CraftVariantAt(int index) => Crafts.IsDefaultOrEmpty ? new CraftVariant(ItemEffect.Default, -1, 0) : Crafts[index];

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
            || PotionHealth is < 0 or > 1_000_000 || PotionMana is < 0 or > 1_000_000 || Looks.Length > 250)
        {
            throw new ArgumentException($"{where}: invalid item.");
        }

        foreach (CraftVariant v in Crafts)
        {
            if (v.Effect > ItemEffect.Mana || v.Spell < -1 || v.Spell >= spells || v.Look < 0 || v.Look > Looks.Length
                || (v.Spell >= 0) != (Kind == ItemKind.SpellBook && v.Effect == ItemEffect.Default))
            {
                throw new ArgumentException($"{where}: invalid craft variant.");
            }
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

    /// <summary>
    /// Halves the attack and takes away the defence of every enemy unit around the point, within the caster's sight, for a
    /// while (curse: spell books and the ghost's banshee scream; ReTzar's reading of the original, 0x4e5841).
    /// </summary>
    Curse = 12,

    /// <summary>Kills the target at once (ninja murder, soul item_kill; heroes immune).</summary>
    Kill = 13,

    /// <summary>The target obeys the caster's kingdom for a while, then goes back to its own (hypnosis; MAG-008).</summary>
    Hypnotize = 14,

    /// <summary>
    /// Binds the target's soul: a <see cref="SpellDefinition.Summon"/> soul item bound to it goes into the caster's
    /// inventory (ITEM-010; ReTzar's reading of the original, 0x473403).
    /// </summary>
    SoulBind = 15,

    /// <summary>
    /// The ninja's murder (MAG-028; ReTzar 0x4e076a): one blow of the caster's attack times <see cref="SpellDefinition.Amount"/>,
    /// through the target's defence like any blow; the caster spends all its mana. Not magic: the Messiah takes it.
    /// </summary>
    Murder = 16,
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

    /// <summary>
    /// The gold a book of this spell is worth per charge, before the half added on top (TECH-030; ReTzar's reading of the
    /// original, 0x4643e0); a summon is worth its creature's gold.
    /// </summary>
    public int BookValue { get; init; }

    /// <summary>When above <see cref="SpellDefinition.DurationMs"/>, a lasting effect lasts a random time up to this (stone: 120-300 s).</summary>
    public int DurationMaxMs { get; init; }

    /// <summary>
    /// An area effect's pulse reaches what meets the square of <see cref="SpellDefinition.RadiusUnits"/> tiles around the
    /// pulse's tile (the original's cells: fire rain 5 × 5, the whirlpool 3 × 3), not a circle.
    /// </summary>
    public bool Square { get; init; }

    /// <summary>Each pulse of an area effect lands at a random point up to this far from the cast point on each axis (fire rain: 2 tiles).</summary>
    public int ScatterUnits { get; init; }

    /// <summary>The first pulse comes this much later than <see cref="SpellDefinition.PulseMs"/> (fire rain: the fireball's fall).</summary>
    public int DelayMs { get; init; }

    /// <summary>
    /// Every this long an area effect takes a random step of one tile where its whole square stays on water (the whirlpool:
    /// 2 s); 0: it stays.
    /// </summary>
    public int MoveMs { get; init; }

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

    /// <summary>A spy takes the look of another kingdom's unit (order <c>impersonate</c>; MAG-012, ADR 0184).</summary>
    Impersonate = 32,

    /// <summary>Lasting spells (freeze, stone, confusion, curse, hypnosis) do not take (the Messiah; WAR-010).</summary>
    MagicImmune = 64,

    /// <summary>After a blow it may let out a banshee scream, <see cref="CombatSettings.BansheeSpell"/> (the ghost; WAR-009).</summary>
    Banshee = 128,
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
/// The categories a unit or tower ranks a possible target by when it chooses on its own (WAR-015; ReTzar's reading of the
/// original, 0x412730), numbered as the original's priority tables are indexed.
/// </summary>
public enum TargetCategory : byte
{
    /// <summary>Nothing to attack on its own.</summary>
    None = 0,

    /// <summary>A wall.</summary>
    Wall = 1,

    /// <summary>A building of no other category.</summary>
    Building = 2,

    /// <summary>A unit that does not fight (a peasant at other work, a trader).</summary>
    Unarmed = 3,

    /// <summary>A tower.</summary>
    Tower = 4,

    /// <summary>A fighting unit, or a peasant that attacks.</summary>
    Fighter = 5,

    /// <summary>A peasant building a tower within the scanner's range and two tiles.</summary>
    TowerBuilder = 6,

    /// <summary>A peasant capturing a building.</summary>
    Capturer = 7,

    /// <summary>A site not begun yet within the scanner's range and four tiles.</summary>
    NewSite = 8,

    /// <summary>A training dummy.</summary>
    Dummy = 9,

    /// <summary>A mage (<see cref="CombatSettings.TargetMages"/>).</summary>
    Mage = 10,

    /// <summary>A barracks, church or workshop (<see cref="CombatSettings.TargetTrainers"/>).</summary>
    Trainer = 11,

    /// <summary>A castle, cathedral, guild, temple or mage tower (<see cref="CombatSettings.TargetSeats"/>).</summary>
    Seat = 12,

    /// <summary>A wall marked for a breach (the original's computer; Tzar2 marks none).</summary>
    MarkedWall = 13,
}

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

    /// <summary>
    /// Experience curve (ReTzar's reading of the original, 0x5022e0): the first point gives level 1, and level <c>k</c>
    /// costs <c>LevelBaseXp + LevelStepXp × (k − 1)</c> more for the next.
    /// </summary>
    public int LevelBaseXp { get; init; } = 100;

    /// <summary>
    /// How much each level costs more than the one before (<see cref="LevelBaseXp"/>); -1, the default, is the same as
    /// <see cref="LevelBaseXp"/>, the original's curve (level <c>k</c> costs <c>base × k</c>); 0 makes every level cost the same.
    /// </summary>
    public int LevelStepXp { get; init; } = -1;

    /// <summary><see cref="LevelStepXp"/>, with -1 resolved to <see cref="LevelBaseXp"/>.</summary>
    public int LevelStep => LevelStepXp < 0 ? LevelBaseXp : LevelStepXp;

    /// <summary>
    /// The share of their type's mana regeneration Nature's casters regain, in percent (ReTzar 0x502ea2: a neutral caster 1 a
    /// second, a kingdom's 2; ADR 0267). 100, the default, treats them like a kingdom's.
    /// </summary>
    public int NatureManaRegenPercent { get; init; } = 100;

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

    /// <summary>Hit points a crafted potion of health restores per point of power (UNK-25).</summary>
    public int CraftHealthPerPower { get; init; } = 10;

    /// <summary>Mana a crafted potion of mana restores per point of power (UNK-25).</summary>
    public int CraftManaPerPower { get; init; } = 5;

    /// <summary>Points of power a crafted potion needs per experience level (UNK-25).</summary>
    public int CraftPowerPerLevel { get; init; } = 5;

    /// <summary>Gold per point of power of a crafted item (UNK-25).</summary>
    public int CraftGoldPerPower { get; init; } = 50;

    /// <summary>Crafting time per point of power (UNK-25).</summary>
    public int CraftMsPerPower { get; init; } = 2000;

    /// <summary>Most power a crafted item may have.</summary>
    public int CraftMaxPower { get; init; } = 50;

    /// <summary>A crafted spell book's price, in percent of another item's of its power (1.05: four times).</summary>
    public int CraftBookCostPercent { get; init; } = 100;

    /// <summary>A crafted spell book's time, in percent of another item's of its power (1.05: twice).</summary>
    public int CraftBookTimePercent { get; init; } = 100;

    /// <summary>How long crafting an item of <paramref name="power"/> takes (a spell book: <see cref="CraftBookTimePercent"/> of it).</summary>
    public int CraftMs(bool book, int power)
        => (int)Math.Min((long)power * CraftMsPerPower * (book ? CraftBookTimePercent : 100) / 100, int.MaxValue);

    /// <summary>Bribe cost as a share of the unit's cost, in gold (percent; UNK-26).</summary>
    public int BribeCostPercent { get; init; } = 200;

    /// <summary>Crusade: the anchor type spawned (<c>CRUSADE_ANCHOR.INI</c>), or -1.</summary>
    public int CrusadeAnchor { get; init; } = -1;

    /// <summary>Crusade: the crusader type, or -1.</summary>
    public int Crusader { get; init; } = -1;

    /// <summary>
    /// Crusade (MAG-016, MAG-017): the crusaders a kingdom has gathered at the start. Its cathedrals gather more over time
    /// up to the ceiling, a crusade takes them all, and once its crusaders have all died the kingdom starts again from
    /// <see cref="CrusadeRestart"/> (ReTzar's reading of the original: 16, 4, ceiling 16 + 4 per full crusade, 36).
    /// </summary>
    public int CrusadeStart { get; init; } = 16;

    /// <summary>Crusade: the crusaders gathered again after a crusade's crusaders have all died.</summary>
    public int CrusadeRestart { get; init; } = 4;

    /// <summary>Crusade: how much each full crusade raises the ceiling of the gathered crusaders (from <see cref="CrusadeStart"/>).</summary>
    public int CrusadeCeilingStep { get; init; } = 4;

    /// <summary>Crusade: the highest ceiling of the gathered crusaders.</summary>
    public int CrusadeCeilingMax { get; init; } = 36;

    /// <summary>
    /// Crusade: how often each finished cathedral gathers a crusader, by the time played (MAG-017; ReTzar's reading of the
    /// original, 0x422ca0): every 12 s from the start, 8 s from minute 15, 4 s from 20, every second from 30. Ascending
    /// minutes, the first from 0.
    /// </summary>
    public ImmutableArray<CrusadeGatherStep> CrusadeGather { get; init; } = [new(0, 12), new(15, 8), new(20, 4), new(30, 1)];

    /// <summary>Crusade: the time between two crusaders arriving at the anchor (500 ms; ReTzar's reading of the original, 0x439d50).</summary>
    public int CrusaderArrivalMs { get; init; } = 500;

    /// <summary>Crusade: the anchor stands at most this far from the cathedral (TDD GAME-11: 4 tiles).</summary>
    public int CrusadeAnchorRadiusUnits { get; init; } = 4 * 2048;

    /// <summary>
    /// The spell a <see cref="AbilityFlags.Banshee"/> unit casts for free after a blow, or -1 (WAR-009: the ghost curses the
    /// enemies around it, ReTzar's reading of the original, 0x4ee780).
    /// </summary>
    public int BansheeSpell { get; init; } = -1;

    /// <summary>
    /// A unit that can murder and has at least this much mana, ordered to attack a unit, murders it with its first blow
    /// (MAG-028; ReTzar 0x4eb950: a ninja with 60); 0, the default, never.
    /// </summary>
    public int MurderBlowMana { get; init; }

    /// <summary>The banshee scream follows a blow when a draw below <see cref="BansheeOutOf"/> is under this (71 of 1001).</summary>
    public int BansheeChance { get; init; }

    /// <summary>
    /// The types whose attacks are magic (mages, dragons, the Messiah, priests): the <see cref="AbilityFlags.MagicImmune"/>
    /// Messiah takes no damage from them (WAR-010; ReTzar's reading of the original, damage category 3), ascending.
    /// </summary>
    public ImmutableArray<ushort> MagicAttackers { get; init; } = [];

    /// <summary>The spell a soul item's berserk order casts on its bound unit (ITEM-010), or -1.</summary>
    public int SoulBerserkSpell { get; init; } = -1;

    /// <summary>
    /// The experience speeds (percent of a level's cost) the technologies give (Warfare 70, Tactics 50, Sun Tzu 35;
    /// ReTzar's reading of the original, 0x4d33d0): a kingdom has the lowest of those it researched (each needs the one
    /// before), 100 without.
    /// </summary>
    public ImmutableArray<ExperienceSpeed> ExperienceSpeeds { get; init; } = [];

    /// <summary>Training dummies teach one point of experience for this much damage they take (75; ReTzar, 0x4481e0).</summary>
    public int DummyDamagePerPoint { get; init; } = 75;

    /// <summary>Each finished building of these types raises its kingdom's level cap by <see cref="LevelCapPerBuilding"/> (academies), ascending.</summary>
    public ImmutableArray<ushort> LevelCapBuildings { get; init; } = [];

    /// <summary>Levels each <see cref="LevelCapBuildings"/> building adds to the cap (10; ReTzar, 0x401850).</summary>
    public int LevelCapPerBuilding { get; init; }

    /// <summary>Jewelry (TECH-030): the technology that lets a kingdom's markets sell and buy items, or -1.</summary>
    public int MarketTechnology { get; init; } = -1;

    /// <summary>Jewelry: the first offer comes this long after the market stands.</summary>
    public int MarketFirstMs { get; init; } = 1000;

    /// <summary>Jewelry: a new offer comes this often.</summary>
    public int MarketEveryMs { get; init; } = 60_000;

    /// <summary>Jewelry: the most offers on the list; a new one pushes out the oldest.</summary>
    public int MarketSize { get; init; } = 8;

    /// <summary>Jewelry: an offer costs this percent of its value in gold (a sale earns 100 %).</summary>
    public int MarketBuyPercent { get; init; } = 400;

    /// <summary>Jewelry: the kinds an offer is drawn from (each equally likely), each a list of variants (equally likely).</summary>
    public ImmutableArray<ImmutableArray<MarketVariant>> MarketOffers { get; init; } = [];

    /// <summary>Jewelry: the spells a book on offer is drawn from (equally likely), in the original's numbering.</summary>
    public ImmutableArray<ushort> MarketBookSpells { get; init; } = [];

    /// <summary>The range of the banshee scream's draw.</summary>
    public int BansheeOutOf { get; init; } = 1001;

    /// <summary>
    /// Tournaments (TECH-029; ReTzar's reading of the original, 0x4de900): the technology, or -1. A kingdom that has it
    /// pools the experience its units learn; each time the pool reaches <see cref="TournamentEvery"/> it empties and
    /// every <see cref="TournamentPupils"/> unit of the kingdom learns <see cref="TournamentGrant"/>.
    /// </summary>
    public int TournamentTechnology { get; init; } = -1;

    /// <summary>Tournaments: the pool that teaches the pupils (75).</summary>
    public int TournamentEvery { get; init; } = 75;

    /// <summary>Tournaments: what each pupil learns when the pool is full (1).</summary>
    public int TournamentGrant { get; init; } = 1;

    /// <summary>Tournaments: the types it teaches (Asian peasants, monks, ninjas and priests), ascending.</summary>
    public ImmutableArray<ushort> TournamentPupils { get; init; } = [];

    /// <summary>
    /// WAR-015 (ReTzar's reading of the original, 0x4129d0): the priority of each <see cref="TargetCategory"/> (1 to 13, at
    /// index category − 1) for a unit or tower choosing what to attack on its own; 0 is never chosen. The original's
    /// table for a human kingdom.
    /// </summary>
    public ImmutableArray<int> TargetPriority { get; init; } = [0, 2000, 3000, 4000, 4000, 6000, 7000, 8000, 500, 4000, 2800, 2200, 0];

    /// <summary>WAR-015: the priorities of the <see cref="SiegeScanners"/> (catapults), as <see cref="TargetPriority"/>.</summary>
    public ImmutableArray<int> SiegeTargetPriority { get; init; } = [4000, 5000, 1000, 6000, 3000, 2000, 2500, 7000, 0, 3000, 5500, 5200, 4000];

    /// <summary>WAR-015: the types that choose by <see cref="SiegeTargetPriority"/> (catapults), ascending.</summary>
    public ImmutableArray<ushort> SiegeScanners { get; init; } = [];

    /// <summary>WAR-015: the types of <see cref="TargetCategory.Mage"/> (mages), ascending.</summary>
    public ImmutableArray<ushort> TargetMages { get; init; } = [];

    /// <summary>WAR-015: the types of <see cref="TargetCategory.Trainer"/> (barracks, churches, workshops), ascending.</summary>
    public ImmutableArray<ushort> TargetTrainers { get; init; } = [];

    /// <summary>WAR-015: the types of <see cref="TargetCategory.Seat"/> (castles, cathedrals, guilds, temples, mage towers), ascending.</summary>
    public ImmutableArray<ushort> TargetSeats { get; init; } = [];

    /// <summary>
    /// WAR-017 (ReTzar, 0x413950): a unit or building that is hit, or a unit that takes on an enemy by itself, calls the
    /// fighters and towers of its kingdom within this many tiles (and its guards wherever they are) to answer.
    /// </summary>
    public int HelpRadiusTiles { get; init; } = 3;

    /// <summary>ORD-001 (ReTzar, 0x4e4f1f): guards stand within this many tiles of what they guard, and walk back beyond it.</summary>
    public int GuardRadiusTiles { get; init; } = 3;


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

    /// <summary>The number of target categories (WAR-015).</summary>
    public const int TargetCategories = 13;

    internal void Validate(int typeCount, int technologies)
    {
        Check(ExperiencePerDamagePercent, 0, 100_000);
        Check(ExperiencePerKill, 0, 1_000_000);
        Check(LevelBaseXp, 1, 1_000_000);
        Check(LevelStepXp, -1, 1_000_000);
        Check(MurderBlowMana, 0, 1_000_000);
        Check(NatureManaRegenPercent, 0, 1_000);
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
        Check(CraftHealthPerPower, 0, 100_000);
        Check(CraftManaPerPower, 0, 100_000);
        Check(CraftPowerPerLevel, 1, 10_000);
        Check(CraftMsPerPower, 0, 600_000);
        Check(CraftMaxPower, 1, 10_000);
        Check(CraftBookCostPercent, 1, 10_000);
        Check(CraftBookTimePercent, 1, 10_000);
        Check(BribeCostPercent, 0, 100_000);
        Check(CrusadeAnchor, -1, typeCount - 1);
        Check(Crusader, -1, typeCount - 1);
        Check(CrusadeStart, 0, 1000);
        Check(CrusadeRestart, 0, 1000);
        Check(CrusadeCeilingStep, 0, 1000);
        Check(CrusadeCeilingMax, 0, 1000);
        Check(CrusadeAnchorRadiusUnits, 0, 64 * 2048);
        Check(BansheeOutOf, 1, 1_000_000);
        Check(BansheeChance, 0, BansheeOutOf);
        Check(TournamentTechnology, -1, technologies - 1);
        Check(TournamentEvery, 1, 255);
        Check(TournamentGrant, 0, 1_000_000);
        if (TournamentPupils.IsDefault)
        {
            throw new ArgumentException("combat: the tournament pupils must not be default.");
        }

        if (MagicAttackers.IsDefault)
        {
            throw new ArgumentException("combat: the magic attackers must not be default.");
        }

        Check(DummyDamagePerPoint, 1, 1_000_000);
        Check(MarketTechnology, -1, technologies - 1);
        Check(MarketFirstMs, 0, 3_600_000);
        Check(MarketEveryMs, 1, 3_600_000);
        Check(MarketSize, 1, 8);
        Check(MarketBuyPercent, 0, 100_000);
        if (MarketOffers.IsDefault || MarketBookSpells.IsDefault)
        {
            throw new ArgumentException("combat: the market offers must not be default.");
        }

        foreach (ImmutableArray<MarketVariant> kind in MarketOffers)
        {
            if (kind.IsDefaultOrEmpty)
            {
                throw new ArgumentException("combat: a market offer kind needs a variant.");
            }

            foreach (MarketVariant v in kind)
            {
                Check(v.Item, 0, typeCount - 1);
                Check(v.Min, 0, 1_000_000);
                Check(v.Max, v.Min, 1_000_000);
            }
        }

        if (CrusadeGather.IsDefaultOrEmpty || CrusadeGather[0].FromMinute != 0)
        {
            throw new ArgumentException("combat: the crusade gathering steps must start at minute 0.");
        }

        for (int i = 0; i < CrusadeGather.Length; i++)
        {
            Check(CrusadeGather[i].FromMinute, i == 0 ? 0 : CrusadeGather[i - 1].FromMinute + 1, 100_000);
            Check(CrusadeGather[i].EverySeconds, 1, 3600);
        }

        Check(CrusaderArrivalMs, 1, 60_000);
        if (ExperienceSpeeds.IsDefault)
        {
            throw new ArgumentException("combat: the experience speeds must not be default.");
        }

        foreach (ExperienceSpeed speed in ExperienceSpeeds)
        {
            Check(speed.Technology, 0, technologies - 1);
            Check(speed.Percent, 1, 1000);
        }

        Check(LevelCapPerBuilding, 0, 10_000);
        if (LevelCapBuildings.IsDefault)
        {
            throw new ArgumentException("combat: the level cap buildings must not be default.");
        }

        foreach (ushort building in LevelCapBuildings)
        {
            Check(building, 0, typeCount - 1);
        }

        foreach (ushort attacker in MagicAttackers)
        {
            Check(attacker, 0, typeCount - 1);
        }

        foreach (ushort pupil in TournamentPupils)
        {
            Check(pupil, 0, typeCount - 1);
        }

        if (TargetPriority.Length != TargetCategories || SiegeTargetPriority.Length != TargetCategories)
        {
            throw new ArgumentException($"combat: the target priorities need {TargetCategories} entries.");
        }

        foreach (int p in TargetPriority.AsSpan())
        {
            Check(p, 0, 1_000_000);
        }

        foreach (int p in SiegeTargetPriority.AsSpan())
        {
            Check(p, 0, 1_000_000);
        }

        foreach (ImmutableArray<ushort> list in (ReadOnlySpan<ImmutableArray<ushort>>)[SiegeScanners, TargetMages, TargetTrainers, TargetSeats])
        {
            if (list.IsDefault)
            {
                throw new ArgumentException("combat: the target type lists must not be default.");
            }

            foreach (ushort t in list)
            {
                Check(t, 0, typeCount - 1);
            }
        }

        Check(HelpRadiusTiles, 0, 64);
        Check(GuardRadiusTiles, 0, 64);

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

/// <summary>From <paramref name="FromMinute"/> of the time played, a cathedral gathers a crusader every <paramref name="EverySeconds"/> (<see cref="CombatSettings.CrusadeGather"/>).</summary>
public sealed record CrusadeGatherStep(int FromMinute, int EverySeconds);

/// <summary>An experience speed a technology gives (<see cref="CombatSettings.ExperienceSpeeds"/>).</summary>
/// <param name="Technology">The technology id.</param>
/// <param name="Percent">Percent of a level's cost.</param>
public sealed record ExperienceSpeed(int Technology, int Percent);

/// <summary>
/// One variant of a market offer (TECH-030): an item of type <paramref name="Item"/> with the effect, look and a power drawn
/// from <paramref name="Min"/> to <paramref name="Max"/>; a <paramref name="Book"/> takes a spell from
/// <see cref="CombatSettings.MarketBookSpells"/> and charges by the time played instead.
/// </summary>
/// <param name="Item">The item type id.</param>
/// <param name="Effect">What it adds (power items and potions).</param>
/// <param name="Look">1 + the index of its look in the item's looks, 0 = the type's own.</param>
/// <param name="Min">Least power.</param>
/// <param name="Max">Most power.</param>
/// <param name="Book">A spell book.</param>
public sealed record MarketVariant(ushort Item, ItemEffect Effect, int Look, int Min, int Max, bool Book);
