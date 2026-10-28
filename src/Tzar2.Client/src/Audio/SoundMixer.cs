using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Tzar2.Client.Logic;
using Tzar2.Client.Viewer;
using Tzar2.Content.Audio;

namespace Tzar2.Client.Audio;

/// <summary>Sound categories with their own toggles (the original's preferences: unit speech, building, nature sounds).</summary>
public enum SoundCategory
{
    /// <summary>Unit acknowledgements and selection voices.</summary>
    Speech = 0,

    /// <summary>Combat, work and building sounds.</summary>
    Effects = 1,

    /// <summary>Building selection sounds.</summary>
    Buildings = 2,

    /// <summary>Interface clicks.</summary>
    Interface = 3,
}

/// <summary>
/// Sound effects (REQ-AUD-01): the imported WAVs played losslessly through <see cref="MaxVoices"/> voices, mixed by the
/// original's rules as ReTzar reads them (<see cref="SoundRules"/>, PRES-012): a set's clip picked by a roll of R(0, 1000)
/// that may pick none; the voices split into channels (effects, the voices of units and of buildings); at most a few
/// effects of a category at once, each one more lowering the next one's priority; an entity's sounds silenced at its new
/// order or its death; and each sound as loud as its tiles from the view's centre make it, heard again from there when
/// the view moves (PRES-013: effects left far behind stop). Per-category toggles. No panning yet (UNK-33). Presentation
/// only.
/// </summary>
public partial class SoundMixer : Node
{
    /// <summary>Voice limit.</summary>
    public const int MaxVoices = 32;

    /// <summary>Name of the effects bus.</summary>
    public const string SfxBus = "Sfx";

    /// <summary>Name of the music bus.</summary>
    public const string MusicBus = "Music";

    private readonly Dictionary<string, AudioStreamWav?> _streams = new(StringComparer.Ordinal);
    private readonly AudioStreamPlayer[] _voices = new AudioStreamPlayer[MaxVoices];
    private readonly VoiceState[] _states = new VoiceState[MaxVoices];
    private readonly SoundPlace?[] _places = new SoundPlace?[MaxVoices];
    private int _centreX;
    private int _centreY;
    private int _heardX;
    private int _heardY;
    private int _viewWidth = SoundRules.WidestView;
    private readonly List<int> _weights = [];
    private readonly Random _random = new(1234);
    private readonly bool[] _enabled = [true, true, true, true];
    private bool _paused;
    private ContentStore? _store;
    private PresentationCatalog? _catalog;
    /// <summary>Sounds started so far (smoke/autoplay statistics).</summary>
    public int Played { get; private set; }

    /// <summary>Ensures the Sfx and Music buses exist and returns their indices.</summary>
    public static (int Sfx, int Music) EnsureBuses()
    {
        int sfx = AudioServer.GetBusIndex(SfxBus);
        if (sfx < 0)
        {
            AudioServer.AddBus();
            sfx = AudioServer.BusCount - 1;
            AudioServer.SetBusName(sfx, SfxBus);
        }

        int music = AudioServer.GetBusIndex(MusicBus);
        if (music < 0)
        {
            AudioServer.AddBus();
            music = AudioServer.BusCount - 1;
            AudioServer.SetBusName(music, MusicBus);
        }

        return (sfx, music);
    }

    /// <summary>Sets a bus volume from 0-100 %.</summary>
    public static void SetVolume(string bus, int percent)
    {
        EnsureBuses();
        int index = AudioServer.GetBusIndex(bus);
        float linear = Math.Clamp(percent, 0, 100) / 100f;
        AudioServer.SetBusMute(index, linear <= 0f);
        AudioServer.SetBusVolumeDb(index, Mathf.LinearToDb(Math.Max(linear, 0.0001f)));
    }

    /// <summary>Binds the package and the catalog.</summary>
    public void Bind(ContentStore store, PresentationCatalog? catalog)
    {
        _store = store;
        _catalog = catalog;
    }

    /// <summary>Enables or disables a category.</summary>
    public void SetEnabled(SoundCategory category, bool enabled) => _enabled[(int)category] = enabled;

