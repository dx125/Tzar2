using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Tools.Cli.Fidelity.Capture;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Scenarios;
using Tzar2.Tools.Cli.Fidelity.Yaml;

namespace Tzar2.Tools.Cli.Fidelity.Analysis;

/// <summary>
/// <c>gather-rates</c> (UNK-17): one peasant gathers one resource and carries it to the castle. Which stockpile it fills
/// is read from the HUD (the resource box whose digits changed between the <c>before</c> and <c>after</c> window
/// captures); the stockpile itself is a memory counter found by value scans, sampled while the peasant works: each
/// upward step is one delivery, its size the load carried, the time between steps one trip (gathering plus walking).
/// </summary>
internal static class GatherRates
{
    /// <summary>HUD resource boxes of the 1024 × 768 layout, left to right (measured on the first session's captures).</summary>
    public static readonly IReadOnlyList<(string Resource, ScreenRect Box)> HudBoxes =
    [
        ("gold", new ScreenRect(340, 2, 70, 20)),
        ("wood", new ScreenRect(432, 2, 70, 20)),
        ("food", new ScreenRect(527, 2, 70, 20)),
        ("stone", new ScreenRect(622, 2, 70, 20)),
    ];

    public static AnalysisOutcome Run(AnalysisContext ctx, IReadOnlyList<TrialData> trials)
    {
        var notes = new List<string>();
        var entries = new List<YamlWriter>();
        int found = 0;
        foreach (TrialData trial in trials)
        {
            var e = new YamlWriter();
            e.Add("trial", trial.Trial);
            e.Add("target", trial.Var("target"));
            CaptureData? before = trial.Of("window").FirstOrDefault(c => c.Name == "before");
            CaptureData? after = trial.Of("window").FirstOrDefault(c => c.Name == "after");
            string? resource = before is not null && after is not null && ctx.TryImage(trial, before) is { } b && ctx.TryImage(trial, after) is { } a
                ? ChangedBox(b, a, HudBoxes)
                : null;
            e.Add("resource", resource, "the HUD box that changed");
            AttackTiming.HitSeries? deliveries = null;
            foreach (CaptureData sample in trial.Of("memsample"))
            {
                try
                {
                    AttackTiming.HitSeries? s = AttackTiming.BestSeries(MemoryProbe.ReadSamples(ctx.Session.ReadTrialFile(trial, sample.File)), rising: true);
                    if (s is not null && (deliveries is null || s.HitTimesMs.Count > deliveries.HitTimesMs.Count))
                    {
                        deliveries = s;
                    }
                }
                catch (Exception ex) when (ex is System.IO.InvalidDataException or System.IO.FileNotFoundException)
                {
                    notes.Add($"{trial.Trial}/{sample.Name}: {ex.Message}");
                }
            }

            if (deliveries is null || deliveries.HitTimesMs.Count < 2)
            {
                e.Add("result", "no stockpile counter with repeated deliveries");
                notes.Add($"{trial.Trial}: no deliveries found");
            }
            else
            {
                found++;
                double[] trips = deliveries.HitTimesMs.Zip(deliveries.HitTimesMs.Skip(1), (x, y) => (y - x) / 1000.0).ToArray();
                e.Add("deliveries", deliveries.HitTimesMs.Count);
                e.Add("load", deliveries.Damage.GroupBy(d => d).OrderByDescending(g => g.Count()).First().Key, "most frequent step of the stockpile");
                e.Add("load_values", new YamlWriter.Flow(deliveries.Damage.GroupBy(d => d).OrderBy(g => g.Key).Select(g => (object?)$"{g.Key}x{g.Count()}").ToList()));
                e.Add("trip_s_median", Math.Round(AttackTiming.Median(trips), 3), "gathering a load plus the walk to the drop-off and back");
                e.Add("stock_start", deliveries.Start);
                e.Add("stock_end", deliveries.End);
            }

            entries.Add(e);
        }

        var facts = new YamlWriter();
        facts.Add("note", "delivery times are memory sample timestamps (wall clock, default game speed)");
        facts.Add("measurements", entries);
        return new AnalysisOutcome(found > 0 ? "ok" : "inconclusive", facts, notes);
    }

    /// <summary>The box with the most changed pixels between two captures (at least 12), or null.</summary>
    public static string? ChangedBox(CaptureImage before, CaptureImage after, IReadOnlyList<(string Name, ScreenRect Box)> boxes)
    {
        string? best = null;
        int bestCount = 11;
        foreach ((string name, ScreenRect box) in boxes)
        {
            int count = 0;
            for (int y = box.Y; y < Math.Min(box.Bottom, Math.Min(before.Height, after.Height)); y++)
            {
                for (int x = box.X; x < Math.Min(box.Right, Math.Min(before.Width, after.Width)); x++)
                {
                    count += before.NativeAt(x, y) != after.NativeAt(x, y) ? 1 : 0;
                }
            }

            if (count > bestCount)
            {
                (best, bestCount) = (name, count);
            }
        }

        return best;
    }
}
