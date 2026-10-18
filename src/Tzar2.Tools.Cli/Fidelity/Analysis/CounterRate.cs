using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Tools.Cli.Fidelity.Capture;
using Tzar2.Tools.Cli.Fidelity.Yaml;

namespace Tzar2.Tools.Cli.Fidelity.Analysis;

/// <summary>
/// <c>counter-rate</c>: one value of the game (a building's or unit's hit points, a stockpile, mana) followed through the
/// phases of a trial. The trial names its <c>primary</c> sample and the <c>direction</c> the value moves in it; the
/// address with the most steps that way (at most one step back) is the counter. Every sample holding that address then
/// reports its steps and its rate (change per minute between the first and the last sample of the phase), so phases
/// such as "attack", "burn" and "repair" of one building can be compared. A phase in which the value never moves reports
/// rate 0.
/// </summary>
internal static class CounterRate
{
    public static AnalysisOutcome Run(AnalysisContext ctx, IReadOnlyList<TrialData> trials)
    {
        var notes = new List<string>();
        var entries = new List<YamlWriter>();
        int found = 0;
        foreach (TrialData trial in trials)
        {
            var samples = new List<(string Name, MemoryProbe.SampleSeries Series)>();
            foreach (CaptureData sample in trial.Of("memsample"))
            {
                try
                {
                    samples.Add((sample.Name, MemoryProbe.ReadSamples(ctx.Session.ReadTrialFile(trial, sample.File))));
                }
                catch (Exception ex) when (ex is System.IO.InvalidDataException or System.IO.FileNotFoundException)
                {
                    notes.Add($"{trial.Trial}/{sample.Name}: {ex.Message}");
                }
            }

            var e = new YamlWriter();
            e.Add("trial", trial.Trial);
            foreach (string var in new[] { "quantity", "subject", "max_value", "cost_before", "n", "odds", "stake", "distance_tiles", "load" })
            {
                if (trial.Var(var) is { } v)
                {
                    e.Add(var, v);
                }
            }

            string primary = trial.Var("primary") ?? samples.FirstOrDefault().Name ?? string.Empty;
            bool rising = trial.Var("direction") == "up";
            (string Name, MemoryProbe.SampleSeries Series) first = samples.FirstOrDefault(s => s.Name == primary);
            AttackTiming.HitSeries? best = first.Series is null ? null : AttackTiming.BestSeries(first.Series, rising);
            if (best is null)
            {
                e.Add("result", $"no counter stepping {(rising ? "up" : "down")} in '{primary}'");
                notes.Add($"{trial.Trial}: no counter in {primary}");
                entries.Add(e);
                continue;
            }

            found++;
            e.Add("primary", primary);
            e.Add("direction", rising ? "up" : "down");
            var phases = new List<YamlWriter>();
            foreach ((string name, MemoryProbe.SampleSeries series) in samples)
            {
                int index = series.Addresses.ToList().IndexOf(best.Address);
                if (index < 0)
                {
                    continue;
                }

                Phase p = Measure(series.TimesMicroseconds, series.Values[index]);
                var w = new YamlWriter();
                w.Add("sample", name);
                w.Add("start", p.Start);
                w.Add("end", p.End);
                w.Add("duration_s", Math.Round(p.DurationSeconds, 2));
                w.Add("rate_per_min", Math.Round(p.RatePerMinute, 2), "end minus start over the phase, per minute");
                w.Add("steps", p.Steps.Count);
                w.Add("step_values", new YamlWriter.Flow(p.Steps.GroupBy(s => s.Size).OrderBy(g => g.Key).Select(g => (object?)$"{g.Key}x{g.Count()}").ToList()));
                w.Add("first_step_s", p.Steps.Count == 0 ? null : Math.Round(p.Steps[0].Seconds, 2), "seconds after the phase's first sample");
                phases.Add(w);
            }

            e.Add("phases", phases);
            entries.Add(e);
        }

        var facts = new YamlWriter();
        facts.Add("note", "memory samples (wall clock, default game speed unless the trial says otherwise)");
        facts.Add("measurements", entries);
        return new AnalysisOutcome(found > 0 ? "ok" : "inconclusive", facts, notes);
    }

    public sealed record Phase(long Start, long End, double DurationSeconds, double RatePerMinute, IReadOnlyList<(double Seconds, long Size)> Steps);

    /// <summary>Start, end, rate and the signed steps of one counter over one phase.</summary>
    public static Phase Measure(IReadOnlyList<long> timesMicroseconds, long[] values)
    {
        if (values.Length == 0)
        {
            return new Phase(0, 0, 0, 0, []);
        }

        var steps = new List<(double, long)>();
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] != values[i - 1])
            {
                steps.Add(((timesMicroseconds[i] - timesMicroseconds[0]) / 1e6, values[i] - values[i - 1]));
            }
        }

        double duration = (timesMicroseconds[^1] - timesMicroseconds[0]) / 1e6;
        double rate = duration <= 0 ? 0 : (values[^1] - values[0]) / duration * 60;
        return new Phase(values[0], values[^1], duration, rate, steps);
    }
}
