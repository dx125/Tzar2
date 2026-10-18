using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Formats;
using Tzar2.Tools.Cli.Fidelity.Yaml;

namespace Tzar2.Tools.Cli.Fidelity.Suite;

/// <summary>Outcome of one fidelity check (ADR 0112).</summary>
internal enum CheckStatus
{
    /// <summary>A conclusive fact agrees with the engine within the check's tolerance.</summary>
    Pass,

    /// <summary>A conclusive fact disagrees with the engine.</summary>
    Fail,

    /// <summary>No conclusive fact yet (scenario not captured, or its analysis was inconclusive).</summary>
    Pending,
}

internal sealed record CheckResult(CheckStatus Status, string Detail);

/// <summary>
/// One item of the fidelity suite and how the engine must match it. <paramref name="Section"/> is the TDD section the item
/// belongs to: "22.3" (the gameplay fidelity suite, the M3 gate) or "22.4" (visual fidelity, S5, reported apart).
/// </summary>
internal sealed record FidelityCheck(string Id, string Section, string Area, string Unknown, string Title, Func<FactsIndex, EngineModel, CheckResult> Evaluate);

/// <summary>The committed fact files (<c>tests/fidelity/facts/*.yaml</c>): scenario → analysis → (status, facts).</summary>
internal sealed class FactsIndex
{
    private readonly Dictionary<string, YamlMapping> _analyses = new(StringComparer.Ordinal);

    public FactsIndex(string directory)
    {
        Directory = directory;
        if (!System.IO.Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in System.IO.Directory.EnumerateFiles(directory, "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (MiniYaml.Parse(File.ReadAllText(file)) is not YamlMapping doc || Text(doc.Get("scenario")) is not { } scenario ||
                doc.Get("analyses") is not YamlMapping analyses)
            {
                continue;
            }

            Files++;
            foreach ((string name, YamlNode node) in analyses.Entries)
            {
                if (node is YamlMapping m)
                {
                    _analyses[scenario + "/" + name] = m;
                }
            }
        }
    }

    public string Directory { get; }

    public int Files { get; }

    /// <summary>The facts of an analysis when its status is <c>ok</c>, else null.</summary>
    public YamlMapping? Ok(string scenario, string analysis) =>
        _analyses.TryGetValue(scenario + "/" + analysis, out YamlMapping? m) && Text(m.Get("status")) == "ok" ? m.Get("facts") as YamlMapping : null;

    /// <summary>Every <c>ok</c> analysis of the given kind, whatever its scenario.</summary>
    public IEnumerable<(string Scenario, YamlMapping Facts)> AllOk(string analysis) =>
        _analyses.Where(kv => kv.Key.EndsWith("/" + analysis, StringComparison.Ordinal) && Text(kv.Value.Get("status")) == "ok" && kv.Value.Get("facts") is YamlMapping)
            .Select(kv => (kv.Key[..kv.Key.IndexOf('/', StringComparison.Ordinal)], (YamlMapping)kv.Value.Get("facts")!));

    public static string? Text(YamlNode? node) => node is YamlScalar s && s.Value != "null" ? s.Value : null;

    public static double? Number(YamlNode? node) =>
        double.TryParse(Text(node), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    public static IEnumerable<YamlMapping> Items(YamlMapping facts, string key) =>
        facts.Get(key) is YamlSequence s ? s.Items.OfType<YamlMapping>() : [];
}

/// <summary>
/// The rebuild's side of every check: the engine's own rules file and compiled ruleset (classic, compiled from the
/// synthetic tables: only values that come from the YAML, never from the imported tables, are read from it), the
/// client's shadow pass and the colour expansion.
/// </summary>
internal sealed class EngineModel
{
    private EngineModel(Ruleset ruleset, YamlMapping gameplay, double? shadowStrength)
    {
        Ruleset = ruleset;
        Gameplay = gameplay;
        ShadowStrength = shadowStrength;
    }

    public Ruleset Ruleset { get; }

    private YamlMapping Gameplay { get; }

    /// <summary>The client's shadow mask strength (<c>shadow_composite.gdshader</c>), or null outside the repository.</summary>
    public double? ShadowStrength { get; }

