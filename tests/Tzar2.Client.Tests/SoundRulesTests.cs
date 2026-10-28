using System.Linq;
using Tzar2.Client.Logic;
using Xunit;

namespace Tzar2.Client.Tests;

/// <summary>The original's mixing as ReTzar reads it (PRES-012): the sixth play-test's sounds that hung on after a fight.</summary>
public sealed class SoundRulesTests
{
    [Fact]
    [Trait("Rule", "PRES-012")]
    public void Blows_sound_about_half_the_time_a_few_at_once_and_give_way_to_each_other()
    {
        // Twelve sword clips of 44 out of 1000 (SOUNDS.INI infantry_sword_attack): the rolls past 528 pick none.
        int[] swords = [.. Enumerable.Repeat(44, 12)];
        Assert.Equal(0, SoundRules.Pick(swords, 0));
        Assert.Equal(11, SoundRules.Pick(swords, 528));
        Assert.Equal(-1, SoundRules.Pick(swords, 529));
        int sounding = Enumerable.Range(0, SoundRules.RollRange).Count(r => SoundRules.Pick(swords, r) >= 0);
        Assert.InRange(sounding, 500, 560);
        Assert.Equal(0, SoundRules.Pick([SoundRules.RollRange], 1000)); // a file named directly always plays

        // 32 voices: 3 for nature, 19 for effects, 4 for the voices of units, 4 for buildings, 2 for the interface.
        Assert.Equal(32, System.Enum.GetValues<SoundChannel>().Sum(SoundRules.Slots));
        Assert.Equal(22, SoundRules.FirstSlot(SoundChannel.Unit));
        Assert.Equal((5, 6, 3, 200), (SoundRules.MostAtOnce("attack_close"), SoundRules.MostAtOnce("attack_far"), SoundRules.MostAtOnce("death"), SoundRules.MostAtOnce("obj_order")));

        // Each blow playing lowers the next one's priority; 255 never falls.
        Assert.Equal((170, 85, 34), (SoundRules.EffectPriority(170, 0), SoundRules.EffectPriority(170, 1), SoundRules.EffectPriority(170, 4)));
        Assert.Equal(255, SoundRules.EffectPriority(255, 7));

        // A free voice first; else a quieter one; else one of the same priority no nearer to the view's centre. At the
        // category's limit, only a blow no nearer gives way to the new one, never a free voice.
        VoiceState free = default;
        var blowFar = new VoiceState(true, "attack_close", 34, 1.0f, 1);
        var death = new VoiceState(true, "death", 100, 0.2f, 2);
        var near = new VoiceState(true, "attack_close", 34, 0.1f, 3);
        Assert.Equal(1, SoundRules.Choose([death, free], near, atLimit: false));
        Assert.Equal(0, SoundRules.Choose([blowFar, death], near, atLimit: false));
        Assert.Equal(-1, SoundRules.Choose([near, death], blowFar, atLimit: false));
        Assert.Equal(1, SoundRules.Choose([free, blowFar], near, atLimit: true));
        Assert.Equal(-1, SoundRules.Choose([free, death], near, atLimit: true));
    }
}
