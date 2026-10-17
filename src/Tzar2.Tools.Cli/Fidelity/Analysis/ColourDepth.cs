using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Yaml;

namespace Tzar2.Tools.Cli.Fidelity.Analysis;

/// <summary>
/// <c>colour-depth</c> (UNK-39, TDD §13.2): (1) the pixel format of the game's 16-bit surface (from its own F11
/// snapshot), (2) how the display path expands surface levels to 8 bits (pairs of snapshot and window capture of the
/// same moment), and (3) how the game converts its RGB555 art into the surface (terrain cells matched exactly).
/// Each mapping is reported as a per-level table plus the best of the candidate rules replicate / shift / round.
/// </summary>
internal static class ColourDepth
{
    public static AnalysisOutcome Run(AnalysisContext ctx, IReadOnlyList<TrialData> trials)
    {
        var notes = new List<string>();
        var trialFacts = new List<YamlWriter>();
        foreach (TrialData trial in trials)
        {
            var display = new Tally();
            var art = new Tally();
            PixelFormat? surface = null;
            int pairs = 0;
            long pixels = 0;
            foreach (CaptureData game in trial.Of("game"))
            {
                CaptureImage? snapshot = ctx.TryImage(trial, game);
                if (snapshot is null || snapshot.Format.Bits != 16)
                {
                    continue;
                }

                surface ??= snapshot.Format;
                CountArt(ctx, trial, snapshot, art);
                CaptureData? window = trial.Of("window").FirstOrDefault(w => w.Name == game.Name);
                CaptureImage? shown = window is null ? null : ctx.TryImage(trial, window);
                if (shown is null)
                {
                    continue;
                }

                if (shown.Width != snapshot.Width || shown.Height != snapshot.Height)
                {
                    notes.Add($"{trial.Trial}/{game.Name}: window capture {shown.Width}×{shown.Height} differs from the snapshot {snapshot.Width}×{snapshot.Height}; not paired");
                    continue;
                }

                pairs++;
                for (int i = 0; i < snapshot.Native.Length; i++)
                {
                    (int r, int g, int b) = snapshot.Levels(snapshot.Native[i]);
                    uint v = shown.Native[i];
                    display.Add(r, g, b, (int)((v >> 16) & 0xFF), (int)((v >> 8) & 0xFF), (int)(v & 0xFF));
                }

                pixels += snapshot.Native.Length;
            }

            var w = new YamlWriter();
            w.Add("trial", trial.Trial);
            w.Add("wrapper_rgb555", trial.Wrapper.GetValueOrDefault("rgb555", "n/a"));
            if (surface is { } f)
            {
                w.Add("game_surface", f.Name);
                w.Add("game_surface_masks", new YamlWriter.Flow([new YamlWriter.Hex(f.RedMask), new YamlWriter.Hex(f.GreenMask), new YamlWriter.Hex(f.BlueMask)]));
            }
            else
            {
                w.Add("game_surface", "unknown");
                notes.Add($"{trial.Trial}: no readable 16-bit F11 snapshot");
            }

            w.Add("pairs", pairs);
            w.Add("pixels", pixels);
            if (surface is { } fmt && pairs > 0)
            {
                w.Map("display", d =>
                {
                    d.Add("note", "game surface level -> 8-bit value shown on screen (window capture)");
                    Channels(d, display, [fmt.RedBits, fmt.GreenBits, fmt.BlueBits], 8);
                });
            }

            if (surface is { } fmt2 && art.Total > 0)
            {
                w.Map("art_to_surface", d =>
                {
                    d.Add("note", "RGB555 art level (terrain texel) -> game surface level, from exactly matched terrain cells");
                    Channels(d, art, [5, 5, 5], 0, [fmt2.RedBits, fmt2.GreenBits, fmt2.BlueBits]);
                });
            }

            trialFacts.Add(w);
        }

        var facts = new YamlWriter();
        facts.Add("trials", trialFacts);
        bool any = trialFacts.Count > 0 && trials.Any(t => t.Of("game").Any());
        return new AnalysisOutcome(any ? "ok" : "inconclusive", facts, notes);
    }