    /// <summary>Legacy TIME column → milliseconds (LegacyImporter: TIME is seconds).</summary>
    public const int MsPerTimeUnit = 1000;

    public double PixelsPerSecondPerSpeedPoint => FactsIndex.Number(Gameplay.Get("speedPixelsPerSecondPerPoint")) ?? 12;

    public double AttackCycleMs => FactsIndex.Number(Gameplay.Get("attackCycleMs")) ?? 6000;

    /// <summary>The engine's flat damage rule for a unit target without bonuses (m2-placeholders "Damage").</summary>
    public int Damage(int attack, int defence) => Math.Max(Ruleset.Gameplay.MinimumDamage, attack - defence);

    /// <summary>The European peasant's load (<c>^class.peon.Harvester.capacity</c>).</summary>
    public int? PeasantLoad => Ruleset.EntityTypes.FirstOrDefault(t => t.Key == "euro.peon")?.Traits.Harvester?.Capacity;

    /// <summary>Kingdom slot (1-based) → GAME.PAL ramp (1-based): the client recolours owner n with ramp n.</summary>
    public static int RampOfSlot(int slot) => slot;

    /// <summary>The engine expands RGB555 by bit replication (Rgb555.Expand5).</summary>
    public static bool ExpandsByReplication => Rgb555.Expand5(16) == 132 && Rgb555.Expand5(31) == 255;

    public static EngineModel Load(string? repositoryRoot)
    {
        Ruleset ruleset = SyntheticLegacyContent.CompileRuleset();
        var defaults = new EngineDefaultsSource();
        var rules = (YamlMapping)MiniYaml.Parse(Encoding.UTF8.GetString(defaults.ReadAllBytes("rules/classic.yaml")));
        var gameplay = rules.Get("gameplay") as YamlMapping ?? new YamlMapping([]);
        double? strength = null;
        string? shader = repositoryRoot is null ? null : Path.Combine(repositoryRoot, "src", "Tzar2.Client", "shaders", "shadow_composite.gdshader");
        if (shader is not null && File.Exists(shader) &&
            Regex.Match(File.ReadAllText(shader), @"uniform float strength = ([0-9.]+);") is { Success: true } m)
        {
            strength = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        return new EngineModel(ruleset, gameplay, strength);
    }
}

/// <summary>
/// The fidelity suite (ADR 0112): the TDD §22.3 catalogue (every item of its scope, measurable today or not) and the
/// §22.4/S5 visual checks, each a comparison of a capture fact with the engine. The M3 gate is passing / all §22.3
/// checks (pending counts as not passing); passing / measured is reported as a secondary figure only.
/// </summary>
internal static class FidelitySuite
{
    public const double SpeedTolerance = 0.10;
    public const double TimeTolerance = 0.10;

    /// <summary>The section of the M3 gate (TDD §22.3) and the visual section reported apart (§22.4, S5).</summary>
    public const string GameplaySection = "22.3";
    public const string VisualSection = "22.4";

    /// <summary>Areas of §22.3 in report order.</summary>
    public static readonly string[] Areas = ["m2-gameplay", "m3-gameplay", "mapgen", "ai"];

    /// <summary>Areas of §22.4 in report order.</summary>
    public static readonly string[] VisualAreas = ["rendering"];

