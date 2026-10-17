using System;
using System.Runtime.InteropServices;
using Tzar2.Core.Ids;

namespace Tzar2.Sim.Events;

/// <summary>Kinds of <see cref="SimEvent"/> (REQ-ARCH-03: sound/effect/notification triggers for the presentation).</summary>
public enum SimEventKind : ushort
{
    /// <summary>Unused.</summary>
    None = 0,

    /// <summary>An entity was created (X, Y = position, Arg = type id).</summary>
    Spawned = 1,

    /// <summary>An entity was removed (Arg = type id).</summary>
    Destroyed = 2,

    /// <summary>A mover reached its target.</summary>
    Arrived = 3,

    /// <summary>A mover gave up: it made no progress after repeated re-planning (X, Y = position).</summary>
    Blocked = 4,

    /// <summary>A command was ignored at execution time (Arg = <see cref="CommandRejection"/>, X = command index in the tick).</summary>
    CommandRejected = 5,

    /// <summary>
    /// The ordered goal is not reachable for the unit (REQ-PATH-01, ORIG-03): it goes to the nearest reachable point
    /// instead (X, Y = that point; Arg = 1 if the unit cannot move at all from where it stands).
    /// </summary>
    GoalUnreachable = 6,
}

/// <summary>Why a command was ignored (REQ-SIM §8.5 step 1: invalid commands are ignored deterministically and logged).</summary>
public enum CommandRejection
{
    /// <summary>The command kind is unknown.</summary>
    UnknownKind = 1,

    /// <summary>The player slot is not in the match or not active.</summary>
    InvalidPlayer = 2,

    /// <summary>No listed entity exists, belongs to the player and can perform the order.</summary>
    NoValidEntities = 3,

    /// <summary>The target point is off the map.</summary>
    OutOfMap = 4,

    /// <summary>A parameter is out of range.</summary>
    InvalidArgument = 5,

    /// <summary>Debug/cheat commands are not allowed in this session (GAME-19).</summary>
    CheatsDisabled = 6,

    /// <summary>The command would create more entities than slots are left (<c>EntityRegistry.RemainingCapacity</c>).</summary>
    EntityLimit = 7,
}

/// <summary>One simulation event. Output only: events are not part of the state, hashes or snapshots.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct SimEvent
{
    /// <summary>Creates an event.</summary>
    public SimEvent(SimEventKind kind, byte player, EntityId entity, int x, int y, int arg)
    {
        Kind = kind;
        Player = player;
        Reserved = 0;
        Entity = entity;
        X = x;
        Y = y;
        Arg = arg;
    }

    /// <summary>What happened.</summary>
    public SimEventKind Kind { get; }

    /// <summary>The player concerned.</summary>
    public byte Player { get; }

    /// <summary>Padding, always 0.</summary>
    public byte Reserved { get; }

    /// <summary>The entity concerned, or <see cref="EntityId.None"/>.</summary>
    public EntityId Entity { get; }

    /// <summary>X in world units, or kind-specific.</summary>
    public int X { get; }

    /// <summary>Y in world units, or kind-specific.</summary>
    public int Y { get; }

    /// <summary>Kind-specific argument.</summary>
    public int Arg { get; }
}

/// <summary>The events of the current tick, in emission order (deterministic: systems emit in fixed order).</summary>
public sealed class SimEventStream
{
    private SimEvent[] _events = new SimEvent[256];
    private int _count;

    /// <summary>Events emitted so far this tick.</summary>
    public ReadOnlySpan<SimEvent> Events => _events.AsSpan(0, _count);

    /// <summary>Appends an event.</summary>
    public void Emit(in SimEvent simEvent)
    {
        if (_count == _events.Length)
        {
            Array.Resize(ref _events, _events.Length * 2);
        }

        _events[_count++] = simEvent;
    }

    /// <summary>Appends events in order.</summary>
    public void EmitRange(ReadOnlySpan<SimEvent> events)
    {
        foreach (SimEvent e in events)
        {
            Emit(e);
        }
    }

    /// <summary>Starts a new tick.</summary>
    public void Clear() => _count = 0;
}
