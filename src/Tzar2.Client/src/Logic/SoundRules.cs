using System;
using System.Collections.Generic;

namespace Tzar2.Client.Logic;

/// <summary>The original's groups of hardware voices (ReTzar's reading, 0x4ceaac).</summary>
public enum SoundChannel
{
    /// <summary>Nature and rain: 3 voices.</summary>
    Nature = 0,

    /// <summary>Blows, shots, work, deaths, explosions: 19 voices.</summary>
    Effect = 1,

    /// <summary>The voices of units (selection, orders): 4 voices.</summary>
    Unit = 2,

    /// <summary>The voices of buildings: 4 voices.</summary>
    Building = 3,

    /// <summary>Tzar2's interface clicks: 2 voices.</summary>
    Interface = 4,
}

/// <summary>What a voice of the mixer is playing, for the choice of a voice for a new sound.</summary>
/// <param name="Busy">Whether it plays (or is held by the pause).</param>
/// <param name="Category">Its SOUNDS.INI <c>[priority]</c> key.</param>
/// <param name="Priority">The priority it started with.</param>
/// <param name="Distance">How far from the view's centre it was when it started (screens).</param>
/// <param name="Owner">The entity it belongs to (1 + its index), or 0.</param>
public readonly record struct VoiceState(bool Busy, string Category, int Priority, float Distance, int Owner);

/// <summary>
/// The original's mixing rules as ReTzar reads them (PRES-012; 0x455210, 0x4ceaac, 0xc97ad8, 0x4cfc87, 0x4cfb50,
/// 0x4d05b0): which clip of a set plays, if any; how many voices each channel has; how many effects of a category sound
/// at once; and which voice a new sound takes. Engine-free and unit-tested; the Godot mixer plays what they decide.
/// </summary>
public static class SoundRules
{
    /// <summary>The roll a set's clip is picked with: R(0, 1000), both ends included (0x455210).</summary>
    public const int RollRange = 1001;

    /// <summary>The voices of a channel (0x4ceaac: 3 + 19 + 4 + 4 of 32; the last 2 are Tzar2's interface).</summary>
    public static int Slots(SoundChannel channel) => channel switch
    {
        SoundChannel.Nature => 3,
        SoundChannel.Effect => 19,
        SoundChannel.Interface => 2,
        _ => 4,
    };

    /// <summary>The first voice of a channel in a pool of 32 laid out channel after channel.</summary>
    public static int FirstSlot(SoundChannel channel)
    {
        int first = 0;
        for (var c = SoundChannel.Nature; c < channel; c++)
        {
            first += Slots(c);
        }

        return first;
    }

    /// <summary>
    /// How many effects of a <c>[priority]</c> category play at once (0xc97ad8): blows 5, shots 6, deaths 3, chopping and
    /// building 4, digging 2, fire 3, explosions 4; the voices of units and buildings are not limited (200).
    /// </summary>
    public static int MostAtOnce(string category) => category switch
    {
        "attack_close" => 5,
        "attack_far" => 6,
        "death" => 3,
        "chop" or "build" or "explosion" => 4,
        "dig" or "mine" => 2,
        "fire" => 3,
        "mapedit_object" or "mapedit_action" => 5,
        "obj_taunt" => 8,
        _ => 200,
    };

    /// <summary>
    /// The clip of a set for a roll of R(0, 1000) (0x455210): the first whose range, from the weights before it to those
    /// plus its own, holds the roll; −1 when none does. A set whose weights add up to less than 1000 is silent for the
    /// rest: the original's sword blows (44 a clip, twelve clips) sound about half the time.
    /// </summary>
    public static int Pick(IReadOnlyList<int> weights, int roll)
    {
        ArgumentNullException.ThrowIfNull(weights);
        long low = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            long high = low + weights[i];
            if (roll >= low && roll <= high)
            {
                return i;
            }

            low = high;
        }

        return -1;
    }

    /// <summary>An effect's priority: its category's divided by one more than the effects of the category playing, unless it is 255 (0x4cfc87).</summary>
    public static int EffectPriority(int basePriority, int playing) => basePriority >= 255 ? 255 : basePriority / (playing + 1);

    /// <summary>
    /// The voice a new sound takes among a channel's (0x4cfb50), or −1: a free one; else one of lower priority; else one of
    /// the same priority no nearer to the view's centre. An effect whose category already plays as often as it may takes
    /// only, in the last pass, a voice of its own category no nearer.
    /// </summary>
    public static int Choose(ReadOnlySpan<VoiceState> slots, in VoiceState sound, bool atLimit)
    {
        for (int pass = atLimit ? 2 : 0; pass < 3; pass++)
        {
            for (int k = 0; k < slots.Length; k++)
            {
                VoiceState v = slots[k];
                bool take = !v.Busy
                    ? pass == 0
                    : pass switch
                    {
                        1 => sound.Priority > v.Priority,
                        2 when atLimit => v.Category == sound.Category && sound.Distance <= v.Distance,
                        2 => v.Priority == sound.Priority && sound.Distance <= v.Distance,
                        _ => false,
                    };
                if (take)
                {
                    return k;
                }
            }
        }

        return -1;
    }
}
