using System;
using System.Collections.Generic;
using System.IO;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Hashing;
using Tzar2.Core.Ids;
using Tzar2.Core.Serialization;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Tzar2.Sim.Snapshots;
using Tzar2.Sim.Systems;

namespace Tzar2.Scripting.Legacy;

/// <summary>
/// The legacy trigger engine (TDD §17.1 REQ-SCR-01, Appendix C; ADR 0130) as a sim system of pipeline step 2. Every
/// <see cref="TriggerOptions.IntervalMs"/> it starts a pass: all events are evaluated in index order into their
/// variables, then the rules in scenario order; a rule whose condition is non-zero runs its actions top to bottom and is
/// disabled afterwards unless one of them is Keep Rule. A pass spends at most <see cref="TriggerOptions.StepBudgetPerTick"/>
/// steps per tick and continues in the next tick where it stopped. Timers advance every tick. Conversations (ADR 0131)
/// wait for <see cref="CommandKind.ScenarioReply"/> commands. All state is the snapshot extension <c>triggers.legacy</c>.
/// </summary>
public sealed partial class LegacyTriggerSystem : ISimSystem, IWorldStateExtension, IScenarioController
{
    /// <summary>The snapshot extension key.</summary>
    public const string ExtensionKey = "triggers.legacy";

    private readonly World _world;
    private readonly ScenarioData _scenario;
    private readonly TriggerOptions _options;
    private readonly TriggerShape _shape;
    private readonly int[] _countingEvents;
    private readonly bool[] _keeps;
    private readonly int _gold;
    private readonly int _stone;
    private readonly int _food;
    private readonly int _wood;
    private TriggerState _state;

    private LegacyTriggerSystem(World world, ScenarioData scenario, TriggerOptions options)
    {
        _world = world;
        _scenario = scenario;
        _options = options;
        if (scenario.ObjectCount != world.Map.Data.Objects.Length)
        {
            throw new ArgumentException($"The scenario describes {scenario.ObjectCount} objects, the map has {world.Map.Data.Objects.Length}.", nameof(scenario));
        }

        var conversations = new int[scenario.Conversations.Length];
        for (int i = 0; i < conversations.Length; i++)
        {
            conversations[i] = scenario.Conversations[i].Phrases.Length;
        }

        var memory = new int[scenario.Events.Length];
        var counting = new List<int>();
        for (int i = 0; i < memory.Length; i++)
        {
            memory[i] = MemoryOf(scenario.Events[i]);
            if (memory[i] > 0)
            {
                counting.Add(i);
            }
        }

        _countingEvents = [.. counting];
        _keeps = new bool[scenario.Rules.Length];
        for (int r = 0; r < _keeps.Length; r++)
        {
            foreach (int a in scenario.Rules[r].ActionIndices)
            {
                _keeps[r] |= scenario.Actions[a] is KeepRuleAction;
            }
        }

        var ruleLengths = new int[scenario.Rules.Length];
        for (int r = 0; r < ruleLengths.Length; r++)
        {
            ruleLengths[r] = scenario.Rules[r].ActionIndices.Length;
        }

        _shape = new TriggerShape(scenario.Variables.Length, scenario.Events.Length, scenario.Actions.Length, scenario.Rules.Length, conversations, scenario.ObjectCount, world.Players.Length, memory, ruleLengths);
        _state = new TriggerState(_shape);
        _gold = world.Ruleset.ResourceId("gold");
        _stone = world.Ruleset.ResourceId("stone");
        _food = world.Ruleset.ResourceId("food");
        _wood = world.Ruleset.ResourceId("wood");
    }

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Scripts;

    /// <inheritdoc />
    public string Name => "triggers";

    /// <inheritdoc />
    public string Key => ExtensionKey;

    /// <summary>The scenario.</summary>
    public ScenarioData Scenario => _scenario;

    /// <summary>The settings.</summary>
    public TriggerOptions Options => _options;

    /// <summary>The runtime state (read it between ticks: quests, conversation, variables, the trigger debugger).</summary>
    public TriggerState State => _state;

