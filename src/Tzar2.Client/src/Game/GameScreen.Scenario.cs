using System;
using System.Collections.Generic;
using Godot;
using Tzar2.Client.Audio;
using Tzar2.Client.Logic;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Ids;
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
    private readonly Queue<(int X, int Y, double Seconds)> _cameraPath = new();
    private (int FromX, int FromY, int ToX, int ToY, double Start, double Seconds)? _glide;

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

    // SCN-008: a scenario that reads $isselected is told what the player selects (no other is).
    private void TellScenarioSelection()
    {
        if (_session.Scenario is not { UsesSelection: true })
        {
            return;
        }

        var batch = new Tzar2.Sim.Commands.CommandList();
        EntityId[] selected = [.. _selection.Selected];
        ScenarioPresenter.Select(batch, selected);
        _session.Submit(batch);
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

        UpdateMapSounds(frame);
        _session.DrainNotices(_notices);
        foreach (ScenarioNotice n in _notices)
        {
            Notice(n);
        }

        _notices.Clear();
        UpdateCameraPath();
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
                PlayVoice(n.Sound);
                break;
            case ScenarioNoticeKind.PhraseSaid:
                Message(n.Text);
                PlayVoice(n.Sound);
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
            case ScenarioNoticeKind.CameraPoint:
                _cameraPath.Enqueue((n.X, n.Y, Math.Max(0.05, n.DurationMs / 1000.0)));
                break;
            case ScenarioNoticeKind.MapRequested:
                MapRequested?.Invoke(n);
                break;
        }
    }

    // Lock View holds the camera (scrolling is ignored until it runs out or Unlock View), and so does a camera path.
    private bool ViewLocked => _viewLockedUntil > _now || _glide is not null || _cameraPath.Count > 0;

    // Camera Path (Tzar2's own action, ADR 0293): the view glides to each point in turn, easing in and out.
    private void UpdateCameraPath()
    {
        if (_glide is null && _cameraPath.Count > 0 && _hud is not null)
        {
            Vector2I view = _hud.Layout.View.Size;
            int fromX = (_pan.X + (view.X / 2)) * WorldRenderer.UnitsPerPixel;
            int fromY = (_pan.Y + (view.Y / 2)) * WorldRenderer.UnitsPerPixel;
            (int x, int y, double seconds) = _cameraPath.Dequeue();
            _glide = (fromX, fromY, x, y, _now, seconds);
        }

        if (_glide is not { } g)
        {
            return;
        }

        double t = Math.Clamp((_now - g.Start) / g.Seconds, 0, 1);
        double eased = t * t * (3 - (2 * t));
        CenterOn((int)(g.FromX + ((g.ToX - g.FromX) * eased)), (int)(g.FromY + ((g.ToY - g.FromY) * eased)));
        if (t >= 1)
        {
            _glide = null;
        }
    }

    // A rewind (ADR 0285) drops a camera path under way.
    private void ForgetCameraPath()
    {
        _cameraPath.Clear();
        _glide = null;
    }

    // A picture the scenario names: the map's own (ADR 0284), else one of the package's.
    private Texture2D? UserImage(string? path)
    {
        if (path is null)
        {
            return null;
        }

        if (_userImages.TryGetValue(path, out Texture2D? known))
        {
            return known;
        }

        Texture2D? texture = null;
        if (_session.Setup.MapAssets.Find(path) is { } bytes)
        {
            var image = new Image();
            Error loaded = bytes.Length > 1 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M' ? image.LoadBmpFromBuffer(bytes)
                : bytes.Length > 3 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' ? image.LoadPngFromBuffer(bytes)
                : image.LoadJpgFromBuffer(bytes);
            texture = loaded == Error.Ok ? ImageTexture.CreateFromImage(image) : null;
        }

        texture ??= _ui.Texture("maps/user/" + path);
        _userImages[path] = texture;
        return texture;
    }

    private readonly Dictionary<string, Texture2D?> _userImages = new(StringComparer.OrdinalIgnoreCase);

    // A scroll's sound or a phrase's voice (ADR 0284): the map's own, heard wherever the view is.
    private void PlayVoice(string? path) => _mixer?.PlayMapSound(path, SoundCategory.Interface, "map_voice");

    // The map's sound sources (ADR 0284): each on its timer, heard from its object or its point.
    private void UpdateMapSounds(GameFrame frame)
    {
        if (_mixer is null || _session.Setup.Scenario is not { Sounds.Length: > 0 } data || _session.Scenario is not { } presenter)
        {
            return;
        }

        _mapSounds ??= new MapSoundScheduler([.. data.Sounds], (min, max) => min + (int)(GD.Randi() % (uint)(max - min + 1)));
        _mapSoundSeen ??= new bool[data.Sounds.Length];
        int tickMs = Tick.DefaultLengthMilliseconds; // every match runs at the default tick

        // Where the frames show a source object now (absent: not appeared yet, or gone); never the sim's own state.
        bool Shown(int index, out SeenPlaces.Place at)
        {
            at = default;
            return presenter.EntityOf(index) is { IsNone: false } e && _seen.TryGet(e.Index, out at);
        }

        MapSoundSource Source(int i)
        {
            if (data.Sounds[i].SourceObject is not { } index)
            {
                return MapSoundSource.Present;
            }

            if (Shown(index, out _))
            {
                _mapSoundSeen[i] = true;
                return MapSoundSource.Present;
            }

            return _mapSoundSeen[i] ? MapSoundSource.Gone : MapSoundSource.Hidden;
        }

        _soundCues.Clear();
        _soundSilence.Clear();
        long now = (long)frame.Tick.Value * tickMs;
        _mapSounds.Update(now, Source, i => _mixer.Sounding(MapSoundOwner + i), _soundCues, _soundSilence);
        foreach (int i in _soundSilence)
        {
            _mixer.StopOwner(MapSoundOwner + i);
        }

        foreach (MapSoundCue cue in _soundCues)
        {
            CustomSoundDef s = data.Sounds[cue.Index];
            SoundPlace place = new(s.X, s.Y);
            if (s.SourceObject is { } index && Shown(index, out SeenPlaces.Place at))
            {
                place = PlaceOf(at.X, at.Y);
            }

            _mixer.PlayMapSound(s.Sound.Path, SoundCategory.Effects, "map_sound", place, MapSoundOwner + cue.Index);
        }
    }

    // Owners of the map's sounds in the mixer: above every entity's (1 + its index).
    private const int MapSoundOwner = 1 << 24;
    private MapSoundScheduler? _mapSounds;
    private bool[]? _mapSoundSeen;
    private readonly List<MapSoundCue> _soundCues = [];
    private readonly List<int> _soundSilence = [];

    private void ShowModal(ScenarioNotice n)
    {
        if (_hud is null)
        {
            return;
        }

        PlayVoice(n.Sound);
        StoryMusic(n.Music);
        ScenarioDialog dialog = n.Kind == ScenarioNoticeKind.StoryScreen
            ? new StoryScreen(_ui, _hud.Layout.Size, n, UserImage(n.Image), _catalog.Text)
            : new ScenarioMessageDialog(_ui, _hud.Layout.Size, n, UserImage(n.Image), _catalog.Text);
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
            StoryMusic(null);
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

        var dialog = new QuestLogDialog(_ui, _hud.Layout.Size, view.Quests, _catalog.Text);
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
