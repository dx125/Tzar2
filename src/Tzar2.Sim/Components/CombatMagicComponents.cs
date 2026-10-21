using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tzar2.Content.Compiled;
using Tzar2.Sim.Ecs;

namespace Tzar2.Sim.Components;

#pragma warning disable CA1051 // Hot components are plain data with public fields (REQ-SIM-08).

/// <summary>Flags of <see cref="Experience"/>.</summary>
[System.Flags]
public enum ExperienceFlags : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>A named hero (level ≥ <c>combat.heroLevel</c>; GAME-08).</summary>
    Hero = 1,
}

/// <summary>Experience of a unit with the <c>Experience</c> trait (GAME-08; ADR 0092).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Experience : IComponent
{
    /// <summary>Experience points (only grow up to the current level cap's threshold).</summary>
    public int Xp;

    /// <summary>Experience earned this tick, not yet counted (step 11 applies gain modifiers, Leadership and levels).</summary>
    public int PendingXp;

    /// <summary>Permanent attack added by potions.</summary>
    public int BonusAttack;

    /// <summary>Permanent defence added by potions.</summary>
    public int BonusDefence;

    /// <summary>Experience level.</summary>
    public ushort Level;

    /// <summary>Flags.</summary>
    public ExperienceFlags Flags;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved;
}

/// <summary>An entity inside a holder (a unit in a castle or ship) or an item in an inventory (GAME-09/10; ADR 0093).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Held : IComponent
{
    /// <summary>The holder (<c>EntityId.Value</c>); always alive (holders release or destroy what they hold first).</summary>
    public uint Holder;

    /// <summary>The unit's movement request serial when it boarded (restored with the next serial when it leaves).</summary>
    public int RequestSerial;
}

/// <summary>A holder's bookkeeping (completed holders; GAME-09).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct HolderState : IComponent
{
    /// <summary>Units inside (equals the number of <see cref="Held"/> entries naming this holder that are units).</summary>
    public int Count;

    /// <summary>Healing time accumulated toward the next hit point of the units inside.</summary>
    public int HealAccumMs;
}

/// <summary>What a unit is walking to do (GAME-09…11): board, pick an item up, give one, bribe, or a conversion timer.</summary>
public enum ErrandKind : byte
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Walk to <see cref="Errand.Target"/> (a holder) and enter it.</summary>
    Board = 1,

    /// <summary>Walk to <see cref="Errand.Target"/> (an item on the ground) and pick it up.</summary>
    Pickup = 2,

    /// <summary>Walk to <see cref="Errand.Target"/> (an own unit) and give it the item in slot <see cref="Errand.Arg"/>.</summary>
    Give = 3,

    /// <summary>Walk to <see cref="Errand.Target"/> (an enemy unit) and bribe it (Bribery).</summary>
    Bribe = 4,

    /// <summary>Turn into type <see cref="Errand.Arg"/> when <see cref="Errand.TimerMs"/> runs out (Jihad warriors from peasants).</summary>
    Convert = 5,

    /// <summary>Walk to <see cref="Errand.Target"/> (another kingdom's unit) and take its look (a spy's impersonate; ADR 0184).</summary>
    Impersonate = 6,
}

/// <summary>
/// A spy's disguise (MAG-012, ADR 0184): other kingdoms see a unit of <see cref="TypeId"/> owned by <see cref="Owner"/>,
/// and that kingdom's units do not pick it as a target. It keeps its own abilities and stats. Its meter
/// (<see cref="Mastery"/>, MAG-021) grows while it keeps the look.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Disguise : IComponent
{
    /// <summary>The type it looks like.</summary>
    public ushort TypeId;

    /// <summary>The kingdom it seems to belong to.</summary>
    public byte Owner;

    /// <summary>
    /// The disguise meter by the portrait, 0-100 (MAG-021): it fills in <c>DisguiseMasteryMs</c>, and another kingdom's
    /// spy must come within (100 − meter) % of its detector radius to unmask it.
    /// </summary>
    public byte Mastery;
}

