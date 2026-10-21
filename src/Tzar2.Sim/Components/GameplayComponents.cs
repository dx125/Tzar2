using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tzar2.Content.Compiled;
using Tzar2.Sim.Ecs;

namespace Tzar2.Sim.Components;

#pragma warning disable CA1051 // Hot components are plain data with public fields (REQ-SIM-08).

/// <summary>Why an armed entity has its target (ADR 0038).</summary>
public enum CombatMode : byte
{
    /// <summary>No target, or a target picked by itself (see <see cref="Auto"/>).</summary>
    None = 0,

    /// <summary>Ordered to attack this target (Attack command): chases it until it dies or another order comes.</summary>
    Explicit = 1,

    /// <summary>Picked the target itself while idle (auto-acquire in sight, or retaliation).</summary>
    Auto = 2,

    /// <summary>Move-advance (Ctrl + move, TDD §9.3): attacks what it meets, then resumes the move order.</summary>
    AttackMove = 3,

    /// <summary>Attack ground (order attack_ground; ADR 0091): fires at the point <see cref="Combat.ChaseX"/>, <see cref="Combat.ChaseY"/>.</summary>
    Ground = 4,

    /// <summary>
    /// Ordered by the scenario to fight this target (an Object Order "attack" on an object, ADR 0135): stances do not
    /// matter, so a script can stage a duel between allies; ends like <see cref="Explicit"/>.
    /// </summary>
    Scripted = 5,
}

/// <summary>
/// Attack state of an entity with an <c>Armament</c> (GAME-07, pipeline step 9): the target, the swing timers (REQ-SIM-02:
/// millisecond accumulators with carry-over) and the last chase request.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Combat : IComponent
{
    /// <summary>Target (<c>EntityId.Value</c>), 0 = none. May be stale; the combat system re-validates it every tick.</summary>
    public uint Target;

    /// <summary>Time until the next swing may start; ≤ 0 = ready.</summary>
    public int CooldownMs;

    /// <summary>Time until the pending hit lands (the hit frame, UNK-07); -1 = no swing in progress.</summary>
    public int HitInMs;

    /// <summary>Target position when the last chase move was requested.</summary>
    public int ChaseX;

    /// <summary>Target position when the last chase move was requested.</summary>
    public int ChaseY;

    /// <summary>Tick of the last chase request, -1 = none (chase requests are throttled).</summary>
    public int ChaseTick;

    /// <summary>Why the entity has its target.</summary>
    public CombatMode Mode;

    /// <summary>1 when a fight interrupted a move-advance, which resumes once the unit is free again.</summary>
    public byte Resume;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved0;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved1;
}

/// <summary>A worker's current task (GAME-02/03).</summary>
public enum WorkerTask : byte
{
    /// <summary>No task.</summary>
    None = 0,

    /// <summary>Gather from <see cref="Worker.Target"/> and carry to drop-offs.</summary>
    Harvest = 1,

    /// <summary>Construct <see cref="Worker.Target"/>.</summary>
    Build = 2,

    /// <summary>Repair <see cref="Worker.Target"/> (own damaged building, ship or siege weapon; GAME-03).</summary>
    Repair = 3,

    /// <summary>Capture <see cref="Worker.Target"/> (enemy building, or a cow under Cow Poaching; GAME-04).</summary>
    Capture = 4,
}

/// <summary>Step of a worker's task.</summary>
public enum WorkerPhase : byte
{
    /// <summary>No task.</summary>
    None = 0,

    /// <summary>Walking to the resource node.</summary>
    ToNode = 1,

    /// <summary>At the node, gathering one unit per gather time.</summary>
    Gathering = 2,

    /// <summary>Carrying the load to <see cref="Worker.DropTarget"/> (found when 0).</summary>
    ToDropOff = 3,

    /// <summary>Walking to the construction site.</summary>
    ToSite = 4,

    /// <summary>At the site, adding construction progress.</summary>
    Building = 5,

    /// <summary>Walking to the entity to repair or capture.</summary>
    ToTarget = 6,

    /// <summary>At the entity, repairing it.</summary>
    Repairing = 7,

    /// <summary>At the building, digging to capture it.</summary>
    Capturing = 8,
}

