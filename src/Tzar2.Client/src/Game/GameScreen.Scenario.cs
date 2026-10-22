using System.Collections.Generic;
using Godot;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Scenario;
using Tzar2.Game.Session;
using Tzar2.Game.Ui;

namespace Tzar2.Client.Game;

/// <summary>
/// Scenario presentation in the game screen (ADR 0132): the session's <see cref="ScenarioNotice"/>s become messages,
/// pop-ups, story screens and camera moves; the frame's <see cref="ScenarioView"/> drives the conversation dialog and
/// the quest log (F8). The client never sees scenario indices or sim state (ADR 0073).
/// </summary>
public partial class GameScreen
{
    private readonly List<ScenarioNotice> _notices = [];
    private readonly Queue<ScenarioNotice> _modal = new();
    private ScenarioDialog? _open;
    private ConversationDialog? _talk;
    private double _viewLockedUntil;
    private bool _storyPause;

    /// <summary>Captures the match for SAVE GAME with a label (set by the app for the games it can continue; null: none, as in multiplayer).</summary>
    public System.Func<string, SaveGame>? CaptureSave { get; set; }

    /// <summary>The player's saves (the save and load dialogs; SHELL-014, SHELL-021, SHELL-022).</summary>
    public SaveLibrary? Saves { get; set; }

    /// <summary>LOAD GAME: the save picked to continue.</summary>
    public event System.Action<SaveGame>? LoadRequested;

