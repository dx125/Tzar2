using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Yaml;

namespace Tzar2.Tools.Cli.Fidelity.Analysis;

/// <summary>
/// <c>day-clock</c> (UNK-32): a long burst of a region holding the clock globe (left) and a piece of the world view with
/// day/night on. The globe is a 24-hour clock (one frame of its strip per hour) that steps once per game hour, so the
/// time between its steps gives the hour and the day; the steps are read on the globe's disc only, which nothing in the
/// world view can cover. The view's brightness (right of <see cref="ViewFromColumn"/>) falls at dusk and rises at dawn
/// and gives the night's share, but only when its crossings are clean: units and buildings walking into the view make
/// crossings seconds apart (batch D, ADR 0173), and then no brightness figure is reported.
/// </summary>
internal static class DayClock
{
    /// <summary>First column of the burst region that is world view only (the globe ends at column 40).</summary>
    public const int ViewFromColumn = 44;

    /// <summary>Centre of the globe's disc in the burst region (LAYOUT1024L clock = 142,31,36,36; the region starts at 138,32).</summary>
    public const int GlobeCenterX = 22;

    /// <inheritdoc cref="GlobeCenterX"/>
    public const int GlobeCenterY = 17;

    /// <summary>Radius of the disc read for the globe's steps (inside its frame).</summary>
    public const int GlobeRadius = 13;

    /// <summary>Frames of the globe's strip, one per hour (IMAGES/SCREENS/S_VIEW/DAYNIGHT.BMP: 36 × 864, 24 frames of 36 × 36).</summary>
    public const int GlobeHours = 24;

    /// <summary>Share of the disc that changes when the globe steps.</summary>
    public const double GlobeStepShare = 0.05;

    /// <summary>Globe changes closer than this are one step.</summary>
    public const double MinimumStepSeconds = 30;

    /// <summary>Brightness crossings closer than this are units or buildings in the view, not dusk or dawn.</summary>
    public const double MinimumCrossingGapSeconds = 60;

    /// <summary>Brightness swing (share of the maximum) below which no day/night cycle is assumed.</summary>
    public const double MinimumSwing = 0.1;

