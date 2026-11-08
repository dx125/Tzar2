using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Scenario;
using Xunit;

namespace Tzar2.Game.Tests;

public class MapResourcesTests
{
    [Fact]
    public void A_maps_resources_are_found_by_the_path_its_scenario_names_case_aside_and_the_campaigns_after()
    {
        // ADR 0284: a converted map names "user/sartor.png"; an authored one may name "sartor.png"; a campaign shares its music.
        var shared = new MapAssets(new Dictionary<string, byte[]> { ["user/1.mid"] = [9] });
        var map = new MapAssets(new Dictionary<string, byte[]> { ["user/Sartor.png"] = [1], ["user/bad1.png"] = [2] }, shared);
        Assert.Equal([1], map.Find("user/sartor.PNG"));
        Assert.Equal([1], map.Find("Sartor.png"));
        Assert.Equal([2], map.Find(@"user\bad1.png"));
        Assert.Equal([9], map.Find("user/1.mid"));
        Assert.Null(map.Find("user/none.wav"));
        Assert.Null(map.Find(null));
        Assert.Equal(0, MapAssets.None.Count);
    }

    [Fact]
    [Trait("Rule", "SCN-003")]
    public void The_maps_sound_sources_keep_their_modes_wait_while_sounding_and_stop_with_their_object()
    {
        // ADR 0284 (ReTzar's reading of the original's map sounds: 0x502953, 0x505580).
        CustomSoundDef[] sounds =
        [
            new("every", new ResourceRef("user/bell.wav"), SoundRepeat.Every, 3000, 0, null, 1, 1),
            new("delayed", new ResourceRef("user/horn.wav"), SoundRepeat.Delayed, 5000, 0, null, 2, 2),
            new("looped", new ResourceRef("user/river.wav"), SoundRepeat.Looped, 0, 0, 7, 0, 0),
            new("random", new ResourceRef("user/bird.wav"), SoundRepeat.Random, 1000, 4000, null, 3, 3),
        ];
        var scheduler = new MapSoundScheduler(sounds, (min, max) => max);
        var source = MapSoundSource.Present;
        bool busy = false;
        var started = new List<(long At, int Index, bool Looped)>();
        var stopped = new List<(long At, int Index)>();
        var start = new List<MapSoundCue>();
        var silence = new List<int>();
        for (long t = 0; t <= 12_000; t += 500)
        {
            if (t == 6_000)
            {
                busy = true; // the river still flows when its look comes
            }

            if (t == 9_000)
            {
                source = MapSoundSource.Gone; // its object died
            }

            start.Clear();
            silence.Clear();
            scheduler.Update(t, i => i == 2 ? source : MapSoundSource.Present, i => i == 2 && busy, start, silence);
            started.AddRange(start.Select(c => (t, c.Index, c.Looped)));
            stopped.AddRange(silence.Select(i => (t, i)));
        }

        Assert.Equal([0L, 3000, 6000, 9000, 12000], started.Where(s => s.Index == 0).Select(s => s.At));
        Assert.Equal([5000L], started.Where(s => s.Index == 1).Select(s => s.At));
        Assert.Equal([0L, 2000, 4000], started.Where(s => s.Index == 2).Select(s => s.At));
        Assert.All(started.Where(s => s.Index == 2), s => Assert.True(s.Looped));
        Assert.Equal([0L, 4000, 8000, 12000], started.Where(s => s.Index == 3).Select(s => s.At));
        Assert.Equal([(9000L, 2)], stopped);

        // A source not appeared yet is not heard, but its timer runs.
        var hidden = new MapSoundScheduler([sounds[0]], (min, max) => min);
        start.Clear();
        hidden.Update(0, _ => MapSoundSource.Hidden, _ => false, start, silence);
        Assert.Empty(start);
        hidden.Update(3000, _ => MapSoundSource.Present, _ => false, start, silence);
        Assert.Single(start);
    }
}