    /// <summary>
    /// Adds the trigger engine to a simulation before its first tick: a new match (the map objects get their scenario
    /// properties and the Not Appear ones leave the world until they appear), or one just restored with
    /// <see cref="Simulation.Restore"/>, whose trigger state is read from the snapshot (it must have been saved with the
    /// same scenario and options).
    /// </summary>
    /// <exception cref="ArgumentException">The scenario does not fit the map or the match.</exception>
    /// <exception cref="InvalidDataException">The restored trigger state is invalid or belongs to another scenario.</exception>
    /// <exception cref="InvalidOperationException">The simulation already ran a tick.</exception>
    public static LegacyTriggerSystem Attach(Simulation simulation, ScenarioData scenario, TriggerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(scenario);
        options ??= TriggerOptions.Default;
        options.Validate();
        if (simulation.Systems.IsSealed)
        {
            throw new InvalidOperationException("The trigger engine must be attached before the first tick.");
        }

        World world = simulation.World;
        var system = new LegacyTriggerSystem(world, scenario, options);
        bool restored = world.Extensions.HasPending(ExtensionKey);
        if (!restored)
        {
            if (world.MapObjectEntities.Length != scenario.ObjectCount)
            {
                throw new InvalidOperationException("A new match must be attached before anything else changes its map objects.");
            }

            system.SetUp();
        }

        // Register first: it reads a restored state and may reject it; only an accepted engine joins the pipeline.
        world.Extensions.Register(system);
        world.Scenario = system;
        simulation.Systems.Add(system);
        simulation.Systems.Add(new Collector(system));
        return system;
    }

    // Match start: map objects take their scenario properties; Not Appear objects leave the world until Appear Object.
    private void SetUp()
    {
        ReadOnlySpan<EntityId> entities = _world.MapObjectEntities;
        for (int i = 0; i < entities.Length; i++)
        {
            EntityId e = entities[i];
            if (!_world.Entities.IsAlive(e))
            {
                continue;
            }

            ObjectInfo info = _scenario.ObjectAt(i);
            if ((info.Flags & ObjectFlags.NotAppear) != 0)
            {
                _world.Destroy(e);
                _state.Hidden[i] = true;
                continue;
            }

            _state.MapEntities[i] = e.Value;
            ScenarioSetup.ApplyInfo(_world, e, info);
        }

        // The first pass runs in the first tick (UNVERIFIED phase, UNK-51).
        _state.ClockMs = _options.IntervalMs;
    }

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Collect(_world, endOfTick: false);
        if (_state.Halted)
        {
            return;
        }

        _steps = 0;
        int tickMs = _world.Config.TickLengthMilliseconds;
        AdvanceTimers(tickMs);
        AnswerConversation();
        if (_state.Delayed.Count > 0)
        {
            RunDelayed(tickMs);
        }

        if (_state.PassRule < 0)
        {
            bool due = _state.ClockMs >= _options.IntervalMs;
            if (due)
            {
                _state.ClockMs -= _options.IntervalMs;
            }

            _state.ClockMs = Math.Min(_state.ClockMs + tickMs, TriggerState.TimerLimitMs);
            if (!due)
            {
                return;
            }

            _state.Passes = _state.Passes == int.MaxValue ? int.MaxValue : _state.Passes + 1;
            for (int e = 0; e < _state.EventValues.Length; e++)
            {
                _state.EventValues[e] = EvaluateEvent(e);
            }

            _state.PassRule = 0;
        }

