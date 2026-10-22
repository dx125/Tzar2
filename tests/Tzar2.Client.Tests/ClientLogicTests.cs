using System;
using System.Linq;
using Tzar2.Client.Logic;
using Xunit;

namespace Tzar2.Client.Tests;

public class ClientLogicTests
{
    [Fact]
    public void Radix_sort_orders_by_key_and_keeps_insertion_order_for_ties()
    {
        uint[] keys = [5, 1, 0x01000000, 5, 0, 1, 0xFFFFFFFF, 0x00010000];
        int[] order = new int[keys.Length];
        new RadixSort().Sort(keys, order);
        Assert.Equal([4, 1, 5, 0, 3, 7, 2, 6], order);
    }

    [Fact]
    public void Radix_sort_matches_a_stable_reference_sort_on_random_keys()
    {
        var random = new Random(1234);
        var sort = new RadixSort();
        foreach (int n in new[] { 0, 1, 2, 17, 300, 5000 })
        {
            uint[] keys = Enumerable.Range(0, n).Select(_ => (uint)random.Next(0, 64) << (8 * random.Next(0, 4))).ToArray();
            int[] order = new int[n + 3];
            sort.Sort(keys, order);
            int[] expected = Enumerable.Range(0, n).OrderBy(i => keys[i]).ThenBy(i => i).ToArray();
            Assert.Equal(expected, order.Take(n));
        }
    }

    [Fact]
    public void Radix_sort_rejects_a_short_order_buffer()
        => Assert.Throws<ArgumentException>(() => new RadixSort().Sort(new uint[3], new int[2]));

    [Fact]
    public void Viewer_args_parse_options_and_report_mistakes()
    {
        ViewerArgs args = ViewerArgs.Parse(["--viewer", "--package", "/x/base.t2pkg", "--soundfont", "gm.sf2", "--smoke", "--select", "e_knight", "--config", "c.cfg", "--capture", "out"]);
        Assert.Equal(("/x/base.t2pkg", "gm.sf2", "e_knight", "c.cfg", "out"), (args.Package, args.SoundFont, args.Select, args.Config, args.CaptureDir));
        Assert.True(args.Smoke);
        Assert.Empty(args.Errors);

        ViewerArgs bad = ViewerArgs.Parse(["--package", "--bogus"]);
        Assert.Null(bad.Package);
        Assert.Equal(["--package needs a value", "unknown option '--bogus'"], bad.Errors);
        Assert.True(ViewerArgs.Parse(["-h"]).Help);
        Assert.Contains("--package", ViewerArgs.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void The_render_bench_has_its_options()
    {
        ViewerArgs bench = ViewerArgs.Parse(["--bench-render", "--bench-seconds", "60", "--resolution", "1920x1080", "--fullscreen"]);
        Assert.Equal((true, 60, (1920, 1080), true), (bench.BenchRender, bench.BenchSeconds, bench.Resolution, bench.Fullscreen));
        Assert.Empty(bench.Errors);
        ViewerArgs defaults = ViewerArgs.Parse(["--bench-render"]);
        Assert.Equal((120, (2560, 1440), false), (defaults.BenchSeconds, defaults.Resolution, defaults.Fullscreen));
        Assert.Equal(["--resolution needs WIDTHxHEIGHT, not 'big'"], ViewerArgs.Parse(["--resolution", "big"]).Errors);
        Assert.Contains("--bench-render", ViewerArgs.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void Sound_font_locator_takes_the_first_existing_candidate()
    {
        Assert.Equal("b", SoundFontLocator.Choose(p => p is "b" or "c", null, " ", "a", "b", "c"));
        Assert.Null(SoundFontLocator.Choose(_ => false, "a", null));
    }
}