/// <summary>Task state of a peasant (traits <c>Harvester</c>, <c>Builder</c>; ADRs 0035, 0036).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Worker : IComponent
{
    /// <summary>Resource node or construction site (<c>EntityId.Value</c>), 0 = none; may be stale.</summary>
    public uint Target;

    /// <summary>Drop-off building (<c>EntityId.Value</c>), 0 = not chosen yet; may be stale.</summary>
    public uint DropTarget;

    /// <summary>Units carried.</summary>
    public int CarryAmount;

    /// <summary>
    /// Gathering time accumulated toward the whole load (one unit at a time for milk; ADR 0180); while repairing, the
    /// hit-point remainder (× build time).
    /// </summary>
    public int TimerMs;

    /// <summary>Where the last node was (the search center for the next node of the same resource).</summary>
    public int NodeX;

    /// <summary>Where the last node was.</summary>
    public int NodeY;

    /// <summary>Movement attempts since the last progress (gives up after a few).</summary>
    public int Retries;

    /// <summary>How long the worker has stood idle without a task (it finds work after <c>idleWorkAfterMs</c>; ADR 0180).</summary>
    public int IdleMs;

    /// <summary>The task.</summary>
    public WorkerTask Task;

    /// <summary>The step of the task.</summary>
    public WorkerPhase Phase;

    /// <summary>Resource type id of the load, <see cref="NoResource"/> when empty.</summary>
    public byte CarryResource;

    /// <summary>Resource type id the harvest order is for, <see cref="NoResource"/> if none.</summary>
    public byte NodeResource;

    /// <summary>Value of <see cref="CarryResource"/>/<see cref="NodeResource"/> meaning none.</summary>
    public const byte NoResource = 0xFF;
}

/// <summary>What is left in a resource node (trait <c>ResourceNode</c>); -1 = unlimited (fields).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ResourceStock : IComponent
{
    /// <summary>Units left, or <see cref="ResourceNodeDefinition.Unlimited"/>.</summary>
    public int Amount;

    /// <summary>Time accumulated toward regrowing the next unit (cows' milk, <c>ResourceNode.regenMs</c>).</summary>
    public int RegenMs;
}

/// <summary>A building under construction (GAME-03): progress toward its build time (ADR 0036).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Construction : IComponent
{
    /// <summary>Build time done.</summary>
    public int ProgressMs;

    /// <summary>Build time needed (the type's <c>BuildTime</c>).</summary>
    public int TotalMs;

    /// <summary>Remainder of sped-up progress in 1/10,000 ms (Architecture's build rate, ADR 0040), 0..9,999.</summary>
    public int CarryBasisPoints;

    /// <summary>The cost modifier the site was paid with (a cancel refunds what was paid).</summary>
    public PaidModifier Paid;
}

/// <summary>
/// One entry of a production queue (ADR 0180): an item (<see cref="QueueItems"/>), how many of it are queued, and the
/// cost modifier they were paid with (research and upgrades: none).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct QueueRun
{
    /// <summary>The item (<see cref="QueueItems"/>).</summary>
    public ushort Item;

    /// <summary>How many are queued (at least 1 in a used entry).</summary>
    public ushort Count;

    /// <summary>The cost modifier each of them was paid with (a refund returns what was paid).</summary>
    public PaidModifier Paid;
}

/// <summary>The entries of a producer's queue, first = in progress.</summary>
[InlineArray(ProducerDefinition.MaxQueueCapacity)]
public struct ProductionQueue
{
    private QueueRun _run;
}

/// <summary>
/// The production queue's rules (ADR 0180; the owner's play-test of 2026-10-20: the original queues any number of any
/// items): one entry per item and payment, in the order the items were first ordered; any number of each.
/// </summary>
public static class QueueRuns
{
    /// <summary>How many of one item an entry holds at most.</summary>
    public const int MaxCount = ushort.MaxValue;

    /// <summary>How many items are queued in all.</summary>
    public static int Items(in Production q)
    {
        int n = 0;
        for (int k = 0; k < q.Count; k++)
        {
            n += q.Queue[k].Count;
        }

        return n;
    }