/// <summary>A unit's errand (ADRs 0093-0095).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Errand : IComponent
{
    /// <summary>Target entity (<c>EntityId.Value</c>), 0 = none; may be stale.</summary>
    public uint Target;

    /// <summary>Kind-specific argument (inventory slot, conversion type).</summary>
    public int Arg;

    /// <summary>Remaining time of a conversion.</summary>
    public int TimerMs;

    /// <summary>Tick of the last movement request toward the target (re-requested when it moves), -1 = none.</summary>
    public int MoveTick;

    /// <summary>Movement attempts without reaching the target.</summary>
    public int Retries;

    /// <summary>The errand.</summary>
    public ErrandKind Kind;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved0;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved1;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved2;
}

/// <summary>Item slots of a unit's inventory (<see cref="Ruleset.InventoryLimit"/>).</summary>
[InlineArray(Ruleset.InventoryLimit)]
public struct InventorySlots
{
    private uint _item;
}

/// <summary>The items a unit carries (GAME-10; ADR 0094): slots 0..Count-1 hold item entities.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Inventory : IComponent
{
    /// <summary>The carried items (<c>EntityId.Value</c>), first <see cref="Count"/> slots; the rest 0.</summary>
    public InventorySlots Items;

    /// <summary>Items carried.</summary>
    public byte Count;

    /// <summary>The default item's slot (Backspace uses it), 0xFF = none.</summary>
    public byte Default;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved0;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved1;

    /// <summary>No default item.</summary>
    public const byte NoDefault = 0xFF;
}

/// <summary>The state of one item entity (GAME-10).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ItemState : IComponent
{
    /// <summary>Magnitude: attack of a weapon, defence of armor, amount of a resource item (defaults to the type's power).</summary>
    public int Power;

    /// <summary>Uses left of a spell book (0 with unlimited charges).</summary>
    public int Charges;

    /// <summary>Tick from which a spell book may be used again.</summary>
    public int ReadyTick;
}

/// <summary>A projectile in flight (GAME-07; ADR 0091): an entity with this component only (no identity, never a target).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Projectile : IComponent
{
    /// <summary>Current position.</summary>
    public int X;

    /// <summary>Current position.</summary>
    public int Y;

    /// <summary>Where it lands (ground-aimed) or the target's last position (homing).</summary>
    public int TargetX;

    /// <summary>Where it lands.</summary>
    public int TargetY;

    /// <summary>The target (<c>EntityId.Value</c>), 0 = a point.</summary>
    public uint Target;

    /// <summary>Who fired it (credited with damage and experience while alive), <c>EntityId.Value</c>.</summary>
    public uint Attacker;

    /// <summary>The attack of the shooter when it fired (before the target's defence).</summary>
    public int Attack;

    /// <summary>The shooter's highest damage roll (<c>max_damage</c>), 0 = fixed.</summary>
    public int MaxAttack;

    /// <summary>The shooter's type (damage bonuses).</summary>
    public ushort AttackerType;

    /// <summary>The spell it carries (freeze bolt, flame bolt), 0xFFFF = a plain hit.</summary>
    public ushort Spell;

    /// <summary>The projectile kind (<see cref="Ruleset.Projectiles"/>).</summary>
    public byte Kind;

    /// <summary>The shooter's owner.</summary>
    public byte Owner;

    /// <summary>The shooter's damage class.</summary>
    public DamageClass DamageClass;

    /// <summary>1 when the shooter was a hero (Honor).</summary>
    public byte FromHero;

    /// <summary>No spell.</summary>
    public const ushort NoSpell = 0xFFFF;
}

/// <summary>An area spell in progress (rain of fire, whirlpool; GAME-11): an entity with this component only.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AreaEffect : IComponent
{
    /// <summary>Center.</summary>
    public int X;

    /// <summary>Center.</summary>
    public int Y;

    /// <summary>Time left.</summary>
    public int RemainingMs;

    /// <summary>Time accumulated toward the next pulse.</summary>
    public int PulseAccumMs;

    /// <summary>The caster (<c>EntityId.Value</c>; credited while alive).</summary>
    public uint Caster;

    /// <summary>The spell.</summary>
    public ushort Spell;

    /// <summary>The caster's owner.</summary>
    public byte Owner;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved;
}

