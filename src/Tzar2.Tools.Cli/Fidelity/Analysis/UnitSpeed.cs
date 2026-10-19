using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Yaml;

namespace Tzar2.Tools.Cli.Fidelity.Analysis;

/// <summary>
/// <c>unit-speed</c> (UNK-12, RE-06): tracks the one moving unit in a burst of window captures. The background is
/// the per-pixel median of the burst (the majority value, as the unit covers each pixel only briefly); pixels that
/// change in most frames (water, animated scenery) are ignored. The centroid of the remaining changed pixels is the
/// unit's position; a straight-line fit over the longest moving stretch gives pixels per second. Trials carry the
/// unit's <c>SPEED</c> value (<c>speed_stat</c>), so several trials give the SPEED → px/s relation. A fit that is too
/// short, or that runs against the direction a burst named <c>west</c> or <c>east</c> was ordered in, is kept with a
/// <c>rejected</c> reason and left out of the relation: in batch B military units broke off to attack Nature's cows
/// and the tracker followed the fight (ADR 0171).
/// </summary>
internal static class UnitSpeed
{
    /// <summary>A clean fit spans at least this long (three seconds of a 100 ms burst).</summary>
    public const double MinimumSeconds = 3.0;

    /// <summary>A clean fit covers at least this distance (about three tiles).</summary>
    public const double MinimumPixels = 100;

    public sealed record Track(IReadOnlyList<(double T, double X, double Y, int Pixels)> Points);

    public sealed record Fit(double PixelsPerSecond, double VelocityX, double VelocityY, double R2, int Frames, double DurationSeconds, double DistancePixels);

