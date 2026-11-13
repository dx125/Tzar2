using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Tzar2.Client.Audio;
using Tzar2.Client.Logic;
using Tzar2.Content.Audio;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;

namespace Tzar2.Client.Editor;

/// <summary>
/// Shows and plays the map's own resources in the editor (ADR 0291): a picture as a thumbnail, a sound (WAV, OGG) or
/// music (MIDI through the player's SoundFont, OGG) played once. One preview plays at a time.
/// </summary>
public partial class AssetPreview : Node
{
    private AudioStreamPlayer? _player;
    private MidiPlayer? _midi;

    /// <summary>The SoundFont the game was started with (MIDI previews), or null for the usual places.</summary>
    public string? SoundFont { get; set; }

    /// <summary>The picture of image bytes (BMP, PNG or JPEG), or null.</summary>
    public static Texture2D? Texture(byte[]? bytes)
    {
        if (bytes is not { Length: > 3 })
        {
            return null;
        }

        var image = new Image();
        Error loaded = bytes[0] == (byte)'B' && bytes[1] == (byte)'M' ? image.LoadBmpFromBuffer(bytes)
            : bytes[1] == (byte)'P' && bytes[2] == (byte)'N' ? image.LoadPngFromBuffer(bytes)
            : image.LoadJpgFromBuffer(bytes);
        return loaded == Error.Ok ? ImageTexture.CreateFromImage(image) : null;
    }

    /// <summary>Plays sound or music bytes once; returns why it cannot, or null.</summary>
    public string? Play(byte[]? bytes)
    {
        Stop();
        if (bytes is not { Length: > 4 })
        {
            return "Nothing to play.";
        }

        try
        {
            if (bytes[0] == (byte)'M' && bytes[1] == (byte)'T' && bytes[2] == (byte)'h' && bytes[3] == (byte)'d')
            {
                if (_midi is null)
                {
                    _midi = new MidiPlayer();
                    AddChild(_midi);
                }

                if (!_midi.HasSoundFont)
                {
                    string? font = SoundFontLocator.Choose(File.Exists, SoundFont, System.Environment.GetEnvironmentVariable(SoundFontLocator.EnvironmentVariable));
                    if (font is null)
                    {
                        return $"MIDI needs a SoundFont: set {SoundFontLocator.EnvironmentVariable} or start the game with --soundfont.";
                    }

                    _midi.LoadSoundFont(font);
                }

                _midi.Play(bytes, loop: false);
                return null;
            }

            AudioStream? stream = bytes[0] == (byte)'O' && bytes[1] == (byte)'g' && bytes[2] == (byte)'g' ? AudioStreamOggVorbis.LoadFromBuffer(bytes) : Wave(bytes);
            if (stream is null)
            {
                return "Not a sound this game plays (WAV, OGG or MIDI).";
            }

            if (_player is null)
            {
                _player = new AudioStreamPlayer();
                AddChild(_player);
            }

            _player.Stream = stream;
            _player.Play();
            return null;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException)
        {
            return e.Message;
        }
    }

    /// <summary>Stops what plays.</summary>
    public void Stop()
    {
        _player?.Stop();
        _midi?.Stop();
    }

    private static AudioStreamWav Wave(byte[] file)
    {
        WaveData wave = WaveData.Parse(file);
        byte[] data = file.AsSpan(wave.DataOffset, wave.DataLength).ToArray();
        if (wave.BitsPerSample == 8)
        {
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
}

/// <summary>
/// Fields that pick instead of being typed (ADR 0291): the map's own resources from a list with a thumbnail or a play
/// button, a value with suggestions, an entity type from the palette, an object or a point clicked on the map.
/// </summary>
public static class Pickers
{
    /// <summary>A resource of the map of one kind: a list with "(none)", and a thumbnail or a play button.</summary>
    public static void Asset(Node parent, string label, MapDocument doc, AssetKind kind, string? current, Action<ResourceRef?> commit, AssetPreview? preview)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(commit);
        Forms.Label(parent, label);
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        string[] paths = ["(none)", .. doc.State.Of(kind)];
        int selected = current is null or "" ? 0 : Array.FindIndex(paths, p => string.Equals(p, current, StringComparison.OrdinalIgnoreCase));
        if (selected < 0)
        {
            paths = [.. paths, current!];
            selected = paths.Length - 1;
        }

        var option = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true, CustomMinimumSize = new Vector2(160, 0) };
        for (int i = 0; i < paths.Length; i++)
        {
            option.AddItem(i > 0 && doc.State.Export(paths[i]) is null ? paths[i] + " (missing)" : paths[i]);
        }

        option.Selected = selected;
        row.AddChild(option);
        TextureRect? thumb = null;
        if (kind == AssetKind.Image)
        {
            thumb = new TextureRect { CustomMinimumSize = new Vector2(40, 40), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
            row.AddChild(thumb);
        }
        else if (preview is not null)
        {
            Forms.Button(row, "▶", () =>
            {
                string? why = option.Selected > 0 ? preview.Play(doc.State.Export(paths[option.Selected])) : "Pick a resource first.";
                if (why is not null)
                {
                    option.TooltipText = why;
                    GD.Print("Editor: " + why);
                }
            }).TooltipText = "Play it";
            Forms.Button(row, "■", preview.Stop).TooltipText = "Stop";
        }

        void Show()
        {
            if (thumb is not null)
            {
                thumb.Texture = option.Selected > 0 ? AssetPreview.Texture(doc.State.Export(paths[option.Selected])) : null;
            }
        }

        option.ItemSelected += i =>
        {
            Show();
            commit(i == 0 ? null : new ResourceRef(paths[i]));
        };
        Show();
        parent.AddChild(row);
    }

    /// <summary>A text field with a list of suggestions next to it (any other value may still be typed).</summary>
    public static LineEdit Suggest(Node parent, string label, string value, IReadOnlyList<string> suggestions, Action<string> commit)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        ArgumentNullException.ThrowIfNull(commit);
        Forms.Label(parent, label);
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var edit = new LineEdit { Text = value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(140, 0) };
        string committed = value;
        void Commit(string text)
        {
            if (text != committed)
            {
                committed = text;
                commit(text);
            }
        }

        edit.TextSubmitted += Commit;
        edit.FocusExited += () => Commit(edit.Text);
        row.AddChild(edit);
        var pick = new MenuButton { Text = "▾", Flat = false, Disabled = suggestions.Count == 0, TooltipText = suggestions.Count == 0 ? "Nothing to suggest here" : "Pick one" };
        PopupMenu popup = pick.GetPopup();
        for (int i = 0; i < suggestions.Count; i++)
        {
            popup.AddItem(suggestions[i], i);
        }

        popup.IdPressed += id =>
        {
            edit.Text = suggestions[(int)id];
            Commit(edit.Text);
        };
        row.AddChild(pick);
        parent.AddChild(row);
        return edit;
    }

