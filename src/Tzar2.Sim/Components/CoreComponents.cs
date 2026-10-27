using System;
using System.Runtime.InteropServices;
using System.Text;
using Tzar2.Content.Compiled;
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
public enum MotionState : byte
{
    /// <summary>Not moving.</summary>
    Idle = 0,

    /// <summary>Executing a movement order (including waiting for its plan).</summary>
    Moving = 1,

    /// <summary>Gave up: no progress after repeated re-planning (stopped with a <c>Blocked</c> event).</summary>
    Blocked = 2,
}

/// <summary>How a moving unit is steered.</summary>
public enum MotionMode : byte
{
    /// <summary>Not steering.</summary>
    None = 0,

    /// <summary>Waiting for its path request (REQ-PATH-03).</summary>
    Waiting = 1,

    /// <summary>Following its own waypoints (<c>PathStore</c>).</summary>
    Path = 2,

    /// <summary>Descending its group's shared flow field toward <see cref="Motion.GoalTile"/> (REQ-PATH-04).</summary>
    Flow = 3,

    /// <summary>Straight to the target (final approach, air units).</summary>
    Direct = 4,

    /// <summary>
    /// Following an abstract route of portals (<c>PathStore</c>) by descending each portal's tree (long paths,
    /// REQ-PATH-02); <see cref="Motion.PathCursor"/> is the index of the portal being approached.
    /// </summary>
    Route = 5,
}

/// <summary>The order behind a unit's movement (TDD §9.3).</summary>
public enum MotionOrder : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Move to a point.</summary>
    Move = 1,

    /// <summary>Patrol between <see cref="Motion.PatrolX"/>/<see cref="Motion.PatrolY"/> and the order point.</summary>
    Patrol = 2,

    /// <summary>Follow <see cref="Motion.FollowTarget"/>.</summary>
    Follow = 3,
}

/// <summary>
/// Movement state of a mobile unit (TDD §9.2): the order, the resolved target (its arrival slot), the steering mode and
/// cursor, progress tracking for stall/deadlock handling, and last tick's velocity for avoidance.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Motion : IComponent
{
    /// <summary>Target X in world units: the order point, or the arrival slot assigned to the unit.</summary>
    public int TargetX;

    /// <summary>Target Y in world units.</summary>
    public int TargetY;

    /// <summary>Own speed in world units per tick.</summary>
    public int SpeedPerTick;

    /// <summary>Collision radius in world units.</summary>
    public int Radius;

    /// <summary>The point currently steered toward.</summary>
    public int SteerX;

    /// <summary>The point currently steered toward.</summary>
    public int SteerY;

    /// <summary>The point the order named.</summary>
    public int OrderX;

    /// <summary>The point the order named.</summary>
    public int OrderY;

    /// <summary>The other end of a patrol.</summary>
    public int PatrolX;

    /// <summary>The other end of a patrol.</summary>
    public int PatrolY;

    /// <summary>Flow-field goal tile (Flow mode), else -1.</summary>
    public int GoalTile;

    /// <summary>Index of the next waypoint (Path mode).</summary>
    public int PathCursor;

    /// <summary>Navigation version the plan was made for (re-validated when obstacles change).</summary>
    public int PlanVersion;

    /// <summary>Incremented by every new order and re-plan; queued requests with an older serial are stale.</summary>
    public int RequestSerial;

    /// <summary>Group of the order (0 = alone).</summary>
    public int GroupId;

    /// <summary>Speed cap of the group (its slowest member), 0 = none.</summary>
    public int GroupSpeed;

    /// <summary>Within this distance of the target a crowded unit settles instead of pushing on (ORIG-02).</summary>
    public int SettleRadius;

    /// <summary>Best distance to the steering point since it was set.</summary>
    public int BestDistance;

    /// <summary>Ticks without progress toward the steering point.</summary>
    public int StallTicks;

    /// <summary>Re-plans because of stalls since the order.</summary>
    public int Replans;

    /// <summary>Velocity of the last tick, X.</summary>
    public int VelocityX;

    /// <summary>Velocity of the last tick, Y.</summary>
    public int VelocityY;

    /// <summary>Followed entity (<c>EntityId.Value</c>), 0 = none.</summary>
    public uint FollowTarget;

    /// <summary>Current state.</summary>
    public MotionState State;

    /// <summary>Steering mode.</summary>
    public MotionMode Mode;

    /// <summary>Navigation layer index (0xFF = flies straight).</summary>
    public byte Layer;

    /// <summary>The order.</summary>
    public MotionOrder Order;
}

