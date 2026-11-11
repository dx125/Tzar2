using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Session;
using Tzar2.Scripting.Legacy;

namespace Tzar2.Game.Editing;

/// <summary>Playtest settings (TDD §18.3: "as any player, time controls, reveal map").</summary>
/// <param name="Kingdom">The kingdom (1–8) the tester plays, or 0 for the map's own player (the first active Human or AI).</param>
/// <param name="RevealMap">No fog and the map explored.</param>
/// <param name="ComputerAi">Computer kingdoms run their AI.</param>
/// <param name="Cheats">Cheat codes allowed (the tester's tool box).</param>
public sealed record PlaytestOptions(int Kingdom = 0, bool RevealMap = false, bool ComputerAi = true, bool Cheats = true);

/// <summary>
/// Playtest from the editor (TDD §18.3, ADR 0153): the document is built into the compiled contract and started as a
/// scenario match exactly as a campaign mission (<see cref="ScenarioMatch"/>), with the tester's kingdom as the human
/// and, on request, the fog lifted.
/// </summary>
public static class Playtest
{
    /// <summary>The map and scenario of a document.</summary>
    /// <exception cref="ArgumentException">The document does not build (the validator's blocking findings).</exception>
    public static MissionContent Mission(MapDocument document, MapAssets? shared = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var assets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, System.Collections.Immutable.ImmutableArray<byte>> a in document.State.Assets)
        {
            assets[a.Key] = [.. a.Value];
        }

        return new MissionContent(document.BuildMap(), document.BuildScenario()) { Assets = new MapAssets(assets, shared) };
    }

    /// <summary>Whether the document may be played: no blocking finding under <paramref name="ruleset"/>.</summary>
    public static ValidationResult Check(MapDocument document, Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(document);
        return MapValidator.Validate(document, new ValidationOptions { Ruleset = ruleset });
    }

    /// <summary>The match of a playtest.</summary>
    /// <param name="mission">The built map and scenario.</param>
    /// <param name="ruleset">The ruleset.</param>
    /// <param name="options">Playtest settings.</param>
    /// <param name="carry">Campaign carry-over (campaign playtests), or null.</param>
    public static MatchSetup Setup(MissionContent mission, Ruleset ruleset, PlaytestOptions options, CampaignProgress? carry = null)
    {
        ArgumentNullException.ThrowIfNull(mission);
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(options);
        MissionContent played = options.Kingdom is >= 1 and <= 8 ? mission with { Scenario = AsKingdom(mission.Scenario, options.Kingdom) } : mission;
        MatchSetup setup = ScenarioMatch.Setup(played, ruleset, carry, options.ComputerAi, options.Cheats);
        return options.RevealMap ? setup with { NoFog = true, Explored = true } : setup;
    }

    /// <summary>A playtest session (not started; the client starts its sim thread, tests step it).</summary>
    public static GameSession Start(MapDocument document, Ruleset ruleset, PlaytestOptions options)
    {
        MatchSetup setup = Setup(Mission(document), ruleset, options);
        return new GameSession(setup, ruleset, fog: !options.RevealMap);
    }

    // The tested kingdom becomes the only Human-or-AI one (others play as AI), and active.
    private static ScenarioData AsKingdom(ScenarioData s, int kingdom)
    {
        var kingdoms = new List<KingdomDef>();
        bool found = false;
        foreach (KingdomDef k in s.Kingdoms)
        {
            if (k.Number == kingdom)
            {
                kingdoms.Add(k with { PlayerType = PlayerType.HumanOrAi, Active = true });
                found = true;
            }
            else
            {
                kingdoms.Add(k.PlayerType == PlayerType.HumanOrAi ? k with { PlayerType = PlayerType.AiOnly } : k);
            }
        }

        if (!found)
        {
            kingdoms.Add(ScenarioModel.DefaultKingdom(kingdom) with { PlayerType = PlayerType.HumanOrAi });
            kingdoms.Sort((a, b) => a.Number.CompareTo(b.Number));
        }

        return new ScenarioData(s.Properties, s.ObjectCount, s.Objects, EquatableArray<KingdomDef>.From(kingdoms), s.Diplomacy, s.Variables, s.Events, s.Actions, s.Rules, s.Conversations, s.Sounds, s.Music);
    }
}

