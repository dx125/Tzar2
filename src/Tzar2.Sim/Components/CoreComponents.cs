using System;
using System.Runtime.InteropServices;
using Tzar2.Core.Serialization;
using Tzar2.Sim.Ecs;

namespace Tzar2.Sim.Components;

#pragma warning disable CA1051 // Hot components are plain data with public fields (REQ-SIM-08).

/// <summary>Position in world units (1 px = 64 units, 1 tile = 2,048 units; TDD §8.2).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Position : IComponent
{
    /// <summary>X, growing east.</summary>
    public int X;

    /// <summary>Y, growing south.</summary>
    public int Y;

    /// <summary>Creates a position.</summary>
    public Position(int x, int y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>What an entity is and who owns it.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Identity : IComponent
{
    /// <summary>Index into <c>Ruleset.EntityTypes</c>.</summary>
    public ushort TypeId;

    /// <summary>Owning player slot.</summary>
    public byte Owner;

    /// <summary>8-direction facing (0 = east, clockwise; ADR 0005).</summary>
    public byte Facing;
}

/// <summary>Hit points.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Health : IComponent
{
    /// <summary>Current hit points.</summary>
    public int Current;

    /// <summary>Maximum hit points.</summary>
    public int Max;
}

/// <summary>State of <see cref="Motion"/>.</summary>
public enum MotionState
{
    /// <summary>Not moving.</summary>
    Idle = 0,

    /// <summary>Moving toward the target.</summary>
    Moving = 1,

    /// <summary>Stopped by impassable terrain (the placeholder mover does not path around obstacles).</summary>
    Blocked = 2,
}

/// <summary>
/// Straight-line movement order (M2 placeholder until pathfinding, §9.2, replaces the target with a path cursor).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Motion : IComponent
{
    /// <summary>Target X in world units.</summary>
    public int TargetX;

    /// <summary>Target Y in world units.</summary>
    public int TargetY;

    /// <summary>Distance per tick in world units.</summary>
    public int SpeedPerTick;

    /// <summary>Current state.</summary>
    public MotionState State;
}

/// <summary>The occupancy cell an entity is counted in (TDD §9.1), or -1.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct OccupancyCell : IComponent
{
    /// <summary>Cell index in <c>MapState</c>'s occupancy grid.</summary>
    public int Cell;
}

#pragma warning restore CA1051

/// <summary>Cold component: a display or script name (heroes, named campaign units).</summary>
public readonly struct EntityName : IColdComponent<EntityName>
{
    /// <summary>Maximum name length in UTF-8 bytes.</summary>
    public const int MaxBytes = 256;

    /// <summary>Creates a name.</summary>
    public EntityName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>The name.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public static void Write(BinaryBufferWriter writer, in EntityName value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(value.Value ?? string.Empty);
    }

    /// <inheritdoc />
    public static EntityName Read(ref BinaryBufferReader reader) => new(reader.ReadString(MaxBytes));
}
