using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Tests.Shared;
using Tzar2.Tools.Cli.Fidelity.Analysis;
using Tzar2.Tools.Cli.Fidelity.Capture;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Scenarios;
using Tzar2.Tools.Cli.Fidelity.Suite;
using Xunit;

namespace Tzar2.Tools.Cli.Tests.Fidelity;

/// <summary>Synthetic truths for the analyses of the second session's scenarios (ADR 0112) and for the suite.</summary>
public class SecondSessionAnalysisTests
{
    private static CaptureImage Flat(int w, int h, uint colour) => new(w, h, PixelFormat.Rgb888, Enumerable.Repeat(colour, w * h).ToArray());

    [Fact]
    public void Region_timing_finds_each_change_once()
    {
        // A box that changes at 2.0 s and 10.25 s (the second change lasts two frames), sampled every 250 ms.
        var frames = new List<(double T, CaptureImage Image)>();
        for (int i = 0; i < 60; i++)
        {
            double t = i * 250;
            uint colour = t < 2000 ? 0x202020u : t < 10250 ? 0x808080u : t < 10500 ? 0xFF0000u : 0x00FF00u;
            frames.Add((t + 1000, Flat(20, 10, colour)));
        }

        Assert.Equal([2.0, 10.25], RegionTiming.Events(frames));
        Assert.Equal(0, RegionTiming.ChangedFraction(Flat(4, 4, 0x101010), Flat(4, 4, 0x181818)));   // below the level threshold
    }

    [Fact]
    public void Day_clock_measures_the_cycle_and_the_night_share()
    {
        // A 600 s day, dark from 200 s to 440 s (night share 0.4), sampled every 2 s for 1,000 s.
        var series = Enumerable.Range(0, 500).Select(i =>
        {
            double t = i * 2.0, phase = t % 600;
            return (t, phase is >= 200 and < 440 ? 40.0 : 160.0);
        }).ToList();
        DayClock.Cycle c = DayClock.Analyse(series);
        Assert.InRange(c.DayLength!.Value, 598, 602);
        Assert.InRange(c.NightShare!.Value, 0.39, 0.41);
        Assert.Null(DayClock.Analyse(series.Select(s => (s.t, 100.0)).ToList()).DayLength);
    }

    [Fact]
    public void Gathering_counts_stockpile_steps_and_names_the_hud_box()
    {
        // Stockpile 1000 rising by 10 every 12 s; a decoy that wobbles.
        var times = Enumerable.Range(0, 400).Select(i => (long)(i * 100_000)).ToList();
        long[] stock = times.Select(t => 1000 + (10 * (t / 12_000_000))).ToArray();
        long[] decoy = times.Select((_, i) => (long)(1000 + (i % 3))).ToArray();
        var series = new MemoryProbe.SampleSeries([1ul, 2ul], 4, times, [decoy, stock]);
        AttackTiming.HitSeries hits = AttackTiming.BestSeries(series, rising: true)!;
        Assert.Equal(2ul, hits.Address);
        Assert.All(hits.Damage, d => Assert.Equal(10, d));
        Assert.Equal(12.0, (hits.HitTimesMs[1] - hits.HitTimesMs[0]) / 1000.0, 3);

        CaptureImage before = Flat(1024, 40, 0x000000);
        CaptureImage after = Flat(1024, 40, 0x000000);
        for (int y = 5; y < 15; y++)
        {
            for (int x = 440; x < 470; x++)
            {
                after.Native[(y * 1024) + x] = 0xFFFFFF;
            }
        }

        Assert.Equal("wood", GatherRates.ChangedBox(before, after, GatherRates.HudBoxes));
        Assert.Null(GatherRates.ChangedBox(before, before, GatherRates.HudBoxes));
    }