    /// <summary>
    /// Holds (true) or resumes the sounds that are playing: the game's pause. New sounds still play (orders and
    /// selections answer while paused); held voices are not reused until they resume.
    /// </summary>
    public void SetPaused(bool paused)
    {
        if (paused == _paused)
        {
            return;
        }

        _paused = paused;
        foreach (AudioStreamPlayer voice in _voices)
        {
            if (voice is null)
            {
                continue;
            }

            if (paused && voice.Playing)
            {
                voice.StreamPaused = true;
            }
            else if (!paused && voice.StreamPaused)
            {
                voice.StreamPaused = false;
            }
        }
    }

    /// <inheritdoc/>
    public override void _Ready()
    {
        EnsureBuses();
        for (int i = 0; i < _voices.Length; i++)
        {
            _voices[i] = new AudioStreamPlayer { Bus = SfxBus };
            AddChild(_voices[i]);
        }
    }

    /// <summary>
    /// Where the player listens from (PRES-013, 0x4d01c0): the view's centre tile and its width in pixels. Once the centre
    /// has moved more than <see cref="SoundRules.RelocateTiles"/> tiles the playing sounds are heard again from it, each at
    /// its owner's tile now (<paramref name="ownerAt"/>, for 1 + an entity's index; null keeps where it was): an effect
    /// left too far behind stops, the voices of units and buildings keep their least volume.
    /// </summary>
    public void Listen(int tileX, int tileY, int viewWidth, Func<int, SoundPlace?> ownerAt)
    {
        ArgumentNullException.ThrowIfNull(ownerAt);
        (_centreX, _centreY, _viewWidth) = (tileX, tileY, viewWidth);
        if (Math.Abs(tileX - _heardX) + Math.Abs(tileY - _heardY) <= SoundRules.RelocateTiles)
        {
            return;
        }

        (_heardX, _heardY) = (tileX, tileY);
        int first = SoundRules.FirstSlot(SoundChannel.Effect);
        int last = SoundRules.FirstSlot(SoundChannel.Interface);
        for (int i = first; i < last; i++)
        {
            AudioStreamPlayer voice = _voices[i];
            if (voice is null || !(voice.Playing || voice.StreamPaused) || _places[i] is not { } at)
            {
                continue;
            }

            if (_states[i].Owner != 0 && ownerAt(_states[i].Owner) is { } now)
            {
                at = now;
                _places[i] = now;
            }

            bool effect = i < SoundRules.FirstSlot(SoundChannel.Unit);
            int dx = at.TileX - tileX;
            int dy = at.TileY - tileY;
            if (SoundRules.Placement(dx, dy, viewWidth, effect) is not { } placed)
            {
                voice.StreamPaused = false;
                voice.Stop();
                _states[i] = default;
                _places[i] = null;
                continue;
            }

            voice.VolumeDb = placed.Volume / 100f;
            _states[i] = _states[i] with { Distance = Math.Abs(dx) + Math.Abs(dy) };
        }
    }

    /// <summary>
    /// Plays a clip of a SOUNDS.INI set (or a direct file reference) in <paramref name="category"/>'s channel, with the
    /// priority of its <c>[priority]</c> key <paramref name="priorityKey"/>, heard from <paramref name="place"/> (null:
    /// the view's centre), for <paramref name="owner"/> (1 + an entity's index, or 0). False when nothing plays: the
    /// category is muted, an effect is too far from the view to start, the roll picked no clip or no voice is free.
    /// </summary>
    public bool PlaySet(string? set, SoundCategory category, string priorityKey, SoundPlace? place = null, int owner = 0)
    {
        ArgumentNullException.ThrowIfNull(priorityKey);
        if (_catalog is null || !_enabled[(int)category])
        {
            return false;
        }

        SoundChannel channel = ChannelOf(category);
        int volume = 0;
        int distance = 0;
        if (place is { } at && channel != SoundChannel.Interface)
        {
            int dx = at.TileX - _centreX;
            int dy = at.TileY - _centreY;
            if (SoundRules.Placement(dx, dy, _viewWidth, channel == SoundChannel.Effect) is not { } placed)
            {
                return false; // 0x4cfc71: too far to start
            }

            volume = placed.Volume;
            distance = Math.Abs(dx) + Math.Abs(dy);
        }

        IReadOnlyList<(string Path, int Weight)> files = _catalog.Sounds(set);
        _weights.Clear();
        foreach ((_, int weight) in files)
        {
            _weights.Add(weight);
        }

        int pick = SoundRules.Pick(_weights, _random.Next(SoundRules.RollRange));
        return pick >= 0 && Play(files[pick].Path, channel, priorityKey, distance, volume, channel == SoundChannel.Interface ? null : place, owner);
    }