/// <summary>A rule that fired or an action that ran, when (the trigger history, ADR 0290).</summary>
/// <param name="Tick">The tick.</param>
/// <param name="IsAction">An action (else a rule).</param>
/// <param name="Name">Its name.</param>
public sealed record DebugFiring(int Tick, bool IsAction, string Name);

/// <summary>One line of the trigger debugger's event log.</summary>
/// <param name="Tick">The tick.</param>
/// <param name="Text">What happened.</param>
public sealed record DebugLogEntry(int Tick, string Text);

/// <summary>The trigger debugger's view of the trigger state after a tick (immutable, safe to read on the main thread).</summary>
/// <param name="Tick">The tick it shows.</param>
/// <param name="Passes">Rule passes so far.</param>
/// <param name="Variables">User variables and their values.</param>
/// <param name="Events">Events and whether they hold.</param>
/// <param name="Timers">Timers: Set Timer actions that ran, their value and whether they count.</param>
/// <param name="Rules">Rules: last condition value (null = not evaluated yet) and whether they are disabled.</param>
public sealed record DebugWatch(
    int Tick,
    int Passes,
    ImmutableArray<(string Name, int Value)> Variables,
    ImmutableArray<(string Name, bool Value)> Events,
    ImmutableArray<(string Name, long Ms, bool Running)> Timers,
    ImmutableArray<(string Name, int? LastValue, bool Disabled)> Rules);

/// <summary>
/// The trigger debugger (TDD §18.3, ADR 0153): the rule list with the last evaluation result, a variable/event/timer
/// watch, breakpoints on rules (when they fire) and actions (when they run), stepping one tick, and an event log. It
/// observes the engine through <see cref="ITriggerTrace"/> and reads the state after each tick; it never changes the match,
/// so a debugged playtest plays exactly like an undebugged one.
/// </summary>
public sealed class TriggerDebugger : ITriggerTrace, IDisposable
{
    /// <summary>Most log entries kept.</summary>
    public const int MaxLog = 2000;

    /// <summary>Most history entries kept (the oldest go first).</summary>
    public const int MaxHistory = 4000;

    private readonly object _gate = new();
    private readonly ScenarioData _scenario;
    private GameSession _session;
    private LegacyTriggerSystem _triggers;
    private readonly int?[] _last;
    private readonly SortedSet<int> _ruleBreaks = [];
    private readonly SortedSet<int> _actionBreaks = [];
    private readonly List<DebugLogEntry> _log = [];
    private readonly List<DebugFiring> _history = [];
    private bool _breakRequested;
    private string? _hit;
    private DebugWatch? _watch;

    /// <summary>Attaches to a scenario session.</summary>
    /// <exception cref="ArgumentException">The session has no scenario.</exception>
    public TriggerDebugger(GameSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _triggers = session.Scenario?.Triggers ?? throw new ArgumentException("The session plays no scenario.", nameof(session));
        _scenario = _triggers.Scenario;
        _last = new int?[_scenario.Rules.Length];
        _triggers.Trace = this;
        session.AfterTick += OnTick;
        _watch = Capture();
    }

    /// <summary>The session being debugged (replaced by <see cref="MoveTo"/>).</summary>
    public GameSession Session => _session;

