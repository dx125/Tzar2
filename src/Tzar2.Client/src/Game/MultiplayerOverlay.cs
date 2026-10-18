using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Tzar2.Game.Multiplayer;
using Tzar2.Game.Session;

namespace Tzar2.Client.Game;

/// <summary>
/// The in-game multiplayer layer (REQ-NET-03, REQ-NET-07, REQ-NET-10; ADR 0168) over the game screen: the chat log and
/// the chat line (Enter opens it, Tab cycles all / allies / enemies, Esc closes; <c>hm*</c> codes go to the sim, which
/// refuses them unless the host allowed cheats), the player list with pings and controllers, a dropped player's
/// countdown with "wait longer", the save consent prompt, and the end of the match.
/// </summary>
public partial class MultiplayerOverlay : Control
{
    private const int VisibleLines = 8;
    private const double LineSeconds = 12;

    private readonly NetworkMatch _match;
    private readonly GameSession _session;
    private readonly VBoxContainer _log = new() { Position = new Vector2(10, 40), Size = new Vector2(620, 180), MouseFilter = MouseFilterEnum.Ignore };
    private readonly LineEdit _input = new() { Position = new Vector2(10, 226), Size = new Vector2(500, 26), Visible = false, MaxLength = 200 };
    private readonly Label _target = new() { Position = new Vector2(516, 230), Visible = false, MouseFilter = MouseFilterEnum.Ignore };
    private readonly Label _players = new() { Position = new Vector2(780, 40), Size = new Vector2(236, 160), MouseFilter = MouseFilterEnum.Ignore };
    private readonly PanelContainer _prompt = new() { Position = new Vector2(312, 300), Visible = false };
    private readonly Label _promptText = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(380, 0) };
    private readonly HBoxContainer _promptButtons = new();
    private readonly List<(Label Label, double Born)> _lines = [];
    private readonly List<ChatLine> _drain = [];
    private ChatTarget _chatTarget;
    private string _promptKind = string.Empty;
    private double _now;

    /// <summary>Creates the layer for <paramref name="match"/>'s running <paramref name="session"/>.</summary>
    public MultiplayerOverlay(NetworkMatch match, GameSession session)
    {
        _match = match;
        _session = session;
        Size = new Vector2(1024, 768);
        MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_log);
        AddChild(_input);
        AddChild(_target);
        AddChild(_players);
        var box = new VBoxContainer();
        box.AddChild(_promptText);
        box.AddChild(_promptButtons);
        _prompt.AddChild(box);
        AddChild(_prompt);
        _input.TextSubmitted += Send;
    }

    /// <summary>The player chose to leave after the end (or LEAVE in the prompt).</summary>
    public event Action? LeaveRequested;

    /// <summary>Opens the chat line (also the HUD's chat button).</summary>
    public void OpenChat()
    {
        _input.Visible = true;
        _target.Visible = true;
        _target.Text = TargetName();
        _input.GrabFocus();
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        _now += delta;
        _session.DrainChat(_drain);
        foreach (ChatLine line in _drain)
        {
            var label = new Label
            {
                Text = line.Kind == ChatLineKind.System ? line.Text : $"{line.Sender}{(line.Recipients == 0xFF ? string.Empty : " (private)")}: {line.Text}",
                Modulate = line.Kind == ChatLineKind.System ? new Color(1f, 0.85f, 0.5f) : Colors.White,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            _log.AddChild(label);
            _lines.Add((label, _now));
        }

        _drain.Clear();
        while (_lines.Count > VisibleLines || (_lines.Count > 0 && _now - _lines[0].Born > LineSeconds && !_input.Visible))
        {
            _lines[0].Label.QueueFree();
            _lines.RemoveAt(0);
        }

        _players.Text = string.Join('\n', _match.Players.Select(p => p.Control switch
        {
            PlayerControl.Human => string.Create(CultureInfo.InvariantCulture, $"{p.Name}   {p.PingMs} ms"),
            PlayerControl.Dropped => $"{p.Name}   dropped {p.DroppedSeconds} s",
            PlayerControl.Computer when p.Name.Length > 0 && !p.Name.StartsWith("Computer", StringComparison.Ordinal) => $"{p.Name}   (computer)",
            _ => $"{p.Name}   {p.Control.ToString().ToLowerInvariant()}",
        })) + string.Create(CultureInfo.InvariantCulture, $"\ndelay {_match.InputDelay} ticks, RTT {_match.RttMs} ms");
        UpdatePrompt();
    }

    /// <inheritdoc/>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true } key)
        {
            return;
        }

        if (_input.Visible)
        {
            if (key.Keycode == Key.Escape)
            {
                CloseChat();
                GetViewport().SetInputAsHandled();
            }

            return;
        }

        if (key.Keycode is Key.Enter or Key.KpEnter)
        {
            OpenChat();
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.F2 && _match.IsHost)
        {
            // Multiplayer save (REQ-NET-10): a proposal every player must agree to.
            _match.RequestSave(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            GetViewport().SetInputAsHandled();
        }
    }

    /// <inheritdoc/>
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Tab } && _input.HasFocus())
        {
            _chatTarget = _chatTarget switch { ChatTarget.All => ChatTarget.Allies, ChatTarget.Allies => ChatTarget.Enemies, _ => ChatTarget.All };
            _target.Text = TargetName();
            AcceptEvent();
        }
    }

    private void Send(string text)
    {
        if (text.Trim().Length > 0)
        {
            _session.SubmitChat(text, _chatTarget);
        }

        CloseChat();
    }

    private void CloseChat()
    {
        _input.Clear();
        _input.Visible = false;
        _target.Visible = false;
        _input.ReleaseFocus();
    }

    private string TargetName() => _chatTarget switch { ChatTarget.Allies => "to allies (Tab)", ChatTarget.Enemies => "to enemies (Tab)", _ => "to all (Tab)" };

    private void UpdatePrompt()
    {
        string kind;
        string text;
        if (_match.Stage == LobbyStage.Ended)
        {
            kind = "end";
            text = _match.EndReason ?? "The match ended.";
        }
        else if (_match.PendingSave is { } save)
        {
            kind = "save" + save.Id.ToString(CultureInfo.InvariantCulture);
            text = $"{save.By} wants to save the game{(save.Label.Length > 0 ? " (" + save.Label + ")" : string.Empty)}. Every player must agree.";
        }
        else if (_match.Players.FirstOrDefault(p => p.Control == PlayerControl.Dropped) is { } dropped)
        {
            kind = "drop" + dropped.Slot.ToString(CultureInfo.InvariantCulture);
            text = $"{dropped.Name} dropped {dropped.DroppedSeconds} s ago. When the timeout ends the computer (or the lobby's rule) takes the kingdom over.";
        }
        else
        {
            kind = string.Empty;
            text = string.Empty;
        }

        _prompt.Visible = kind.Length > 0;
        _promptText.Text = text;
        if (kind == _promptKind)
        {
            return;
        }

        _promptKind = kind;
        foreach (Node child in _promptButtons.GetChildren())
        {
            child.QueueFree();
        }

        if (kind == "end")
        {
            AddButton("LEAVE", () => LeaveRequested?.Invoke());
        }
        else if (kind.StartsWith("save", StringComparison.Ordinal))
        {
            AddButton("AGREE", () => _match.VoteSave(true));
            AddButton("REFUSE", () => _match.VoteSave(false));
        }
        else if (kind.StartsWith("drop", StringComparison.Ordinal) && _match.Players.FirstOrDefault(p => p.Control == PlayerControl.Dropped) is { } d)
        {
            AddButton("WAIT LONGER", () => _match.VoteWait(d.Slot));
        }
    }

    private void AddButton(string text, Action pressed)
    {
        var button = new Button { Text = text };
        button.Pressed += pressed;
        _promptButtons.AddChild(button);
    }
}

