using System;
using System.Collections.Generic;
using Tzar2.AI.Executors;
using Tzar2.AI.Legacy.Program;
using Tzar2.Core.Ids;
using Tzar2.Sim;

namespace Tzar2.AI.Runtime;

/// <summary>The difficulty levels of the original (lobby and map settings; REQ-AI-04).</summary>
public enum AiDifficulty : byte
{
    /// <summary>Easy.</summary>
    Easy = 0,

    /// <summary>Normal.</summary>
    Normal = 1,

    /// <summary>Hard.</summary>
    Hard = 2,
}

/// <summary>
/// One AI-controlled kingdom (map "Kingdoms → AI" page, lobby AI choice; REQ-AI-01/04/05). Part of the match setup:
/// identical on every peer and stored in the AI state of snapshots.
/// </summary>
public sealed record AiPlayerSettings
{
    /// <summary>The player slot the AI controls.</summary>
    public required byte Slot { get; init; }

    /// <summary>The compiled strategy script.</summary>
    public required AiProgram Program { get; init; }

    /// <summary>The kingdom's civilization: a ruleset key (<c>euro</c>) or a script name (<c>european</c>).</summary>
    public required string Civilization { get; init; }

    /// <summary>The difficulty (seen by <c>difficulty</c> conditions). The original data gives the AI no resource bonus
    /// at any level (UNK-36 open, ADR 0052): the levels differ only through the scripts.</summary>
    public AiDifficulty Difficulty { get; init; } = AiDifficulty.Normal;

    /// <summary>Map setting "Economy AI": the AI runs its economy, construction and production.</summary>
    public bool EconomyAi { get; init; } = true;

    /// <summary>Map setting "War AI": the AI defends and attacks.</summary>
    public bool WarAi { get; init; } = true;

    /// <summary>Lobby "AI resign" flag (1.09): the AI may resign on its own (<c>have resign</c>).</summary>
    public bool MayResign { get; init; } = true;

    /// <summary>
    /// A human kingdom of a multiplayer match: the AI is attached from the start but stays idle until the kingdom is
    /// handed to the computer (<see cref="PlayerFlags.AiControlled"/>, REQ-NET-07 drop policy; ADR 0163).
    /// </summary>
    public bool Standby { get; init; }
}

/// <summary>
/// Tuning of the AI host. Every value is part of the match (identical on all peers) because it changes the commands.
/// The defaults are placeholders marked UNVERIFIED in docs/mechanics/ai-placeholders.md until RE-07 measures the
/// original (UNK-35).
/// </summary>
public sealed record AiOptions
{
    /// <summary>The defaults.</summary>
    public static AiOptions Default { get; } = new();

    /// <summary>Ticks between the starts of two script passes of one AI (UNVERIFIED: 1 s, like the triggers' cadence).</summary>
    public int ThinkIntervalTicks { get; init; } = 20;

    /// <summary>Script instructions (rule visits, conditions, actions) one AI may execute per tick; a pass that needs
    /// more continues in the next tick (REQ-AI-03: count-based budget).</summary>
    public int InstructionBudgetPerTick { get; init; } = 512;

    /// <summary>Ticks between two executor runs of one AI (staggered by slot).</summary>
    public int ExecutorIntervalTicks { get; init; } = 10;

    /// <summary>Most commands one executor run of one AI issues.</summary>
    public int MaxCommandsPerRun { get; init; } = 24;

    /// <summary>Most goals one pass collects (further goals are dropped).</summary>
    public int MaxGoalsPerPass { get; init; } = 1024;

    /// <summary>Military tuning.</summary>
    public MilitaryOptions Military { get; init; } = new();

    /// <summary>Economy tuning.</summary>
    public EconomyOptions Economy { get; init; } = new();

    /// <summary>Naval tuning (ADR 0144).</summary>
    public NavalOptions Naval { get; init; } = new();

    /// <summary>Technology state: <see cref="AiTechnologies.Sim"/> (the players' researched technologies, GAME-06) by default.</summary>
    public IAiTechnologies Technologies { get; init; } = AiTechnologies.Sim;

    /// <summary>
    /// Additional executors run after the built-in ones (extension point, TDD §16.2). The instances are used by every
    /// simulation these options are attached to: they must be stateless, or the options must be created per simulation.
    /// </summary>
    public IReadOnlyList<IAiExecutor> ExtraExecutors { get; init; } = [];

    /// <summary>Checks the ranges.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ThinkIntervalTicks, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(InstructionBudgetPerTick, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ExecutorIntervalTicks, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCommandsPerRun, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxGoalsPerPass, 1);
        ArgumentNullException.ThrowIfNull(Technologies);
        ArgumentNullException.ThrowIfNull(ExtraExecutors);
        Military.Validate();
        Economy.Validate();
        ArgumentNullException.ThrowIfNull(Naval);
        Naval.Validate();
    }
}

/// <summary>Whether a technology is researched.</summary>
public enum AiTechnologyState : byte
{
    /// <summary>The ruleset has no such technology: the AI treats it as researched (ADR 0050).</summary>
    Unsupported = 0,

    /// <summary>Not researched (it may be queued).</summary>
    NotResearched = 1,

    /// <summary>Researched.</summary>
    Researched = 2,
}

/// <summary>
/// The AI's view of technologies (GAME-06). <see cref="AiTechnologies.Sim"/> reads the players' researched
/// technologies; a name the ruleset does not have is <see cref="AiTechnologyState.Unsupported"/>, which keeps a
/// script's gate on it open (ADR 0050). The research executor queues what the goals ask for.
/// </summary>
public interface IAiTechnologies
{
    /// <summary>The state of technology <paramref name="key"/> (e.g. <c>te_fields</c>; ruleset id <paramref name="technologyId"/>, -1 if none) for <paramref name="player"/>.</summary>
    AiTechnologyState StateOf(World world, byte player, string key, int technologyId);
}

/// <summary>Built-in technology views.</summary>
public static class AiTechnologies
{
    /// <summary>The sim's research state.</summary>
    public static IAiTechnologies Sim { get; } = new SimTechnologies();

    /// <summary>No technologies are modelled: every one counts as researched (tests, rulesets without GAME-06).</summary>
    public static IAiTechnologies None { get; } = new NoTechnologies();

    private sealed class SimTechnologies : IAiTechnologies
    {
        public AiTechnologyState StateOf(World world, byte player, string key, int technologyId)
            => technologyId < 0 || !world.HasPlayer(player) ? AiTechnologyState.Unsupported
                : world.Players[player].HasTechnology(technologyId) ? AiTechnologyState.Researched : AiTechnologyState.NotResearched;
    }

    private sealed class NoTechnologies : IAiTechnologies
    {
        public AiTechnologyState StateOf(World world, byte player, string key, int technologyId) => AiTechnologyState.Unsupported;
    }
}

/// <summary>A chat message of an AI (<c>say</c>); output for the presentation, not state.</summary>
/// <param name="Tick">The tick.</param>
/// <param name="Player">The AI's slot.</param>
/// <param name="Text">The text.</param>
public readonly record struct AiChatMessage(Tick Tick, byte Player, string Text);