    [Fact]
    public void Suite_turns_facts_into_pass_fail_and_pending()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "unit-speed.yaml"), """
            schema: tzar2-fidelity-facts/1
            scenario: unit-speed
            analyses:
              unit-speed:
                status: ok
                facts:
                  measurements:
                    - {unit: peon, speed_stat: "4", px_per_s: 48.5, r2: 0.99}
                    - {unit: knight, speed_stat: "6", px_per_s: 71, r2: 0.99}
                    - {unit: pikeman, speed_stat: "2", px_per_s: 24.2, r2: 0.98}
            """);
        File.WriteAllText(Path.Combine(dir.Path, "attack-timing.yaml"), """
            schema: tzar2-fidelity-facts/1
            scenario: attack-timing
            analyses:
              attack-timing:
                status: ok
                facts:
                  measurements:
                    - {trial: archer-vs-peon, rate_stat: "4", attack_stat: "5", defence_stat: "0", hits: 6, period_ms_median: 2000, sample_interval_ms: 10, damage_values: [4x6]}
            """);
        var engine = EngineModel.Load(OriginPaths.RepositoryRoot);
        var results = FidelitySuite.Evaluate(new FactsIndex(dir.Path), engine).ToDictionary(r => r.Check.Id, r => r.Result.Status);

        // Speeds: 12 px/s per point in the engine -> 48, 72, 24 within 10 %.
        Assert.Equal(CheckStatus.Pass, results["m2.speed-per-point"]);
        // 2,000 ms measured against 6,000 / 4 = 1,500 ms; 4 damage against max(1, 5 - 0) = 5.
        Assert.Equal(CheckStatus.Fail, results["m2.attack-cycle"]);
        Assert.Equal(CheckStatus.Fail, results["m2.damage"]);
        Assert.Equal(CheckStatus.Pending, results["m3.day-length"]);

        // The M3 gate counts every §22.3 check, pending ones as not passing; pass / measured is only secondary.
        var report = new StringWriter();
        FidelitySuite.Gate gate = FidelitySuite.WriteReport(FidelitySuite.Evaluate(new FactsIndex(dir.Path), engine), report);
        int total = FidelitySuite.Checks.Count(c => c.Section == FidelitySuite.GameplaySection);
        Assert.Equal((total, 3, 1), (gate.Total, gate.Measured, gate.Passing));
        Assert.Equal(1.0 / total, gate.PassRate, 6);
        Assert.Equal(1 / 3.0, gate.MeasuredPassRate!.Value, 6);
        Assert.Contains("M3 GATE", report.ToString(), StringComparison.Ordinal);
        Assert.Contains("secondary, not the gate", report.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FidelitySuite.Checks, c => c.Section == FidelitySuite.GameplaySection && c.Area == "rendering");
    }

    [Fact]
    public void Construction_time_removes_the_measured_walk()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "unit-speed.yaml"), """
            schema: tzar2-fidelity-facts/1
            scenario: unit-speed
            analyses:
              unit-speed:
                status: ok
                facts:
                  measurements:
                    - {unit: peon, speed_stat: "4", px_per_s: 40, r2: 0.99}
            """);
        // 100 px from the builder to the site's centre, 20 px half footprint: 80 px at 40 px/s = 2 s of walking.
        File.WriteAllText(Path.Combine(dir.Path, "build-time.yaml"), """
            schema: tzar2-fidelity-facts/1
            scenario: build-time
            analyses:
              region-timing:
                status: ok
                facts:
                  measurements:
                    - {measure: build, subject: lumber, expected_s: "30", last_event_s: 32.4, builder_from: "100,100", site: "160,180", site_half_px: "20"}
            """);
        var engine = EngineModel.Load(OriginPaths.RepositoryRoot);
        var build = FidelitySuite.Evaluate(new FactsIndex(dir.Path), engine).Single(r => r.Check.Id == "m2.build-time").Result;
        Assert.Equal(CheckStatus.Pass, build.Status);
        Assert.Contains("2 s walk", build.Detail, StringComparison.Ordinal);

        // Without the peasant's measured speed the walk cannot be removed: pending, not a guess.
        File.Delete(Path.Combine(dir.Path, "unit-speed.yaml"));
        Assert.Equal(CheckStatus.Pending, FidelitySuite.Evaluate(new FactsIndex(dir.Path), engine).Single(r => r.Check.Id == "m2.build-time").Result.Status);
    }
}