    /// <summary>The entry of <paramref name="item"/> paid with <paramref name="paid"/>, or −1.</summary>
    public static int Find(in Production q, ushort item, PaidModifier paid)
    {
        for (int k = 0; k < q.Count; k++)
        {
            QueueRun run = q.Queue[k];
            if (run.Item == item && run.Paid.Add == paid.Add && run.Paid.BasisPoints == paid.BasisPoints)
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>Whether <paramref name="item"/> is queued at all.</summary>
    public static bool Contains(in Production q, ushort item)
    {
        for (int k = 0; k < q.Count; k++)
        {
            if (q.Queue[k].Item == item)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether one more <paramref name="item"/> paid with <paramref name="paid"/> fits (its entry, or a free one).</summary>
    public static bool HasRoom(in Production q, ushort item, PaidModifier paid, int capacity)
        => Find(q, item, paid) is var k && (k >= 0 ? q.Queue[k].Count < MaxCount : q.Count < capacity);

    /// <summary>Queues one <paramref name="item"/> (call <see cref="HasRoom"/> first).</summary>
    public static void Add(ref Production q, ushort item, PaidModifier paid)
    {
        int k = Find(q, item, paid);
        if (k < 0)
        {
            k = q.Count++;
            q.Queue[k] = new QueueRun { Item = item, Count = 0, Paid = paid };
        }

        q.Queue[k].Count++;
    }

    /// <summary>
    /// Takes one item off entry <paramref name="run"/> (the last ordered of them; the first entry's item in progress goes
    /// last) and returns it; an emptied entry leaves the queue, and the progress restarts when the first entry empties.
    /// </summary>
    public static QueueRun TakeOne(ref Production q, int run)
    {
        QueueRun taken = q.Queue[run];
        taken.Count = 1;
        if (--q.Queue[run].Count > 0)
        {
            return taken;
        }

        for (int k = run + 1; k < q.Count; k++)
        {
            q.Queue[k - 1] = q.Queue[k];
        }

        q.Count--;
        q.Queue[q.Count] = default;
        if (run == 0)
        {
            q.ProgressMs = 0;
            q.Stall = 0;
        }

        return taken;
    }
}

/// <summary>
/// The encoding of production queue items (ADR 0040): below <see cref="UpgradeFlag"/> a unit type id
/// (<see cref="Ruleset.MaxEntityTypes"/>), <see cref="UpgradeFlag"/> | upgrade id, <see cref="ResearchFlag"/> |
/// technology id.
/// </summary>
public static class QueueItems
{
    /// <summary>Tag of an upgrade item.</summary>
    public const ushort UpgradeFlag = 0x4000;

    /// <summary>Tag of a research item.</summary>
    public const ushort ResearchFlag = 0x8000;

    /// <summary>The item of a technology.</summary>
    public static ushort Research(int technology) => (ushort)(ResearchFlag | technology);

    /// <summary>The item of an upgrade.</summary>
    public static ushort Upgrade(int upgrade) => (ushort)(UpgradeFlag | upgrade);

    /// <summary>Whether the item is a research.</summary>
    public static bool IsResearch(ushort item) => (item & ResearchFlag) != 0;

    /// <summary>Whether the item is an upgrade.</summary>
    public static bool IsUpgrade(ushort item) => (item & (ResearchFlag | UpgradeFlag)) == UpgradeFlag;

    /// <summary>The technology, upgrade or unit type id of the item.</summary>
    public static int Id(ushort item) => item & 0x3FFF;
}

/// <summary>What a unit does when it reaches the rally point (TDD §9.3; GAME-05).</summary>
public enum RallyMode : byte
{
    /// <summary>Ctrl + right-click: a plain move.</summary>
    Move = 0,

    /// <summary>Alt + right-click: the default action on the rally target (harvest it, build it, attack it), else a move.</summary>
    DefaultAction = 1,

    /// <summary>Ctrl + Alt + right-click: a move-advance (attack what it meets).</summary>
    MoveAdvance = 2,
}

/// <summary>Training queue and rally point of a completed producer building (GAME-05; ADR 0037).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Production : IComponent
{
    /// <summary>Training time done on the first item.</summary>
    public int ProgressMs;

    /// <summary>Rally point X (valid when <see cref="HasRally"/> is 1).</summary>
    public int RallyX;

    /// <summary>Rally point Y.</summary>
    public int RallyY;

    /// <summary>Rally target (<c>EntityId.Value</c>) for <see cref="RallyMode.DefaultAction"/>, 0 = none; may be stale.</summary>
    public uint RallyTarget;

    /// <summary>Queue entries used (<see cref="QueueRuns"/>).</summary>
    public byte Count;

    /// <summary>1 when a rally point is set.</summary>
    public byte HasRally;

    /// <summary>Why the finished first item waits (<see cref="ProductionStall"/>), 0 = not waiting.</summary>
    public byte Stall;

    /// <summary>What units do at the rally point (<see cref="Components.RallyMode"/>).</summary>
    public RallyMode RallyMode;

    /// <summary>The queue's entries, first = in progress.</summary>
    public ProductionQueue Queue;
}

/// <summary>Level upgrades an entity has bought (bit = upgrade id; ship cannon levels; GAME-06).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct UpgradeLevels : IComponent
{
    /// <summary>Bought level upgrades.</summary>
    public uint Mask;
}

/// <summary>Capture progress on a building peasants are digging at (GAME-04; ADR 0044).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CaptureState : IComponent
{
    /// <summary>Capture points dug so far (milli-points: 1,000 per point).</summary>
    public int MilliPoints;

    /// <summary>Time since the last tick with a capturer at work.</summary>
    public int IdleMs;

    /// <summary>The capturing player.</summary>
    public byte Player;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved0;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved1;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved2;
}

/// <summary>How a gate opens (GAME-03; orders gateopen/gateclose, gate_subai_on/off).</summary>
public enum GateMode : byte
{
    /// <summary>Opened and closed by orders only.</summary>
    Manual = 0,

    /// <summary>Opens while one of its owner's units is near, closes when none is (the default).</summary>
    Automatic = 1,
}

/// <summary>
/// Whether a gate is open (its passage tiles are free), what it should become, and how it is controlled. A gate changes
/// state at most once per <c>gateChangeIntervalMs</c> (ADR 0043: bounded navigation work per tick).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GateState : IComponent
{
    /// <summary>1 = open.</summary>
    public byte Open;

    /// <summary>Control mode.</summary>
    public GateMode Mode;

    /// <summary>1 = should be open (manual orders and the automatic rule set it; step 10 applies it).</summary>
    public byte Desired;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved;

    /// <summary>Tick of the last change of <see cref="Open"/> (or the order that set <see cref="Desired"/>).</summary>
    public int LastChangeTick;
}

/// <summary>
/// Repair bookkeeping of an entity being repaired (GAME-03; ADR 0043): hit points restored so far (the pro-rata repair
/// cost is charged from it without a per-resource remainder) and the hit-point remainder of the repair rate.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RepairProgress : IComponent
{
    /// <summary>Hit points restored by repairs so far.</summary>
    public int HealedHp;

    /// <summary>Remainder of the repair rate (numerator units, below the denominator of the rate).</summary>
    public int Carry;
}

/// <summary>The cost modifier a player paid with (flat add and basis points of <c>ModifierStat.Cost</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PaidModifier
{
    /// <summary>Flat add to every cost component.</summary>
    public int Add;

    /// <summary>Basis points on every cost component.</summary>
    public int BasisPoints;
}

/// <summary>A building on fire (GAME-03): fire damage accumulates per tick (UNK-42).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Burning : IComponent
{
    /// <summary>Fire time accumulated toward the next lost hit point.</summary>
    public int AccumMs;
}

/// <summary>Neglect of an entity with <c>Decay</c> (fields; GAME-02).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Neglect : IComponent
{
    /// <summary>Time since a worker last gathered from it.</summary>
    public int UntendedMs;

    /// <summary>Decay time accumulated toward the next lost hit point.</summary>
    public int AccumMs;
}

/// <summary>Mana of a caster (trait <c>ManaPool</c>; GAME-01), in milli-mana so regeneration carries over.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ManaState : IComponent
{
    /// <summary>Mana × 1,000.</summary>
    public int MilliMana;
}

/// <summary>An entity that disappears at a tick (trait <c>Lifetime</c>: meat rots).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Lifetime : IComponent
{
    /// <summary>The tick whose cleanup removes it.</summary>
    public int ExpiresTick;
}

/// <summary>Why a finished unit cannot leave its building yet (<c>ProductionStalled</c> event argument).</summary>
public enum ProductionStall : byte
{
    /// <summary>Not stalled.</summary>
    None = 0,

    /// <summary>The population cap is reached (GAME-05).</summary>
    Population = 1,

    /// <summary>No free tile around the building to put the unit on.</summary>
    NoExit = 2,

    /// <summary>The entity limit is reached.</summary>
    EntityLimit = 3,
}

#pragma warning restore CA1051