    public static IReadOnlyList<FidelityCheck> Checks { get; } =
    [
        // §22.3: measurable with the scenarios of ADR 0112.
        new("m2.speed-per-point", "22.3", "m2-gameplay", "UNK-12", "Unit speed = SPEED x speedPixelsPerSecondPerPoint (all measured units)", SpeedPerPoint),
        new("m2.attack-cycle", "22.3", "m2-gameplay", "UNK-07", "Time between hits = attackCycleMs / RATE (all measured pairs)", AttackCycle),
        new("m2.damage", "22.3", "m2-gameplay", "UNK-08", "Damage per hit = max(minimum, ATTACK - DEFENCE) (all measured pairs)", DamagePerHit),
        new("m2.carry-capacity", "22.3", "m2-gameplay", "UNK-17", "Load per trip = the peasant's Harvester capacity (every resource)", CarryCapacity),
        new("m2.population-limit", "22.3", "m2-gameplay", "UNK-20", "Default population limit = gameplay.populationLimit", PopulationLimit),
        new("m2.train-time", "22.3", "m2-gameplay", "UNK-20", "Production time = TIME seconds", TrainTime),
        new("m2.build-time", "22.3", "m2-gameplay", "UNK-18", "Construction with one builder = TIME seconds", BuildTime),
        new("m3.day-length", "22.3", "m3-gameplay", "UNK-32", "Day length = world.dayLengthMs", DayLength),
        new("m3.night-share", "22.3", "m3-gameplay", "UNK-32", "Night share of the day = world night hours / 24", NightShare),

        // §22.3: in scope, no scenario or analysis yet (pending until one exists; the reason says what would measure it).
        NotYet("m2.combat-outcomes", "m2-gameplay", "UNK-08", "Per-pair combat outcomes within 5 % over 100 trials (S4)",
            "needs the combat-pairs scenario run 100 times per pair (spawn and kingdom cheats from batch A) and an outcome analysis (survivor and remaining hit points)"),
        NotYet("m2.damage-bonuses", "m2-gameplay", "UNK-08", "Damage with BONUS.INI matches and building RESISTANCE",
            "needs attack-timing pairs with bonus matches (pikeman vs mounted) and against a building (hit-point counter of the building)"),
        NotYet("m2.group-move-speed", "m2-gameplay", "UNK-12", "A mixed group moves at its slowest member's speed",
            "needs a unit-speed trial with a knight and a pikeman selected together"),
        NotYet("m2.gather-time", "m2-gameplay", "UNK-17", "Gathering time per unit of each resource",
            "needs a peasant next to a drop-off (trip without walking) or a frame track of the gathering animation; batch B gives trip times with the walk included"),
        NotYet("m2.research-time", "m2-gameplay", "UNK-21", "Research and upgrade time = TIME seconds",
            "needs the research sub-menu coordinates of one building and a completion marker (the HUD card)"),
        NotYet("m2.repair-rate", "m2-gameplay", "UNK-18", "Repair time and cost with 1-8 repairers",
            "needs a damaged building (attack it first) and the repair order; hit-point counter in memory"),
        NotYet("m2.xp-gain", "m2-gameplay", "UNK-23", "Experience gained per hit and per kill",
            "no on-screen number; needs the experience value found in memory (scan after known kills)"),
        NotYet("m2.level-effects", "m2-gameplay", "UNK-23", "Attack and defence per experience level",
            "needs xp-gain first, then attack-timing against a levelled unit"),
        NotYet("m2.healing", "m2-gameplay", "UNK-24", "Healing rates in holders, by priests and outposts",
            "needs a wounded unit and a hit-point counter sampled while it heals"),
        NotYet("m3.capture-time", "m3-gameplay", "UNK-19", "Capture time of a building with n diggers, near and far from towns",
            "needs a neutral building in reach and the capture order (command card C); time to the owner change"),
        NotYet("m3.trade-profit", "m3-gameplay", "UNK-28", "Caravan profit vs. distance between markets",
            "needs two markets and a caravan route (built by hand or in a prepared map) and a stockpile counter"),
        NotYet("m3.loan-growth", "m3-gameplay", "UNK-28", "Loan limits and repayment",
            "needs the market's loan dialog coordinates and the stockpile counter"),
        NotYet("m3.gambling", "m3-gameplay", "UNK-28", "Gambling odds and stakes",
            "needs the market's gamble dialog coordinates; many trials"),
        NotYet("m3.spell-effects", "m3-gameplay", "UNK-26", "Spell magnitudes and areas",
            "needs a mage with enough mana (cheat) and per-spell targets; hit-point and position counters"),
        NotYet("m3.mana-regeneration", "m3-gameplay", "UNK-26", "Mana regained per minute",
            "needs the mana value in memory or the mana bar of a selected mage sampled over time"),
        NotYet("m3.technology-effects", "m3-gameplay", "UNK-21", "Technology magnitudes (samples)",
            "needs research-time first, then a stat before and after (gather load, attack, hit points)"),
        NotYet("m3.field-decay", "m3-gameplay", "UNK-17", "Field decay when untended",
            "needs a field (B, I) left alone for several minutes and its hit points sampled"),
        NotYet("m3.fire", "m3-gameplay", "UNK-42", "Burning threshold and fire damage",
            "needs a building damaged step by step (attack pairs) with its hit points sampled"),
        NotYet("m3.score", "m3-gameplay", "UNK-29", "Score formula",
            "needs the F4 score screen read (digits) after a scripted game"),
        NotYet("mapgen.statistics", "mapgen", "RE-10", "Random maps per type: land share, resources, castle spacing",
            "needs random maps generated by the original (random-map dialog or editor) read with the M4-A map reader (PR #33)"),
        NotYet("ai.script-semantics", "ai", "UNK-35", "Legacy AI script semantics and executor behaviour (RE-07)",
            "needs script experiments in the original (a test script per point) observed through hmkingdom or the AI's own messages"),

        // §22.4 / S5: visual fidelity, reported apart and not part of the M3 gate.
        new("render.shadow-factor", "22.4", "rendering", "UNK-03", "Shadow darkening = the client's mask strength", ShadowFactor),
        new("render.shadow-overlap", "22.4", "rendering", "UNK-03", "Overlapping shadows darken once (the client's single mask pass)", ShadowOverlap),
        new("render.player-colour-ui", "22.4", "rendering", "UNK-01a", "Kingdom n's colour squares use ramp n", PlayerColourUi),
        new("render.player-colour-sprites", "22.4", "rendering", "UNK-01a", "Kingdom n's sprites use ramp n", PlayerColourSprites),
        new("render.surface-rgb555", "22.4", "rendering", "UNK-39", "The game draws RGB555; the engine expands by replication", Surface),
    ];