    /// <summary>
    /// Silences the sounds of an entity (1 + its index): at its new order or its death, as the original (PRES-012,
    /// 0x4d05b0); a horseman's voice no longer runs on once it is sent elsewhere.
    /// </summary>
    public void StopOwner(int owner)
    {
        if (owner == 0)
        {
            return;
        }

        for (int i = 0; i < _voices.Length; i++)
        {
            if (_states[i].Owner == owner && _voices[i] is { } voice && (voice.Playing || voice.StreamPaused))
            {
                voice.StreamPaused = false;
                voice.Stop();
                _states[i] = default;
                _places[i] = null;
            }
        }
    }

    private static SoundChannel ChannelOf(SoundCategory category) => category switch
    {
        SoundCategory.Speech => SoundChannel.Unit,
        SoundCategory.Buildings => SoundChannel.Building,
        SoundCategory.Interface => SoundChannel.Interface,
        _ => SoundChannel.Effect,
    };

    private bool Play(string path, SoundChannel channel, string key, int distance, int volume, SoundPlace? place, int owner)
    {
        if (_catalog is null || Load(path) is not { } stream)
        {
            return false;
        }

        int first = SoundRules.FirstSlot(channel), count = SoundRules.Slots(channel), playing = 0;
        for (int i = first; i < first + count; i++)
        {
            bool busy = _voices[i].Playing || _voices[i].StreamPaused;
            _states[i] = busy ? _states[i] with { Busy = true } : default;
            playing += busy && _states[i].Category == key ? 1 : 0;
        }

        // Effects of a category give way to each other: each one playing lowers the next one's priority, and only a few
        // play at once (0x4cfc87, 0xc97ad8).
        bool effect = channel == SoundChannel.Effect;
        int basePriority = _catalog.SoundPriority(key);
        var sound = new VoiceState(true, key, effect ? SoundRules.EffectPriority(basePriority, playing) : basePriority, distance, owner);
        int k = SoundRules.Choose(_states.AsSpan(first, count), sound, atLimit: effect && playing >= SoundRules.MostAtOnce(key));
        if (k < 0)
        {
            return false;
        }

        AudioStreamPlayer voice = _voices[first + k];
        voice.StreamPaused = false;
        voice.Stream = stream;
        voice.VolumeDb = volume / 100f; // hundredths of a decibel
        voice.Play();
        _states[first + k] = sound;
        _places[first + k] = place;
        Played++;
        return true;
    }

    private AudioStreamWav? Load(string path)
    {
        if (_streams.TryGetValue(path, out AudioStreamWav? cached))
        {
            return cached;
        }

        AudioStreamWav? stream = null;
        if (_store is not null && _store.Vfs.Exists(path))
        {
            try
            {
                byte[] file = _store.Read(path);
                WaveData wave = WaveData.Parse(file);
                byte[] data = file.AsSpan(wave.DataOffset, wave.DataLength).ToArray();
                if (wave.BitsPerSample == 8)
                {
                    for (int i = 0; i < data.Length; i++)
                    {
                        data[i] ^= 0x80;
                    }
                }

                stream = new AudioStreamWav
                {
                    Format = wave.BitsPerSample == 8 ? AudioStreamWav.FormatEnum.Format8Bits : AudioStreamWav.FormatEnum.Format16Bits,
                    MixRate = wave.SampleRate,
                    Stereo = wave.Channels == 2,
                    Data = data,
                };
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                GD.PrintErr($"Sound {path}: {e.Message}");
            }
        }

        _streams[path] = stream;
        return stream;
    }
}
