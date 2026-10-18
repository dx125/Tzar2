using System;
using System.Globalization;
using System.Linq;
using Godot;
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
    private readonly GameSession _session;
    private readonly ItemList _rules = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly ItemList _actions = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly ItemList _watch = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly ItemList _log = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly Label _status = new();
    private int _logShown = -1;
    private int _tickShown = -1;

    /// <summary>Creates the window over a debugged session.</summary>
    public DebuggerWindow(TriggerDebugger debugger, GameSession session)
    {
        _debugger = debugger;
        _session = session;
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
        Forms.Button(controls, "Slower", () => _session.SpeedIndex--);
        Forms.Button(controls, "Faster", () => _session.SpeedIndex++);
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
        CloseRequested += Hide;
        foreach (string action in debugger.ActionNames)
        {
            _actions.AddItem(action);
        }
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        DebugWatch w = _debugger.Watch;
        _status.Text = $"tick {w.Tick} · pass {w.Passes} · {(_session.Paused ? "PAUSED" : "running")} · speed {_session.SpeedPercent} %" + (_debugger.Hit is { } hit ? " · " + hit : string.Empty);
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
