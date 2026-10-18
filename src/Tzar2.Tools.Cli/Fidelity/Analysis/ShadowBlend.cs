using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Yaml;

namespace Tzar2.Tools.Cli.Fidelity.Analysis;

/// <summary>
/// <c>shadow-blend</c> (UNK-03, TDD §13.3): measures how the game darkens terrain under shadows. Terrain cells are
/// identified (see <see cref="TileMatcher"/>), so the colour each covered pixel would have without the shadow is
/// known exactly. Pixels darker than their texel in every channel are shadow candidates (sprites also produce such
/// pairs, but not consistently). Candidate rules are fitted per channel in the surface's own levels: multiply by
/// k/32 (floor or round) and subtract d; the rule explaining the most pairs wins. Applying the rule twice tests
/// whether overlapping shadows darken twice. Pixels at most one level darker in every channel are counted apart: the
/// first capture (2026-10-18) showed them in large soft patches all over the terrain (not under shadow sprites), and
/// they outnumbered the shadowed pixels ten to one.
/// </summary>
internal static class ShadowBlend
{
    public sealed record Model(string Name, Func<int, int, int> Apply);

    /// <summary>The candidate rules; <c>Apply(level, bits)</c>.</summary>
    public static readonly IReadOnlyList<Model> Models = BuildModels();

    private static List<Model> BuildModels()
    {
        var models = new List<Model>();
        for (int k = 1; k < 32; k++)
        {
            int kk = k;
            models.Add(new Model($"floor(c*{k}/32)", (c, _) => c * kk / 32));
            models.Add(new Model($"round(c*{k}/32)", (c, _) => ((c * kk) + 16) / 32));
        }

        for (int d = 1; d < 16; d++)
        {
            int dd = d;
            models.Add(new Model($"c-{d}", (c, bits) => Math.Max(0, c - (bits == 6 ? 2 * dd : dd))));
        }

        return models;
    }

    public static AnalysisOutcome Run(AnalysisContext ctx, IReadOnlyList<TrialData> trials)
    {
        var notes = new List<string>();
        if (!ctx.AvailableSets.Any())
        {
            return AnalysisOutcome.Skipped("no terrain images: pass --original <install> (needs IMAGES/TERRAIN)");
        }

        // (texel levels, observed levels) -> count, in surface levels.
        var pairs = new Dictionary<(int, int, int, int, int, int), long>();
        int[] bits = [5, 5, 5];
        long comparedPixels = 0;
        long faint = 0;
        int snapshots = 0;
        foreach (TrialData trial in trials)
        {
            List<TerrainTiles.Located> located = TerrainTiles.Locate(ctx, trial, notes, out _);
            foreach (TerrainTiles.Located l in located)
            {
                CaptureImage? image = ctx.TryImage(trial, l.Capture);
                if (image is null)
                {
                    continue;
                }

                snapshots++;
                bits = image.Format.Bits == 16 ? [image.Format.RedBits, image.Format.GreenBits, image.Format.BlueBits] : [8, 8, 8];
                Func<ushort, (int, int, int)> toSurface = ArtToSurface(image, l.Match);
                foreach (CellMatch cell in l.Match.Cells.Where(c => c.Kind == CellMatchKind.Partial))
                {
                    for (int y = 0; y < TerrainTile.Size; y++)
                    {
                        for (int x = 0; x < TerrainTile.Size; x++)
                        {
                            ushort texel = cell.Tile.Pixels[(y * TerrainTile.Size) + x];
                            uint native = image.NativeAt(cell.X + x, cell.Y + y);
                            comparedPixels++;
                            if (CaptureImage.To555(native, image.Format) == texel)
                            {
                                continue;
                            }

                            (int tr, int tg, int tb) = toSurface(texel);
                            (int or, int og, int ob) = image.Levels(native);
                            if (or <= tr && og <= tg && ob <= tb && or + og + ob < tr + tg + tb)
                            {
                                // At most one level darker in every channel: not a shadow (a large-area effect that
                                // swamps the fit, see the class remarks); counted apart.
                                if (tr - or <= 1 && tg - og <= 1 && tb - ob <= 1)
                                {
                                    faint++;
                                    continue;
                                }

                                var key = (tr, tg, tb, or, og, ob);
                                pairs[key] = pairs.GetValueOrDefault(key) + 1;
                            }
                        }
                    }
                }
            }
        }

        var facts = new YamlWriter();
        facts.Add("snapshots", snapshots);
        facts.Add("terrain_pixels_compared", comparedPixels);
        facts.Add("faint_pixels", faint, "at most one level darker in every channel; excluded from the fit");
        long candidates = pairs.Values.Sum();
        facts.Add("darker_pixel_pairs", candidates);
        if (candidates == 0)
        {
            return new AnalysisOutcome("inconclusive", facts, [.. notes, "no darkened terrain pixels found"]);
        }

        var scored = Models.Select(m => (Model: m, Single: Score(pairs, m, bits, twice: false), Double: Score(pairs, m, bits, twice: true)))
            .OrderByDescending(s => s.Single)
            .ToList();
        var best = scored[0];
        facts.Add("model", best.Model.Name, "per channel, in surface levels");
        facts.Add("model_pairs", best.Single);
        facts.Add("model_fraction", best.Single / (double)candidates, "of all darker pairs (sprites over terrain account for the rest)");
        facts.Add("double_application_pairs", best.Double, "pairs explained only by applying the model twice (overlapping shadows)");
        facts.Add("runner_up", scored.Count > 1 ? scored[1].Model.Name : null);
        facts.Add("runner_up_pairs", scored.Count > 1 ? scored[1].Single : 0);
        facts.Add("surface_bits", new YamlWriter.Flow(bits.Select(b => (object?)b).ToList()));
        string[] names = ["red", "green", "blue"];
        int[][] tables = [.. Enumerable.Range(0, 3).Select(c => Table(pairs, best.Model, bits, c))];
        facts.Map("observed", o =>
        {
            o.Add("note", "most frequent shadowed level per texel level, from pairs whose other two channels fit the model");
            for (int c = 0; c < 3; c++)
            {
                o.Add(names[c], new YamlWriter.Flow(tables[c].Select(v => (object?)v).ToList()));
            }
        });

        // Neighbouring factors share most pairs (they agree on many levels), so the pair counts alone rarely separate
        // them; the per-level table does: the right rule reproduces it at (nearly) every level seen.
        double agreement = TableAgreement(tables, best.Model, bits);
        double runnerAgreement = scored.Count > 1 ? TableAgreement(tables, scored[1].Model, bits) : 0;
        facts.Add("table_agreement", Math.Round(agreement, 4), "share of the observed levels (all channels) the model reproduces");
        facts.Add("runner_up_table_agreement", Math.Round(runnerAgreement, 4));
        bool conclusive = best.Single >= 500 && best.Single * 10 >= candidates * 3 &&
                          (scored.Count < 2 || best.Single > scored[1].Single * 1.2 || (agreement >= 0.9 && agreement - runnerAgreement >= 0.2));
        if (!conclusive)
        {
            notes.Add("the best rule explains too few pairs or is not clearly better than the runner-up; more shadow-heavy captures needed");
        }

        return new AnalysisOutcome(conclusive ? "ok" : "inconclusive", facts, notes);
    }