        RunRules();
    }

    private void AdvanceTimers(int tickMs)
    {
        TimerState[] timers = _state.Timers;
        for (int i = 0; i < timers.Length; i++)
        {
            ref TimerState t = ref timers[i];
            if (!t.Running)
            {
                continue;
            }

            t.ValueMs = t.CountDown ? Math.Max(0, t.ValueMs - tickMs) : Math.Min(TriggerState.TimerLimitMs, t.ValueMs + tickMs);
        }
    }

    // The rules of the pass from where it stopped, until the end or the tick's budget (at least one rule per tick).
    private void RunRules()
    {
        int rules = _scenario.Rules.Length;
        bool first = true;
        while (_state.PassRule < rules)
        {
            if (!first && _steps >= _options.StepBudgetPerTick)
            {
                Fault(ScenarioFault.BudgetDeferred);
                return;
            }

            first = false;
            int r = _state.PassRule++;
            if (_state.RuleDisabled[r])
            {
                continue;
            }

            _rule = r;
            _action = -1;
            RuleDef rule = _scenario.Rules[r];

            // An empty or unparsable condition is false (ADR 0120 convention; UNVERIFIED, UNK-59).
            if (Evaluate(rule.Condition, 0, EntityId.None) == 0)
            {
                continue;
            }

            // "Disabled unless its action list contains Keep Rule" (Appendix C.3): decided by the list, also when a Delay
            // action postpones the rest of it.
            if (!_keeps[r])
            {
                _state.RuleDisabled[r] = true;
            }

            RunActions(r, 0);
        }

        _rule = -1;
        _action = -1;
        _state.PassRule = -1;
    }

    // Runs rule r's actions from position `from` until the end or a Delay action, which parks the rest (UNK-65).
    private void RunActions(int r, int from)
    {
        _rule = r;
        EquatableArray<int> actions = _scenario.Rules[r].ActionIndices;
        for (int k = from; k < actions.Length; k++)
        {
            int action = actions[k];
            if (_scenario.Actions[action] is DelayAction delay && k + 1 < actions.Length)
            {
                if (_state.Delayed.Count < TriggerState.MaxDelayed)
                {
                    _state.Delayed.Add(new DelayedActions(r, k + 1, Math.Max(0, delay.TimeMs)));
                }
                else
                {
                    Fault(ScenarioFault.ActionFailed);
                }

                return;
            }

            Run(action);
        }
    }

    // Delayed action lists whose time is up, in the order they were parked.
    private void RunDelayed(int tickMs)
    {
        List<DelayedActions> delayed = _state.Delayed;
        for (int i = 0; i < delayed.Count; i++)
        {
            delayed[i] = delayed[i] with { RemainingMs = delayed[i].RemainingMs - tickMs };
        }

        int count = delayed.Count;
        for (int i = 0; i < count && i < delayed.Count;)
        {
            if (delayed[i].RemainingMs > 0)
            {
                i++;
                continue;
            }

            DelayedActions due = delayed[i];
            delayed.RemoveAt(i);
            count--;
            RunActions(due.Rule, due.Next);
        }

        _rule = -1;
        _action = -1;
    }

    // ----- IScenarioController -------------------------------------------------------------------------------------

    /// <inheritdoc />
    public bool IsLocked(EntityId entity) => _state.IsLocked(entity.Value);

    /// <inheritdoc />
    public bool CannotWin(int player) => (uint)player < (uint)_state.PlayerStatus.Length && (_state.PlayerStatus[player] & 1) != 0;

    /// <inheritdoc />
    public bool CannotLose(int player) => (uint)player < (uint)_state.PlayerStatus.Length && (_state.PlayerStatus[player] & 2) != 0;

    /// <inheritdoc />
    public CommandRejection? Reply(World world, in Command command)
    {
        if (_state.Conversation < 0 || command.Arg0 != _state.ConversationSerial || (uint)command.Arg1 >= (uint)_state.Candidates.Count
            || _state.PendingReply >= 0 || (_options.ResponderMask & (1u << command.Player)) == 0)
        {
            return CommandRejection.NoScenarioPrompt;
        }

        _state.PendingReply = command.Arg1;
        return null;
    }

    // ----- IWorldStateExtension ------------------------------------------------------------------------------------

    private const byte HeaderVersion = 1;

    /// <inheritdoc />
    public void WriteTo(BinaryBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(HeaderVersion);
        writer.WriteUInt64(_scenario.ContentHash);
        writer.WriteUInt64(_options.Fingerprint());
        _state.WriteTo(writer);
    }

    /// <inheritdoc />
    public void ReadFrom(ReadOnlySpan<byte> data)
    {
        var reader = new BinaryBufferReader(data);
        if (reader.ReadByte() != HeaderVersion)
        {
            throw new InvalidDataException("Unsupported trigger state header.");
        }

        if (reader.ReadUInt64() != _scenario.ContentHash)
        {
            throw new InvalidDataException("The snapshot's trigger state belongs to another scenario.");
        }

        if (reader.ReadUInt64() != _options.Fingerprint())
        {
            throw new InvalidDataException("The snapshot's trigger state was saved with other trigger settings.");
        }

        TriggerState state = TriggerState.ReadFrom(data[reader.Position..], _shape);
        for (int i = 0; i < state.Locked.Count; i++)
        {
            if (state.Locked[i] == 0)
            {
                throw new InvalidDataException("A locked entity is invalid.");
            }
        }

        _state = state;
    }

    /// <summary>A fingerprint of the current state (tests).</summary>
    internal ulong StateHash()
    {
        var writer = new BinaryBufferWriter();
        WriteTo(writer);
        return StateHasher.Hash(writer.WrittenSpan, 0);
    }
}
