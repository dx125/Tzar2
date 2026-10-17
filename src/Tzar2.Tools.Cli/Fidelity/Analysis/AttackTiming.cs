using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Tools.Cli.Fidelity.Capture;
using Tzar2.Tools.Cli.Fidelity.Yaml;

namespace Tzar2.Tools.Cli.Fidelity.Analysis;

/// <summary>
/// <c>attack-timing</c> (UNK-07/UNK-08, §22.3): from memory samples of the defender's hit points (addresses found by
/// value scans), the time between hits (attack period) and the damage per hit. The candidate address with the most
/// downward steps (and almost no upward ones) is taken as the defender's HP.
/// </summary>
internal static class AttackTiming
{
    public sealed record HitSeries(ulong Address, IReadOnlyList<double> HitTimesMs, IReadOnlyList<long> Damage, long Start, long End);

    public static AnalysisOutcome Run(AnalysisContext ctx, IReadOnlyList<TrialData> trials)
    {
        var notes = new List<string>();
        var entries = new List<YamlWriter>();
        int found = 0;
        foreach (TrialData trial in trials)
        {
            foreach (CaptureData sample in trial.Of("memsample"))
            {
                MemoryProbe.SampleSeries series;
                try
                {
                    series = MemoryProbe.ReadSamples(ctx.Session.ReadTrialFile(trial, sample.File));
                }
                catch (Exception ex) when (ex is System.IO.InvalidDataException or System.IO.FileNotFoundException)
                {
                    notes.Add($"{trial.Trial}/{sample.Name}: {ex.Message}");
                    continue;
                }

                HitSeries? hits = BestSeries(series);
                var e = new YamlWriter();
                e.Add("trial", trial.Trial);
                e.Add("sample", sample.Name);
                e.Add("attacker", trial.Var("attacker"));
                e.Add("defender", trial.Var("defender"));
                e.Add("rate_stat", trial.Var("rate_stat"));
                e.Add("addresses", series.Addresses.Count);
                e.Add("samples", series.TimesMicroseconds.Count);
                if (series.TimesMicroseconds.Count > 1)
                {
                    e.Add("sample_interval_ms", Math.Round((series.TimesMicroseconds[^1] - series.TimesMicroseconds[0]) / 1000.0 / (series.TimesMicroseconds.Count - 1), 3));
                }

                if (hits is null || hits.HitTimesMs.Count < 3)
                {
                    e.Add("result", "no address with repeated hits");
                    notes.Add($"{trial.Trial}/{sample.Name}: no hit series found");
                }
                else
                {
                    found++;
                    double[] intervals = hits.HitTimesMs.Zip(hits.HitTimesMs.Skip(1), (a, b) => b - a).ToArray();
                    e.Add("hits", hits.HitTimesMs.Count);
                    e.Add("hp_start", hits.Start);
                    e.Add("hp_end", hits.End);
                    e.Add("period_ms_median", Math.Round(Median(intervals), 2));
                    e.Add("period_ms_min", Math.Round(intervals.Min(), 2));
                    e.Add("period_ms_max", Math.Round(intervals.Max(), 2));
                    e.Add("damage_values", new YamlWriter.Flow(hits.Damage.GroupBy(d => d).OrderBy(g => g.Key).Select(g => (object?)$"{g.Key}x{g.Count()}").ToList()));
                }

                entries.Add(e);
            }
        }

        var facts = new YamlWriter();
        facts.Add("note", "hit times are memory sample timestamps (wall clock); resolution = sample interval");
        facts.Add("measurements", entries);
        return new AnalysisOutcome(found > 0 ? "ok" : "inconclusive", facts, notes);
    }

    public static HitSeries? BestSeries(MemoryProbe.SampleSeries series)
    {
        HitSeries? best = null;
        for (int a = 0; a < series.Addresses.Count; a++)
        {
            long[] v = series.Values[a];
            if (v.Length < 3 || v.Any(x => x < -1000 || x > 100000))
            {
                continue;
            }

            var times = new List<double>();
            var damage = new List<long>();
            int rises = 0;
            for (int i = 1; i < v.Length; i++)
            {
                if (v[i] < v[i - 1])
                {
                    times.Add(series.TimesMicroseconds[i] / 1000.0);
                    damage.Add(v[i - 1] - v[i]);
                }
                else if (v[i] > v[i - 1])
                {
                    rises++;
                }
            }

            if (rises > 1 || times.Count == 0)
            {
                continue;
            }

            if (best is null || times.Count > best.HitTimesMs.Count)
            {
                best = new HitSeries(series.Addresses[a], times, damage, v[0], v[^1]);
            }
        }

        return best;
    }

    private static double Median(double[] values)
    {
        double[] sorted = [.. values.OrderBy(v => v)];
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