    public static AnalysisOutcome Run(AnalysisContext ctx, IReadOnlyList<TrialData> trials)
    {
        var notes = new List<string>();
        var entries = new List<YamlWriter>();
        int found = 0;
        foreach (TrialData trial in trials)
        {
            foreach (CaptureData burst in trial.Of("burst"))
            {
                var series = new List<(double T, double Luma, CaptureImage Image)>();
                foreach ((string file, double t) in burst.Frames)
                {
                    try
                    {
                        CaptureImage image = ctx.Session.ReadImage(trial, file);
                        series.Add((t / 1000.0, Luma(image, ViewFromColumn), image));
                    }
                    catch (Exception ex) when (ex is System.IO.InvalidDataException or System.IO.FileNotFoundException)
                    {
                        notes.Add($"{trial.Trial}/{burst.Name}: unreadable frame {file}");
                    }
                }

                var e = new YamlWriter();
                e.Add("trial", trial.Trial);
                e.Add("frames", series.Count);
                if (series.Count < 3)
                {
                    e.Add("result", "too few frames");
                    entries.Add(e);
                    continue;
                }

                double t0 = series[0].T;
                Cycle cycle = Analyse(series.Select(s => (s.T - t0, s.Luma)).ToList());
                double[] steps = GlobeSteps(series.Select(s => (s.T - t0, s.Image)).ToList());
                double? hour = HourLength(steps);
                e.Add("duration_s", Math.Round(series[^1].T - t0, 1));
                e.Add("globe_steps_s", new YamlWriter.Flow(steps.Select(v => (object?)Math.Round(v, 1)).ToList()), "one step per game hour");
                if (hour is { } h)
                {
                    found++;
                    e.Add("hour_s", Math.Round(h, 2));
                    e.Add("day_length_s", Math.Round(GlobeHours * h, 1), "24 globe hours; assumes the night's hours are as long as the measured ones");
                    e.Add("day_length_from", "globe");
                }
                else
                {
                    notes.Add($"{trial.Trial}: {steps.Length} globe steps, not three at regular intervals");
                }

                e.Add("luma_min", Math.Round(cycle.Min, 2));
                e.Add("luma_max", Math.Round(cycle.Max, 2));
                e.Add("dusk_s", new YamlWriter.Flow(cycle.Falls.Select(v => (object?)Math.Round(v, 1)).ToList()), "brightness falls through the mid level");
                e.Add("dawn_s", new YamlWriter.Flow(cycle.Rises.Select(v => (object?)Math.Round(v, 1)).ToList()));
                e.Add("brightness", cycle.Clean ? "clean" : "noisy");
                if (!cycle.Clean)
                {
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"{trial.Trial}: brightness crossings less than {MinimumCrossingGapSeconds:0} s apart (units or buildings in the view); no brightness figures"));
                }

                if (hour is null && cycle.DayLength is { } day)
                {
                    found++;
                    e.Add("day_length_s", Math.Round(day, 1));
                    e.Add("day_length_from", "brightness");
                }

                if (cycle.NightShare is { } share)
                {
                    e.Add("night_share", Math.Round(share, 4), "dusk to dawn as a share of the day");
                }

                entries.Add(e);
            }
        }

        var facts = new YamlWriter();
        facts.Add("note", "wall-clock seconds at the default game speed; day/night switched on for this scenario only");
        facts.Add("measurements", entries);
        return new AnalysisOutcome(found > 0 ? "ok" : "inconclusive", facts, notes);
    }

    /// <summary>Brightness extremes, its crossings of the mid level, and the cycle they give when they are clean.</summary>
    public sealed record Cycle(double Min, double Max, IReadOnlyList<double> Falls, IReadOnlyList<double> Rises, double? DayLength, double? NightShare, bool Clean);

    public static Cycle Analyse(IReadOnlyList<(double T, double Luma)> series)
    {
        double min = series.Min(s => s.Luma), max = series.Max(s => s.Luma);
        var falls = new List<double>();
        var rises = new List<double>();
        if (max <= 0 || (max - min) / max < MinimumSwing)
        {
            return new Cycle(min, max, falls, rises, null, null, true);
        }

        double mid = (min + max) / 2;
        for (int i = 1; i < series.Count; i++)
        {
            (double ta, double a) = series[i - 1];
            (double tb, double b) = series[i];
            double at = ta + ((tb - ta) * (mid - a) / (b - a == 0 ? 1 : b - a));
            if (a >= mid && b < mid)
            {
                falls.Add(at);
            }
            else if (a < mid && b >= mid)
            {
                rises.Add(at);
            }
        }

        double[] crossings = [.. falls.Concat(rises).Order()];
        if (crossings.Zip(crossings.Skip(1), (a, b) => b - a).Any(gap => gap < MinimumCrossingGapSeconds))
        {
            return new Cycle(min, max, falls, rises, null, null, false);
        }

        double? day = falls.Count >= 2 ? falls[1] - falls[0] : rises.Count >= 2 ? rises[1] - rises[0] : null;
        double? share = null;
        if (day is { } d && falls.Count > 0 && rises.FirstOrDefault(r => r > falls[0]) is var rise && rise > falls[0])
        {
            share = (rise - falls[0]) / d;
        }

        return new Cycle(min, max, falls, rises, day, share, true);
    }

    /// <summary>Times (seconds) at which the globe's disc changed; changes closer than <see cref="MinimumStepSeconds"/> are one step.</summary>
    public static double[] GlobeSteps(IReadOnlyList<(double T, CaptureImage Image)> frames)
    {
        var steps = new List<double>();
        for (int i = 1; i < frames.Count; i++)
        {
            if (DiscChange(frames[i - 1].Image, frames[i].Image) >= GlobeStepShare && (steps.Count == 0 || frames[i].T - steps[^1] >= MinimumStepSeconds))
            {
                steps.Add(frames[i].T);
            }
        }

        return [.. steps];
    }

    /// <summary>
    /// The time per globe step from at least three steps at regular intervals (the longest gap at most 25 % above the
    /// shortest), or null.
    /// </summary>
    public static double? HourLength(IReadOnlyList<double> steps)
    {
        if (steps.Count < 3)
        {
            return null;
        }

        double[] gaps = [.. steps.Zip(steps.Skip(1), (a, b) => b - a)];
        return gaps.Max() <= gaps.Min() * 1.25 ? (steps[^1] - steps[0]) / (steps.Count - 1) : null;
    }

    private static double DiscChange(CaptureImage a, CaptureImage b)
    {
        int changed = 0, n = 0;
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        for (int y = Math.Max(0, GlobeCenterY - GlobeRadius); y <= Math.Min(h - 1, GlobeCenterY + GlobeRadius); y++)
        {
            for (int x = Math.Max(0, GlobeCenterX - GlobeRadius); x <= Math.Min(w - 1, GlobeCenterX + GlobeRadius); x++)
            {
                int dx = x - GlobeCenterX, dy = y - GlobeCenterY;
                if ((dx * dx) + (dy * dy) > GlobeRadius * GlobeRadius)
                {
                    continue;
                }

                n++;
                (int r1, int g1, int b1) = RegionTiming.Rgb8(a, a.NativeAt(x, y));
                (int r2, int g2, int b2) = RegionTiming.Rgb8(b, b.NativeAt(x, y));
                if (Math.Abs(r1 - r2) > RegionTiming.LevelThreshold || Math.Abs(g1 - g2) > RegionTiming.LevelThreshold || Math.Abs(b1 - b2) > RegionTiming.LevelThreshold)
                {
                    changed++;
                }
            }
        }

        return n == 0 ? 0 : changed / (double)n;
    }

    private static double Luma(CaptureImage image, int fromColumn)
    {
        double sum = 0;
        int n = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = fromColumn; x < image.Width; x++)
            {
                (int r, int g, int b) = RegionTiming.Rgb8(image, image.NativeAt(x, y));
                sum += (0.299 * r) + (0.587 * g) + (0.114 * b);
                n++;
            }
        }

        return n == 0 ? 0 : sum / n;
    }
}