    /// <summary>An entity type key: typed, picked from the palette, or taken from the selected object.</summary>
    public static void TypeKey(Node parent, string label, string value, EditorScreen screen, Action<string> commit)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(commit);
        Forms.Label(parent, label);
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var edit = new LineEdit { Text = value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(140, 0) };
        edit.TextSubmitted += t => commit(t.Trim());
        edit.FocusExited += () =>
        {
            if (edit.Text != value)
            {
                value = edit.Text;
                commit(edit.Text.Trim());
            }
        };
        row.AddChild(edit);
        Forms.Button(row, "Pick…", () => screen.AddChild(new TypePicker(screen.Controller.Palette, edit.Text, key =>
        {
            edit.Text = value = key;
            commit(key);
        })));
        Forms.Button(row, "Selected", () =>
        {
            if (!screen.Controller.Selection.IsEmpty && screen.Document.State.Find(screen.Controller.Selection[0]) is { } o)
            {
                edit.Text = value = o.Placement.TypeKey;
                commit(value);
            }
        }).TooltipText = "The type of the object selected on the map";
        parent.AddChild(row);
    }

    /// <summary>A button that waits for a click on an object of the map.</summary>
    public static Button PickObject(Node parent, EditorScreen screen, Action<int> picked, string text = "Pick on map")
    {
        ArgumentNullException.ThrowIfNull(screen);
        Button button = Forms.Button(parent, text, () => screen.PickObject(picked));
        button.TooltipText = "Then click an object on the map (Esc cancels)";
        return button;
    }

    /// <summary>A button that waits for a click on a tile of the map.</summary>
    public static Button PickPoint(Node parent, EditorScreen screen, Action<int, int> picked, string text = "Pick on map")
    {
        ArgumentNullException.ThrowIfNull(screen);
        Button button = Forms.Button(parent, text, () => screen.PickPoint(picked));
        button.TooltipText = "Then click a tile of the map (Esc cancels)";
        return button;
    }
}

/// <summary>Picks an entity type from the palette, filtered by words of its key or legacy class (ADR 0291).</summary>
public partial class TypePicker : ConfirmationDialog
{
    private readonly EditorPalette _palette;
    private readonly ItemList _list = new() { CustomMinimumSize = new Vector2(420, 360), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly Action<string> _chosen;
    private List<PaletteEntry> _shown = [];

    /// <summary>Opens the picker on <paramref name="current"/>.</summary>
    public TypePicker(EditorPalette palette, string current, Action<string> chosen)
    {
        ArgumentNullException.ThrowIfNull(palette);
        _palette = palette;
        _chosen = chosen;
        Title = "Pick a type";
        OkButtonText = "Use";
        var box = new VBoxContainer();
        var filter = new LineEdit { PlaceholderText = "Filter: words of the key or class, e.g. euro knight", Text = string.Empty };
        filter.TextChanged += Fill;
        box.AddChild(filter);
        box.AddChild(_list);
        AddChild(box);
        _list.ItemActivated += _ => Choose();
        Confirmed += Choose;
        Canceled += QueueFree;
        Fill(string.Empty);
        int at = _shown.FindIndex(e => string.Equals(e.Key, current, StringComparison.Ordinal));
        if (at >= 0)
        {
            _list.Select(at);
            _list.EnsureCurrentIsVisible();
        }
    }

    /// <inheritdoc/>
    public override void _Ready() => Forms.ShowCentered(this);

    private void Fill(string filter)
    {
        _shown = [.. _palette.Search(filter)];
        _list.Clear();
        foreach (PaletteEntry e in _shown)
        {
            _list.AddItem(e.LegacyClass.Length > 0 ? $"{e.Key}   ({e.Kind}, {e.LegacyClass})" : $"{e.Key}   ({e.Kind})");
        }
    }

    private void Choose()
    {
        int[] selected = _list.GetSelectedItems();
        if (selected.Length > 0)
        {
            _chosen(_shown[selected[0]].Key);
        }

        QueueFree();
    }
}
