using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tzar2.Formats;
using Tzar2.Formats.Pal;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Scenarios;
using Tzar2.Tools.Cli.Fidelity.Yaml;

namespace Tzar2.Tools.Cli.Fidelity.Analysis;

/// <summary>
/// <c>player-colours</c> (UNK-01a follow-up, docs/formats/pal.md): which <c>GAME.PAL</c> ramp the game uses for each
/// player slot. Snapshots tagged <c>slot: N</c> (the camera on that kingdom's castle) are scanned for pixels within
/// one level of a ramp colour; the ramp with the most such pixels in the inspected region is the slot's colour. The
/// grey ramp (8) also appears as a static colour in sprites, so it only wins when no other ramp is present.
/// <para>Swatches: the kingdom colour squares of the setup screen and the diplomacy dialog (F5) are flat UI colours
/// (<c>IMAGES/SCREENS/COMMON/COLOR BARS.BMP</c>), one per kingdom in kingdom order. A capture tagged
/// <c>swatches: "x,y,dy,first,count"</c> (or the window capture named <c>diplomacy</c>, whose geometry for the 1024 × 768
/// layout was measured on the 2026-10-18 capture: the other seven kingdoms of an eight-kingdom game, viewed as kingdom 1)
/// is sampled at (x, y + i·dy) for kingdoms first … first + count − 1, and each colour is matched to the nearest ramp.</para>
/// </summary>
internal static class PlayerColours
{
    public static readonly string[] RampNames = ["blue", "red", "green", "purple", "yellow", "cyan", "orange", "grey"];

    public static AnalysisOutcome Run(AnalysisContext ctx, IReadOnlyList<TrialData> trials)
    {
        GamePalette? palette = ctx.Palette;
        if (palette is null)
        {
            return AnalysisOutcome.Skipped("no IMAGES/GAME.PAL: pass --original <install>");
        }

        var notes = new List<string>();
        var slots = new SortedDictionary<int, long[]>();
        foreach (TrialData trial in trials)
        {
            string? set = trial.Var("terrain_set") ?? (trial.Var("map") is { } m ? ctx.Map(m)?.TerrainSet : null);
            IEnumerable<HashSet<ushort>> terrainColours = (set is not null ? [set] : ctx.AvailableSets)
                .Select(ctx.Library)
                .Where(l => l is not null)
                .SelectMany(l => l!.ColoursByTerrain.Values);
            sbyte[] lookup = BuildLookup(palette, terrainColours);
            IEnumerable<CaptureData> captures = trial.Of("game").Any() ? trial.Of("game") : trial.Of("window");
            foreach (CaptureData capture in captures)
            {
                if (!capture.Tags.TryGetValue("slot", out string? slotText) ||
                    !int.TryParse(slotText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot))
                {
                    continue;
                }

                CaptureImage? image = ctx.TryImage(trial, capture);
                if (image is null)
                {
                    continue;
                }

                ScreenRect region = Region(capture, ctx.Layout);
                long[] counts = Count(image, region, lookup);
                if (!slots.TryGetValue(slot, out long[]? total))
                {
                    slots[slot] = total = new long[8];
                }

                for (int r = 0; r < 8; r++)
                {
                    total[r] += counts[r];
                }
            }
        }

        List<YamlWriter> swatches = Swatches(ctx, trials, palette, out int swatchesDecided, out bool swatchIdentity);

        var facts = new YamlWriter();
        var entries = new List<YamlWriter>();
        bool identity = true;
        int decided = 0;
        foreach ((int slot, long[] counts) in slots)
        {
            int ramp = Decide(counts, out double confidence);
            var e = new YamlWriter();
            e.Add("slot", slot);
            e.Add("ramp", ramp >= 0 ? ramp + 1 : null, "1-based GAME.PAL ramp (entries 112 + 16 * (ramp - 1) ...)");
            e.Add("colour", ramp >= 0 ? RampNames[ramp] : null);
            e.Add("confidence", Math.Round(confidence, 3), "top / second ramp pixel count");
            e.Add("pixels", new YamlWriter.Flow(counts.Select(c => (object?)c).ToList()), "per ramp 1..8");
            entries.Add(e);
            if (ramp >= 0 && confidence >= 3)
            {
                decided++;
                identity &= ramp == slot - 1;
            }
        }

        facts.Add("slots", entries);
        facts.Add("decided", decided);
        facts.Add("identity_mapping", decided > 0 ? identity : null, "slot N uses ramp N for every decided slot");
        facts.Add("swatches", swatches);
        facts.Add("swatches_decided", swatchesDecided);
        facts.Add("swatch_identity", swatchesDecided > 0 ? swatchIdentity : null, "kingdom N's swatch is nearest to ramp N for every decided swatch");
        if (slots.Count == 0)
        {
            notes.Add("no snapshot carries a 'slot' tag");
        }

        return new AnalysisOutcome(decided > 0 || swatchesDecided > 0 ? "ok" : "inconclusive", facts, notes);
    }

