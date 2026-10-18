using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Yaml;

namespace Tzar2.Tools.Cli.Fidelity.Analysis;

/// <summary>
/// <c>day-clock</c> (UNK-32): a long burst of a region holding the clock globe (left) and a piece of the world view
/// (right of x = <see cref="ViewFromColumn"/>) with day/night on. The view's mean brightness falls at dusk and rises at
/// dawn; the threshold is half-way between the darkest and brightest frame. Two crossings in the same direction give the
/// day length; a fall and the next rise give the night's share. The globe's own changes are reported as well.
/// </summary>
internal static class DayClock
{
    /// <summary>First column of the burst region that belongs to the world view (the globe sits left of it).</summary>
    public const int ViewFromColumn = 20;

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
                var series = new List<(double T, double Luma, CaptureImage Globe)>();
                foreach ((string file, double t) in burst.Frames)
                {
                    try
                    {
                        CaptureImage image = ctx.Session.ReadImage(trial, file);
                        series.Add((t / 1000.0, Luma(image, ViewFromColumn), Crop(image, 0, Math.Min(ViewFromColumn, image.Width))));
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
                double[] globe = RegionTiming.Events(series.Select(s => (s.T * 1000, s.Globe)).ToList());
                e.Add("duration_s", Math.Round(series[^1].T - t0, 1));
                e.Add("luma_min", Math.Round(cycle.Min, 2));
                e.Add("luma_max", Math.Round(cycle.Max, 2));
                e.Add("dusk_s", new YamlWriter.Flow(cycle.Falls.Select(v => (object?)Math.Round(v, 1)).ToList()), "brightness falls through the mid level");
                e.Add("dawn_s", new YamlWriter.Flow(cycle.Rises.Select(v => (object?)Math.Round(v, 1)).ToList()));
                e.Add("globe_changes", globe.Length);
                if (globe.Length >= 2)
                {
                    e.Add("globe_interval_s_median", Math.Round(AttackTiming.Median(globe.Zip(globe.Skip(1), (a, b) => b - a).ToArray()), 2));
                }

                if (cycle.DayLength is { } day)
                {
                    found++;
                    e.Add("day_length_s", Math.Round(day, 1));
                }
                else
                {
                    notes.Add($"{trial.Trial}: no full day/night cycle in {series[^1].T - t0:0} s");
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

    public sealed record Cycle(double Min, double Max, IReadOnlyList<double> Falls, IReadOnlyList<double> Rises, double? DayLength, double? NightShare);

    public static Cycle Analyse(IReadOnlyList<(double T, double Luma)> series)
    {
        double min = series.Min(s => s.Luma), max = series.Max(s => s.Luma);
        var falls = new List<double>();
        var rises = new List<double>();
        if (max <= 0 || (max - min) / max < MinimumSwing)
        {
            return new Cycle(min, max, falls, rises, null, null);
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

        double? day = falls.Count >= 2 ? falls[1] - falls[0] : rises.Count >= 2 ? rises[1] - rises[0] : null;
        double? share = null;
        if (day is { } d && falls.Count > 0 && rises.FirstOrDefault(r => r > falls[0]) is var rise && rise > falls[0])
        {
            share = (rise - falls[0]) / d;
        }

        return new Cycle(min, max, falls, rises, day, share);
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

    private static CaptureImage Crop(CaptureImage image, int x0, int x1)
    {
        int w = Math.Max(1, x1 - x0);
        uint[] native = new uint[w * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < w; x++)
            {
                native[(y * w) + x] = image.NativeAt(Math.Min(image.Width - 1, x0 + x), y);
            }
        }

        return new CaptureImage(w, image.Height, image.Format, native);
    }
}
