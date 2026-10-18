using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Yaml;

namespace Tzar2.Tools.Cli.Fidelity.Analysis;

/// <summary>
/// <c>region-timing</c>: when does a small screen region change? Used for times the HUD shows (TDD §22.3):
/// the population box steps once per trained unit (production time, a queue of several gives the interval), the
/// selection panel of a construction site turns into the building's card when it is finished (construction time).
/// A frame is a change when at least 2 % of the region's pixels differ from the previous frame by more than 16 levels
/// in some channel; consecutive changed frames count as one event, timed at its first frame.
/// </summary>
internal static class RegionTiming
{
    public const double ChangedShare = 0.02;
    public const int LevelThreshold = 16;

    public static AnalysisOutcome Run(AnalysisContext ctx, IReadOnlyList<TrialData> trials)
    {
        var notes = new List<string>();
        var entries = new List<YamlWriter>();
        int found = 0;
        foreach (TrialData trial in trials)
        {
            foreach (CaptureData burst in trial.Of("burst"))
            {
                var frames = new List<(double T, CaptureImage Image)>();
                foreach ((string file, double t) in burst.Frames)
                {
                    try
                    {
                        frames.Add((t, ctx.Session.ReadImage(trial, file)));
                    }
                    catch (Exception ex) when (ex is System.IO.InvalidDataException or System.IO.FileNotFoundException)
                    {
                        notes.Add($"{trial.Trial}/{burst.Name}: unreadable frame {file}");
                    }
                }

                double[] events = Events(frames);
                var e = new YamlWriter();
                e.Add("trial", trial.Trial);
                e.Add("burst", burst.Name);
                e.Add("measure", trial.Var("measure"));
                e.Add("subject", trial.Var("subject"));
                e.Add("expected_s", trial.Var("expected_s"), "the reference build's TIME column");
                e.Add("count", trial.Var("count"));
                if (trial.Var("site") is { } site)
                {
                    e.Add("builder_from", trial.Var("builder_from"), "screen x,y of the builder when ordered");
                    e.Add("site", site, "screen x,y of the site's centre");
                    e.Add("site_half_px", trial.Var("site_half_px"), "half the footprint: the builder stops at the edge");
                }

                e.Add("frames", frames.Count);
                e.Add("events_s", new YamlWriter.Flow(events.Select(v => (object?)Math.Round(v, 3)).ToList()), "seconds after the first frame");
                if (events.Length >= 2)
                {
                    double[] intervals = events.Zip(events.Skip(1), (a, b) => b - a).ToArray();
                    e.Add("interval_s_median", Math.Round(AttackTiming.Median(intervals), 3));
                }

                if (events.Length > 0)
                {
                    found++;
                    e.Add("last_event_s", Math.Round(events[^1], 3));
                }
                else
                {
                    notes.Add($"{trial.Trial}/{burst.Name}: the region never changed");
                }

                entries.Add(e);
            }
        }

        var facts = new YamlWriter();
        facts.Add("note", "times are window-capture timestamps (QueryPerformanceCounter) at the default game speed");
        facts.Add("measurements", entries);
        return new AnalysisOutcome(found > 0 ? "ok" : "inconclusive", facts, notes);
    }

    /// <summary>Event times (seconds after the first frame) of changes between consecutive frames.</summary>
    public static double[] Events(IReadOnlyList<(double T, CaptureImage Image)> frames)
    {
        var events = new List<double>();
        bool inEvent = false;
        for (int i = 1; i < frames.Count; i++)
        {
            bool changed = ChangedFraction(frames[i - 1].Image, frames[i].Image) >= ChangedShare;
            if (changed && !inEvent)
            {
                events.Add((frames[i].T - frames[0].T) / 1000.0);
            }

            inEvent = changed;
        }

        return [.. events];
    }

    public static double ChangedFraction(CaptureImage a, CaptureImage b)
    {
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        if (w == 0 || h == 0)
        {
            return 0;
        }

        int changed = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                (int r1, int g1, int b1) = Rgb8(a, a.NativeAt(x, y));
                (int r2, int g2, int b2) = Rgb8(b, b.NativeAt(x, y));
                if (Math.Abs(r1 - r2) > LevelThreshold || Math.Abs(g1 - g2) > LevelThreshold || Math.Abs(b1 - b2) > LevelThreshold)
                {
                    changed++;
                }
            }
        }

        return changed / (double)(w * h);
    }

    /// <summary>A pixel as 8-bit RGB whatever the capture's format.</summary>
    public static (int R, int G, int B) Rgb8(CaptureImage image, uint native)
    {
        (int r, int g, int b) = image.Levels(native);
        return image.Format.Bits == 16
            ? (ColourDepth.Replicate(r, image.Format.RedBits, 8), ColourDepth.Replicate(g, image.Format.GreenBits, 8), ColourDepth.Replicate(b, image.Format.BlueBits, 8))
            : (r, g, b);
    }

    internal static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
}
