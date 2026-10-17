using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Yaml;

namespace Tzar2.Tools.Cli.Fidelity.Analysis;

/// <summary>
/// <c>capture-quality</c>: are the captures usable? Formats, sizes, blank (single-colour) images and how many trials
/// worked. Runs on Windows right after the session (so the operator learns about black captures immediately) and
/// again during the analysis.
/// </summary>
internal static class CaptureQuality
{
    public sealed record ImageQuality(string Trial, string Kind, string Name, int Width, int Height, string Format, bool Blank, int DistinctColours, string? Error);

    public static IReadOnlyList<ImageQuality> Inspect(CaptureSession session, IEnumerable<TrialData> trials)
    {
        var result = new List<ImageQuality>();
        foreach (TrialData trial in trials)
        {
            foreach (CaptureData c in trial.Captures.Where(c => c.Kind is "game" or "window"))
            {
                try
                {
                    CaptureImage image = session.ReadImage(trial, c.File);
                    var distinct = new HashSet<uint>();
                    for (int i = 0; i < image.Native.Length && distinct.Count < 4096; i += 7)
                    {
                        distinct.Add(image.Native[i]);
                    }

                    result.Add(new ImageQuality($"{trial.Scenario}/{trial.Trial}", c.Kind, c.Name, image.Width, image.Height, image.Format.Name,
                        distinct.Count <= 1, distinct.Count, null));
                }
                catch (Exception ex) when (ex is System.IO.InvalidDataException or System.IO.FileNotFoundException)
                {
                    result.Add(new ImageQuality($"{trial.Scenario}/{trial.Trial}", c.Kind, c.Name, 0, 0, "?", true, 0, ex.Message));
                }
            }
        }

        return result;
    }

    public static AnalysisOutcome Run(AnalysisContext ctx, IReadOnlyList<TrialData> trials)
    {
        IReadOnlyList<ImageQuality> images = Inspect(ctx.Session, trials);
        var facts = new YamlWriter();
        var notes = new List<string>();
        facts.Add("trials", trials.Count);
        facts.Add("trials_ok", trials.Count(t => t.Status is "ok" or "partial"));
        foreach (string kind in new[] { "game", "window" })
        {
            var of = images.Where(i => i.Kind == kind).ToList();
            facts.Map(kind + "_captures", w =>
            {
                w.Add("count", of.Count);
                w.Add("blank", of.Count(i => i.Blank));
                w.Add("unreadable", of.Count(i => i.Error is not null));
                w.Add("formats", new YamlWriter.Flow(of.Where(i => i.Error is null).Select(i => (object?)i.Format).Distinct().ToList()));
                w.Add("sizes", new YamlWriter.Flow(of.Where(i => i.Error is null).Select(i => (object?)$"{i.Width}x{i.Height}").Distinct().ToList()));
            });
            if (of.Count > 0 && of.All(i => i.Blank))
            {
                notes.Add($"every {kind} capture is blank (single colour)");
            }
        }

        int bursts = trials.Sum(t => t.Of("burst").Count());
        facts.Add("bursts", bursts);
        facts.Add("burst_frames", trials.Sum(t => t.Of("burst").Sum(b => b.Frames.Count)));
        facts.Add("memory_scans", trials.Sum(t => t.Of("memscan").Count()));
        bool usable = images.Any(i => !i.Blank && i.Error is null) || bursts > 0 || trials.Any(t => t.Of("memsample").Any());
        return new AnalysisOutcome(usable ? "ok" : "inconclusive", facts, notes);
    }
}
