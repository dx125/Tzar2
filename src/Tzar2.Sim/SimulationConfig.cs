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

    /// <summary>No Fog (lobby, GAME-12): explored areas show enemy units too, as if always in sight.</summary>
    public bool NoFog { get; init; }

    /// <summary>Explore (lobby, GAME-12): the whole map is explored from the start.</summary>
    public bool Explored { get; init; }

    /// <summary>
    /// Victory condition (lobby, GAME-17). The default is <see cref="VictoryCondition.NotSpecified"/> (a sandbox: no automatic
    /// defeat or victory); the skirmish lobby sets <see cref="VictoryCondition.Standard"/>.
    /// </summary>
    public VictoryCondition Victory { get; init; } = VictoryCondition.NotSpecified;

    /// <summary>Game type (lobby, GAME-17).</summary>
    public GameType GameType { get; init; } = GameType.Standard;

    /// <summary>Score limit of <see cref="VictoryCondition.ScoreLimit"/>.</summary>
    public int ScoreLimit { get; init; } = 10_000;

    /// <summary>Time limit in minutes of <see cref="VictoryCondition.TimeLimit"/>.</summary>
    public int TimeLimitMinutes { get; init; } = 60;

    /// <summary>Day/night cycle (lobby DAY-NIGHT, GAME-16); off = always day.</summary>
    public bool DayNight { get; init; } = true;

    /// <summary>Weather (lobby WEATHER EFFECTS, GAME-16); off = always clear.</summary>
    public bool Weather { get; init; } = true;

    /// <summary>
    /// Teams at match start, 4 bits per player slot (slot 0 in the low bits): kingdoms with the same non-zero team are
    /// mutual allies with shared vision and shared victory (GAME-13); 0 = no team (enemy of everyone).
    /// </summary>
    public ulong Teams { get; init; }

    /// <summary>A ranked match: cheats are never allowed (GAME-19).</summary>
    public bool Ranked { get; init; }

    /// <summary>The team of a slot (0 = none).</summary>
    public int TeamOf(int player) => (uint)player < 16 ? (int)((Teams >> (player * 4)) & 0xF) : 0;

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
        if (Victory > VictoryCondition.NotSpecified || GameType > GameType.Deathmatch)
        {
            throw new ArgumentOutOfRangeException(nameof(Victory));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(ScoreLimit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ScoreLimit, 100_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(TimeLimitMinutes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TimeLimitMinutes, 10_000);
        if (PlayerCount < 16 && (Teams >> (PlayerCount * 4)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Teams), "Teams name slots that are not in the match.");
        }

        if (Ranked && CheatsAllowed)
        {
            throw new ArgumentException("A ranked match never allows cheats (GAME-19).", nameof(CheatsAllowed));
        }
    }
}

/// <summary>Victory conditions (lobby VICTORY CONDITION; editor manual §8.3).</summary>
public enum VictoryCondition : byte
{
    /// <summary>The last kingdom (or allied team) standing wins.</summary>
    Standard = 0,

    /// <summary>A kingdom without a castle is defeated.</summary>
    DestroyCastles = 1,

    /// <summary>At the time limit the highest score wins.</summary>
    TimeLimit = 2,

    /// <summary>The first to reach the score limit wins.</summary>
    ScoreLimit = 3,

    /// <summary>No automatic victory (scenario triggers decide).</summary>
    NotSpecified = 4,
}

/// <summary>Game types (lobby GAME TYPE: Standard or Deathmatch, manual ch. 3).</summary>
public enum GameType : byte
{
    /// <summary>Standard.</summary>
    Standard = 0,

    /// <summary>Deathmatch: every kingdom starts rich (world.deathmatchStartingResources).</summary>
    Deathmatch = 1,
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

    /// <summary>The kingdom has won (GAME-17).</summary>
    Victorious = 4,

    /// <summary>The kingdom resigned (GAME-13: also set with <see cref="Defeated"/>).</summary>
    Resigned = 8,

    /// <summary>
    /// A computer runs the kingdom in place of its dropped human (REQ-NET-07 drop policy; ADR 0163): set and cleared only
    /// by <see cref="Commands.CommandKind.SetController"/>, which the game layer issues from the relay's control events.
    /// </summary>
    AiControlled = 16,

    /// <summary>
    /// The computer runs the kingdom's economy for its human (AI assistance, ORDERS.INI <c>set_ai_eco</c>/<c>set_ai_full</c>;
    /// ADR 0183): set by <see cref="Commands.CommandKind.SetAiAssist"/>.
    /// </summary>
    AssistEconomy = 32,

    /// <summary>
    /// The computer defends and attacks for the kingdom's human (AI assistance, <c>set_ai_war</c>/<c>set_ai_full</c>; ADR
    /// 0183): set by <see cref="Commands.CommandKind.SetAiAssist"/>.
    /// </summary>
    AssistWar = 64,
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

    /// <summary>Crusaders the kingdom's cathedrals have gathered for its next crusade (MAG-016, MAG-017).</summary>
    public ushort Crusaders;

    /// <summary>Full crusades preached, each raising the ceiling of <see cref="Crusaders"/>.</summary>
    public byte FullCrusades;

    /// <summary>Padding, always 0.</summary>
    public byte Reserved;

    /// <summary>Researched technologies (bit = technology id).</summary>
    public TechnologyBits Technologies;
#pragma warning restore CA1051

    /// <summary>Whether technology <paramref name="technology"/> is researched.</summary>
    public readonly bool HasTechnology(int technology)
        => (uint)technology < Ruleset.MaxTechnologies && (Technologies[technology >> 6] & (1UL << (technology & 63))) != 0;

    /// <summary>Marks technology <paramref name="technology"/> researched.</summary>
    public void AddTechnology(int technology) => Technologies[technology >> 6] |= 1UL << (technology & 63);
}
