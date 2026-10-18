using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.AI.Runtime;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;

namespace Tzar2.AI.Executors;

/// <summary>
/// An executor turns the script's goals into regular sim commands (REQ-AI-02; Appendix D.6): it reads the world and
/// the census and calls <see cref="AiExecutorContext.Issue"/>. It must be deterministic and integer-only, keep no state
/// across runs outside <see cref="AiPlayerState"/> (snapshots), and respect the context's command budget.
/// </summary>
public interface IAiExecutor
{
    /// <summary>Name for diagnostics.</summary>
    string Name { get; }

    /// <summary>Runs once for one AI player.</summary>
    void Run(AiExecutorContext context);
}

/// <summary>Economy tuning (UNVERIFIED placeholders, docs/mechanics/ai-placeholders.md).</summary>
public sealed record EconomyOptions
{
    /// <summary>Base share of gatherers per resource key when no goal asks for more.</summary>
    public IReadOnlyDictionary<string, int> BaseShares { get; init; } = new SortedDictionary<string, int>(StringComparer.Ordinal)
    {
        ["food"] = 30,
        ["wood"] = 30,
        ["gold"] = 20,
        ["stone"] = 20,
    };

    /// <summary>Share added in proportion to what the unmet goals still need of each resource.</summary>
    public int DemandShare { get; init; } = 100;

    /// <summary>A resource with at least this much in stock loses its base share (its goals still attract gatherers).</summary>
    public int SurplusStock { get; init; } = 800;

    /// <summary>Most idle workers sent to work per run.</summary>
    public int AssignmentsPerRun { get; init; } = 12;

    /// <summary>Most workers moved between resources per run.</summary>
    public int RebalancesPerRun { get; init; } = 2;

    /// <summary>Workers per tree or small node (≤ <see cref="SmallNodeAmount"/>).</summary>
    public int WorkersPerSmallNode { get; init; } = 2;

    /// <summary>Workers per rock.</summary>
    public int WorkersPerLargeNode { get; init; } = 8;

    /// <summary>Workers per field (unlimited node).</summary>
    public int WorkersPerField { get; init; } = 1;

    /// <summary>Nodes holding at most this much are "small".</summary>
    public int SmallNodeAmount { get; init; } = 500;

    /// <summary>Nodes further than this from every own drop-off of their resource are not used.</summary>
    public int MaxNodeDistanceTiles { get; init; } = 32;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(BaseShares);
        ArgumentOutOfRangeException.ThrowIfNegative(DemandShare);
        ArgumentOutOfRangeException.ThrowIfNegative(AssignmentsPerRun);
        ArgumentOutOfRangeException.ThrowIfNegative(RebalancesPerRun);
        ArgumentOutOfRangeException.ThrowIfLessThan(WorkersPerSmallNode, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(WorkersPerLargeNode, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(WorkersPerField, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxNodeDistanceTiles, 1);
    }
}

/// <summary>Military tuning (UNVERIFIED placeholders: the original's war AI is hard-coded in the executable).</summary>
public sealed record MilitaryOptions
{
    /// <summary>No attack wave before this much game time.</summary>
    public int FirstWaveMs { get; init; } = 240_000;

    /// <summary>Least time between two waves.</summary>
    public int WaveCooldownMs { get; init; } = 60_000;

    /// <summary>Soldiers at home that launch the first wave.</summary>
    public int FirstWaveSize { get; init; } = 10;

    /// <summary>Soldiers added to the threshold after each wave.</summary>
    public int WaveGrowth { get; init; } = 4;

    /// <summary>Largest threshold.</summary>
    public int MaxWaveSize { get; init; } = 40;

    /// <summary>A wave reduced below this share of its launch size goes home.</summary>
    public int RetreatPercent { get; init; } = 25;

    /// <summary>Enemy units this many tiles around the own buildings are a threat to defend against.</summary>
    public int DefenceMarginTiles { get; init; } = 8;

    /// <summary>Where the army gathers: this many tiles from the town center toward the map center.</summary>
    public int GatherDistanceTiles { get; init; } = 7;

