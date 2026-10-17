using System;
using System.IO;
using Godot;
using Tzar2.Client.Audio;
using Tzar2.Client.Logic;

namespace Tzar2.Client.Viewer;

/// <summary>
/// MIDI music (<c>music/*.mid</c>) through MeltySynth with a user-supplied General MIDI SoundFont (ADR 0020). No
/// SoundFont ships with the game; without one this page says how to get one and playback stays disabled.
/// </summary>
public sealed class MusicPage : IViewerPage
{
    /// <summary>Where <c>tools/soundfont/fetch-soundfont.sh</c> puts the dev SoundFont, relative to the repository root.</summary>
    public const string DevSoundFont = "soundfonts/GeneralUser-GS.sf2";

    private readonly AssetViewer _viewer;
    private readonly ViewerSettings _settings;
    private readonly VBoxContainer _panel;
    private readonly ItemList _list;
    private readonly Button _play;
    private readonly Label _status;
    private readonly MidiPlayer _player;
    private readonly FileDialog _dialog;

    /// <summary>Creates the page and loads the first available SoundFont (command line, settings, environment, dev folder).</summary>
    public MusicPage(AssetViewer viewer, ViewerSettings settings, string? soundFontArg)
    {
        _viewer = viewer;
        _settings = settings;
        _panel = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        foreach (AudioEntry entry in viewer.Store.Music)
        {
            _list.AddItem($"{Path.GetFileName(entry.Asset)}  ({entry.Detail})");
        }

        _list.ItemActivated += i => Play((int)i);
        _panel.AddChild(_list);
        var buttons = new HBoxContainer();
        _play = new Button { Text = "Play" };
        _play.Pressed += () =>
        {
            int[] selected = _list.GetSelectedItems();
            Play(selected.Length > 0 ? selected[0] : 0);
        };
        var stop = new Button { Text = "Stop" };
        stop.Pressed += () => _player!.Stop();
        var choose = new Button { Text = "SoundFont…" };
        choose.Pressed += () => _dialog!.PopupCentered(new Vector2I(900, 600));
        buttons.AddChild(_play);
        buttons.AddChild(stop);
        buttons.AddChild(choose);
        _panel.AddChild(buttons);
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 120) };
        _panel.AddChild(_status);

        _player = new MidiPlayer();
        viewer.AddChild(_player);
        _dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = ["*.sf2 ; SoundFont 2"],
            Title = "Choose a General MIDI SoundFont",
            UseNativeDialog = false,
        };
        _dialog.FileSelected += path =>
        {
            if (TryLoad(path))
            {
                _settings.SoundFont = path;
            }
        };
        viewer.AddChild(_dialog);

        string devPath = ProjectSettings.GlobalizePath("res://../../" + DevSoundFont);
        string? chosen = SoundFontLocator.Choose(
            File.Exists, soundFontArg, settings.SoundFont, System.Environment.GetEnvironmentVariable(SoundFontLocator.EnvironmentVariable), devPath);
        if (chosen is null)
        {
            UpdateStatus(null);
        }
        else
        {
            TryLoad(chosen);
        }
    }

    /// <inheritdoc/>
    public string Title => "Music";

    /// <inheritdoc/>
    public Control Panel => _panel;

    /// <summary>The MIDI player.</summary>
    public MidiPlayer Player => _player;

    /// <summary>Status text (SoundFont and playback).</summary>
    public string Status => _status.Text;

    /// <inheritdoc/>
    public void SetActive(bool active)
    {
    }

    /// <inheritdoc/>
    public void Update()
    {
    }

    /// <inheritdoc/>
    public void Dispose() => _player.Stop();

    private bool TryLoad(string path)
    {
        try
        {
            _player.LoadSoundFont(path);
            UpdateStatus(null);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            UpdateStatus(e.Message);
            return false;
        }
    }

    private void UpdateStatus(string? error)
    {
        _play.Disabled = !_player.HasSoundFont;
        _status.Text = _player.HasSoundFont
            ? $"SoundFont: {_player.SoundFontPath}"
            : (error is null ? string.Empty : error + "\n")
              + "MIDI playback is disabled: no SoundFont. None is bundled (licensing, ADR 0020). Run "
              + "tools/soundfont/fetch-soundfont.sh (GeneralUser GS) or choose any General MIDI .sf2 with 'SoundFont…', "
              + $"--soundfont <path> or {SoundFontLocator.EnvironmentVariable}.";
    }

    private void Play(int index)
    {
        if (!_player.HasSoundFont || index < 0 || index >= _viewer.Store.Music.Count)
        {
            return;
        }

        AudioEntry entry = _viewer.Store.Music[index];
        try
        {
            _player.Play(_viewer.Store.Read(entry.Asset));
            _status.Text = $"Playing {entry.Asset} (looping)\nSoundFont: {_player.SoundFontPath}";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException)
        {
            _status.Text = e.Message;
        }
    }
}
