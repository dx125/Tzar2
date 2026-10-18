using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using Tzar2.AI.Executors;
using Tzar2.AI.Legacy.Syntax;
using Tzar2.AI.Runtime;
using Tzar2.Core.Hashing;
using Tzar2.Core.Serialization;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Snapshots;
using Tzar2.Sim.Systems;

namespace Tzar2.AI;

/// <summary>
/// The AI players of a match as a sim system (TDD §8.5 step 3, REQ-AI-03; ADR 0051). On every peer it runs the same
/// script passes (budgeted, staggered by player) and executors, issues their orders as regular commands through the
/// command system's validation in the same tick, and keeps its state in the world's snapshot extension
/// <c>ai.legacy</c>. It draws randomness only from each player's <c>ai[p]</c> stream and iterates in a fixed order, so
/// every peer, worker count and restored snapshot produces the same commands.
/// </summary>
public sealed class LegacyAiSystem : ISimSystem, IWorldStateExtension
{
    /// <summary>The snapshot extension key.</summary>
    public const string ExtensionKey = "ai.legacy";

    private const byte StateVersion = 3;

    private readonly World _world;
    private readonly AiOptions _options;
    private readonly AiRulesetModel _model;
    private readonly AiCensus _census;
    private readonly AiPlayerState[] _states; // replaced element-wise only by a successful ReadFrom
    private readonly AiBinding[] _bindings;
    private readonly IAiExecutor[] _executors;
    private readonly CommandList _commands = new();
    private readonly AiExecutorContext _context;
    private readonly List<AiChatMessage> _chat = [];
    private readonly long[] _issued;
    private readonly ulong _optionsFingerprint;

    private LegacyAiSystem(Simulation simulation, IReadOnlyList<AiPlayerSettings> players, AiOptions options)
    {
        _world = simulation.World;
        _options = options;
        _model = new AiRulesetModel(_world.Ruleset);
        _census = new AiCensus(_model, _world.Config.PlayerCount);
        _context = new AiExecutorContext(_census, _commands);
        var sorted = new List<AiPlayerSettings>(players);
        sorted.Sort(static (a, b) => a.Slot.CompareTo(b.Slot));
        _states = new AiPlayerState[sorted.Count];
        _bindings = new AiBinding[sorted.Count];
        _issued = new long[sorted.Count];
        var bindings = new List<AiBinding>();
        var diagnostics = ImmutableArray.CreateBuilder<AiDiagnostic>();
        for (int i = 0; i < sorted.Count; i++)
        {
            AiPlayerSettings s = sorted[i];
            if (!_world.HasPlayer(s.Slot) || (i > 0 && sorted[i - 1].Slot == s.Slot))
            {
                throw new ArgumentException($"AI slot {s.Slot} is not a player of the match or is listed twice.", nameof(players));
            }

            AiBinding? binding = bindings.Find(b => ReferenceEquals(b.Program, s.Program) && string.Equals(b.Civilization, AiRulesetModel.CivilizationKey(s.Civilization), StringComparison.Ordinal));
            if (binding is null)
            {
                binding = AiBinding.Bind(s.Program, _model, s.Civilization);
                bindings.Add(binding);
                diagnostics.AddRange(binding.Diagnostics);
            }

            _bindings[i] = binding;
            _states[i] = new AiPlayerState(s, s.Program.Flags.Length, s.Program.Rules.Length)
            {
                // Stagger the passes of the players over the think interval.
                NextPassTick = i * options.ThinkIntervalTicks / Math.Max(1, sorted.Count),
            };
        }

        Diagnostics = diagnostics.ToImmutable();
        _optionsFingerprint = Fingerprint(options, _world.Ruleset.ResourceTypes);
        // The naval executor runs first: it reserves what its harbor and ships cost (it does nothing on land maps).
        var executors = new List<IAiExecutor> { new NavalExecutor(), new ConstructionExecutor(), new ProductionExecutor(), new ResearchExecutor(), new EconomyExecutor(), new MilitaryExecutor() };
        executors.AddRange(options.ExtraExecutors);
        _executors = [.. executors];
    }

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.AI;

    /// <inheritdoc />
    public string Name => "ai";

    /// <inheritdoc />
    public string Key => ExtensionKey;

    /// <summary>The AI players' states, by ascending slot.</summary>
    public IReadOnlyList<AiPlayerState> Players => _states;

    /// <summary>Binding warnings of the scripts against the ruleset (unknown and misused names).</summary>
    public ImmutableArray<AiDiagnostic> Diagnostics { get; }

    /// <summary>The commands the AI issued in the last tick (output, cleared every tick).</summary>
    public CommandList LastCommands => _commands;

    /// <summary>Chat messages (<c>say</c>) of the last tick (output).</summary>
    public IReadOnlyList<AiChatMessage> LastChat => _chat;

    /// <summary>Commands issued per AI player since this host was created (diagnostics, not state).</summary>
    public long Issued(int index) => _issued[index];

