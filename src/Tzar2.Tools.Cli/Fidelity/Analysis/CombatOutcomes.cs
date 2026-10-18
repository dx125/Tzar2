using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tzar2.Formats.Pal;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Scenarios;
using Tzar2.Tools.Cli.Fidelity.Yaml;

namespace Tzar2.Tools.Cli.Fidelity.Analysis;

/// <summary>
/// <c>combat-outcomes</c> (UNK-08, TDD S4): many duels at once, each at its own spot; after the fights a snapshot tagged
/// <c>spots: "x,y;x,y;…"</c> (and <c>half</c>, the half-size of the square looked at) shows who is left. Side a is
/// kingdom 1 (ramp 1, blue), side b kingdom 2 (ramp 2, red); the side whose ramp colours dominate a spot (three times
/// the other's pixels, at least <see cref="MinimumPixels"/>) won it, otherwise the spot is undecided.
/// </summary>
internal static class CombatOutcomes
{
    public const int MinimumPixels = 20;

    public static AnalysisOutcome Run(AnalysisContext ctx, IReadOnlyList<TrialData> trials)
    {
        if (ctx.Palette is not { } palette)
        {
            return AnalysisOutcome.Skipped("no IMAGES/GAME.PAL: pass --original <install>");
        }

        var notes = new List<string>();
        var entries = new List<YamlWriter>();
        int found = 0;
        foreach (TrialData trial in trials)
        {
            sbyte[] lookup = PlayerColours.BuildLookup(palette, ctx.AvailableSets.Select(ctx.Library).Where(l => l is not null).SelectMany(l => l!.ColoursByTerrain.Values));
            int a = 0, b = 0, undecided = 0;
            foreach (CaptureData capture in trial.Captures.Where(c => c.Kind is "game" or "window" && c.Tags.ContainsKey("spots")))
            {
                if (ctx.TryImage(trial, capture) is not { } image)
                {
                    continue;
                }

                int half = int.TryParse(capture.Tags.GetValueOrDefault("half"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) ? h : 30;
                foreach ((int x, int y) in ParseSpots(capture.Tags["spots"]))
                {
                    long[] counts = PlayerColours.Count(image, new ScreenRect(x - half, y - half, 2 * half, 2 * half), lookup);
                    switch (Winner(counts[0], counts[1]))
                    {
                        case 'a': a++; break;
                        case 'b': b++; break;
                        default: undecided++; break;
                    }
                }

                // One kind of capture per trial is enough (the game snapshot when there is one).
                if (capture.Kind == "game")
                {
                    break;
                }
            }

            var e = new YamlWriter();
            e.Add("trial", trial.Trial);
            foreach (string v in new[] { "a", "b", "a_hp", "a_attack", "a_defence", "a_rate", "b_hp", "b_attack", "b_defence", "b_rate" })
            {
                e.Add(v, trial.Var(v));
            }

            e.Add("a_wins", a);
            e.Add("b_wins", b);
            e.Add("undecided", undecided);
            if (a + b > 0)
            {
                found++;
                e.Add("a_win_share", Math.Round(a / (double)(a + b), 4));
            }
            else
            {
                notes.Add($"{trial.Trial}: no decided spot");
            }

            entries.Add(e);
        }

        var facts = new YamlWriter();
        facts.Add("note", "winner per spot from the kingdom ramp colours left after the fights (a = kingdom 1, b = kingdom 2)");
        facts.Add("measurements", entries);
        return new AnalysisOutcome(found > 0 ? "ok" : "inconclusive", facts, notes);
    }

    public static char Winner(long a, long b) =>
        a >= MinimumPixels && a >= 3 * b ? 'a' : b >= MinimumPixels && b >= 3 * a ? 'b' : '?';

    public static IEnumerable<(int X, int Y)> ParseSpots(string text)
    {
        foreach (string spot in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] p = spot.Split(',', StringSplitOptions.TrimEntries);
            if (p.Length == 2 && int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) &&
                int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
            {
                yield return (x, y);
            }
        }
    }
}