    private static void CountArt(AnalysisContext ctx, TrialData trial, CaptureImage snapshot, Tally art)
    {
        string? set = trial.Var("terrain_set") ?? (trial.Var("map") is { } m ? ctx.Map(m)?.TerrainSet : null);
        IEnumerable<string> sets = set is not null ? [set] : ctx.AvailableSets;
        ushort[] canonical = snapshot.ToCanonical555();
        TileMatchResult? best = null;
        TerrainLibrary? bestLibrary = null;
        foreach (string s in sets)
        {
            TileMatcher? matcher = ctx.Matcher(s);
            if (matcher is null)
            {
                continue;
            }

            TileMatchResult result = matcher.Match(canonical, snapshot.Width, snapshot.Height, ctx.Layout.View);
            if (best is null || result.Count(CellMatchKind.Exact) > best.Count(CellMatchKind.Exact))
            {
                (best, bestLibrary) = (result, ctx.Library(s));
            }
        }

        if (best is null || bestLibrary is null)
        {
            return;
        }

        foreach (CellMatch cell in best.Cells.Where(c => c.Kind == CellMatchKind.Exact))
        {
            for (int y = 0; y < TerrainTile.Size; y++)
            {
                for (int x = 0; x < TerrainTile.Size; x++)
                {
                    ushort texel = cell.Tile.Pixels[(y * TerrainTile.Size) + x];
                    (int r, int g, int b) = snapshot.Levels(snapshot.NativeAt(cell.X + x, cell.Y + y));
                    art.Add((texel >> 10) & 31, (texel >> 5) & 31, texel & 31, r, g, b);
                }
            }
        }
    }

    private static void Channels(YamlWriter w, Tally tally, int[] fromBits, int toBitsFixed, int[]? toBits = null)
    {
        string[] names = ["red", "green", "blue"];
        for (int c = 0; c < 3; c++)
        {
            int from = fromBits[c];
            int to = toBits?[c] ?? toBitsFixed;
            int[] table = tally.Table(c, 1 << from);
            (string model, int matches) = BestModel(table, from, to);
            int c2 = c;
            w.Map(names[c], m =>
            {
                m.Add("from_bits", from);
                m.Add("to_bits", to);
                m.Add("model", model);
                m.Add("model_matches_levels", matches);
                m.Add("levels_seen", table.Count(v => v >= 0));
                m.Add("agreement", tally.Agreement(c2, table));
                m.Add("table", new YamlWriter.Flow(table.Select(v => (object?)v).ToList()), "index = input level, -1 = not seen");
            });
        }
    }

    /// <summary>The candidate rule matching the most observed levels.</summary>
    public static (string Model, int Matches) BestModel(int[] table, int from, int to)
    {
        var models = new (string Name, Func<int, int> F)[]
        {
            ("replicate", v => Replicate(v, from, to)),
            ("shift", v => to >= from ? v << (to - from) : v >> (from - to)),
            ("round", v => (int)Math.Round(v * ((1 << to) - 1) / (double)((1 << from) - 1), MidpointRounding.AwayFromZero)),
        };
        string best = "none";
        int bestMatches = -1;
        foreach ((string name, Func<int, int> f) in models)
        {
            int matches = 0;
            for (int level = 0; level < table.Length; level++)
            {
                if (table[level] >= 0 && table[level] == f(level))
                {
                    matches++;
                }
            }

            if (matches > bestMatches)
            {
                (best, bestMatches) = (name, matches);
            }
        }

        int seen = table.Count(v => v >= 0);
        return (bestMatches == seen ? best : $"{best} (partial)", bestMatches);
    }

    /// <summary>Bit replication from <paramref name="from"/> to <paramref name="to"/> bits.</summary>
    public static int Replicate(int level, int from, int to)
    {
        if (to <= from)
        {
            return level >> (from - to);
        }

        int value = 0;
        for (int shift = to - from; shift > -from; shift -= from)
        {
            value |= shift >= 0 ? level << shift : level >> -shift;
        }

        return value & ((1 << to) - 1);
    }

    /// <summary>Histograms of output value per input level, per channel.</summary>
    private sealed class Tally
    {
        private readonly Dictionary<(int Channel, int Level), Dictionary<int, long>> _counts = [];

        public long Total { get; private set; }

        public void Add(int r, int g, int b, int outR, int outG, int outB)
        {
            Inc(0, r, outR);
            Inc(1, g, outG);
            Inc(2, b, outB);
            Total++;
        }

        private void Inc(int channel, int level, int value)
        {
            if (!_counts.TryGetValue((channel, level), out Dictionary<int, long>? h))
            {
                _counts[(channel, level)] = h = [];
            }

            h[value] = h.GetValueOrDefault(value) + 1;
        }

        /// <summary>The most frequent output per level (-1 = level not seen).</summary>
        public int[] Table(int channel, int levels)
        {
            int[] table = new int[levels];
            for (int level = 0; level < levels; level++)
            {
                table[level] = _counts.TryGetValue((channel, level), out Dictionary<int, long>? h) ? h.MaxBy(kv => kv.Value).Key : -1;
            }

            return table;
        }

        /// <summary>Fraction of samples whose output equals the table value of their level.</summary>
        public double Agreement(int channel, int[] table)
        {
            long hit = 0, all = 0;
            foreach (((int c, int level), Dictionary<int, long> h) in _counts)
            {
                if (c != channel)
                {
                    continue;
                }

                foreach ((int value, long n) in h)
                {
                    all += n;
                    hit += value == table[level] ? n : 0;
                }
            }

            return all == 0 ? 0 : hit / (double)all;
        }
    }
}
