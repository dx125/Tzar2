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
/// Sound effects (REQ-AUD-01): the imported WAVs played losslessly through a pool of <see cref="MaxVoices"/> voices
/// (<c>MIXERCHANNELS</c> default), SOUNDS.INI priorities (a full pool drops the lowest-priority voice for a higher one),
/// weighted variation choice with a presentation RNG, per-category toggles and distance attenuation from the screen
/// centre (⚠ UNK-33: the original's attenuation and panning are not measured; no panning yet). Presentation only.
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
    private readonly int[] _priorities = new int[MaxVoices];
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
    /// Plays one file of a SOUNDS.INI set (or a direct file reference) with <paramref name="priority"/> (0-255) at
    /// <paramref name="distance"/> (0 = screen centre, 1 = a screen away; beyond 1.5 inaudible).
    /// </summary>
    public bool PlaySet(string? set, SoundCategory category, int priority, float distance = 0f)
    {
        if (_catalog is null || !_enabled[(int)category] || distance > 1.5f)
        {
            return false;
        }

        IReadOnlyList<(string Path, int Weight)> files = _catalog.Sounds(set);
        if (files.Count == 0)
        {
            return false;
        }

        int total = 0;
        foreach ((_, int w) in files)
        {
            total += w;
        }

        int pick = _random.Next(total);
        string path = files[0].Path;
        foreach ((string p, int w) in files)
        {
            if (pick < w)
            {
                path = p;
                break;
            }

            pick -= w;
        }

        return PlayFile(path, priority, distance);
    }

    /// <summary>Plays a package WAV.</summary>
    public bool PlayFile(string path, int priority, float distance = 0f)
    {
        if (Load(path) is not { } stream)
        {
            return false;
        }

        int slot = -1;
        for (int i = 0; i < _voices.Length && slot < 0; i++)
        {
            if (!_voices[i].Playing && !_voices[i].StreamPaused)
            {
                slot = i;
            }
        }

        if (slot < 0)
        {
            int lowest = 0;
            for (int i = 1; i < _voices.Length; i++)
            {
                if (_priorities[i] < _priorities[lowest])
                {
                    lowest = i;
                }
            }

            if (_priorities[lowest] > priority)
            {
                return false;
            }

            slot = lowest;
        }

        AudioStreamPlayer voice = _voices[slot];
        voice.StreamPaused = false;
        voice.Stream = stream;
        voice.VolumeDb = Mathf.LinearToDb(Math.Clamp(1f - (distance * 0.6f), 0.05f, 1f));
        voice.Play();
        _priorities[slot] = priority;
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
