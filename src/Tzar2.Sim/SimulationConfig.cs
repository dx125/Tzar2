using System;
using System.Runtime.InteropServices;
using Tzar2.Core.Ids;

namespace Tzar2.Sim;

/// <summary>Match settings that affect the simulation. Identical on every peer; stored in snapshots and replays.</summary>
public sealed record SimulationConfig
{
    /// <summary>The match seed all RNG streams derive from (REQ-SIM-06).</summary>
    public ulong MatchSeed { get; init; }

    /// <summary>Number of player slots (kingdoms), 1 to the ruleset's maximum.</summary>
    public int PlayerCount { get; init; } = 2;

    /// <summary>Whether debug/cheat commands are accepted (GAME-19: the lobby flag "cheats enabled").</summary>
    public bool CheatsAllowed { get; init; }

    /// <summary>Ticks between full-state hashes (REQ-SIM-12: every 20 ticks).</summary>
    public int FullHashIntervalTicks { get; init; } = 20;

    /// <summary>Game time per tick (REQ-SIM-01).</summary>
    public int TickLengthMilliseconds { get; init; } = Tick.DefaultLengthMilliseconds;

    /// <summary>Default <see cref="PathBudgetPerTick"/>.</summary>
    public const int DefaultPathBudgetPerTick = 750_000;

    /// <summary>
    /// Path-request budget: search-node expansions per tick (REQ-PATH-03: count-based, identical on all peers, never
    /// time-based). At least one queued request is completed per tick whatever its cost.
    /// </summary>
    public int PathBudgetPerTick { get; init; } = DefaultPathBudgetPerTick;

    /// <summary>Checks the ranges.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public void Validate(int maxPlayers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(PlayerCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PlayerCount, maxPlayers);
        ArgumentOutOfRangeException.ThrowIfLessThan(FullHashIntervalTicks, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(TickLengthMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TickLengthMilliseconds, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(PathBudgetPerTick, 1);
    }
}

/// <summary>Flags of <see cref="PlayerState"/>.</summary>
[Flags]
public enum PlayerFlags
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>The slot is in the match and may issue commands.</summary>
    Active = 1,

    /// <summary>The kingdom has been defeated or resigned.</summary>
    Defeated = 2,
}

/// <summary>Per-player hot state (hashed every tick, REQ-SIM-12). Resources per GAME-01.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PlayerState
{
#pragma warning disable CA1051 // Plain data.
    /// <summary>Flags.</summary>
    public PlayerFlags Flags;

    /// <summary>Food.</summary>
    public int Food;

    /// <summary>Wood.</summary>
    public int Wood;

    /// <summary>Stone.</summary>
    public int Stone;

    /// <summary>Gold.</summary>
    public int Gold;
#pragma warning restore CA1051
}
