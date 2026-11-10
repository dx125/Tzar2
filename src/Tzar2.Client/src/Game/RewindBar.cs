using System;
using System.Collections.Immutable;
using System.Globalization;
using Godot;
using Tzar2.Game.Session;

namespace Tzar2.Client.Game;

/// <summary>
/// The rewind bar of a playtest (ADR 0285), at the top of the screen: back a minute, back to the previous point
/// (Ctrl+Backspace), a time line to drag back to any point, pause, the speed (to 2000 %), and FINISH once the match has
/// ended (it waits so that the tester can go back instead).
/// </summary>
public partial class RewindBar : Control
{
    private const float Width = 660;
    private readonly GameScreen _screen;
    private readonly MatchRewind _rewind;
    private readonly HSlider _line = new() { Position = new Vector2(150, 7), Size = new Vector2(220, 18), Step = 1, FocusMode = FocusModeEnum.None };
    private readonly Label _time = new() { Position = new Vector2(376, 6), Size = new Vector2(92, 20), MouseFilter = MouseFilterEnum.Ignore };
    private readonly Button _play = new() { Position = new Vector2(470, 3), Size = new Vector2(52, 24), FocusMode = FocusModeEnum.None };
    private readonly Label _speed = new() { Position = new Vector2(548, 6), Size = new Vector2(44, 20), MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button _finish = new() { Position = new Vector2(0, 32), Size = new Vector2(Width, 26), Visible = false, FocusMode = FocusModeEnum.None };
    private bool _dragging;

    /// <summary>Creates the bar of <paramref name="screen"/>'s rewind.</summary>
    public RewindBar(GameScreen screen, MatchRewind rewind)
    {
        _screen = screen;
        _rewind = rewind;
        Size = new Vector2(Width, 30);
        MouseFilter = MouseFilterEnum.Ignore;
        AddChild(new ColorRect { Size = Size, Color = new Color(0, 0, 0, 0.6f), MouseFilter = MouseFilterEnum.Ignore });
        AddButton(new Vector2(4, 3), 70, "<< 1 min", "Back a minute of game time", () => Back(() => _screen.RewindBy(60)));
        AddButton(new Vector2(76, 3), 70, "< Back", $"Back to the previous point (every {MatchRewind.IntervalSeconds} s of game time; Ctrl+Backspace)", () => Back(_screen.RewindBack));
        _line.TooltipText = "Drag back to any earlier point";
        _line.DragStarted += () => _dragging = true;
        _line.DragEnded += changed =>
        {
            _dragging = false;
            if (changed && (int)_line.Value < _rewind.Tick)
            {
                Back(() => _screen.RewindTo((int)_line.Value));
            }
        };
        AddChild(_line);
        AddChild(_time);
        _play.Pressed += () => _rewind.Session.Paused = !_rewind.Session.Paused;
        AddChild(_play);
        AddButton(new Vector2(524, 3), 22, "-", "Slower", () => _rewind.Session.SpeedIndex--);
        AddChild(_speed);
        AddButton(new Vector2(594, 3), 22, "+", "Faster (to 2000 %)", () => _rewind.Session.SpeedIndex++);
        _finish.Pressed += _screen.FinishHeldOutcome;
        AddChild(_finish);
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        Position = new Vector2(MathF.Floor((GetViewportRect().Size.X - Width) / 2), 2);
        GameSession session = _rewind.Session;
        int tick = _rewind.Tick;
        ImmutableArray<int> points = _rewind.Points;
        int first = points.IsEmpty ? 0 : points[0];
        _line.MinValue = first;
        _line.MaxValue = Math.Max(first + 1, tick);
        if (!_dragging)
        {
            _line.SetValueNoSignal(tick);
        }

        _time.Text = _dragging ? "to " + Clock(PointAtOrBefore(points, (int)_line.Value)) : Clock(tick);
        _play.Text = session.Paused ? "Play" : "Pause";
        _speed.Text = "x" + (session.SpeedPercent / 100.0).ToString("0.##", CultureInfo.InvariantCulture);
        MatchOutcome held = _screen.HeldOutcome;
        _finish.Visible = held != MatchOutcome.None;
        _finish.Text = held == MatchOutcome.Victory ? "Victory. Go back, or FINISH the match" : "Defeat. Go back, or FINISH the match";
    }

    private static int PointAtOrBefore(ImmutableArray<int> points, int tick)
    {
        int best = points.IsEmpty ? 0 : points[0];
        foreach (int p in points)
        {
            if (p <= tick)
            {
                best = p;
            }
        }

        return best;
    }

    private string Clock(int tick)
    {
        int seconds = (int)((long)tick * _rewind.TickMilliseconds / 1000);
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }

    private void Back(Func<bool> rewind)
    {
        if (!rewind())
        {
            _screen.Message("No earlier rewind point.");
        }
    }

    private void AddButton(Vector2 at, float width, string text, string tip, Action pressed)
    {
        var button = new Button { Position = at, Size = new Vector2(width, 24), Text = text, TooltipText = tip, FocusMode = FocusModeEnum.None };
        button.Pressed += pressed;
        AddChild(button);
    }
}