/// <summary>
/// A land unit's step from tile to tile (ADR 0270, <see cref="Tzar2.Content.Compiled.MovementModel.Cells"/>): the tile it
/// enters (it is the unit's from the step's start), where the step began and how far it has come; and the tile it
/// stepped aside from to let a friend pass, which it goes back to.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CellStep : IComponent
{
    /// <summary>The anchor tile entered (its navigation layer's), or -1 while the unit stands on its tile.</summary>
    public int To;

    /// <summary>Where the step began, X (world units).</summary>
    public int FromX;

    /// <summary>Where the step began, Y (world units).</summary>
    public int FromY;

    /// <summary>World units walked into the step, 0 up to a tile (a step takes as long in any of the eight directions).</summary>
    public int Progress;

    /// <summary>The tile the unit stepped aside from for a friend, or -1.</summary>
    public int Home;

    /// <summary>The tick it stepped aside.</summary>
    public int HomeTick;

    /// <summary>The tile off a friend's way it walks to, to make room, or -1.</summary>
    public int Aside;

    /// <summary>Ticks waited behind a unit entering the next tile.</summary>
    public byte Waits;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved0;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved1;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved2;
}

/// <summary>The occupancy cell an entity is counted in (TDD §9.1), or -1.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct OccupancyCell : IComponent
{
    /// <summary>Cell index in <c>MapState</c>'s occupancy grid.</summary>
    public int Cell;
}

/// <summary>
/// The tiles a structure blocks (buildings, trees, rocks; TDD §9.1): the footprint's top-left tile. The mask and size
/// come from the entity type. Placing or destroying a structure adds/removes the blockers (REQ-PATH-01).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Footprint : IComponent
{
    /// <summary>Left tile column.</summary>
    public int TileX;

    /// <summary>Top tile row.</summary>
    public int TileY;

    /// <summary>
    /// 1 when the type's footprint is turned by 90° (gates and bridges placed the other way, GAME-03: the type's W×H
    /// mask is read transposed, H×W on the map).
    /// </summary>
    public byte Rotated;

    /// <summary>
    /// 1 when a gate faces the other way along its axis, its passage on the other side (the map's gate shape bit 0; the
    /// original's facings 1 and 3, CTRL-024); 0 otherwise.
    /// </summary>
    public byte Mirrored;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved1;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved2;

    /// <summary>Footprint width on the map: the type's, or its turned footprint's.</summary>
    public readonly int Width(EntityTypeDefinition type) => type.FootprintSize(Rotated != 0).Width;

    /// <summary>Footprint height on the map.</summary>
    public readonly int Height(EntityTypeDefinition type) => type.FootprintSize(Rotated != 0).Height;

    /// <summary>The footprint mask as placed (row-major over <see cref="Width"/> × <see cref="Height"/>; 1 = blocked).</summary>
    public readonly ReadOnlySpan<byte> Mask(EntityTypeDefinition type) => type.FootprintMask(Rotated != 0);

    /// <summary>A gate's passage as placed (row-major over <see cref="Width"/> × <see cref="Height"/>).</summary>
    public readonly ReadOnlySpan<byte> Passage(EntityTypeDefinition type) => type.Traits.Gate is { } gate ? gate.PassageOf(Rotated != 0) : [];

    /// <summary>The original's facing of a gate (0 and 1 across, 2 and 3 down; ReTzar's reading of the original).</summary>
    public readonly int Facing => (Rotated != 0 ? 2 : 0) + (Mirrored != 0 ? 1 : 0);
}

#pragma warning restore CA1051

/// <summary>Cold component: a display or script name (heroes, named campaign units).</summary>
public readonly struct EntityName : IColdComponent<EntityName>
{
    /// <summary>Maximum name length in UTF-8 bytes.</summary>
    public const int MaxBytes = 256;

    /// <summary>Creates a name.</summary>
    /// <exception cref="ArgumentException">The name is longer than <see cref="MaxBytes"/> UTF-8 bytes or is not valid UTF-16.</exception>
    public EntityName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int bytes;
        try
        {
            bytes = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("The name is not valid UTF-16.", nameof(value), ex);
        }

        if (bytes > MaxBytes)
        {
            // Snapshots read names with this bound, so a longer one would make an unloadable save.
            throw new ArgumentException($"The name is {bytes} UTF-8 bytes; at most {MaxBytes} are allowed.", nameof(value));
        }

        Value = value;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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
