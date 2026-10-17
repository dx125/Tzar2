using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Yaml;

namespace Tzar2.Tools.Cli.Fidelity.Analysis;

/// <summary>
/// <c>terrain-tiles</c> (UNK-10, docs/formats/bmp.md §3): identifies the tile drawn in every screen cell of the
/// game snapshots, aligns the screen grid with the map's <c>tiles</c> words, and reports which tile each word
/// produced. If one word shows several tiles, the variant is not stored in the word (e.g. chosen by a position hash);
/// the (x, y, word, tile) samples then let us derive that rule offline. Also checks the transition-key hypothesis
/// (magenta = lower terrain) and whether atlas cells outside the declared <c>[terrainsize]</c> appear.
/// </summary>
internal static class TerrainTiles
{
    private const int MaxWordEntries = 400;
    private const int MaxSamples = 600;

    public sealed record Located(TrialData Trial, CaptureData Capture, TileMatchResult Match, int? WorldX, int? WorldY, double Consistency, MapInfo? Map);

    public static AnalysisOutcome Run(AnalysisContext ctx, IReadOnlyList<TrialData> trials)
    {
        var notes = new List<string>();
        if (!ctx.AvailableSets.Any())
        {
            return AnalysisOutcome.Skipped("no terrain images: pass --original <install> (needs IMAGES/TERRAIN)");
        }

        var trialFacts = new List<YamlWriter>();
        foreach (TrialData trial in trials)
        {
            List<Located> located = Locate(ctx, trial, notes, out string? set);
            if (located.Count == 0)
            {
                continue;
            }

            trialFacts.Add(Summarise(trial, set, located));
        }

        var facts = new YamlWriter();
        facts.Add("trials", trialFacts);
        return new AnalysisOutcome(trialFacts.Count > 0 ? "ok" : "inconclusive", facts, notes);
    }

    /// <summary>Matches every game snapshot of a trial and places it on the map when the map is available.</summary>
    public static List<Located> Locate(AnalysisContext ctx, TrialData trial, List<string> notes, out string? set)
    {
        MapInfo? map = trial.Var("map") is { } name ? ctx.Map(name) : null;
        set = trial.Var("terrain_set") ?? map?.TerrainSet;
        var result = new List<Located>();
        IEnumerable<CaptureData> captures = trial.Of("game").Any() ? trial.Of("game") : trial.Of("window");
        foreach (CaptureData capture in captures)
        {
            CaptureImage? image = ctx.TryImage(trial, capture);
            if (image is null)
            {
                continue;
            }

            ushort[] canonical = image.ToCanonical555();
            TileMatchResult? match = null;
            foreach (string s in set is not null ? [set] : ctx.AvailableSets.ToArray())
            {
                TileMatcher? matcher = ctx.Matcher(s);
                TileMatchResult? candidate = matcher?.Match(canonical, image.Width, image.Height, ctx.Layout.View);
                if (candidate is not null && (match is null || candidate.Cells.Count > match.Cells.Count))
                {
                    match = candidate;
                    set ??= candidate.Cells.Count > 0 ? s : null;
                }
            }

            if (match is null || match.Cells.Count == 0)
            {
                notes.Add($"{trial.Trial}/{capture.Name}: no terrain tiles recognised");
                continue;
            }

            (int? wx, int? wy, double consistency) = map is null ? (null, null, 0) : Align(match, map, capture.Minimap);
            result.Add(new Located(trial, capture, match, wx, wy, consistency, map));
        }

        if (map is null && trial.Var("map") is { } missing)
        {
            notes.Add($"{trial.Trial}: map '{missing}' not found in the install; grid not aligned with map words");
        }

        return result;
    }