/// <summary>
/// The replay viewer's controls (§19.4, ADR 0168): play/pause, speed 0.25x-16x, a seek bar (keyframes make backward
/// seeks quick), the view (any kingdom's fog, or everything), the time, and a warning if the replay no longer matches.
/// </summary>
public partial class ReplayControls : Control
{
    private readonly ReplayPlayback _playback;
    private readonly HSlider _seek = new() { Position = new Vector2(260, 8), Size = new Vector2(330, 20), MinValue = 0, Step = 1 };
    private readonly Label _time = new() { Position = new Vector2(596, 8), MouseFilter = MouseFilterEnum.Ignore };
    private readonly Button _play = new() { Position = new Vector2(10, 4), Size = new Vector2(60, 26) };
    private readonly Label _speed = new() { Position = new Vector2(140, 8), MouseFilter = MouseFilterEnum.Ignore };
    private readonly OptionButton _follow = new() { Position = new Vector2(700, 4), Size = new Vector2(180, 26) };
    private bool _dragging;

    /// <summary>Creates the controls.</summary>
    public ReplayControls(ReplayPlayback playback)
    {
        _playback = playback;
        Size = new Vector2(1024, 34);
        AddChild(new ColorRect { Size = Size, Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Ignore });
        _play.Pressed += () => _playback.Paused = !_playback.Paused;
        AddChild(_play);
        var slower = new Button { Position = new Vector2(76, 4), Size = new Vector2(28, 26), Text = "-" };
        slower.Pressed += () => _playback.ChangeSpeed(-1);
        AddChild(slower);
        var faster = new Button { Position = new Vector2(106, 4), Size = new Vector2(28, 26), Text = "+" };
        faster.Pressed += () => _playback.ChangeSpeed(+1);
        AddChild(faster);
        AddChild(_speed);
        _seek.MaxValue = playback.Length;
        _seek.DragStarted += () => _dragging = true;
        _seek.DragEnded += changed =>
        {
            _dragging = false;
            if (changed)
            {
                _playback.Seek((int)_seek.Value);
            }
        };
        AddChild(_seek);
        AddChild(_time);
        _follow.AddItem("Everything");
        foreach (string name in playback.Players)
        {
            _follow.AddItem(name);
        }

        _follow.ItemSelected += index => _playback.Follow = index == 0 ? null : (byte)(index - 1);
        AddChild(_follow);
        var back = new Button { Position = new Vector2(890, 4), Size = new Vector2(120, 26), Text = "LEAVE REPLAY" };
        back.Pressed += () => LeaveRequested?.Invoke();
        AddChild(back);
    }

    /// <summary>LEAVE REPLAY.</summary>
    public event Action? LeaveRequested;

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        int tick = _playback.Tick;
        _play.Text = _playback.Paused ? "PLAY" : "PAUSE";
        _speed.Text = _playback.Speed.ToString("0.##", CultureInfo.InvariantCulture) + "x";
        if (!_dragging)
        {
            _seek.SetValueNoSignal(tick);
        }

        int seconds = tick * _playback.TickMilliseconds / 1000;
        int total = _playback.Length * _playback.TickMilliseconds / 1000;
        _time.Text = string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00} / {total / 60}:{total % 60:00}")
            + (_playback.MismatchTick >= 0 ? string.Create(CultureInfo.InvariantCulture, $"  DESYNC at tick {_playback.MismatchTick}") : string.Empty);
    }
}