    public static AnalysisOutcome Run(AnalysisContext ctx, IReadOnlyList<TrialData> trials)
    {
        var notes = new List<string>();
        var entries = new List<YamlWriter>();
        var relation = new List<(double Stat, double Speed)>();
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

                Track? track = TrackMotion(frames);
                Fit? fit = track is null ? null : FitMotion(track);
                var e = new YamlWriter();
                e.Add("trial", trial.Trial);
                e.Add("burst", burst.Name);
                e.Add("unit", trial.Var("unit"));
                e.Add("speed_stat", trial.Var("speed_stat"));
                e.Add("frames", frames.Count);
                if (fit is null)
                {
                    e.Add("result", "no single moving object found");
                    notes.Add($"{trial.Trial}/{burst.Name}: no usable motion");
                }
                else
                {
                    e.Add("px_per_s", Math.Round(fit.PixelsPerSecond, 3));
                    e.Add("tiles_per_s", Math.Round(fit.PixelsPerSecond / 32, 4));
                    e.Add("velocity_px_per_s", new YamlWriter.Flow([Math.Round(fit.VelocityX, 3), Math.Round(fit.VelocityY, 3)]));
                    e.Add("r2", Math.Round(fit.R2, 4));
                    e.Add("moving_frames", fit.Frames);
                    e.Add("moving_s", Math.Round(fit.DurationSeconds, 3));
                    e.Add("distance_px", Math.Round(fit.DistancePixels, 1));
                    if (Rejection(burst.Name, fit) is { } reason)
                    {
                        e.Add("rejected", reason);
                        notes.Add($"{trial.Trial}/{burst.Name}: rejected, {reason}");
                    }
                    else if (double.TryParse(trial.Var("speed_stat"), NumberStyles.Float, CultureInfo.InvariantCulture, out double stat) && fit.R2 > 0.95)
                    {
                        relation.Add((stat, fit.PixelsPerSecond));
                    }
                }

                entries.Add(e);
            }
        }

        var facts = new YamlWriter();
        facts.Add("note", "wall-clock speeds at the game's default speed setting; window capture timestamps (QueryPerformanceCounter)");
        facts.Add("measurements", entries);
        if (relation.Select(r => r.Stat).Distinct().Count() >= 2)
        {
            (double slope, double intercept, double r2) = LinearFit(relation.Select(r => r.Stat).ToArray(), relation.Select(r => r.Speed).ToArray());
            facts.Map("speed_stat_relation", r =>
            {
                r.Add("px_per_s_per_speed", Math.Round(slope, 4));
                r.Add("intercept_px_per_s", Math.Round(intercept, 4));
                r.Add("r2", Math.Round(r2, 4));
                r.Add("points", relation.Count);
            });
        }

        bool ok = entries.Count > 0 && relation.Count > 0;
        return new AnalysisOutcome(ok ? "ok" : "inconclusive", facts, notes);
    }

    /// <summary>Why a fit cannot be the ordered walk, or null when it can.</summary>
    public static string? Rejection(string burst, Fit fit)
    {
        if (fit.DurationSeconds < MinimumSeconds || fit.DistancePixels < MinimumPixels)
        {
            return $"too short ({fit.DurationSeconds:0.0} s, {fit.DistancePixels:0} px; needs {MinimumSeconds:0} s and {MinimumPixels:0} px)";
        }

        int ordered = burst switch { "west" => -1, "east" => 1, _ => 0 };
        return ordered != 0 && Math.Sign(fit.VelocityX) != ordered
            ? $"moves {(fit.VelocityX < 0 ? "west" : "east")} in a burst ordered {burst}"
            : null;
    }

    public static Track? TrackMotion(IReadOnlyList<(double T, CaptureImage Image)> frames)
    {
        if (frames.Count < 5 || frames.Any(f => f.Image.Width != frames[0].Image.Width || f.Image.Height != frames[0].Image.Height))
        {
            return null;
        }

        int w = frames[0].Image.Width, h = frames[0].Image.Height, n = w * h;
        ushort[][] canonical = frames.Select(f => f.Image.ToCanonical555()).ToArray();
        int samples = Math.Min(canonical.Length, 31);
        ushort[] background = new ushort[n];
        ushort[] column = new ushort[samples];
        for (int i = 0; i < n; i++)
        {
            for (int s = 0; s < samples; s++)
            {
                column[s] = canonical[s * canonical.Length / samples][i];
            }

            Array.Sort(column);
            background[i] = column[samples / 2];
        }

        int[] changes = new int[n];
        foreach (ushort[] frame in canonical)
        {
            for (int i = 0; i < n; i++)
            {
                changes[i] += frame[i] != background[i] ? 1 : 0;
            }
        }

        int noisy = canonical.Length * 2 / 5;
        var points = new List<(double, double, double, int)>();
        for (int f = 0; f < canonical.Length; f++)
        {
            long sx = 0, sy = 0;
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                if (canonical[f][i] != background[i] && changes[i] <= noisy)
                {
                    sx += i % w;
                    sy += i / w;
                    count++;
                }
            }

            if (count >= 20)
            {
                points.Add((frames[f].T, sx / (double)count, sy / (double)count, count));
            }
        }

        return points.Count >= 5 ? new Track(points) : null;
    }

    public static Fit? FitMotion(Track track)
    {
        var p = track.Points;
        // Longest run of consecutive points that move faster than 5 px/s.
        int bestStart = 0, bestLength = 0, start = 0;
        for (int i = 1; i <= p.Count; i++)
        {
            bool moving = i < p.Count && Distance(p[i], p[i - 1]) / Math.Max(1e-6, (p[i].T - p[i - 1].T) / 1000) > 5;
            if (!moving)
            {
                if (i - start > bestLength)
                {
                    (bestStart, bestLength) = (start, i - start);
                }

                start = i;
            }
        }

        if (bestLength < 5)
        {
            return null;
        }

        var run = p.Skip(bestStart).Take(bestLength).ToList();
        double[] t = run.Select(q => q.T / 1000).ToArray();
        (double vx, _, double r2x) = LinearFit(t, run.Select(q => q.X).ToArray());
        (double vy, _, double r2y) = LinearFit(t, run.Select(q => q.Y).ToArray());
        double speed = Math.Sqrt((vx * vx) + (vy * vy));
        // Weight each axis' fit quality by its share of the motion.
        double r2 = speed == 0 ? 0 : ((Math.Abs(vx) * r2x) + (Math.Abs(vy) * r2y)) / (Math.Abs(vx) + Math.Abs(vy));
        return new Fit(speed, vx, vy, r2, run.Count, t[^1] - t[0], Distance(run[^1], run[0]));
    }

    private static double Distance((double T, double X, double Y, int Pixels) a, (double T, double X, double Y, int Pixels) b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    public static (double Slope, double Intercept, double R2) LinearFit(double[] x, double[] y)
    {
        double mx = x.Average(), my = y.Average();
        double sxx = 0, sxy = 0, syy = 0;
        for (int i = 0; i < x.Length; i++)
        {
            sxx += (x[i] - mx) * (x[i] - mx);
            sxy += (x[i] - mx) * (y[i] - my);
            syy += (y[i] - my) * (y[i] - my);
        }

        double slope = sxx == 0 ? 0 : sxy / sxx;
        double r2 = sxx == 0 || syy == 0 ? (syy == 0 ? 1 : 0) : sxy * sxy / (sxx * syy);
        return (slope, my - (slope * mx), r2);
    }
}
