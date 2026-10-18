using System;
using System.Globalization;
using Godot;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Game.Editing;

namespace Tzar2.Client.Editor;

/// <summary>The editor's Triggers and Play menus: the rule editor, drawing areas on the map, playtest and the trigger debugger.</summary>
public partial class EditorScreen
{
    private TriggerWindow? _triggers;
    private Action<AreaSpec>? _areaCallback;
    private EditorTool _toolBeforeArea;
    private int _playKingdom;
    private bool _reveal;

    /// <summary>Raised when the user asks to play the map (the game mode builds the session; ADR 0153).</summary>
    public event Action<PlaytestOptions, bool>? PlaytestRequested;

    /// <summary>Opens (or focuses) the rule editor.</summary>
    public TriggerWindow OpenTriggers()
    {
        if (_triggers is null || !IsInstanceValid(_triggers))
        {
            _triggers = new TriggerWindow(this);
            AddChild(_triggers);
        }

        _triggers.GrabFocus();
        return _triggers;
    }

    /// <summary>Lets the user draw a rectangle on the map; <paramref name="done"/> receives it.</summary>
    public void DrawArea(Action<AreaSpec> done)
    {
        _areaCallback = done;
        _toolBeforeArea = _controller.Tool == EditorTool.Area ? EditorTool.Select : _controller.Tool;
        _controller.Tool = EditorTool.Area;
        Status("Drag a rectangle on the map.");
        _view.GrabFocus();
    }

    /// <summary>Outlines an area on the map until the next one is shown.</summary>
    public void ShowArea(RectArea area, string label)
    {
        _view.Areas.RemoveAll(a => a.Label.StartsWith("» ", StringComparison.Ordinal));
        _view.Areas.Add((area, "» " + label, new Color(1f, 0.5f, 0.9f)));
        _view.Invalidate();
    }

    private void HookTriggers()
    {
        _controller.AreaDrawn += area =>
        {
            Action<AreaSpec>? callback = _areaCallback;
            _areaCallback = null;
            _controller.Tool = _toolBeforeArea;
            ShowArea(area, "drawn");
            callback?.Invoke(area);
        };
        LocateOther += at => OpenTriggers().Locate(at);
        RefreshNamedAreas();
        _controller.Document.Changed += _ => RefreshNamedAreas();
    }

    private void RefreshNamedAreas()
    {
        _view.Areas.RemoveAll(a => !a.Label.StartsWith("» ", StringComparison.Ordinal));
        foreach (NamedArea a in Document.State.Extras.Areas)
        {
            _view.Areas.Add((a.Area, a.Name, new Color(0.5f, 0.9f, 1f)));
        }
    }

    private void CloseTriggers()
    {
        if (_triggers is not null && IsInstanceValid(_triggers))
        {
            _triggers.QueueFree();
        }

        _triggers = null;
    }

    private void TriggerMenus(MenuBar bar)
    {
        PopupMenu triggers = Menu(bar, "Triggers");
        Item(triggers, "Rules, events and actions…", () => OpenTriggers());
        Item(triggers, "Show validation", () => _right.CurrentTab = 2);

        PopupMenu play = Menu(bar, "Play");
        Item(play, "Playtest", () => Play(debugger: false));
        Item(play, "Playtest with the trigger debugger", () => Play(debugger: true));
        play.AddSeparator();
        for (int k = 0; k <= 8; k++)
        {
            int kingdom = k;
            Item(play, k == 0 ? "Play as the map's player" : "Play as kingdom " + k.ToString(CultureInfo.InvariantCulture), () =>
            {
                _playKingdom = kingdom;
                Status(kingdom == 0 ? "Playtests use the map's own player." : $"Playtests play kingdom {kingdom}.");
            });
        }

        play.AddSeparator();
        Item(play, "Toggle reveal map", () =>
        {
            _reveal = !_reveal;
            Status(_reveal ? "Playtests reveal the map." : "Playtests keep the fog.");
        });
    }

    private void Play(bool debugger)
    {
        ValidationResult result = Revalidate();
        if (result.BlocksPlay)
        {
            _right.CurrentTab = 2;
            Status("Playing is blocked: fix the blocking findings in the validation panel first.");
            return;
        }

        PlaytestRequested?.Invoke(new PlaytestOptions(_playKingdom, _reveal), debugger);
    }
}