/// <summary>Lasting spell effects on a unit (GAME-11): each ends at a tick (0 = not active). Removed when none is active.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct StatusEffects : IComponent
{
    /// <summary>Frozen until this tick: cannot move or act.</summary>
    public int FrozenUntil;

    /// <summary>Stone until this tick: cannot move or act and takes no damage.</summary>
    public int StoneUntil;

    /// <summary>Invisible until this tick (until it attacks or casts).</summary>
    public int CloakUntil;

    /// <summary>Confused until this tick: hostile to everyone.</summary>
    public int ConfuseUntil;

    /// <summary>Blessed until this tick.</summary>
    public int BlessUntil;

    /// <summary>Cursed until this tick.</summary>
    public int CurseUntil;

    /// <summary>Attack bonus of the blessing (percent).</summary>
    public short BlessPercent;

    /// <summary>Attack and defence malus of the curse (percent).</summary>
    public short CursePercent;
}

/// <summary>Phase of a cast.</summary>
public enum CastPhase : byte
{
    /// <summary>Not casting.</summary>
    None = 0,

    /// <summary>Walking into range of the target.</summary>
    Approach = 1,

    /// <summary>In range, the cast time running.</summary>
    Casting = 2,
}

/// <summary>A caster's spell in progress and its cooldown (GAME-11; ADR 0095).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Caster : IComponent
{
    /// <summary>Target unit (<c>EntityId.Value</c>), 0 = a point or self.</summary>
    public uint Target;

    /// <summary>Target point.</summary>
    public int X;

    /// <summary>Target point.</summary>
    public int Y;

    /// <summary>Cast time left.</summary>
    public int CastLeftMs;

    /// <summary>Tick from which the caster may cast again.</summary>
    public int ReadyTick;

    /// <summary>The spell book cast from (<c>EntityId.Value</c>), 0 = the caster's own spell.</summary>
    public uint Book;

    /// <summary>Tick of the last approach move request, -1 = none.</summary>
    public int MoveTick;

    /// <summary>The spell being cast.</summary>
    public ushort Spell;

    /// <summary>The phase.</summary>
    public CastPhase Phase;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved;
}

/// <summary>How an armed unit fights (orders <c>stand_ground</c>, <c>hold_steady</c>, <c>holdfire</c>/<c>fireatwill</c>; ADR 0091).</summary>
public enum CombatStance : byte
{
    /// <summary>Attacks enemies in sight and chases them (default; no stance component).</summary>
    Aggressive = 0,

    /// <summary>Attacks what is in reach but never moves to fight (stand ground, hold steady).</summary>
    StandGround = 1,

    /// <summary>Never attacks by itself (hold fire; towers); explicit attack orders still apply.</summary>
    HoldFire = 2,
}

/// <summary>A non-default combat stance.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct StanceState : IComponent
{
    /// <summary>The stance.</summary>
    public CombatStance Value;
}

/// <summary>An item being crafted (Magic Workshop; GAME-10).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CraftJob : IComponent
{
    /// <summary>Item type.</summary>
    public int ItemType;

    /// <summary>Power the item will have.</summary>
    public int Power;

    /// <summary>Time left.</summary>
    public int LeftMs;
}

/// <summary>A crusade anchor's state (GAME-11; ADR 0095): its crusaders and the cooldown after they die.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CrusadeState : IComponent
{
    /// <summary>Crusaders still to raise.</summary>
    public int ToRaise;

    /// <summary>Tick at which the anchor goes away once its crusaders are dead (cooldown), 0 = crusaders alive.</summary>
    public int EndTick;
}

/// <summary>A crusader raised by an anchor.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CrusaderOf : IComponent
{
    /// <summary>The anchor (<c>EntityId.Value</c>; may be gone).</summary>
    public uint Anchor;
}

#pragma warning restore CA1051