    private static FidelityCheck NotYet(string id, string area, string unknown, string title, string measuredBy) =>
        new(id, GameplaySection, area, unknown, title, (_, _) => Pending("not measurable yet: " + measuredBy));

    /// <summary>The M3 figures of TDD §22.3.</summary>
    public sealed record Gate(int Total, int Measured, int Passing)
    {
        /// <summary>The M3 exit number: passing / all §22.3 checks (pending counts as not passing).</summary>
        public double PassRate => Total == 0 ? 0 : Passing / (double)Total;

        /// <summary>Secondary: passing / measured (null when nothing is measured).</summary>
        public double? MeasuredPassRate => Measured == 0 ? null : Passing / (double)Measured;
    }

    public static Gate GateOf(IReadOnlyList<(FidelityCheck Check, CheckResult Result)> results)
    {
        var r = results.Where(x => x.Check.Section == GameplaySection).ToList();
        return new Gate(r.Count, r.Count(x => x.Result.Status != CheckStatus.Pending), r.Count(x => x.Result.Status == CheckStatus.Pass));
    }

    public static IReadOnlyList<(FidelityCheck Check, CheckResult Result)> Evaluate(FactsIndex facts, EngineModel engine) =>
        [.. Checks.Select(c => (c, Safe(c, facts, engine)))];

