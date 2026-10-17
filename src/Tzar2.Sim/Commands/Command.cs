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
    /// at placement; or, with <see cref="Command.Target"/> set, help build that existing construction site.
    /// </summary>
    Build = 7,

    /// <summary>Queue Arg1 (1-16) units of type Arg0 in each listed producer, paid when queued (GAME-05).</summary>
    Train = 8,

    /// <summary>
    /// Cancel: a listed construction site is removed with a refund; a listed producer drops queue item Arg0 (-1 = the last)
    /// with a refund.
    /// </summary>
    Cancel = 9,

    /// <summary>Set the rally point of the listed producers to (X, Y); Arg0 = 1 clears it.</summary>
    SetRally = 10,

    /// <summary>Attack <see cref="Command.Target"/> until it dies (GAME-07).</summary>
    Attack = 11,

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
