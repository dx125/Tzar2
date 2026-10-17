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

    /// <summary>Gathering time accumulated toward the next unit.</summary>
    public int TimerMs;

    /// <summary>Where the last node was (the search center for the next node of the same resource).</summary>
    public int NodeX;

    /// <summary>Where the last node was.</summary>
    public int NodeY;

    /// <summary>Movement attempts since the last progress (gives up after a few).</summary>
    public int Retries;

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
}

/// <summary>A building under construction (GAME-03): progress toward its build time (ADR 0036).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Construction : IComponent
{
    /// <summary>Build time done.</summary>
    public int ProgressMs;

    /// <summary>Build time needed (the type's <c>BuildTime</c>).</summary>
    public int TotalMs;
}

/// <summary>Queued unit type ids of a producer.</summary>
[InlineArray(ProducerDefinition.MaxQueueCapacity)]
public struct ProductionQueue
{
    private ushort _item;
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

    /// <summary>Items queued.</summary>
    public byte Count;

    /// <summary>1 when a rally point is set.</summary>
    public byte HasRally;

    /// <summary>Why the finished first item waits (<see cref="ProductionStall"/>), 0 = not waiting.</summary>
    public byte Stall;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved;

    /// <summary>The queued type ids, first = in training.</summary>
    public ProductionQueue Queue;
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