    /// <summary>
    /// Finds the world cell of screen cell (0, 0): the offset at which the map words explain the observed base
    /// terrain best (each word should map to one terrain index). Ties (repetitive maps) are broken by how well the
    /// words explain the exact tiles, then by the distance to the camera position of the minimap click, which also
    /// narrows the search.
    /// </summary>
    public static (int? X, int? Y, double Consistency) Align(TileMatchResult match, MapInfo map, (double U, double V)? minimap)
    {
        var baseCells = match.Cells.Where(c => c.Kind != CellMatchKind.Transition).ToList();
        if (baseCells.Count < 8 || match.Columns > map.Side || match.Rows > map.Side)
        {
            return (null, null, 0);
        }

        int maxX = map.Side - match.Columns, maxY = map.Side - match.Rows;
        int x0 = 0, x1 = maxX, y0 = 0, y1 = maxY;
        (int X, int Y) expected = (maxX / 2, maxY / 2);
        if (minimap is { } mm)
        {
            const int radius = 24;
            expected = (Math.Clamp((int)Math.Round((mm.U * map.Side) - (match.Columns / 2.0)), 0, maxX),
                Math.Clamp((int)Math.Round((mm.V * map.Side) - (match.Rows / 2.0)), 0, maxY));
            (x0, x1) = (Math.Clamp(expected.X - radius, 0, maxX), Math.Clamp(expected.X + radius, 0, maxX));
            (y0, y1) = (Math.Clamp(expected.Y - radius, 0, maxY), Math.Clamp(expected.Y + radius, 0, maxY));
        }

        // Small integer ids for words, and a reusable count table.
        var wordIds = new Dictionary<ushort, int>();
        foreach (ushort w in map.Words)
        {
            wordIds.TryAdd(w, wordIds.Count);
        }

        int[] wordOf = map.Words.Select(w => wordIds[w]).ToArray();
        int[] terrain = baseCells.Select(c => Math.Clamp(c.Tile.BaseTerrain, 0, 9)).ToArray();
        int[] counts = new int[wordIds.Count * 10];
        int[] best = new int[wordIds.Count];
        var touched = new List<int>();
        int bestExplained = -1;
        var ties = new List<(int X, int Y)>();
        for (int oy = y0; oy <= y1; oy++)
        {
            for (int ox = x0; ox <= x1; ox++)
            {
                int explained = 0;
                for (int i = 0; i < baseCells.Count; i++)
                {
                    int word = wordOf[((oy + baseCells[i].Cy) * map.Side) + ox + baseCells[i].Cx];
                    int n = ++counts[(word * 10) + terrain[i]];
                    if (n == 1)
                    {
                        touched.Add((word * 10) + terrain[i]);
                    }

                    if (n > best[word])
                    {
                        best[word] = n;
                        explained++;
                    }
                }

                foreach (int t in touched)
                {
                    counts[t] = 0;
                    best[t / 10] = 0;
                }

                touched.Clear();
                if (explained > bestExplained)
                {
                    bestExplained = explained;
                    ties.Clear();
                }

                if (explained == bestExplained)
                {
                    ties.Add((ox, oy));
                }
            }
        }

        (int X, int Y) bestAt = ties
            .OrderByDescending(t => TileConsistency(baseCells, map, t.X, t.Y))
            .ThenBy(t => Math.Abs(t.X - expected.X) + Math.Abs(t.Y - expected.Y))
            .First();
        return (bestAt.X, bestAt.Y, bestExplained / (double)baseCells.Count);
    }

