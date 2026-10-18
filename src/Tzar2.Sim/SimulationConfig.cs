using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tzar2.Content.Compiled;
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

    /// <summary>
    /// Starting-resource level (lobby STARTING RESOURCES): an index into the ruleset's
    /// <c>GameplaySettings.StartingResources</c>, or -1 for the ruleset's default level.
    /// </summary>
    public int StartingResources { get; init; } = -1;

    /// <summary>Population limit of the match (lobby POPULATION LIMIT), or -1 for the ruleset's default.</summary>
    public int PopulationLimit { get; init; } = -1;

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
        ArgumentOutOfRangeException.ThrowIfLessThan(StartingResources, -1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(StartingResources, 15);
        ArgumentOutOfRangeException.ThrowIfLessThan(PopulationLimit, -1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PopulationLimit, 100_000);
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

/// <summary>A stockpile: one amount per resource type id of the ruleset (GAME-01; at most <see cref="Ruleset.MaxResourceTypes"/>).</summary>
[InlineArray(Ruleset.MaxResourceTypes)]
public struct ResourceAmounts
{
    private int _amount;
}

/// <summary>One bit per technology id of the ruleset (GAME-06; at most <see cref="Ruleset.MaxTechnologies"/>).</summary>
[InlineArray(Ruleset.MaxTechnologies / 64)]
public struct TechnologyBits
{
    private ulong _word;
}

/// <summary>
/// Per-player hot state (hashed every tick, REQ-SIM-12): stockpile (GAME-01), population (GAME-05; ADR 0034) and the
/// researched technologies (GAME-06; ADR 0040).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PlayerState
{
#pragma warning disable CA1051 // Plain data.
    /// <summary>Flags.</summary>
    public PlayerFlags Flags;

    /// <summary>Stockpile by resource type id.</summary>
    public ResourceAmounts Resources;

    /// <summary>Population used by the player's units (recomputed every tick, pipeline step 4).</summary>
    public int Population;

    /// <summary>Population room: completed buildings' <c>PopulationProvided</c>, at most <see cref="PopulationLimit"/>.</summary>
    public int PopulationCap;

    /// <summary>The match's population limit.</summary>
    public int PopulationLimit;

    /// <summary>
    /// The kingdom's civilization: an index into <c>Ruleset.Civilizations</c> (set at match start, ADR 0040). Common
    /// buildings and units (towers, walls, ships) research and upgrade by their owner's technology tree.
    /// </summary>
    public int Civilization;

    /// <summary>Padding, always 0.</summary>
    public int Reserved;

    /// <summary>Researched technologies (bit = technology id).</summary>
    public TechnologyBits Technologies;
#pragma warning restore CA1051

    /// <summary>Whether technology <paramref name="technology"/> is researched.</summary>
    public readonly bool HasTechnology(int technology)
        => (uint)technology < Ruleset.MaxTechnologies && (Technologies[technology >> 6] & (1UL << (technology & 63))) != 0;

    /// <summary>Marks technology <paramref name="technology"/> researched.</summary>
    public void AddTechnology(int technology) => Technologies[technology >> 6] |= 1UL << (technology & 63);
}