    /// <summary>
    /// Adds the AI to a simulation before its first tick: a new match, or one just restored with
    /// <see cref="Simulation.Restore"/>, in which case the AI state is read from the snapshot (and must have been saved with
    /// the same scripts and settings).
    /// </summary>
    /// <exception cref="ArgumentException">A slot is invalid or duplicated.</exception>
    /// <exception cref="InvalidDataException">The restored AI state does not match the scripts or settings.</exception>
    /// <exception cref="InvalidOperationException">The simulation already ran a tick.</exception>
    public static LegacyAiSystem Attach(Simulation simulation, IReadOnlyList<AiPlayerSettings> players, AiOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(players);
        options ??= AiOptions.Default;
        options.Validate();
        if (simulation.Systems.IsSealed)
        {
            throw new InvalidOperationException("The AI must be attached before the first tick.");
        }

        var system = new LegacyAiSystem(simulation, players, options);

        // Register first: it reads a restored state and may reject it; only an accepted AI joins the pipeline.
        simulation.World.Extensions.Register(system);
        simulation.Systems.Add(system);
        return system;
    }

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        _commands.Clear();
        _chat.Clear();
        int tick = world.Tick.Value;
        for (int i = 0; i < _states.Length; i++)
        {
            AiPlayerState state = _states[i];
            byte slot = state.Settings.Slot;
            PlayerFlags flags = world.Players[slot].Flags;
            if ((flags & PlayerFlags.Active) == 0 || (flags & PlayerFlags.Defeated) != 0)
            {
                continue;
            }

            if (state.InPass || tick >= state.NextPassTick)
            {
                _census.Update(world);
                AiInterpreter.Step(world, _census, _bindings[i], state, _options, _chat, _options.InstructionBudgetPerTick);
            }

            int interval = _options.ExecutorIntervalTicks;
            if ((tick + (i * interval / _states.Length)) % interval == 0)
            {
                _census.Update(world);
                int before = _commands.Count;
                _context.Begin(world, _bindings[i], state, _options);
                foreach (IAiExecutor executor in _executors)
                {
                    if (_context.CommandsLeft <= 0)
                    {
                        break;
                    }

                    executor.Run(_context);
                }

                _issued[i] += _commands.Count - before;
            }
        }

        if (_commands.Count > 0)
        {
            CommandSystem.ExecuteInTick(world, _commands);
        }
    }

    // Every option that changes the commands (ADR 0051: they are part of the match), canonically.
    private static ulong Fingerprint(AiOptions o, IReadOnlyList<string> resources)
    {
        var h = new StateHasher();
        h.Add(o.ThinkIntervalTicks).Add(o.InstructionBudgetPerTick).Add(o.ExecutorIntervalTicks).Add(o.MaxCommandsPerRun).Add(o.MaxGoalsPerPass);
        MilitaryOptions m = o.Military;
        h.Add(m.FirstWaveMs).Add(m.WaveCooldownMs).Add(m.FirstWaveSize).Add(m.WaveGrowth).Add(m.MaxWaveSize).Add(m.RetreatPercent)
            .Add(m.DefenceMarginTiles).Add(m.GatherDistanceTiles).Add(m.GroupSize);
        EconomyOptions e = o.Economy;
        foreach (string r in resources)
        {
            h.Add(e.BaseShares.TryGetValue(r, out int share) ? share : -1);
        }

        h.Add(e.DemandShare).Add(e.SurplusStock).Add(e.AssignmentsPerRun).Add(e.RebalancesPerRun).Add(e.WorkersPerSmallNode)
            .Add(e.WorkersPerLargeNode).Add(e.WorkersPerField).Add(e.SmallNodeAmount).Add(e.MaxNodeDistanceTiles);
        NavalOptions n = o.Naval;
        h.Add(n.FishingBoats).Add(n.Transports).Add(n.Warships).Add(n.FishRadiusTiles).Add(n.EmbarkTimeoutMs).Add(n.SailRetryMs).Add(n.TransportLeadMs);
        AddText(h, o.Technologies.GetType().FullName ?? string.Empty);
        h.Add(o.ExtraExecutors.Count);
        foreach (IAiExecutor x in o.ExtraExecutors)
        {
            AddText(h, x.Name);
        }

        return h.GetCurrentHash();

        static void AddText(StateHasher h, string s)
        {
            h.Add(s.Length);
            foreach (char c in s)
            {
                h.Add((int)c);
            }
        }
    }

    /// <inheritdoc />
    public void WriteTo(BinaryBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(StateVersion);
        writer.WriteUInt64(_optionsFingerprint);
        writer.WriteVarUInt64((ulong)_states.Length);
        foreach (AiPlayerState state in _states)
        {
            state.WriteTo(writer);
        }
    }

    /// <inheritdoc />
    public void ReadFrom(ReadOnlySpan<byte> data)
    {
        var reader = new BinaryBufferReader(data);
        try
        {
            if (reader.ReadByte() != StateVersion)
            {
                throw new InvalidDataException("The AI state was saved by another AI version.");
            }

            if (reader.ReadUInt64() != _optionsFingerprint)
            {
                throw new InvalidDataException("The AI state was saved with other AI options (cadence, budgets, executors); they are part of the match.");
            }

            if (reader.ReadVarUInt64() != (ulong)_states.Length)
            {
                throw new InvalidDataException("The AI state was saved with another set of AI players.");
            }

            // Parse into fresh states and replace the current ones only when everything is valid.
            var parsed = new AiPlayerState[_states.Length];
            for (int i = 0; i < parsed.Length; i++)
            {
                AiPlayerSettings s = _states[i].Settings;
                parsed[i] = new AiPlayerState(s, s.Program.Flags.Length, s.Program.Rules.Length);
                parsed[i].ReadFrom(ref reader, _options.MaxGoalsPerPass);
            }

            if (!reader.IsAtEnd)
            {
                throw new InvalidDataException("Trailing bytes after the AI state.");
            }

            parsed.CopyTo(_states, 0);
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or EndOfStreamException)
        {
            throw new InvalidDataException("The AI state is malformed.", e);
        }
    }
}
