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

    [Fact]
    [Trait("Rule", "PRES-013")]
    public void A_sound_is_as_loud_as_its_tiles_from_the_views_centre_and_effects_far_off_do_not_start()
    {
        // 0x4d00e0 in a 1024 pixel view: an effect loses 2500 over 96 tiles (each way added up), a unit's voice 400 over 64.
        Assert.Equal((0, 0), SoundRules.Attenuation(0, 0, 1024, effect: true));
        Assert.Equal((-1250, 1600), SoundRules.Attenuation(32, 16, 1024, effect: true));
        Assert.Equal(-2500, SoundRules.Attenuation(60, 36, 1024, effect: true).Volume);
        Assert.Equal(-10000, SoundRules.Attenuation(60, 37, 1024, effect: true).Volume); // past the reach: silent
        Assert.Equal(-200, SoundRules.Attenuation(-16, 16, 1024, effect: false).Volume);
        Assert.Equal(10000, SoundRules.Attenuation(200, 0, 1024, effect: true).Pan); // the pan within ±10000
        Assert.Equal(SoundRules.Attenuation(30, 0, 1024, true), SoundRules.Attenuation(30, 0, 1920, true)); // wider views as 1024

        // An effect starts only louder than -2500 (0x4cfc71); a unit's or a building's voice is never quieter than -400.
        Assert.NotNull(SoundRules.Placement(95, 0, 1024, effect: true));
        Assert.Null(SoundRules.Placement(96, 0, 1024, effect: true));
        Assert.Null(SoundRules.Placement(60, 0, 640, effect: true)); // a narrower view hears less far (60 tiles)
        Assert.Equal(-400, SoundRules.Placement(500, 0, 1024, effect: false)!.Value.Volume);
    }

    [Fact]
    [Trait("Rule", "PRES-013")]
    public void A_unit_is_heard_only_while_the_frames_show_it_not_where_it_was_last_seen()
    {
        // The seventh play-test: blows sounded where a fight had been, from units gone into the fog or dead there.
        var seen = new SeenPlaces();
        seen.BeginFrame();
        seen.Saw(7, 100, 200, 3, 1);
        seen.Saw(8, 300, 400, 4, 2);
        Assert.True(seen.TryGet(7, out SeenPlaces.Place at));
        Assert.Equal((100, 200, (ushort)3, (byte)1), (at.X, at.Y, at.Type, at.Owner));

        // The next frame shows only 8: 7 died since (its death cry is heard where it fell) or went into the fog.
        seen.BeginFrame();
        seen.Saw(8, 310, 400, 4, 2);
        Assert.True(seen.TryGet(7, out _));
        Assert.Equal(310, seen.TryGet(8, out SeenPlaces.Place moved) ? moved.X : 0);

        // A frame later 7 is no longer heard at all, though it fights on in the fog.
        seen.BeginFrame();
        Assert.False(seen.TryGet(7, out _));
        Assert.False(seen.TryGet(9, out _));
        for (int i = 0; i < 600; i++)
        {
            seen.BeginFrame(); // forgetting the old ones now and then
        }

        Assert.False(seen.TryGet(8, out _));
    }
}
