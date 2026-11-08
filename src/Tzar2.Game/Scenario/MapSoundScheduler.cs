using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled.Scenario;

namespace Tzar2.Game.Scenario;

/// <summary>A map sound to start now (ADR 0284).</summary>
/// <param name="Index">The sound's index in <see cref="ScenarioData.Sounds"/>.</param>
/// <param name="Looped">Whether it is a looped sound (it starts again as soon as it ends).</param>
public readonly record struct MapSoundCue(int Index, bool Looped);

/// <summary>What a sound's source is now.</summary>
public enum MapSoundSource : byte
{
    /// <summary>In play: its sound is heard.</summary>
    Present,

    /// <summary>Not appeared yet (Not Appear): its timer runs, its sound is not heard.</summary>
    Hidden,

    /// <summary>Gone from the game: its timer stops and its sound with it.</summary>
    Gone,
}

/// <summary>
/// The map's sound sources placed in the editor (TDD §18.2 "Custom sounds", ADR 0284), each on its own timer as the
/// original's (ReTzar's reading, <c>map_sounds</c>: message 0x2009, 0x502953, 0x505580):
/// <list type="bullet">
/// <item><see cref="SoundRepeat.Every"/>: at once, then every interval (once when the interval is 0);</item>
/// <item><see cref="SoundRepeat.Looped"/>: at once, then looked at every 2 s and started again when it has ended;</item>
/// <item><see cref="SoundRepeat.Random"/>: at once, then after a random time between the interval and the maximum;</item>
/// <item><see cref="SoundRepeat.Delayed"/>: once, after the interval.</item>
/// </list>
/// A source still sounding when its time comes waits another second. Times are game time (paused with the game).
/// Presentation only: the random times come from the client, never the simulation.
/// </summary>
public sealed class MapSoundScheduler
{
    private const int LoopLookMs = 2000;
    private const int BusyWaitMs = 1000;
    private readonly IReadOnlyList<CustomSoundDef> _sounds;
    private readonly Func<int, int, int> _random;
    private readonly long?[] _due;

    /// <summary>Creates the timers; <paramref name="random"/> gives a number in [min, max] (the client's own random).</summary>
    public MapSoundScheduler(IReadOnlyList<CustomSoundDef> sounds, Func<int, int, int> random)
    {
        ArgumentNullException.ThrowIfNull(sounds);
        ArgumentNullException.ThrowIfNull(random);
        _sounds = sounds;
        _random = random;
        _due = new long?[sounds.Count];
        for (int i = 0; i < _due.Length; i++)
        {
            CustomSoundDef s = sounds[i];
            _due[i] = s.Sound.Path.Length == 0 ? null : s.Repeat == SoundRepeat.Delayed ? Math.Max(0, s.IntervalMs) : 0;
        }
    }

    /// <summary>
    /// The sounds to start at game time <paramref name="nowMs"/> into <paramref name="start"/>, and the indices of sounds
    /// whose source is gone while they still play into <paramref name="silence"/>.
    /// </summary>
    /// <param name="nowMs">Game time in ms.</param>
    /// <param name="source">What sound <c>i</c>'s source is now.</param>
    /// <param name="sounding">Whether sound <c>i</c> still plays.</param>
    /// <param name="start">Receives the sounds to start.</param>
    /// <param name="silence">Receives the sounds to stop.</param>
    public void Update(long nowMs, Func<int, MapSoundSource> source, Func<int, bool> sounding, List<MapSoundCue> start, List<int> silence)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sounding);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(silence);
        for (int i = 0; i < _due.Length; i++)
        {
            if (_due[i] is not { } due || due > nowMs)
            {
                continue;
            }

            MapSoundSource state = source(i);
            if (state == MapSoundSource.Gone)
            {
                _due[i] = null;
                if (sounding(i))
                {
                    silence.Add(i);
                }

                continue;
            }

            if (sounding(i))
            {
                _due[i] = due + BusyWaitMs;
                continue;
            }

            CustomSoundDef s = _sounds[i];
            if (state == MapSoundSource.Present)
            {
                start.Add(new MapSoundCue(i, s.Repeat == SoundRepeat.Looped));
            }

            long? next = s.Repeat switch
            {
                SoundRepeat.Every when s.IntervalMs > 0 => s.IntervalMs,
                SoundRepeat.Looped => LoopLookMs,
                SoundRepeat.Random => _random(Math.Max(0, s.IntervalMs), Math.Max(s.IntervalMs, s.IntervalMaxMs)),
                _ => null,
            };
            _due[i] = next is { } ms ? due + Math.Max(1, ms) : null;
        }
    }
}