    /// <summary>Share of the observed levels (all channels; -1 = not seen) that <paramref name="model"/> reproduces.</summary>
    public static double TableAgreement(int[][] tables, Model model, int[] bits)
    {
        int seen = 0, fit = 0;
        for (int c = 0; c < tables.Length; c++)
        {
            for (int level = 0; level < tables[c].Length; level++)
            {
                if (tables[c][level] >= 0)
                {
                    seen++;
                    fit += model.Apply(level, bits[c]) == tables[c][level] ? 1 : 0;
                }
            }
        }

        return seen == 0 ? 0 : fit / (double)seen;
    }

    private static long Score(Dictionary<(int, int, int, int, int, int), long> pairs, Model model, int[] bits, bool twice)
    {
        long n = 0;
        foreach (((int tr, int tg, int tb, int or, int og, int ob), long count) in pairs)
        {
            bool single = model.Apply(tr, bits[0]) == or && model.Apply(tg, bits[1]) == og && model.Apply(tb, bits[2]) == ob;
            if (!twice)
            {
                n += single ? count : 0;
                continue;
            }

            bool doubled = model.Apply(model.Apply(tr, bits[0]), bits[0]) == or && model.Apply(model.Apply(tg, bits[1]), bits[1]) == og &&
                           model.Apply(model.Apply(tb, bits[2]), bits[2]) == ob;
            n += doubled && !single ? count : 0;
        }

        return n;
    }

    private static int[] Table(Dictionary<(int, int, int, int, int, int), long> pairs, Model model, int[] bits, int channel)
    {
        var votes = new Dictionary<(int In, int Out), long>();
        foreach (((int tr, int tg, int tb, int or, int og, int ob), long count) in pairs)
        {
            int[] t = [tr, tg, tb];
            int[] o = [or, og, ob];
            bool othersFit = Enumerable.Range(0, 3).Where(c => c != channel).All(c => model.Apply(t[c], bits[c]) == o[c]);
            if (othersFit)
            {
                votes[(t[channel], o[channel])] = votes.GetValueOrDefault((t[channel], o[channel])) + count;
            }
        }

        int[] table = Enumerable.Repeat(-1, 1 << bits[channel]).ToArray();
        foreach (var group in votes.GroupBy(v => v.Key.In))
        {
            if (group.Key < table.Length)
            {
                table[group.Key] = group.MaxBy(v => v.Value).Key.Out;
            }
        }

        return table;
    }

    /// <summary>
    /// How RGB555 texels appear in this capture's surface levels, learnt from exactly matched cells (falls back to
    /// bit replication when there are none).
    /// </summary>
    private static Func<ushort, (int, int, int)> ArtToSurface(CaptureImage image, TileMatchResult match)
    {
        var map = new Dictionary<ushort, (int, int, int)>();
        foreach (CellMatch cell in match.Cells.Where(c => c.Kind == CellMatchKind.Exact))
        {
            for (int i = 0; i < cell.Tile.Pixels.Length; i += 3)
            {
                ushort texel = cell.Tile.Pixels[i];
                if (!map.ContainsKey(texel))
                {
                    map[texel] = image.Levels(image.NativeAt(cell.X + (i % TerrainTile.Size), cell.Y + (i / TerrainTile.Size)));
                }
            }
        }

        int rb = image.Format.Bits == 16 ? image.Format.RedBits : 8;
        int gb = image.Format.Bits == 16 ? image.Format.GreenBits : 8;
        int bb = image.Format.Bits == 16 ? image.Format.BlueBits : 8;
        return texel => map.TryGetValue(texel, out var v)
            ? v
            : (ColourDepth.Replicate((texel >> 10) & 31, 5, rb), ColourDepth.Replicate((texel >> 5) & 31, 5, gb), ColourDepth.Replicate(texel & 31, 5, bb));
    }
}