    /// <summary>Default swatch geometry of the diplomacy dialog (1024 × 768): x, y of the first square, row pitch.</summary>
    public static readonly (int X, int Y, int Dy, int First, int Count) DiplomacySwatches = (309, 254, 32, 2, 7);

    private static List<YamlWriter> Swatches(AnalysisContext ctx, IReadOnlyList<TrialData> trials, GamePalette palette, out int decided, out bool identity)
    {
        var result = new List<YamlWriter>();
        decided = 0;
        identity = true;
        foreach (TrialData trial in trials)
        {
            foreach (CaptureData capture in trial.Captures.Where(c => c.Kind is "window" or "game"))
            {
                (int X, int Y, int Dy, int First, int Count)? geometry = capture.Tags.TryGetValue("swatches", out string? text)
                    ? ParseSwatches(text)
                    : capture.Kind == "window" && capture.Name == "diplomacy" && !capture.Tags.ContainsKey("slot") ? DiplomacySwatches : null;
                if (geometry is not { } g || ctx.TryImage(trial, capture) is not { } image)
                {
                    continue;
                }

                for (int i = 0; i < g.Count; i++)
                {
                    int x = g.X, y = g.Y + (i * g.Dy);
                    if (x < 2 || y < 2 || x + 2 >= image.Width || y + 2 >= image.Height)
                    {
                        continue;
                    }

                    (int r, int gr, int b) = SwatchColour(image, x, y);
                    int ramp = NearestRamp(palette, (r, gr, b), out double margin);
                    int kingdom = g.First + i;
                    var e = new YamlWriter();
                    e.Add("capture", $"{trial.Trial}/{capture.Name}");
                    e.Add("kingdom", kingdom);
                    e.Add("colour", $"{r},{gr},{b}");
                    e.Add("ramp", ramp >= 0 ? ramp + 1 : null);
                    e.Add("ramp_name", ramp >= 0 ? RampNames[ramp] : null);
                    e.Add("margin", Math.Round(margin, 3), "distance to the second-nearest ramp / to the nearest");
                    result.Add(e);
                    if (ramp >= 0 && margin >= 1.25)
                    {
                        decided++;
                        identity &= ramp == kingdom - 1;
                    }
                }
            }
        }

        return result;
    }

    private static (int X, int Y, int Dy, int First, int Count)? ParseSwatches(string text)
    {
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 5)
        {
            return null;
        }