    /// <summary>Units per move command of a group order.</summary>
    public int GroupSize { get; init; } = 64;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(FirstWaveMs);
        ArgumentOutOfRangeException.ThrowIfNegative(WaveCooldownMs);
        ArgumentOutOfRangeException.ThrowIfLessThan(FirstWaveSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(WaveGrowth);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxWaveSize, FirstWaveSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxWaveSize, AiPlayerState.MaxWaveUnits);
        ArgumentOutOfRangeException.ThrowIfNegative(RetreatPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RetreatPercent, 100);
        ArgumentOutOfRangeException.ThrowIfNegative(DefenceMarginTiles);
        ArgumentOutOfRangeException.ThrowIfNegative(GatherDistanceTiles);
        ArgumentOutOfRangeException.ThrowIfLessThan(GroupSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(GroupSize, CommandList.MaxEntitiesPerCommand);
    }
}

/// <summary>
/// What an executor sees during one run for one AI player: the world (read-only), the census, the bound script and
/// its goals, a resource budget that commands of the run have already spent from, and the command output.
/// </summary>
public sealed class AiExecutorContext
{
    private readonly int[] _budget = new int[Ruleset.MaxResourceTypes];
    private readonly List<EntityId> _selection = [];
    private int[] _claims = [];
    private int[] _inProgressDelta = [];
    private int[] _attempted = [];
    private int _run;

    internal AiExecutorContext(AiCensus census, CommandList output)
    {
        Census = census;
        Output = output;
    }

    /// <summary>The world (do not change it; issue commands).</summary>
    public World World { get; private set; } = null!;

    /// <summary>The census of this tick.</summary>
    public AiCensus Census { get; }

    /// <summary>The ruleset model.</summary>
    public AiRulesetModel Model => Census.Model;

    /// <summary>The bound script.</summary>
    public AiBinding Binding { get; private set; } = null!;

    /// <summary>The player's AI state.</summary>
    public AiPlayerState State { get; private set; } = null!;

    /// <summary>The player's settings.</summary>
    public AiPlayerSettings Settings => State.Settings;

    /// <summary>The player's slot.</summary>
    public byte Slot => State.Settings.Slot;

    /// <summary>Host options.</summary>
    public AiOptions Options { get; private set; } = null!;

    /// <summary>The player's census.</summary>
    public AiCensusPlayer Player => Census.Player(Slot);

    /// <summary>The goals of the last complete script pass, in priority order.</summary>
    public ReadOnlySpan<AiGoal> Goals => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(State.Current);

    /// <summary>Commands this run may still issue.</summary>
    public int CommandsLeft { get; private set; }

    /// <summary>Commands issued this run.</summary>
    public int Issued { get; private set; }

    internal CommandList Output { get; }

    internal void Begin(World world, AiBinding binding, AiPlayerState state, AiOptions options)
    {
        World = world;
        Binding = binding;
        State = state;
        Options = options;
        CommandsLeft = options.MaxCommandsPerRun;
        Issued = 0;
        PlacementSearchesLeft = MaxPlacementSearchesPerRun;
        int classes = Census.Model.ClassCount;
        if (_inProgressDelta.Length != classes)
        {
            _inProgressDelta = new int[classes];
            _attempted = new int[classes];
        }

        Array.Clear(_inProgressDelta);
        ref readonly PlayerState player = ref world.Players[state.Settings.Slot];
        for (int r = 0; r < _budget.Length; r++)
        {
            _budget[r] = player.Resources[r];
        }

        if (_claims.Length < world.Entities.HighWater)
        {
            _claims = new int[Math.Max(world.Entities.HighWater, _claims.Length * 2)];
        }

        // Stamps of claims and attempts must never repeat: wrap (after ~500 million runs) clears every stamped array.
        if (_run >= (int.MaxValue / 4) - 1)
        {
            _run = 0;
            Array.Clear(_claims);
            Array.Clear(_attempted);
        }

        _run++;
    }

    /// <summary>Most build-site searches per run (each may check up to <see cref="AiBuildPlacement.MaxCandidates"/> positions).</summary>
    public const int MaxPlacementSearchesPerRun = 3;

    /// <summary>Build-site searches this run may still make.</summary>
    public int PlacementSearchesLeft { get; private set; }

    /// <summary>Takes one build-site search from the run's budget; false when none is left.</summary>
    public bool TakePlacementSearch()
    {
        if (PlacementSearchesLeft <= 0)
        {
            return false;
        }

        PlacementSearchesLeft--;
        return true;
    }

    /// <summary>Sites and queued units of a class, including what this run's commands added.</summary>
    public int InProgress(int classId) => Player.InProgress[classId] + _inProgressDelta[classId];

    /// <summary>Records <paramref name="count"/> more units or sites of a class ordered in this run.</summary>
    public void AddInProgress(int classId, int count) => _inProgressDelta[classId] += count;

