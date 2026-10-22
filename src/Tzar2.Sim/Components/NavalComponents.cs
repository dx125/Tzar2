using System.Runtime.InteropServices;
using Tzar2.Sim.Ecs;

namespace Tzar2.Sim.Components;

/// <summary>Why a ship sails to a shore (REQ-PATH-09, ADR 0140).</summary>
public enum DockPurpose : byte
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Meet units that board it; it waits at the shore.</summary>
    Board = 1,

    /// <summary>Let its units out at the shore (they stay where they land).</summary>
    Unload = 2,

    /// <summary>Let its units out at the shore and send them on to <see cref="Docking.TowardX"/>, <see cref="Docking.TowardY"/>.</summary>
    UnloadTo = 3,

    /// <summary>Come to the peasants that repair it; it waits at the shore.</summary>
    Repair = 4,
}

/// <summary>A ship on its way to a shore point (hot; REQ-PATH-09, ADR 0140).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Docking : IComponent
{
    /// <summary>The water point the ship sails to (world units).</summary>
    public int WaterX;

    /// <summary>The water point the ship sails to (world units).</summary>
    public int WaterY;

    /// <summary>Unloading: where the units step out toward (and go to, <see cref="DockPurpose.UnloadTo"/>); else 0.</summary>
    public int TowardX;

    /// <summary>Unloading: where the units step out toward (and go to, <see cref="DockPurpose.UnloadTo"/>); else 0.</summary>
    public int TowardY;

    /// <summary>Tick of the last movement request, -1 = none.</summary>
    public int MoveTick;

    /// <summary>Movement requests without arriving.</summary>
    public int Retries;

    /// <summary>Most units to let out (<see cref="DockPurpose.Unload"/>, <see cref="DockPurpose.UnloadTo"/>), 0 = all.</summary>
    public ushort Limit;

    /// <summary>The purpose.</summary>
    public DockPurpose Purpose;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved;
}

/// <summary>A unit exploring on its own (order Explore; hot; ADR 0143).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Exploring : IComponent
{
    /// <summary>The unexplored tile it heads for (tile index), -1 = none yet.</summary>
    public int GoalTile;

    /// <summary>Tick of the next look for a goal.</summary>
    public int NextTick;

    /// <summary>The middle of the area it was sent to explore (tile index, ORD-007), -1 = around itself.</summary>
    public int AreaTile;
}
