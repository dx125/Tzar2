using System.Runtime.InteropServices;
using Tzar2.Sim.Ecs;

namespace Tzar2.Sim.Components;

#pragma warning disable CA1051 // Hot components are plain data with public fields (REQ-SIM-08).

/// <summary>Where a trader is heading (GAME-14).</summary>
public enum TradePhase : byte
{
    /// <summary>To dock A.</summary>
    ToA = 1,

    /// <summary>To dock B.</summary>
    ToB = 2,
}

/// <summary>
/// A trader's route between two docks (GAME-14, ADR 0103): it loads <see cref="GoodsA"/> at A (paid from the stockpile),
/// carries it to B and sells it with a profit that grows with the distance, loads <see cref="GoodsB"/> there, and so on.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TradeRoute : IComponent
{
    /// <summary>Dock A (entity value).</summary>
    public uint DockA;

    /// <summary>Dock B (entity value).</summary>
    public uint DockB;

    /// <summary>Goods loaded per trip.</summary>
    public int Amount;

    /// <summary>Goods carried now.</summary>
    public int Carry;

    /// <summary>Time left at the dock (unloading and loading).</summary>
    public int TimerMs;

    /// <summary>Where the carried goods were loaded (X, world units).</summary>
    public int LoadX;

    /// <summary>Where the carried goods were loaded (Y).</summary>
    public int LoadY;

    /// <summary>Resource loaded at A.</summary>
    public byte GoodsA;

    /// <summary>Resource loaded at B.</summary>
    public byte GoodsB;

    /// <summary>Resource carried now.</summary>
    public byte CarryResource;

    /// <summary>The dock the carried goods come from: 0 none, 1 A, 2 B.</summary>
    public byte LoadedAt;

    /// <summary>Heading.</summary>
    public TradePhase Phase;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved;

    /// <summary>Padding, always 0.</summary>
    public short Reserved1;
}

/// <summary>The home of a wandering or guarding Nature unit (GAME-15, ADR 0104).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NatureHome : IComponent
{
    /// <summary>Home X (world units).</summary>
    public int X;

    /// <summary>Home Y.</summary>
    public int Y;

    /// <summary>The tick of the next wander move.</summary>
    public int NextMoveTick;

    /// <summary>1 while walking back home after being lured away.</summary>
    public byte Returning;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved0;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved1;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved2;
}

#pragma warning restore CA1051