    /// <summary>
    /// Whether <paramref name="classId"/> is tried for the first time in this run by the executor <paramref name="kind"/>
    /// (0 construction, 1 production, 2 economy demand, 3 free for extensions; a goal repeated by many rules is worked
    /// on once per run).
    /// </summary>
    public bool FirstAttempt(int classId, int kind)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(kind);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(kind, 3);
        int stamp = (_run * 4) + kind;
        if (_attempted[classId] == stamp)
        {
            return false;
        }

        _attempted[classId] = stamp;
        return true;
    }

    /// <summary>The stock left for this run's commands.</summary>
    public int Available(int resource) => _budget[resource];

    /// <summary>
    /// Whether <paramref name="cost"/> × <paramref name="count"/> fits the budget without spending a resource in
    /// <paramref name="locked"/> (bit per resource: the goal comes after an active <c>needs_locked</c>, ADR 0050).
    /// </summary>
    public bool CanAfford(ImmutableArray<int> cost, byte locked, int count = 1)
    {
        for (int r = 0; r < cost.Length; r++)
        {
            if (cost[r] <= 0)
            {
                continue;
            }

            if ((r < 8 && (locked & (1 << r)) != 0) || (long)cost[r] * count > _budget[r])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Takes <paramref name="cost"/> × <paramref name="count"/> from the run's budget.</summary>
    public void Spend(ImmutableArray<int> cost, int count = 1)
    {
        for (int r = 0; r < cost.Length; r++)
        {
            _budget[r] = (int)Math.Max(0, _budget[r] - ((long)cost[r] * count));
        }
    }

    /// <summary>Whether an entity was already given an order in this run (by any executor).</summary>
    public bool IsClaimed(EntityId entity) => (uint)entity.Index < (uint)_claims.Length && _claims[entity.Index] == _run;

    /// <summary>Marks an entity as ordered in this run.</summary>
    public void Claim(EntityId entity)
    {
        if ((uint)entity.Index < (uint)_claims.Length)
        {
            _claims[entity.Index] = _run;
        }
    }

    /// <summary>A reusable selection list (cleared by the caller).</summary>
    public List<EntityId> Selection => _selection;

    /// <summary>Issues a command for this player (validated when it executes); false when the run's budget is spent.</summary>
    public bool Issue(CommandKind kind, ReadOnlySpan<EntityId> entities, int x = 0, int y = 0, EntityId target = default, int arg0 = 0, int arg1 = 0, CommandFlags flags = CommandFlags.None)
    {
        if (CommandsLeft <= 0 || Output.Count >= CommandList.MaxCommands)
        {
            return false;
        }

        Output.Add(kind, entities, x, y, target, arg0, arg1, flags, Slot);
        foreach (EntityId e in entities)
        {
            Claim(e);
        }

        CommandsLeft--;
        Issued++;
        return true;
    }

    /// <summary>The target count of an object goal (N, or N % of the population limit rounded up).</summary>
    public int TargetOf(AiGoal goal) => AiInterpreter.ObjectTarget(goal.Number, goal.Percent, World.Players[Slot].PopulationLimit);

    /// <summary>The bound symbol of a goal.</summary>
    public AiSymbol SymbolOf(AiGoal goal) => Binding.Symbols[goal.Symbol];

    /// <summary>Whether a worker is free: no task, not moving.</summary>
    public bool IsIdleWorker(EntityId worker)
    {
        int w = World.Workers.IndexOf(worker);
        return w >= 0 && World.Workers.At(w).Task == WorkerTask.None && !IsMoving(worker);
    }

    /// <summary>Whether a unit is moving.</summary>
    public bool IsMoving(EntityId unit)
    {
        int m = World.Motions.IndexOf(unit);
        return m >= 0 && World.Motions.At(m).State == MotionState.Moving;
    }

    /// <summary>A distance estimate in world units (octile: max + min/2), integer and symmetric.</summary>
    public static long Distance(int x0, int y0, int x1, int y1)
    {
        long dx = Math.Abs((long)x1 - x0);
        long dy = Math.Abs((long)y1 - y0);
        return Math.Max(dx, dy) + (Math.Min(dx, dy) / 2);
    }

    /// <summary>The tile of a world point.</summary>
    public static (int X, int Y) TileOf(int x, int y) => (x / MapState.UnitsPerTile, y / MapState.UnitsPerTile);
}
