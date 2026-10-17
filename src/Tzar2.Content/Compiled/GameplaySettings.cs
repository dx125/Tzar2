using System;
using System.Collections.Immutable;

namespace Tzar2.Content.Compiled;

/// <summary>How fast the members of a group move order travel (UNK-12; ADR 0024 question, owner decision for M2).</summary>
public enum GroupMoveSpeed : byte
{
    /// <summary>The whole group moves at its slowest member's speed (default of both rulesets until UNK-12 is captured).</summary>
    Slowest = 0,

    /// <summary>Every member moves at its own speed.</summary>
    Own = 1,
}

/// <summary>A named starting-resource level (lobby "STARTING RESOURCES": low / normal / high; <c>RES.INI</c>).</summary>
/// <param name="Key">Level key, e.g. <c>low</c>, <c>medium</c>, <c>high</c>.</param>
/// <param name="Amounts">Amount per resource type id.</param>
public sealed record StartingResources(string Key, ImmutableArray<int> Amounts);

/// <summary>
/// Ruleset-wide gameplay constants (TDD §10: "expose every constant as rules data"). Values that are not in the original
/// data tables and are not measured yet are placeholders marked <c>UNVERIFIED</c> in the rules YAML and in
/// docs/mechanics/m2-placeholders.md, each with its UNK reference.
/// </summary>
public sealed record GameplaySettings
{
    /// <summary>Defaults for a ruleset without gameplay rules (the M2 movement test rulesets).</summary>
    public static GameplaySettings Default { get; } = new();

    /// <summary>Group move speed rule (UNK-12).</summary>
    public GroupMoveSpeed GroupMoveSpeed { get; init; } = GroupMoveSpeed.Slowest;

    /// <summary>Default population limit of a match (lobby "POPULATION LIMIT").</summary>
    public int PopulationLimit { get; init; } = 200;

    /// <summary>Starting-resource levels (empty = players start with nothing).</summary>
    public ImmutableArray<StartingResources> StartingResources { get; init; } = [];

    /// <summary>Index into <see cref="StartingResources"/> used when the lobby does not choose.</summary>
    public int DefaultStartingResources { get; init; }

    /// <summary>Smallest damage of a hit after defence (UNK-08).</summary>
    public int MinimumDamage { get; init; } = 1;

    /// <summary>
    /// Damage to a building with resistance R is multiplied by <c>scale / (scale + R)</c> (UNK-08; the manual only says
    /// resistance "reduces the amount of damage a building suffers").
    /// </summary>
    public int ResistanceScale { get; init; } = 100;

    /// <summary>
    /// How far (world units, edge to edge) a worker may be from the footprint of the node, drop-off or site it works on
    /// (UNK-17/18).
    /// </summary>
    public int InteractionReachUnits { get; init; } = 2048;

    /// <summary>How far (world units, edge to edge) a melee attacker reaches (UNK-07/11).</summary>
    public int MeleeReachUnits { get; init; } = 512;

    /// <summary>Time between two target scans of an idle armed unit (§8.9: staggered acquisition).</summary>
    public int AcquireIntervalMs { get; init; } = 200;

    /// <summary>Radius in tiles in which a worker looks for another node of the same resource when its node is gone.</summary>
    public int NodeSearchRadiusTiles { get; init; } = 12;

    /// <summary>Share of the cost refunded when a construction site is cancelled (basis points).</summary>
    public int ConstructionRefundBasisPoints { get; init; } = 10_000;

    /// <summary>Share of the cost refunded when a queued unit is cancelled (basis points).</summary>
    public int ProductionRefundBasisPoints { get; init; } = 10_000;

    internal void Validate(int resourceTypes)
    {
        if (GroupMoveSpeed > GroupMoveSpeed.Own)
        {
            throw new ArgumentOutOfRangeException(nameof(GroupMoveSpeed));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(PopulationLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(MinimumDamage);
        ArgumentOutOfRangeException.ThrowIfLessThan(ResistanceScale, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(InteractionReachUnits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(InteractionReachUnits, 8 * 2048);
        ArgumentOutOfRangeException.ThrowIfNegative(MeleeReachUnits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MeleeReachUnits, 8 * 2048);
        ArgumentOutOfRangeException.ThrowIfLessThan(AcquireIntervalMs, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(AcquireIntervalMs, 60_000);
        ArgumentOutOfRangeException.ThrowIfNegative(NodeSearchRadiusTiles);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(NodeSearchRadiusTiles, 64);
        ArgumentOutOfRangeException.ThrowIfNegative(ConstructionRefundBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ConstructionRefundBasisPoints, 10_000);
        ArgumentOutOfRangeException.ThrowIfNegative(ProductionRefundBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ProductionRefundBasisPoints, 10_000);
        if (StartingResources.IsDefault)
        {
            throw new ArgumentException("Starting resources must not be default.", nameof(StartingResources));
        }

        foreach (StartingResources level in StartingResources)
        {
            ArgumentException.ThrowIfNullOrEmpty(level.Key);
            if (level.Amounts.IsDefault || level.Amounts.Length != resourceTypes)
            {
                throw new ArgumentException($"Starting resources '{level.Key}' need one amount per resource type.", nameof(StartingResources));
            }

            foreach (int amount in level.Amounts)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(amount);
            }
        }

        if (StartingResources.Length > 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(DefaultStartingResources);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(DefaultStartingResources, StartingResources.Length);
        }
    }
}
