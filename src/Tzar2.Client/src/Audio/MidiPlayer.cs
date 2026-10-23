using System;
using System.IO;
using Godot;
using MeltySynth;

namespace Tzar2.Client.Audio;

/// <summary>
/// MIDI music through MeltySynth (MIT, pure C#; REQ-AUD-02) rendered into an <c>AudioStreamGenerator</c>. The
/// SoundFont is supplied by the user (never bundled, ADR 0020); without one, playback is unavailable and callers show
/// why. Rendering happens on the main thread in <see cref="_Process"/> with a 0.5 s buffer, enough for a dev tool; the
/// game moves it to a streaming thread (TDD §7.3).
/// </summary>
public partial class MidiPlayer : Node
{
    private const int SampleRate = 44100;

    private AudioStreamPlayer? _output;
    private AudioStreamGeneratorPlayback? _playback;
    private Synthesizer? _synth;
    private MidiFileSequencer? _sequencer;
    private string? _soundFontPath;
    private float[] _left = new float[4096];
    private float[] _right = new float[4096];
    // Rendered and pushed in whole chunks of this many frames (11.6 ms; the generator buffers 0.5 s), through one array:
    // PushBuffer needs an array of the exact length, and slicing a fresh one every frame allocated (code review 2026-10).
    private const int Chunk = 512;
    private readonly Vector2[] _frames = new Vector2[Chunk];

    /// <summary>The loaded SoundFont, or null.</summary>
    public string? SoundFontPath => _soundFontPath;

    /// <summary>Whether a SoundFont is loaded.</summary>
    public bool HasSoundFont => _synth is not null;

    /// <summary>Whether music is playing.</summary>
    public bool Playing => _sequencer is not null && _output is { Playing: true };

    /// <inheritdoc/>
    public override void _Ready()
    {
        SoundMixer.EnsureBuses();
        _output = new AudioStreamPlayer
        {
            Stream = new AudioStreamGenerator { MixRate = SampleRate, BufferLength = 0.5f },
            Bus = SoundMixer.MusicBus,
        };
        AddChild(_output);
    }

    /// <summary>Loads a SoundFont (.sf2).</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="InvalidDataException">The file is not a SoundFont MeltySynth can read.</exception>
    public void LoadSoundFont(string path)
    {
        Stop();
        try
        {
            using FileStream stream = File.OpenRead(path);
            var soundFont = new SoundFont(stream);
            _synth = new Synthesizer(soundFont, SampleRate);
            _soundFontPath = path;
        }
        catch (Exception e) when (e is not IOException and not OutOfMemoryException)
        {
            _synth = null;
            _soundFontPath = null;
            throw new InvalidDataException($"'{path}' is not a usable SoundFont: {e.Message}", e);
        }
    }

    /// <summary>Starts looping playback of a Standard MIDI File.</summary>
    /// <exception cref="InvalidOperationException">No SoundFont is loaded.</exception>
    /// <exception cref="InvalidDataException">The MIDI file cannot be parsed.</exception>
    public void Play(byte[] midi)
    {
        if (_synth is null)
        {
            throw new InvalidOperationException("No SoundFont loaded.");
        }

        MidiFile file;
        try
        {
            file = new MidiFile(new MemoryStream(midi));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new InvalidDataException("MIDI: " + e.Message, e);
        }

        Stop();
        _synth.Reset();
        _sequencer = new MidiFileSequencer(_synth);
        _sequencer.Play(file, true);
        _output!.Play();
        _playback = (AudioStreamGeneratorPlayback)_output.GetStreamPlayback();
        Fill();
    }

    /// <summary>Stops playback.</summary>
    public void Stop()
    {
        _sequencer?.Stop();
        _sequencer = null;
        _output?.Stop();
        _playback = null;
    }

    /// <summary>Renders <paramref name="frames"/> sample frames without an audio device and returns the peak level (smoke test).</summary>
    public float RenderOffline(byte[] midi, int frames)
    {
        if (_synth is null)
        {
            throw new InvalidOperationException("No SoundFont loaded.");
        }

        Stop();
        var sequencer = new MidiFileSequencer(_synth);
        sequencer.Play(new MidiFile(new MemoryStream(midi)), false);
        float[] left = new float[frames], right = new float[frames];
        sequencer.Render(left, right);
        float peak = 0;
        for (int i = 0; i < frames; i++)
        {
            peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        }

        _synth.Reset();
        return peak;
    }

    /// <inheritdoc/>
    public override void _Process(double delta) => Fill();

    private void Fill()
    {
        if (_playback is null || _sequencer is null)
        {
            return;
        }

        int available = _playback.GetFramesAvailable();
        while (available >= Chunk)
        {
            Span<float> left = _left.AsSpan(0, Chunk), right = _right.AsSpan(0, Chunk);
            _sequencer.Render(left, right);
            for (int i = 0; i < Chunk; i++)
            {
                _frames[i] = new Vector2(left[i], right[i]);
            }

            _playback.PushBuffer(_frames);
            available -= Chunk;
        }
    }
}
