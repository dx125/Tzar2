using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Tzar2.Content.Compiled;
using Tzar2.Game.Editing;

namespace Tzar2.Client.Editor;

/// <summary>
/// The editor's playability check (ADR 0290): the acceptance's bot plays the map or the campaign headless on a worker
/// thread; the window shows its progress, then each attempt (completed or lost, by play alone or with help) and, for the
/// selected one, where the bot got to (units, the rules that fired, the variables, its goal).
/// </summary>
public partial class PlayabilityWindow : Window
{
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly Label _status = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly ItemList _rows = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 180) };
    private readonly TextEdit _trace = new() { Editable = false, SizeFlagsVertical = Control.SizeFlags.ExpandFill, WrapMode = TextEdit.LineWrappingMode.Boundary };
    private readonly Task<PlayabilityReport> _run;
    private readonly Button _stop;
    private PlayabilityReport? _report;
    private bool _shown;

    /// <summary>Starts the check of <paramref name="subject"/> (built by <see cref="PlayabilityCheck.Prepare(Tzar2.Content.Editing.MapDocument)"/>).</summary>
    public PlayabilityWindow(string name, PlayabilitySubject subject, Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(subject);
        Title = "Playability check: " + name;
        Size = new Vector2I(760, 600);
        Transient = false;
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var box = new VBoxContainer();
        margin.AddChild(box);
        AddChild(margin);
        Forms.Label(box, "The acceptance's bot plays it as a player would: it answers conversations, walks the units a rule waits for into their areas and attacks. A lost mission is tried again with help (reinforcements, the enemies cleared, the heroes protected).").AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.Text = "Starting…";
        box.AddChild(_status);
        box.AddChild(_rows);
        Forms.Label(box, "Where the bot got to (the selected attempt):");
        box.AddChild(_trace);
        var buttons = new HBoxContainer();
        _stop = Forms.Button(buttons, "Stop", () => _cancel.Cancel());
        Forms.Button(buttons, "Close", Close);
        box.AddChild(buttons);
        _rows.ItemSelected += i => ShowTrace((int)i);
        CloseRequested += Close;
        _run = Task.Run(() => PlayabilityCheck.Run(subject, ruleset, _cancel.Token, line => _lines.Enqueue(line)));
    }

    /// <inheritdoc/>
    public override void _Ready() => Forms.ShowCentered(this);

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        string? last = null;
        while (_lines.TryDequeue(out string? line))
        {
            last = line;
        }

        if (last is not null && !_shown)
        {
            _status.Text = "Playing… " + last;
        }

        if (_shown || !_run.IsCompleted)
        {
            return;
        }

        _shown = true;
        _stop.Disabled = true;
        if (_run.IsFaulted)
        {
            _status.Text = "The check failed: " + (_run.Exception?.InnerException?.Message ?? "unknown error");
            return;
        }

        _report = _run.Result;
        _status.Text = _report.Summary;
        foreach (PlayabilityRow r in _report.Rows)
        {
            string time = string.Create(CultureInfo.InvariantCulture, $"{r.GameSeconds / 60}:{r.GameSeconds % 60:00}");
            _rows.AddItem($"{(r.ByPlay ? "✓" : r.Completed ? "◐" : "✗")}  {r.MapId}: {r.How}, {time}{(r.NextMapId is { } next ? " → " + next : string.Empty)}{(r.Faults > 0 ? string.Create(CultureInfo.InvariantCulture, $", {r.Faults} scenario fault(s)") : string.Empty)}");
        }

        if (_report.Rows.Length > 0)
        {
            int first = Math.Max(0, _report.Rows.IndexOf(_report.Rows.FirstOrDefault(r => !r.Completed) ?? _report.Rows[0]));
            _rows.Select(first);
            ShowTrace(first);
        }
    }

    private void ShowTrace(int index)
    {
        if (_report is { } report && (uint)index < (uint)report.Rows.Length)
        {
            _trace.Text = report.Rows[index].Trace.Replace(" | ", "\n", StringComparison.Ordinal);
        }
    }

    private void Close()
    {
        _cancel.Cancel();
        QueueFree();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancel.Dispose();
        }

        base.Dispose(disposing);
    }
}
