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
    public void Construction_rate_comes_from_the_health_bar_before_helpers_join()
    {
        // A 60 px health bar on row 5 that one builder fills in 40 s (1.5 px/s); a second builder joins at 20 s and the
        // rate doubles (batch A: the lumber mill's rate doubled after 22 s). Sampled every 500 ms.
        const uint Green = 0x00C000, Red = 0xC00000, Grey = 0x404040;
        var frames = new List<(double T, CaptureImage Image)>();
        for (int i = 0; i < 80; i++)
        {
            double s = i * 0.5;
            int fill = (int)Math.Min(60, s <= 20 ? 1.5 * s : 30 + (3.0 * (s - 20)));
            uint[] px = Enumerable.Repeat(Grey, 80 * 10).ToArray();
            for (int x = 0; x < 60; x++)
            {
                px[(5 * 80) + 10 + x] = x < fill ? Green : Red;
            }

            frames.Add((5000 + (s * 1000), new CaptureImage(80, 10, PixelFormat.Rgb888, px)));
        }

        var bar = RegionTiming.BarRate(frames);
        Assert.NotNull(bar);
        Assert.Equal(5, bar!.Value.Row);
        Assert.Equal(60, bar.Value.Length);
        Assert.InRange(bar.Value.Length / bar.Value.PxPerSecond, 39, 41.5);   // the one-builder rate, not the doubled one
        Assert.InRange(bar.Value.ToS, 18, 22);
        Assert.Null(RegionTiming.BarRate([.. frames.Select(f => (f.T, Flat(80, 10, Grey)))]));   // no bar at all
    }

    [Fact]
    public void Construction_time_uses_the_health_bar_rate_and_the_time_unit()
    {
        using var dir = new TempDir();
        // Batch A: one builder fills the lumber mill's bar (TIME 30) in 36.85 s; the engine builds it in 30 x 1.25 s.
        File.WriteAllText(Path.Combine(dir.Path, "build-time.yaml"), """
            schema: tzar2-fidelity-facts/1
            scenario: build-time
            analyses:
              region-timing:
                status: ok
                facts:
                  measurements:
                    - {measure: build, subject: lumber, expected_s: "30", one_builder_full_s: 36.85, bar_px: 54}
            """);
        var engine = EngineModel.Load(OriginPaths.RepositoryRoot);
        var build = FidelitySuite.Evaluate(new FactsIndex(dir.Path), engine).Single(r => r.Check.Id == "m2.build-time").Result;
        Assert.Equal(CheckStatus.Pass, build.Status);   // no peasant speed needed: the bar rate has no walk in it
        Assert.Contains("37.5 s", build.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Carry_capacity_compares_each_load_with_the_load_from_that_node()
    {
        // Batch B: wood, stone and gold come 10 per trip, a cow's milk 50 (ADR 0170).
        using var dir = new TempDir();
        void Gather(string name, string target, string resource, int load) => File.WriteAllText(Path.Combine(dir.Path, $"{name}.yaml"), $"""
            schema: tzar2-fidelity-facts/1
            scenario: {name}
            analyses:
              gather-rates:
                status: ok
                facts:
                  measurements:
                    - {"{"}trial: {target}, target: {target}, resource: {resource}, deliveries: 5, load: {load}{"}"}
            """);
        Gather("gather-wood", "wood", "wood", 10);
        Gather("gather-cow", "cow", "food", 50);
        var engine = EngineModel.Load(OriginPaths.RepositoryRoot);
        CheckResult Carry() => FidelitySuite.Evaluate(new FactsIndex(dir.Path), engine).Single(r => r.Check.Id == "m2.carry-capacity").Result;
        Assert.Equal(CheckStatus.Pass, Carry().Status);

        Gather("gather-cow", "cow", "food", 10);   // a cow that gave only 10 would disagree with the engine's 50
        Assert.Equal(CheckStatus.Fail, Carry().Status);
        Assert.Contains("food 10 (engine 50)", Carry().Detail, StringComparison.Ordinal);
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
    public void Speed_check_waits_for_a_second_speed_value()
    {
        // Batch B (ADR 0171): four units at SPEED 4 fix a scale, not the proportionality the check is about.
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "unit-speed.yaml"), """
            schema: tzar2-fidelity-facts/1
            scenario: unit-speed
            analyses:
              unit-speed:
                status: ok
                facts:
                  measurements:
                    - {unit: peon, speed_stat: "4", px_per_s: 40.0, r2: 0.99}
                    - {unit: archer, speed_stat: "4", px_per_s: 39.9, r2: 0.98}
                    - {unit: priest, speed_stat: "4", px_per_s: 40.1, r2: 0.99}
                    - {unit: crusader, speed_stat: "5", px_per_s: 69.1, r2: 0.99, rejected: too short}
            """);
        var engine = EngineModel.Load(OriginPaths.RepositoryRoot);
        var check = FidelitySuite.Evaluate(new FactsIndex(dir.Path), engine).Single(r => r.Check.Id == "m2.speed-per-point");
        Assert.Equal(CheckStatus.Pending, check.Result.Status);
        Assert.Contains("SPEED 4 (needs 3 units and 2 SPEED values)", check.Result.Detail, StringComparison.Ordinal);
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
                    - {unit: peon, speed_stat: "4", px_per_s: 40.4, r2: 0.99}
                    - {unit: knight, speed_stat: "6", px_per_s: 59, r2: 0.99}
                    - {unit: pikeman, speed_stat: "2", px_per_s: 20.2, r2: 0.98}
                    - {unit: maceman, speed_stat: "3", px_per_s: 41, r2: 0.99, rejected: moves east in a burst ordered west}
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

        // Speeds: 10 px/s per point in the engine -> 40, 60, 20 within 10 %; the rejected maceman fit does not count.
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
                    - {measure: build, subject: lumber, expected_s: "30", last_event_s: 39.4, builder_from: "100,100", site: "160,180", site_half_px: "20"}
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