    /// <summary>
    /// Follows the match to <paramref name="session"/>, restored by a rewind (ADR 0285; call before it starts): the
    /// breakpoints stay, the log keeps what happened before the session's tick, the rules' last results are unknown again.
    /// </summary>
    /// <exception cref="ArgumentException">The session plays no scenario.</exception>
    public void MoveTo(GameSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        LegacyTriggerSystem triggers = session.Scenario?.Triggers ?? throw new ArgumentException("The session plays no scenario.", nameof(session));
        _session.AfterTick -= OnTick;
        if (ReferenceEquals(_triggers.Trace, this))
        {
            _triggers.Trace = null;
        }

        _session = session;
        _triggers = triggers;
        triggers.Trace = this;
        session.AfterTick += OnTick;
        lock (_gate)
        {
            Array.Clear(_last);
            _breakRequested = false;
            _hit = null;
        }

        DebugWatch watch = Capture();
        lock (_gate)
        {
            _watch = watch;
            _log.RemoveAll(e => e.Tick >= watch.Tick);
            _history.RemoveAll(h => h.Tick >= watch.Tick);
        }

        Add(watch.Tick, string.Create(CultureInfo.InvariantCulture, $"rewound to tick {watch.Tick}"));
    }

    /// <summary>The names of the scenario's actions, by index (the breakpoint list).</summary>
    public ImmutableArray<string> ActionNames => [.. _scenario.Actions.Items.Select(a => a.Name)];

    /// <summary>Whether to log every rule evaluation (else only firing rules and actions).</summary>
    public bool LogEvaluations { get; set; }

    /// <summary>The latest watch.</summary>
    public DebugWatch Watch
    {
        get
        {
            lock (_gate)
            {
                return _watch!;
            }
        }
    }

    /// <summary>The last breakpoint hit ("rule X fired at tick N"), or null.</summary>
    public string? Hit
    {
        get
        {
            lock (_gate)
            {
                return _hit;
            }
        }
    }

    /// <summary>
    /// The rules that fired and the actions that ran, in order, with their ticks (ADR 0290): the trigger history, from
    /// which the editor rewinds a playtest to a moment.
    /// </summary>
    public ImmutableArray<DebugFiring> History
    {
        get
        {
            lock (_gate)
            {
                return [.. _history];
            }
        }
    }

    /// <summary>A copy of the event log.</summary>
    public ImmutableArray<DebugLogEntry> Log
    {
        get
        {
            lock (_gate)
            {
                return [.. _log];
            }
        }
    }

    /// <summary>Sets or clears a breakpoint on a rule (it pauses after the tick in which the rule fires).</summary>
    public void BreakOnRule(int rule, bool on)
    {
        lock (_gate)
        {
            _ = on ? _ruleBreaks.Add(rule) : _ruleBreaks.Remove(rule);
        }
    }

    /// <summary>Sets or clears a breakpoint on an action (it pauses after the tick in which the action runs).</summary>
    public void BreakOnAction(int action, bool on)
    {
        lock (_gate)
        {
            _ = on ? _actionBreaks.Add(action) : _actionBreaks.Remove(action);
        }
    }

    /// <summary>Whether a rule has a breakpoint.</summary>
    public bool HasRuleBreak(int rule)
    {
        lock (_gate)
        {
            return _ruleBreaks.Contains(rule);
        }
    }

    /// <summary>Whether an action has a breakpoint.</summary>
    public bool HasActionBreak(int action)
    {
        lock (_gate)
        {
            return _actionBreaks.Contains(action);
        }
    }

    /// <summary>Pauses the match.</summary>
    public void Pause() => _session.Paused = true;

    /// <summary>Resumes after a breakpoint or a pause.</summary>
    public void Continue()
    {
        lock (_gate)
        {
            _hit = null;
        }

        _session.Paused = false;
    }

    /// <summary>Runs one tick while paused.</summary>
    public void Step() => _session.StepPaused();

    /// <inheritdoc/>
    public void PassStarted(int tick, int pass)
    {
        if (LogEvaluations)
        {
            Add(tick, string.Create(CultureInfo.InvariantCulture, $"pass {pass}"));
        }
    }