    /// <summary>Cells explained if every word stood for one tile (sum over words of their most frequent tile).</summary>
    private static int TileConsistency(List<CellMatch> cells, MapInfo map, int ox, int oy)
    {
        var counts = new Dictionary<(ushort Word, int Tile), int>();
        foreach (CellMatch c in cells)
        {
            var key = (map.Words[((oy + c.Cy) * map.Side) + ox + c.Cx], c.Tile.Index);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts.GroupBy(kv => kv.Key.Word).Sum(g => g.Max(kv => kv.Value));
    }

    private static YamlWriter Summarise(TrialData trial, string? set, List<Located> located)
    {
        var w = new YamlWriter();
        w.Add("trial", trial.Trial);
        w.Add("map", trial.Var("map"));
        w.Add("terrain_set", set);
        w.Add("snapshots", located.Count);
        IEnumerable<CellMatch> cells = located.SelectMany(l => l.Match.Cells);
        w.Map("cells", c =>
        {
            c.Add("screen_cells", located.Sum(l => l.Match.CellsTotal));
            c.Add("exact", cells.Count(x => x.Kind == CellMatchKind.Exact));
            c.Add("partial", cells.Count(x => x.Kind == CellMatchKind.Partial));
            c.Add("transition", cells.Count(x => x.Kind == CellMatchKind.Transition));
            c.Add("outside_declared_size", cells.Count(x => !x.Tile.InDeclaredSize), "atlas cells beyond [terrainsize] that the game drew");
        });
        w.Add("grid_offsets", new YamlWriter.Flow(located.Select(l => (object?)$"{l.Match.OffsetX},{l.Match.OffsetY}").ToList()), "screen x,y of the cell grid modulo 32");

        var transitions = cells.Where(x => x.Kind == CellMatchKind.Transition).ToList();
        w.Map("transition_keys", t =>
        {
            t.Add("note", "key pixels whose colour belongs only to the lower or only to the higher terrain of the pair (hypothesis: magenta = lower)");
            t.Add("magenta_lower", transitions.Sum(x => x.KeyEvidence.MagentaLower));
            t.Add("magenta_higher", transitions.Sum(x => x.KeyEvidence.MagentaHigher));
            t.Add("green_lower", transitions.Sum(x => x.KeyEvidence.GreenLower));
            t.Add("green_higher", transitions.Sum(x => x.KeyEvidence.GreenHigher));
        });

        var aligned = located.Where(l => l.WorldX is not null && l.Consistency >= 0.9).ToList();
        w.Add("aligned_snapshots", aligned.Count, "snapshots placed on the map with >= 90 % word/terrain consistency");
        w.Add("alignment_consistency", new YamlWriter.Flow(located.Select(l => (object?)Math.Round(l.Consistency, 4)).ToList()));
        if (aligned.Count == 0)
        {
            return w;
        }

        // word -> tile observations over all aligned snapshots (each world cell counted once).
        var seen = new HashSet<(int, int)>();
        var byWord = new SortedDictionary<ushort, Dictionary<string, int>>();
        var samples = new List<YamlWriter.Flow>();
        foreach (Located l in aligned)
        {
            foreach (CellMatch cell in l.Match.Cells)
            {
                int wx = l.WorldX!.Value + cell.Cx, wy = l.WorldY!.Value + cell.Cy;
                if (!seen.Add((wx, wy)))
                {
                    continue;
                }

                ushort word = l.Map!.Words[(wy * l.Map.Side) + wx];
                if (!byWord.TryGetValue(word, out Dictionary<string, int>? tiles))
                {
                    byWord[word] = tiles = new Dictionary<string, int>(StringComparer.Ordinal);
                }

                tiles[cell.Tile.Label] = tiles.GetValueOrDefault(cell.Tile.Label) + 1;
                if (samples.Count < MaxSamples)
                {
                    samples.Add(new YamlWriter.Flow([wx, wy, new YamlWriter.Hex(word), cell.Tile.ImageNumber, cell.Tile.Col, cell.Tile.Row]));
                }
            }
        }

        w.Add("distinct_words", byWord.Count);
        w.Add("words_with_one_tile", byWord.Count(kv => kv.Value.Count == 1));
        w.Add("words_with_several_tiles", byWord.Count(kv => kv.Value.Count > 1), "> 0 means the tile variant is not (only) in the word");
        var words = new List<YamlWriter>();
        foreach ((ushort word, Dictionary<string, int> tiles) in byWord.Take(MaxWordEntries))
        {
            var e = new YamlWriter();
            e.Add("word", new YamlWriter.Hex(word));
            e.Add("cells", tiles.Values.Sum());
            e.Add("tiles", new YamlWriter.Flow(tiles.OrderByDescending(kv => kv.Value).Select(kv => (object?)$"{kv.Key}x{kv.Value}").ToList()));
            words.Add(e);
        }

        w.Add("words", words);
        w.Add("samples_note", "samples are [world_x, world_y, word, image (e.g. 22), atlas_col, atlas_row]");
        w.Add("samples", samples);
        return w;
    }
}