        int[] v = new int[5];
        for (int i = 0; i < 5; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]))
            {
                return null;
            }
        }

        return (v[0], v[1], v[2], v[3], v[4]);
    }

    /// <summary>The most frequent 8-bit colour of the 5 × 5 block around (x, y) (robust against a cursor or an edge).</summary>
    private static (int R, int G, int B) SwatchColour(CaptureImage image, int x, int y)
    {
        var counts = new Dictionary<(int, int, int), int>();
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                (int r, int g, int b) = image.Levels(image.NativeAt(x + dx, y + dy));
                (int, int, int) c = image.Format.Bits == 16
                    ? (ColourDepth.Replicate(r, image.Format.RedBits, 8), ColourDepth.Replicate(g, image.Format.GreenBits, 8), ColourDepth.Replicate(b, image.Format.BlueBits, 8))
                    : (r, g, b);
                counts[c] = counts.GetValueOrDefault(c) + 1;
            }
        }

        return counts.MaxBy(kv => kv.Value).Key;
    }

    /// <summary>The ramp (0–7) with the entry nearest to <paramref name="colour"/>; margin = second distance / first.</summary>
    public static int NearestRamp(GamePalette palette, (int R, int G, int B) colour, out double margin)
    {
        double[] best = new double[GamePalette.PlayerCount];
        for (int ramp = 0; ramp < best.Length; ramp++)
        {
            best[ramp] = palette.PlayerRamp(ramp).Min(c => Math.Sqrt(Square(c.R - colour.R) + Square(c.G - colour.G) + Square(c.B - colour.B)));
        }

        int[] order = [.. Enumerable.Range(0, best.Length).OrderBy(r => best[r])];
        margin = best[order[1]] / Math.Max(1, best[order[0]]);
        return order[0];
    }

    private static double Square(int v) => (double)v * v;

    private static ScreenRect Region(CaptureData capture, GameLayout layout)
    {
        if (capture.Tags.TryGetValue("region", out string? text))
        {
            int[] v = text.Split(',', StringSplitOptions.TrimEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            if (v.Length == 4)
            {
                return new ScreenRect(v[0], v[1], v[2], v[3]);
            }
        }

        // Default: the middle of the world view, where Space centres the selected castle.
        ScreenRect view = layout.View;
        return new ScreenRect(view.X + (view.Width / 4), view.Y + (view.Height / 4), view.Width / 2, view.Height / 2);
    }

    public static long[] Count(CaptureImage image, ScreenRect region, sbyte[] lookup)
    {
        long[] counts = new long[8];
        for (int y = Math.Max(0, region.Y); y < Math.Min(image.Height, region.Bottom); y++)
        {
            for (int x = Math.Max(0, region.X); x < Math.Min(image.Width, region.Right); x++)
            {
                int ramp = lookup[CaptureImage.To555(image.NativeAt(x, y), image.Format)];
                if (ramp >= 0)
                {
                    counts[ramp]++;
                }
            }
        }

        return counts;
    }

    public static int Decide(long[] counts, out double confidence)
    {
        // Prefer the coloured ramps; grey wins only when nothing else is present.
        var order = Enumerable.Range(0, 7).OrderByDescending(r => counts[r]).ToList();
        int top = counts[order[0]] > 0 ? order[0] : (counts[7] > 0 ? 7 : -1);
        long second = top == 7 ? 0 : counts[order[1]];
        confidence = top < 0 ? 0 : counts[top] / (double)Math.Max(1, second);
        return top;
    }

    /// <summary>First ramp entry used for matching: the darker half of every ramp is too close to terrain and outlines.</summary>
    public const int FirstMatchedEntry = 6;

    /// <summary>
    /// RGB555 value → ramp (0–7) for the brighter ramp entries (RGB888 → RGB555 by truncation, which the game's
    /// 16-bit conversion preserves in the top bits), or -1. Colours shared by two ramps or used by the terrain are
    /// excluded, so grass cannot pass for the green player.
    /// </summary>
    public static sbyte[] BuildLookup(GamePalette palette, IEnumerable<HashSet<ushort>>? excluded = null)
    {
        sbyte[] lookup = Enumerable.Repeat((sbyte)-1, 32768).ToArray();
        bool[] blocked = new bool[32768];
        foreach (HashSet<ushort> set in excluded ?? [])
        {
            foreach (ushort v in set)
            {
                blocked[v & 0x7FFF] = true;
            }
        }

        for (int ramp = 0; ramp < GamePalette.PlayerCount; ramp++)
        {
            Rgb24[] colours = palette.PlayerRamp(ramp);
            for (int k = FirstMatchedEntry; k < colours.Length; k++)
            {
                Rgb24 c = colours[k];
                int v = ((c.R >> 3) << 10) | ((c.G >> 3) << 5) | (c.B >> 3);
                if (lookup[v] >= 0 && lookup[v] != ramp)
                {
                    blocked[v] = true;
                }

                lookup[v] = (sbyte)ramp;
            }
        }

        for (int v = 0; v < lookup.Length; v++)
        {
            if (blocked[v])
            {
                lookup[v] = -1;
            }
        }

        return lookup;
    }
}