    /// <inheritdoc/>
    public void RuleEvaluated(int tick, int rule, int value)
    {
        lock (_gate)
        {
            if ((uint)rule < (uint)_last.Length)
            {
                _last[rule] = value;
            }

            if (value != 0 && _ruleBreaks.Contains(rule))
            {
                _breakRequested = true;
                _hit = string.Create(CultureInfo.InvariantCulture, $"rule '{_scenario.Rules[rule].Name}' fired at tick {tick}");
            }
        }

        if (value != 0)
        {
            Remember(new DebugFiring(tick, false, _scenario.Rules[rule].Name));
        }

        if (value != 0 || LogEvaluations)
        {
            Add(tick, string.Create(CultureInfo.InvariantCulture, $"rule '{_scenario.Rules[rule].Name}' = {value}{(value != 0 ? " → fires" : string.Empty)}"));
        }
    }

    /// <inheritdoc/>
    public void ActionRun(int tick, int rule, int action)
    {
        lock (_gate)
        {
            if (_actionBreaks.Contains(action))
            {
                _breakRequested = true;
                _hit = string.Create(CultureInfo.InvariantCulture, $"action '{_scenario.Actions[action].Name}' ran at tick {tick}");
            }
        }

        Remember(new DebugFiring(tick, true, _scenario.Actions[action].Name));
        Add(tick, $"  action '{_scenario.Actions[action].Name}' ({_scenario.Actions[action].GetType().Name.Replace("Action", string.Empty, StringComparison.Ordinal)})");
    }

    private void Remember(DebugFiring firing)
    {
        lock (_gate)
        {
            if (_history.Count >= MaxHistory)
            {
                _history.RemoveRange(0, MaxHistory / 4);
            }

            _history.Add(firing);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _session.AfterTick -= OnTick;
        if (ReferenceEquals(_triggers.Trace, this))
        {
            _triggers.Trace = null;
        }
    }

    private void Add(int tick, string text)
    {
        lock (_gate)
        {
            if (_log.Count >= MaxLog)
            {
                _log.RemoveRange(0, MaxLog / 4);
            }

            _log.Add(new DebugLogEntry(tick, text));
        }
    }

    // Sim thread, after a tick.
    private void OnTick(GameSession session)
    {
        DebugWatch watch = Capture();
        bool pause;
        lock (_gate)
        {
            _watch = watch;
            pause = _breakRequested;
            _breakRequested = false;
        }

        if (pause)
        {
            session.Paused = true;
            Add(watch.Tick, "breakpoint: " + Hit);
        }
    }

    private DebugWatch Capture()
    {
        TriggerState s = _triggers.State;
        var variables = ImmutableArray.CreateBuilder<(string, int)>(_scenario.Variables.Length);
        for (int i = 0; i < _scenario.Variables.Length; i++)
        {
            variables.Add((_scenario.Variables[i], s.Variables[i]));
        }

        var events = ImmutableArray.CreateBuilder<(string, bool)>(_scenario.Events.Length);
        for (int i = 0; i < _scenario.Events.Length; i++)
        {
            events.Add((_scenario.Events[i].Name, s.EventValues[i]));
        }

        var timers = ImmutableArray.CreateBuilder<(string, long, bool)>();
        for (int i = 0; i < _scenario.Actions.Length; i++)
        {
            if (_scenario.Actions[i] is SetTimerAction && s.Timers[i].Started)
            {
                timers.Add((_scenario.Actions[i].Name, s.Timers[i].ValueMs, s.Timers[i].Running));
            }
        }

        var rules = ImmutableArray.CreateBuilder<(string, int?, bool)>(_scenario.Rules.Length);
        lock (_gate)
        {
            for (int i = 0; i < _scenario.Rules.Length; i++)
            {
                rules.Add((_scenario.Rules[i].Name, _last[i], s.RuleDisabled[i]));
            }
        }

        return new DebugWatch(_session.Simulation.World.Tick.Value, s.Passes, variables.MoveToImmutable(), events.MoveToImmutable(), timers.ToImmutable(), rules.MoveToImmutable());
    }
}