    private static CheckResult Safe(FidelityCheck check, FactsIndex facts, EngineModel engine)
    {
        try
        {
            return check.Evaluate(facts, engine);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return new CheckResult(CheckStatus.Fail, $"malformed fact: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes both sections and the per-check lines; returns the M3 gate (§22.3: passing / all checks, pending counts
    /// as not passing).
    /// </summary>
    public static Gate WriteReport(IReadOnlyList<(FidelityCheck Check, CheckResult Result)> results, TextWriter o)
    {
        Gate gate = GateOf(results);
        o.WriteLine("TDD §22.3 fidelity suite (the M3 gate)");
        Table(results.Where(x => x.Check.Section == GameplaySection).ToList(), Areas, o);
        o.WriteLine();
        o.WriteLine($"M3 GATE (§22.3, >= 80 % required): {gate.Passing} of {gate.Total} checks pass = {Percent(gate.PassRate)} (pending counts as not passing)");
        o.WriteLine($"secondary, not the gate: {gate.Passing} of {gate.Measured} measured checks pass = {(gate.MeasuredPassRate is { } m ? Percent(m) : "-")}");
        o.WriteLine();
        o.WriteLine("TDD §22.4 / S5 visual fidelity (reported apart, not in the M3 gate)");
        Table(results.Where(x => x.Check.Section == VisualSection).ToList(), VisualAreas, o);
        o.WriteLine();
        foreach ((FidelityCheck check, CheckResult result) in results)
        {
            o.WriteLine($"{result.Status.ToString().ToUpperInvariant(),-7} §{check.Section} {check.Id,-30} {check.Unknown,-8} {result.Detail}");
        }

        return gate;
    }

    private static string Percent(double v) => (100 * v).ToString("0.#", CultureInfo.InvariantCulture) + " %";

    private static void Table(IReadOnlyList<(FidelityCheck Check, CheckResult Result)> results, string[] areas, TextWriter o)
    {
        o.WriteLine($"{"area",-12} {"checks",6} {"measured",8} {"pass",5} {"fail",5} {"pending",7} {"pass/all",8} {"pass/measured",13}");
        foreach (string area in areas)
        {
            Row(area, results.Where(x => x.Check.Area == area).ToList(), o);
        }

        Row("total", results, o);
    }

    private static void Row(string name, IReadOnlyCollection<(FidelityCheck Check, CheckResult Result)> r, TextWriter o)
    {
        int measured = r.Count(x => x.Result.Status != CheckStatus.Pending);
        int pass = r.Count(x => x.Result.Status == CheckStatus.Pass);
        int fail = r.Count(x => x.Result.Status == CheckStatus.Fail);
        string all = r.Count == 0 ? "-" : Percent(pass / (double)r.Count);
        string ofMeasured = measured == 0 ? "-" : Percent(pass / (double)measured);
        o.WriteLine($"{name,-12} {r.Count,6} {measured,8} {pass,5} {fail,5} {r.Count - measured,7} {all,8} {ofMeasured,13}");
    }

    // ---- checks ----------------------------------------------------------------------------------------------------

    private static CheckResult Pending(string why) => new(CheckStatus.Pending, why);

    private static CheckResult Verdict(bool pass, string detail) => new(pass ? CheckStatus.Pass : CheckStatus.Fail, detail);

    private static bool Within(double measured, double expected, double tolerance) => Math.Abs(measured - expected) <= tolerance * Math.Abs(expected);

    private static CheckResult ShadowFactor(FactsIndex f, EngineModel e)
    {
        if (f.Ok("shadow-blend", "shadow-blend") is not { } facts)
        {
            return Pending("no conclusive shadow-blend fact");
        }

        string? model = FactsIndex.Text(facts.Get("model"));
        Match m = Regex.Match(model ?? string.Empty, @"^floor\(c\*(\d+)/32\)$");
        if (!m.Success || e.ShadowStrength is not { } strength)
        {
            return new CheckResult(CheckStatus.Fail, $"model {model}, engine strength {e.ShadowStrength?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
        }

        double keep = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 32.0;
        return Verdict(Math.Abs((1 - strength) - keep) <= 1 / 32.0, $"original keeps {keep:0.###} of each channel, the client {1 - strength:0.###}");
    }

    private static CheckResult ShadowOverlap(FactsIndex f, EngineModel e)
    {
        if (f.Ok("shadow-overlap", "shadow-blend") is not { } facts)
        {
            return Pending("scenario shadow-overlap not captured yet");
        }

        double single = FactsIndex.Number(facts.Get("model_pairs")) ?? 0;
        double twice = FactsIndex.Number(facts.Get("double_application_pairs")) ?? 0;
        if (single < 500)
        {
            return Pending($"too few shadowed pixels ({single})");
        }

        double share = twice / single;
        return share is > 0.1 and < 0.3
            ? Pending($"ambiguous: {share:P0} of the pairs darkened twice")
            : Verdict(share <= 0.1, $"{twice} doubly against {single} singly darkened pairs ({share:P1}); the client darkens once");
    }

    private static CheckResult PlayerColourUi(FactsIndex f, EngineModel e)
    {
        if (f.Ok("player-colours", "player-colours") is not { } facts || FactsIndex.Number(facts.Get("swatches_decided")) is not >= 1)
        {
            return Pending("no decided swatches");
        }

        var swatches = FactsIndex.Items(facts, "swatches").Where(s => FactsIndex.Number(s.Get("ramp")) is not null).ToList();
        int matching = swatches.Count(s => (int)FactsIndex.Number(s.Get("ramp"))! == EngineModel.RampOfSlot((int)FactsIndex.Number(s.Get("kingdom"))!));
        return Verdict(matching == swatches.Count && FactsIndex.Text(facts.Get("swatch_identity")) == "true", $"{matching} of {swatches.Count} swatches on the engine's ramp");
    }

    private static CheckResult PlayerColourSprites(FactsIndex f, EngineModel e)
    {
        if (f.Ok("player-colours", "player-colours") is not { } facts || FactsIndex.Number(facts.Get("decided")) is not { } decided || decided < 7)
        {
            return Pending("sprite ramps of fewer than 7 kingdoms decided");
        }

        var slots = FactsIndex.Items(facts, "slots").Where(s => FactsIndex.Number(s.Get("ramp")) is not null && FactsIndex.Number(s.Get("confidence")) >= 3).ToList();
        int matching = slots.Count(s => (int)FactsIndex.Number(s.Get("ramp"))! == EngineModel.RampOfSlot((int)FactsIndex.Number(s.Get("slot"))!));
        return Verdict(matching == slots.Count, $"{matching} of {slots.Count} kingdoms' sprites on the engine's ramp");
    }

    private static CheckResult Surface(FactsIndex f, EngineModel e)
    {
        if (f.Ok("colour-depth", "colour-depth") is not { } facts)
        {
            return Pending("no colour-depth fact");
        }

        var trials = FactsIndex.Items(facts, "trials").ToList();
        bool rgb555 = trials.Count > 0 && trials.All(t => FactsIndex.Text(t.Get("game_surface")) == "rgb555");
        return Verdict(rgb555 && EngineModel.ExpandsByReplication, $"{trials.Count} trials, surface rgb555: {rgb555}; engine expands by replication");
    }

    private static CheckResult SpeedPerPoint(FactsIndex f, EngineModel e)
    {
        var measured = f.AllOk("unit-speed").SelectMany(x => FactsIndex.Items(x.Facts, "measurements"))
            .Where(m => FactsIndex.Number(m.Get("r2")) >= 0.95 && FactsIndex.Number(m.Get("speed_stat")) is > 0 && FactsIndex.Number(m.Get("px_per_s")) is not null)
            .ToList();
        if (measured.Select(m => FactsIndex.Text(m.Get("unit"))).Distinct().Count() < 3)
        {
            return Pending($"{measured.Count} clean speed measurements (needs 3 units)");
        }

        var off = measured.Where(m => !Within(FactsIndex.Number(m.Get("px_per_s"))!.Value, FactsIndex.Number(m.Get("speed_stat"))!.Value * e.PixelsPerSecondPerSpeedPoint, SpeedTolerance))
            .Select(m => $"{FactsIndex.Text(m.Get("unit"))} {FactsIndex.Number(m.Get("px_per_s")):0.#} px/s")
            .Distinct().ToList();
        return Verdict(off.Count == 0, off.Count == 0
            ? $"{measured.Count} measurements within {SpeedTolerance:P0} of SPEED x {e.PixelsPerSecondPerSpeedPoint}"
            : $"engine SPEED x {e.PixelsPerSecondPerSpeedPoint} px/s misses: {string.Join(", ", off.Take(6))}");
    }

    private static IEnumerable<YamlMapping> HitSeries(FactsIndex f) =>
        f.AllOk("attack-timing").SelectMany(x => FactsIndex.Items(x.Facts, "measurements")).Where(m => FactsIndex.Number(m.Get("hits")) >= 3);

    private static CheckResult AttackCycle(FactsIndex f, EngineModel e)
    {
        var series = HitSeries(f).Where(m => FactsIndex.Number(m.Get("rate_stat")) is > 0).ToList();
        if (series.Count == 0)
        {
            return Pending("no attack with three or more hits measured");
        }

        var off = series.Where(m =>
        {
            double expected = e.AttackCycleMs / FactsIndex.Number(m.Get("rate_stat"))!.Value;
            double period = FactsIndex.Number(m.Get("period_ms_median"))!.Value;
            double slack = Math.Max(TimeTolerance * expected, 2 * (FactsIndex.Number(m.Get("sample_interval_ms")) ?? 0));
            return Math.Abs(period - expected) > slack;
        }).Select(m => $"{FactsIndex.Text(m.Get("trial"))} {FactsIndex.Number(m.Get("period_ms_median")):0} ms").Distinct().ToList();
        return Verdict(off.Count == 0, off.Count == 0 ? $"{series.Count} series within tolerance of {e.AttackCycleMs} / RATE" : $"misses: {string.Join(", ", off)}");
    }

    private static CheckResult DamagePerHit(FactsIndex f, EngineModel e)
    {
        var series = HitSeries(f).Where(m => FactsIndex.Number(m.Get("attack_stat")) is not null && FactsIndex.Number(m.Get("defence_stat")) is not null).ToList();
        if (series.Count == 0)
        {
            return Pending("no attack with known ATTACK/DEFENCE and three or more hits");
        }

        var off = new List<string>();
        foreach (YamlMapping m in series)
        {
            int expected = e.Damage((int)FactsIndex.Number(m.Get("attack_stat"))!, (int)FactsIndex.Number(m.Get("defence_stat"))!);
            string? mode = (m.Get("damage_values") as YamlSequence)?.Items.Select(FactsIndex.Text).OfType<string>()
                .Select(v => v.Split('x')).OrderByDescending(p => int.Parse(p[1], CultureInfo.InvariantCulture)).Select(p => p[0]).FirstOrDefault();
            if (mode != expected.ToString(CultureInfo.InvariantCulture))
            {
                off.Add($"{FactsIndex.Text(m.Get("trial"))}: {mode} vs {expected}");
            }
        }

        return Verdict(off.Count == 0, off.Count == 0 ? $"{series.Count} pairs match the flat rule" : $"misses: {string.Join(", ", off)}");
    }

    private static CheckResult CarryCapacity(FactsIndex f, EngineModel e)
    {
        var loads = f.AllOk("gather-rates").SelectMany(x => FactsIndex.Items(x.Facts, "measurements"))
            .Where(m => FactsIndex.Number(m.Get("load")) is not null).ToList();
        if (loads.Count == 0)
        {
            return Pending("no gather scenario with deliveries yet");
        }

        if (e.PeasantLoad is not { } engine)
        {
            return new CheckResult(CheckStatus.Fail, "the engine's peasant has no Harvester");
        }

        var off = loads.Where(m => (int)FactsIndex.Number(m.Get("load"))! != engine)
            .Select(m => $"{FactsIndex.Text(m.Get("resource")) ?? FactsIndex.Text(m.Get("target"))} {FactsIndex.Number(m.Get("load"))}").ToList();
        return Verdict(off.Count == 0, off.Count == 0 ? $"{loads.Count} resources carry {engine}" : $"engine {engine}; measured {string.Join(", ", off)}");
    }

    private static CheckResult PopulationLimit(FactsIndex f, EngineModel e)
    {
        if (f.Ok("manual", "setup-screen") is not { } facts || FactsIndex.Number(facts.Get("population_limit_default")) is not { } measured)
        {
            return Pending("no setup-screen reading");
        }

        int engine = e.Ruleset.Gameplay.PopulationLimit;
        return Verdict((int)measured == engine, $"setup screen {measured}, engine {engine}");
    }

    private static IEnumerable<YamlMapping> Timings(FactsIndex f, string measure) =>
        f.AllOk("region-timing").SelectMany(x => FactsIndex.Items(x.Facts, "measurements")).Where(m => FactsIndex.Text(m.Get("measure")) == measure);

    private static CheckResult TrainTime(FactsIndex f, EngineModel e)
    {
        var m = Timings(f, "train").FirstOrDefault(x => FactsIndex.Number(x.Get("interval_s_median")) is not null && FactsIndex.Number(x.Get("expected_s")) is not null);
        if (m is null)
        {
            return Pending("no production timing with two or more units yet");
        }

        double expected = FactsIndex.Number(m.Get("expected_s"))!.Value * EngineModel.MsPerTimeUnit / 1000.0;
        double measured = FactsIndex.Number(m.Get("interval_s_median"))!.Value;
        return Verdict(Within(measured, expected, TimeTolerance), $"{FactsIndex.Text(m.Get("subject"))}: {measured:0.##} s measured, engine {expected:0.##} s");
    }

    private static CheckResult BuildTime(FactsIndex f, EngineModel e)
    {
        var m = Timings(f, "build").FirstOrDefault(x => FactsIndex.Number(x.Get("last_event_s")) is not null && FactsIndex.Number(x.Get("expected_s")) is not null);
        if (m is null)
        {
            return Pending("no construction timing yet");
        }

        // The panel's last change comes after the builder has walked to the site: that walk is removed with the distance
        // the scenario records and the peasant's speed measured by unit-speed (never an engine placeholder speed).
        if (Point(m.Get("builder_from")) is not { } from || Point(m.Get("site")) is not { } site || FactsIndex.Number(m.Get("site_half_px")) is not { } half)
        {
            return Pending("the construction fact lacks the builder's start and the site (scenario build-time of ADR 0112)");
        }

        if (MeasuredSpeed(f, "peon") is not { } speed)
        {
            return Pending("needs the peasant's measured speed (unit-speed, batch B) to remove the walk");
        }

        double walk = Math.Max(0, Math.Sqrt(((site.X - from.X) * (site.X - from.X)) + ((site.Y - from.Y) * (site.Y - from.Y))) - half) / speed;
        double expected = FactsIndex.Number(m.Get("expected_s"))!.Value * EngineModel.MsPerTimeUnit / 1000.0;
        double measured = FactsIndex.Number(m.Get("last_event_s"))!.Value - walk;
        double slack = Math.Max(TimeTolerance * expected, 1.0);   // 10 % or one second (two panel frames)
        return Verdict(Math.Abs(measured - expected) <= slack,
            $"{FactsIndex.Text(m.Get("subject"))}: {measured:0.#} s after a {walk:0.#} s walk at {speed:0.#} px/s, engine {expected:0.#} s");
    }

    private static (double X, double Y)? Point(YamlNode? node)
    {
        string[] parts = (FactsIndex.Text(node) ?? string.Empty).Split(',');
        return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
               double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
            ? (x, y)
            : null;
    }

    /// <summary>Median measured speed (px/s) of a unit class over clean unit-speed fits, or null.</summary>
    private static double? MeasuredSpeed(FactsIndex f, string unit)
    {
        double[] v = [.. f.AllOk("unit-speed").SelectMany(x => FactsIndex.Items(x.Facts, "measurements"))
            .Where(m => FactsIndex.Text(m.Get("unit")) == unit && FactsIndex.Number(m.Get("r2")) >= 0.95)
            .Select(m => FactsIndex.Number(m.Get("px_per_s"))).OfType<double>()];
        return v.Length == 0 ? null : Tzar2.Tools.Cli.Fidelity.Analysis.AttackTiming.Median(v);
    }

    private static YamlMapping? Day(FactsIndex f) =>
        f.AllOk("day-clock").SelectMany(x => FactsIndex.Items(x.Facts, "measurements")).FirstOrDefault(m => FactsIndex.Number(m.Get("day_length_s")) is not null);

    private static CheckResult DayLength(FactsIndex f, EngineModel e)
    {
        if (Day(f) is not { } m)
        {
            return Pending("no full day captured yet");
        }

        double measured = FactsIndex.Number(m.Get("day_length_s"))!.Value;
        double engine = e.Ruleset.World.DayLengthMs / 1000.0;
        return Verdict(Within(measured, engine, TimeTolerance), $"{measured:0} s measured, engine {engine:0} s");
    }

    private static CheckResult NightShare(FactsIndex f, EngineModel e)
    {
        if (Day(f) is not { } m || FactsIndex.Number(m.Get("night_share")) is not { } measured)
        {
            return Pending("no dusk and dawn in one captured day yet");
        }

        WorldSettings w = e.Ruleset.World;
        double engine = (((w.NightEndMinute - w.NightStartMinute) % 1440) + 1440) % 1440 / 1440.0;
        return Verdict(Math.Abs(measured - engine) <= 0.05, $"{measured:0.###} measured, engine {engine:0.###}");
    }
}
