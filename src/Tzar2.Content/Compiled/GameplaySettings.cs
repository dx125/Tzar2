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

    /// <summary>Share of the cost refunded when a queued research or upgrade is cancelled (basis points).</summary>
    public int ResearchRefundBasisPoints { get; init; } = 10_000;

    /// <summary>Capture points one capturing peasant adds per second (GAME-04, UNK-19).</summary>
    public int CapturePointsPerSecond { get; init; } = 10;

    /// <summary>Least capture resistance of a building, whatever the influence around it (UNK-19).</summary>
    public int MinimumCaptureResistance { get; init; } = 1;

    /// <summary>Capture progress is lost after this long without a capturer at work (UNK-19).</summary>
    public int CaptureResetMs { get; init; } = 5_000;

    /// <summary>Radius in tiles of the EMIT influence; it fades linearly to 0 there (⚠ HYPOTHESIS UNK-19).</summary>
    public int InfluenceRadiusTiles { get; init; } = 16;

    /// <summary>A full repair by one peasant takes this share of the build time (basis points; UNK-18).</summary>
    public int RepairTimeBasisPoints { get; init; } = 10_000;

    /// <summary>A burnable building below this share of its hit points burns (basis points; UNK-42).</summary>
    public int BurnThresholdBasisPoints { get; init; } = 5_000;

    /// <summary>Hit points a burning building loses per minute (UNK-42).</summary>
    public int BurnDamagePerMinute { get; init; } = 60;

    /// <summary>Radius in tiles in which a builder looks for the next unfinished segment of the wall it built.</summary>
    public int WallContinueRadiusTiles { get; init; } = 6;

    /// <summary>Most segments one dragged wall may place.</summary>
    public int MaxWallSegments { get; init; } = 64;

    /// <summary>An automatic gate opens while a unit of its owner is this close to it (world units, edge to edge).</summary>
    public int GateOpenReachUnits { get; init; } = 2048;

    /// <summary>An automatic gate closes when no unit of its owner is this close (more than the open reach: hysteresis).</summary>
    public int GateCloseReachUnits { get; init; } = 4096;

    /// <summary>Least time between two state changes of one gate (orders earlier are rejected).</summary>
    public int GateChangeIntervalMs { get; init; } = 1000;

    /// <summary>Most gate state changes applied per tick (the rest wait: bounded navigation work).</summary>
    public int MaxGateChangesPerTick { get; init; } = 16;

    /// <summary>A full repair (0 → max) costs this share of the effective construction cost, charged pro rata (bp; UNK-18).</summary>
    public int RepairCostBasisPoints { get; init; } = 5_000;

    /// <summary>
    /// Each further repairer on one entity adds this many basis points less than the one before (UNK-18): with 2,500 the
    /// first four add 100%, 75%, 50%, 25% and more add nothing.
    /// </summary>
    public int RepairerFalloffBasisPoints { get; init; } = 2_500;

    internal void Validate(int resourceTypes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ResearchRefundBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ResearchRefundBasisPoints, 10_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(CapturePointsPerSecond, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(CapturePointsPerSecond, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MinimumCaptureResistance, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(CaptureResetMs, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(InfluenceRadiusTiles);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(InfluenceRadiusTiles, 128);
        ArgumentOutOfRangeException.ThrowIfLessThan(RepairTimeBasisPoints, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RepairTimeBasisPoints, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfNegative(BurnThresholdBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BurnThresholdBasisPoints, 10_000);
        ArgumentOutOfRangeException.ThrowIfNegative(BurnDamagePerMinute);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BurnDamagePerMinute, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfNegative(WallContinueRadiusTiles);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(WallContinueRadiusTiles, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxWallSegments, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxWallSegments, 256);
        ArgumentOutOfRangeException.ThrowIfNegative(GateOpenReachUnits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(GateOpenReachUnits, 8 * 2048);
        ArgumentOutOfRangeException.ThrowIfLessThan(GateCloseReachUnits, GateOpenReachUnits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(GateCloseReachUnits, 16 * 2048);
        ArgumentOutOfRangeException.ThrowIfNegative(GateChangeIntervalMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(GateChangeIntervalMs, 600_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxGateChangesPerTick, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxGateChangesPerTick, 4096);
        ArgumentOutOfRangeException.ThrowIfNegative(RepairCostBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RepairCostBasisPoints, 100_000);
        ArgumentOutOfRangeException.ThrowIfNegative(RepairerFalloffBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RepairerFalloffBasisPoints, 10_000);

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
