using System;
using System.Runtime.InteropServices;
using Tzar2.Core.Ids;

namespace Tzar2.Sim.Commands;

/// <summary>
/// Command kinds (REQ-SIM-11). Values are wire format: never renumber, only append. Ranges: 1-0x0FFF orders mirroring
/// <c>ORDERS.INI</c> (filled in with the gameplay systems), 0xF000-0xFFFF debug/cheat commands (GAME-19).
/// </summary>
public enum CommandKind : ushort
{
    /// <summary>Invalid.</summary>
    None = 0,

    /// <summary>Stop the listed units.</summary>
    Stop = 1,

    /// <summary>
    /// Move the listed units to (X, Y) (TDD §9.3 right-click). Units of one command that share a locomotor form a
    /// group: arrival slots around the goal (REQ-PATH-05) and, from 6 units, a shared flow field (REQ-PATH-04).
    /// </summary>
    Move = 2,

    /// <summary>Delete the listed units (the original's Del key).</summary>
    Delete = 3,

    /// <summary>Patrol between each unit's current position and (X, Y) until given another order.</summary>
    Patrol = 4,

    /// <summary>Follow <see cref="Command.Target"/>: keep re-pathing to it and stay close (guard/escort base order).</summary>
    Follow = 5,

    /// <summary>Gather from resource node <see cref="Command.Target"/> and carry to the nearest drop-off (GAME-02).</summary>
    Harvest = 6,

    /// <summary>
    /// Construct (GAME-03): a new building of type Arg0 with its footprint's top-left at the tile containing (X, Y), paid
    /// at placement; Arg1 = 1 turns the footprint by 90° (gates and bridges, GAME-03). With <see cref="Command.Target"/>
    /// set, help build that existing construction site.
    /// </summary>
    Build = 7,

    /// <summary>Queue Arg1 (1-16) units of type Arg0 in each listed producer, paid when queued (GAME-05).</summary>
    Train = 8,

    /// <summary>
    /// Cancel: a listed construction site is removed with a refund; a listed producer drops queue item Arg0 (-1 = the last)
    /// with a refund.
    /// </summary>
    Cancel = 9,

    /// <summary>
    /// Set the rally point of the listed producers to (X, Y); Arg0 = 1 clears it. Arg1 = <c>RallyMode</c> (0 move, 1 default
    /// action on <see cref="Command.Target"/>, 2 move-advance; TDD §9.3).
    /// </summary>
    SetRally = 10,

    /// <summary>Attack <see cref="Command.Target"/> until it dies (GAME-07). Own cows may be attacked: butchering for meat.</summary>
    Attack = 11,

    /// <summary>
    /// Research technology Arg0 (GAME-06): queued in the first listed own building that researches it, paid when queued;
    /// rejected if researched or queued anywhere already.
    /// </summary>
    Research = 12,

    /// <summary>Buy upgrade Arg0 for each listed own entity that offers it (towers, walls, gates, galleons), paid when queued.</summary>
    Upgrade = 13,

    /// <summary>Repair own damaged building, ship or siege weapon <see cref="Command.Target"/> (GAME-03).</summary>
    Repair = 14,

    /// <summary>Capture enemy building <see cref="Command.Target"/> (GAME-04), or steal an enemy cow under Cow Poaching.</summary>
    Capture = 15,

    /// <summary>
    /// Drag a wall (GAME-03): segments of wall type Arg0 on the straight tile line from (X, Y) to the tile packed in
    /// Arg1 (<c>tileY &lt;&lt; 16 | tileX</c>), each a site paid when placed; tiles that do not fit are skipped.
    /// </summary>
    BuildWall = 16,

    /// <summary>Set the listed gates: Arg0 = 0 close, 1 open (both manual), 2 automatic (GAME-03).</summary>
    SetGate = 17,

    /// <summary>Debug: spawn Arg1 (1-64) units of type Arg0 at (X, Y); needs <see cref="SimulationConfig.CheatsAllowed"/>.</summary>
    DebugSpawn = 0xF000,

    /// <summary>Debug: kill the listed entities of any owner (leaving corpses); needs <see cref="SimulationConfig.CheatsAllowed"/>.</summary>
    DebugKill = 0xF001,
}

/// <summary>Input modifiers carried by a command (TDD §9.3: Shift/Ctrl/Alt variants of orders).</summary>
[Flags]
public enum CommandFlags : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Shift: queue (waypoints) instead of replacing the current order.</summary>
    Queue = 1,

    /// <summary>Ctrl: e.g. move-advance (attack-move: a Move with Ctrl attacks enemies met on the way).</summary>
    Ctrl = 2,

    /// <summary>Alt: e.g. move-sneak.</summary>
    Alt = 4,
}

/// <summary>
/// The fixed part of one command (32 bytes, unmanaged). The entity set lives in the owning <see cref="CommandList"/>
/// at <see cref="EntityOffset"/>..+<see cref="EntityCount"/>, sorted ascending and free of duplicates.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Command
{
    internal Command(CommandKind kind, byte player, CommandFlags flags, EntityId target, int x, int y, int arg0, int arg1, int entityOffset, int entityCount)
    {
        Kind = kind;
        Player = player;
        Flags = flags;
        Target = target;
        X = x;
        Y = y;
        Arg0 = arg0;
        Arg1 = arg1;
        EntityOffset = entityOffset;
        EntityCount = entityCount;
    }

    /// <summary>What to do.</summary>
    public CommandKind Kind { get; }

    /// <summary>Issuing player slot, stamped by the relay/session from the sender (ADR 0009), never trusted from the client.</summary>
    public byte Player { get; }

    /// <summary>Modifiers.</summary>
    public CommandFlags Flags { get; }

    /// <summary>Target entity, or <see cref="EntityId.None"/>.</summary>
    public EntityId Target { get; }

    /// <summary>Target X in world units.</summary>
    public int X { get; }

    /// <summary>Target Y in world units.</summary>
    public int Y { get; }

    /// <summary>Kind-specific argument (type id, count, mode...).</summary>
    public int Arg0 { get; }

    /// <summary>Kind-specific argument.</summary>
    public int Arg1 { get; }

    /// <summary>Start of the entity set in the owning list.</summary>
    public int EntityOffset { get; }

    /// <summary>Size of the entity set.</summary>
    public int EntityCount { get; }

    internal Command WithPlayer(byte player, int entityOffset)
        => new(Kind, player, Flags, Target, X, Y, Arg0, Arg1, entityOffset, EntityCount);
}
