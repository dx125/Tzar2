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
/// effects of a category at once, each one more lowering the next one's priority; and an entity's sounds silenced at its
/// new order or its death. Per-category toggles and distance attenuation from the screen centre (⚠ UNK-33: the
/// original's attenuation and panning are not measured; no panning yet). Presentation only.
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
    /// Plays a clip of a SOUNDS.INI set (or a direct file reference) in <paramref name="category"/>'s channel, with the
    /// priority of its <c>[priority]</c> key <paramref name="priorityKey"/>, at <paramref name="distance"/> (0 = screen
    /// centre, 1 = a screen away; beyond 1.5 inaudible), for <paramref name="owner"/> (1 + an entity's index, or 0).
    /// False when nothing plays: the roll picked no clip, the category is muted or no voice is free.
    /// </summary>
    public bool PlaySet(string? set, SoundCategory category, string priorityKey, float distance = 0f, int owner = 0)
    {
        ArgumentNullException.ThrowIfNull(priorityKey);
        if (_catalog is null || !_enabled[(int)category] || distance > 1.5f)
        {
            return false;
        }

        IReadOnlyList<(string Path, int Weight)> files = _catalog.Sounds(set);
        _weights.Clear();
        foreach ((_, int weight) in files)
        {
            _weights.Add(weight);
        }

        int pick = SoundRules.Pick(_weights, _random.Next(SoundRules.RollRange));
        return pick >= 0 && Play(files[pick].Path, ChannelOf(category), priorityKey, distance, owner);
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

    private bool Play(string path, SoundChannel channel, string key, float distance, int owner)
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
        voice.VolumeDb = Mathf.LinearToDb(Math.Clamp(1f - (distance * 0.6f), 0.05f, 1f));
        voice.Play();
        _states[first + k] = sound;
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