    // F2 and the menu's SAVE GAME: the save dialog over the paused match.
    private bool ShowSaveDialog()
    {
        if (CaptureSave is null || Saves is null || _hud is null || GetNodeOrNull("SaveGame") is not null)
        {
            return false;
        }

        bool paused = _session.Paused;
        _session.Paused = true;
        var dialog = new SaveDialog(_ui, _hud.Layout.Size, new SaveGameScreen(Saves), _catalog.Text);
        var layer = new CanvasLayer { Layer = 3, Name = "SaveGame" };
        layer.AddChild(dialog);
        AddChild(layer);
        void Done()
        {
            layer.QueueFree();
            _session.Paused = paused;
        }

        dialog.Closed += Done;
        dialog.Saving += model =>
        {
            try
            {
                model.Save(CaptureSave(System.DateTime.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
                Message("Game saved: " + model.Name);
            }
            catch (System.Exception e) when (e is System.IO.IOException or System.TimeoutException or System.InvalidOperationException or System.ArgumentException or System.UnauthorizedAccessException)
            {
                Message("Save failed: " + e.Message);
            }

            Done();
        };
        return true;
    }

    // F3 and the menu's LOAD GAME: the load dialog; a picked save replaces the match.
    private bool ShowLoadDialog()
    {
        if (Saves is null || LoadRequested is null || _hud is null || GetNodeOrNull("LoadGame") is not null)
        {
            return false;
        }

        bool paused = _session.Paused;
        _session.Paused = true;
        var dialog = new LoadDialog(_ui, _hud.Layout.Size, new LoadGameScreen(Saves, campaigns: _session.Scenario is not null), _catalog.Text);
        var layer = new CanvasLayer { Layer = 3, Name = "LoadGame" };
        layer.AddChild(dialog);
        AddChild(layer);
        dialog.Closed += () =>
        {
            layer.QueueFree();
            _session.Paused = paused;
        };
        dialog.Loading += model =>
        {
            SaveGame save;
            try
            {
                save = model.Load()!;
            }
            catch (System.Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException or System.UnauthorizedAccessException)
            {
                Message("Load failed: " + e.Message);
                return;
            }

            layer.QueueFree();
            LoadRequested?.Invoke(save);
        };
        return true;
    }

    /// <summary>Raised when the scenario asks for the next campaign map (Load Map, Player Win, Player Lose).</summary>
    public event System.Action<ScenarioNotice>? MapRequested;

    // Called every frame after the sim events.
    private void UpdateScenario(GameFrame frame)
    {
        if (_session.Scenario is null)
        {
            return;
        }

        _session.DrainNotices(_notices);
        foreach (ScenarioNotice n in _notices)
        {
            Notice(n);
        }

        _notices.Clear();
        if (_open is null && _modal.Count > 0)
        {
            ShowModal(_modal.Dequeue());
        }

        ConversationView? talk = frame.Scenario?.Conversation;
        if (talk is null && _talk is not null)
        {
            CloseTalk();
        }
        else if (talk is not null && (_talk is null || !ReferenceEquals(_talk.View, talk)) && _open is null)
        {
            CloseTalk();
            OpenTalk(talk);
        }
    }

    private void Notice(ScenarioNotice n)
    {
        if (!n.ForLocalPlayer)
        {
            return;
        }

        switch (n.Kind)
        {
            case ScenarioNoticeKind.QuestAdded:
                Message("New quest: " + n.Title);
                break;
            case ScenarioNoticeKind.QuestRemoved:
                Message("Quest done: " + n.Title);
                break;
            case ScenarioNoticeKind.Chat:
                Message(n.Text);
                break;
            case ScenarioNoticeKind.PhraseSaid when n.Title.Length > 0:
                Message($"{n.Title}: {n.Text}");
                break;
            case ScenarioNoticeKind.PhraseSaid:
                Message(n.Text);
                break;
            case ScenarioNoticeKind.Message or ScenarioNoticeKind.StoryScreen:
                _modal.Enqueue(n);
                break;
            case ScenarioNoticeKind.LockView:
                CenterOn(n.X, n.Y);
                _viewLockedUntil = _now + (n.DurationMs / 1000.0);
                break;
            case ScenarioNoticeKind.UnlockView:
                _viewLockedUntil = 0;
                break;
            case ScenarioNoticeKind.ScrollView:
                CenterOn(n.X, n.Y);
                break;
            case ScenarioNoticeKind.MapRequested:
                MapRequested?.Invoke(n);
                break;
        }
    }

    // Lock View holds the camera (scrolling is ignored until it runs out or Unlock View).
    private bool ViewLocked => _viewLockedUntil > _now;

    private Texture2D? UserImage(string? path) => path is null ? null : _ui.Texture("maps/user/" + path);

    private void ShowModal(ScenarioNotice n)
    {
        if (_hud is null)
        {
            return;
        }

        ScenarioDialog dialog = n.Kind == ScenarioNoticeKind.StoryScreen
            ? new StoryScreen(_ui, _hud.Layout.Size, n, UserImage(n.Image))
            : new ScenarioMessageDialog(_ui, _hud.Layout.Size, n, UserImage(n.Image));
        bool story = n.Kind == ScenarioNoticeKind.StoryScreen;
        if (story && !_session.Paused)
        {
            // REQ-SCR-03: story screens pause single player.
            _storyPause = true;
            _session.Paused = true;
        }

        var layer = new CanvasLayer { Layer = 3 };
        layer.AddChild(dialog);
        AddChild(layer);
        _open = dialog;
        dialog.Closed += () =>
        {
            layer.QueueFree();
            _open = null;
            if (_storyPause)
            {
                _storyPause = false;
                _session.Paused = false;
            }
        };
    }

    private void OpenTalk(ConversationView view)
    {
        if (_hud is null)
        {
            return;
        }

        var dialog = new ConversationDialog(_ui, _hud.Layout.Size, view, UserImage);
        var layer = new CanvasLayer { Layer = 3, Name = "Conversation" };
        layer.AddChild(dialog);
        AddChild(layer);
        _talk = dialog;
        dialog.Chosen += position =>
        {
            _session.ReplyToConversation(view.Serial, position);
            CloseTalk();
        };
    }

    private void CloseTalk()
    {
        if (_talk is not null)
        {
            _talk.GetParent()?.QueueFree();
            _talk = null;
        }
    }

    private void ShowQuests()
    {
        if (_hud is null || _open is not null || _session.Scenario is null || _frame?.Scenario is not { } view)
        {
            return;
        }

        var dialog = new QuestLogDialog(_ui, _hud.Layout.Size, view.Quests);
        var layer = new CanvasLayer { Layer = 3, Name = "Quests" };
        layer.AddChild(dialog);
        AddChild(layer);
        _open = dialog;
        dialog.Closed += () =>
        {
            layer.QueueFree();
            _open = null;
        };
    }
}
