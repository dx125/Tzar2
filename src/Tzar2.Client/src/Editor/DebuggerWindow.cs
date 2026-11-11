using System;
using System.Globalization;
using System.Linq;
using Godot;
using Tzar2.Core.Ids;
using Tzar2.Game.Editing;
using Tzar2.Game.Session;

namespace Tzar2.Client.Editor;

/// <summary>
/// The trigger debugger during a playtest (TDD §18.3): the rule list with the last evaluation result and a breakpoint
/// per rule, the actions with breakpoints, the variable, event and timer watch, pause / continue / step, the game speed,
/// and the event log.
/// </summary>
public partial class DebuggerWindow : Window
{
    private readonly TriggerDebugger _debugger;
    private readonly ItemList _rules = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly ItemList _actions = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly ItemList _watch = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly ItemList _log = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly ItemList _history = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private System.Collections.Immutable.ImmutableArray<DebugFiring> _historyShown = [];
    private int _historyCount = -1;
    private DebugFiring? _historyLast;
    private readonly Label _status = new();
    private int _logShown = -1;
    private int _tickShown = -1;

    /// <summary>Creates the window over a debugger (it follows the debugger's session through rewinds, ADR 0285).</summary>
    public DebuggerWindow(TriggerDebugger debugger)
    {
        _debugger = debugger;
        Title = "Trigger debugger";
        Size = new Vector2I(760, 620);
        Position = new Vector2I(40, 40);
        Transient = false;
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var box = new VBoxContainer();
        margin.AddChild(box);
        AddChild(margin);
        var controls = new HBoxContainer();
        Forms.Button(controls, "Pause", _debugger.Pause);
        Forms.Button(controls, "Continue", _debugger.Continue);
        Forms.Button(controls, "Step (1 tick)", _debugger.Step);
        Forms.Button(controls, "Slower", () => _debugger.Session.SpeedIndex--);
        Forms.Button(controls, "Faster", () => _debugger.Session.SpeedIndex++);
        Forms.Check(controls, "Log every evaluation", false, v => _debugger.LogEvaluations = v, inGrid: false);
        box.AddChild(controls);
        box.AddChild(_status);
        var split = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var left = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        Forms.Label(left, "Rules (double-click: breakpoint)");
        left.AddChild(_rules);
        Forms.Label(left, "Actions (double-click: breakpoint)");
        left.AddChild(_actions);
        var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        Forms.Label(right, "Watch");
        right.AddChild(_watch);
        Forms.Label(right, "Event log");
        right.AddChild(_log);
        Forms.Label(right, "History (double-click: rewind the playtest to that moment)");
        right.AddChild(_history);
        split.AddChild(left);
        split.AddChild(right);
        box.AddChild(split);
        _rules.ItemActivated += i =>
        {
            _debugger.BreakOnRule((int)i, !_debugger.HasRuleBreak((int)i));
            _tickShown = -1;
        };
        _actions.ItemActivated += i =>
        {
            _debugger.BreakOnAction((int)i, !_debugger.HasActionBreak((int)i));
            _tickShown = -1;
        };
        _history.ItemActivated += i =>
        {
            // ADR 0290: back to the rewind point at or before the moment, a little before the rule fired.
            if ((uint)i < (uint)_historyShown.Length && RewindTo is { } rewind)
            {
                int tick = _historyShown[(int)i].Tick;
                _status.Text = rewind(Math.Max(0, tick - 1)) ? $"Rewound to before tick {tick}." : "No rewind point that early.";
            }
        };
        CloseRequested += Hide;
        foreach (string action in debugger.ActionNames)
        {
            _actions.AddItem(action);
        }
    }

    /// <summary>Rewinds the playtest to the point at or before a tick (the game screen's; ADR 0285), or null without rewind.</summary>
    public Func<int, bool>? RewindTo { get; set; }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        System.Collections.Immutable.ImmutableArray<DebugFiring> history = _debugger.History;
        DebugFiring? newest = history.Length > 0 ? history[^1] : null;
        if (history.Length != _historyCount || newest != _historyLast)
        {
            _historyCount = history.Length;
            _historyLast = newest;
            _history.Clear();
            int tickMs = Tick.DefaultLengthMilliseconds;
            foreach (DebugFiring f in history.Skip(Math.Max(0, history.Length - 500)))
            {
                int seconds = (int)((long)f.Tick * tickMs / 1000);
                _history.AddItem(string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}  {(f.IsAction ? "action" : "rule")} '{f.Name}'"));
            }

            _historyShown = [.. history.Skip(Math.Max(0, history.Length - 500))];
        }

        DebugWatch w = _debugger.Watch;
        GameSession session = _debugger.Session;
        _status.Text = $"tick {w.Tick} · pass {w.Passes} · {(session.Paused ? "PAUSED" : "running")} · speed {session.SpeedPercent} %" + (_debugger.Hit is { } hit ? " · " + hit : string.Empty);
        if (w.Tick != _tickShown)
        {
            _tickShown = w.Tick;
            _rules.Clear();
            for (int i = 0; i < w.Rules.Length; i++)
            {
                (string name, int? last, bool disabled) = w.Rules[i];
                string value = last is int v ? v.ToString(CultureInfo.InvariantCulture) : "–";
                _rules.AddItem($"{(_debugger.HasRuleBreak(i) ? "● " : "   ")}{name} = {value}{(disabled ? " (done)" : string.Empty)}");
            }

            for (int i = 0; i < _actions.ItemCount; i++)
            {
                string text = _actions.GetItemText(i).TrimStart('●', ' ');
                _actions.SetItemText(i, (_debugger.HasActionBreak(i) ? "● " : "   ") + text);
            }

            _watch.Clear();
            foreach ((string name, int value) in w.Variables)
            {
                _watch.AddItem($"${name} = {value}");
            }

            foreach ((string name, bool value) in w.Events)
            {
                _watch.AddItem($"{name} = {(value ? 1 : 0)}");
            }

            foreach ((string name, long ms, bool running) in w.Timers)
            {
                _watch.AddItem($"{name} = {ms} ms{(running ? string.Empty : " (stopped)")}");
            }
        }

        var log = _debugger.Log;
        if (log.Length != _logShown)
        {
            _logShown = log.Length;
            _log.Clear();
            foreach (DebugLogEntry e in log.Skip(Math.Max(0, log.Length - 300)))
            {
                _log.AddItem($"[{e.Tick}] {e.Text}");
            }

            if (_log.ItemCount > 0)
            {
                _log.EnsureCurrentIsVisible();
                _log.Select(_log.ItemCount - 1);
            }
        }
    }
}
