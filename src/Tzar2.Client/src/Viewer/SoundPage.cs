using System;
using System.IO;
using Godot;
using Tzar2.Content.Audio;

namespace Tzar2.Client.Viewer;

/// <summary>Plays the imported sound effects (<c>sound/**.wav</c>, REQ-AUD-01: lossless PCM).</summary>
public sealed class SoundPage : IViewerPage
{
    private readonly AssetViewer _viewer;
    private readonly VBoxContainer _panel;
    private readonly LineEdit _filter;
    private readonly ItemList _list;
    private readonly Label _info;
    private readonly AudioStreamPlayer _player;
    private readonly System.Collections.Generic.List<int> _visible = [];

    /// <summary>Creates the page.</summary>
    public SoundPage(AssetViewer viewer)
    {
        _viewer = viewer;
        _panel = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _filter = new LineEdit { PlaceholderText = "Filter…", ClearButtonEnabled = true };
        _filter.TextChanged += _ => Refilter();
        _panel.AddChild(_filter);
        _list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _list.ItemActivated += i => Play(_visible[(int)i]);
        _panel.AddChild(_list);
        var buttons = new HBoxContainer();
        var play = new Button { Text = "Play" };
        play.Pressed += () =>
        {
            int[] selected = _list.GetSelectedItems();
            if (selected.Length > 0)
            {
                Play(_visible[selected[0]]);
            }
        };
        var stop = new Button { Text = "Stop" };
        stop.Pressed += () => _player!.Stop();
        buttons.AddChild(play);
        buttons.AddChild(stop);
        _panel.AddChild(buttons);
        _info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = "Double-click a sound to play it." };
        _panel.AddChild(_info);
        _player = new AudioStreamPlayer();
        viewer.AddChild(_player);
        Refilter();
    }

    /// <inheritdoc/>
    public string Title => "Sounds";

    /// <inheritdoc/>
    public Control Panel => _panel;

    /// <summary>Number of sounds.</summary>
    public int Count => _viewer.Store.Sounds.Count;

    /// <summary>Builds the stream of sound <paramref name="index"/> (exposed for the smoke test).</summary>
    public AudioStreamWav Load(int index)
    {
        byte[] file = _viewer.Store.Read(_viewer.Store.Sounds[index].Asset);
        WaveData wave = WaveData.Parse(file);
        byte[] data = file.AsSpan(wave.DataOffset, wave.DataLength).ToArray();
        if (wave.BitsPerSample == 8)
        {
            // WAV 8-bit is unsigned; Godot expects signed 8-bit samples.
            for (int i = 0; i < data.Length; i++)
            {
                data[i] ^= 0x80;
            }
        }

        return new AudioStreamWav
        {
            Format = wave.BitsPerSample == 8 ? AudioStreamWav.FormatEnum.Format8Bits : AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = wave.SampleRate,
            Stereo = wave.Channels == 2,
            Data = data,
        };
    }

    /// <inheritdoc/>
    public void SetActive(bool active)
    {
        if (!active)
        {
            _player.Stop();
        }
    }

    /// <inheritdoc/>
    public void Update()
    {
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    private void Play(int index)
    {
        try
        {
            AudioStreamWav stream = Load(index);
            _player.Stream = stream;
            _player.Play();
            AudioEntry entry = _viewer.Store.Sounds[index];
            _info.Text = $"{entry.Asset}\n{entry.Detail}";
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            _info.Text = e.Message;
        }
    }

    private void Refilter()
    {
        _list.Clear();
        _visible.Clear();
        string filter = _filter.Text.Trim();
        for (int i = 0; i < _viewer.Store.Sounds.Count; i++)
        {
            AudioEntry entry = _viewer.Store.Sounds[i];
            if (filter.Length == 0 || entry.Asset.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                _visible.Add(i);
                string name = entry.Asset.StartsWith("sound/", StringComparison.Ordinal) ? entry.Asset["sound/".Length..] : entry.Asset;
                _list.AddItem($"{name}  ({entry.Detail})");
            }
        }
    }
}
