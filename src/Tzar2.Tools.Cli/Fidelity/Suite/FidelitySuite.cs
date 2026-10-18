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

/// <summary>One measurable fact of the fidelity suite and how the engine must match it.</summary>
internal sealed record FidelityCheck(string Id, string Area, string Unknown, string Title, Func<FactsIndex, EngineModel, CheckResult> Evaluate);

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
/// The fidelity suite (ADR 0112): the catalogue of measurable facts, each a comparison of a capture fact with the
/// engine. The pass rate is passing / measured; coverage is measured / catalogue.
/// </summary>
internal static class FidelitySuite
{
    public const double SpeedTolerance = 0.10;
    public const double TimeTolerance = 0.10;
    public const double BuildTolerance = 0.20;

    /// <summary>Areas in report order.</summary>
    public static readonly string[] Areas = ["rendering", "m2-gameplay", "m3-gameplay", "mapgen", "ai"];

    public static IReadOnlyList<FidelityCheck> Checks { get; } =
    [
        new("render.shadow-factor", "rendering", "UNK-03", "Shadow darkening = the client's mask strength", ShadowFactor),
        new("render.shadow-overlap", "rendering", "UNK-03", "Overlapping shadows darken once (the client's single mask pass)", ShadowOverlap),
        new("render.player-colour-ui", "rendering", "UNK-01a", "Kingdom n's colour squares use ramp n", PlayerColourUi),
        new("render.player-colour-sprites", "rendering", "UNK-01a", "Kingdom n's sprites use ramp n", PlayerColourSprites),
        new("render.surface-rgb555", "rendering", "UNK-39", "The game draws RGB555; the engine expands by replication", Surface),
        new("m2.speed-per-point", "m2-gameplay", "UNK-12", "Unit speed = SPEED x speedPixelsPerSecondPerPoint (all measured units)", SpeedPerPoint),
        new("m2.attack-cycle", "m2-gameplay", "UNK-07", "Time between hits = attackCycleMs / RATE (all measured pairs)", AttackCycle),
        new("m2.damage", "m2-gameplay", "UNK-08", "Damage per hit = max(minimum, ATTACK - DEFENCE) (all measured pairs)", DamagePerHit),
        new("m2.carry-capacity", "m2-gameplay", "UNK-17", "Load per trip = the peasant's Harvester capacity (every resource)", CarryCapacity),
        new("m2.population-limit", "m2-gameplay", "UNK-20", "Default population limit = gameplay.populationLimit", PopulationLimit),
        new("m2.train-time", "m2-gameplay", "UNK-20", "Production time = TIME seconds", TrainTime),
        new("m2.build-time", "m2-gameplay", "UNK-18", "Construction with one builder = TIME seconds", BuildTime),
        new("m3.day-length", "m3-gameplay", "UNK-32", "Day length = world.dayLengthMs", DayLength),
        new("m3.night-share", "m3-gameplay", "UNK-32", "Night share of the day = world night hours / 24", NightShare),
    ];

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

    /// <summary>Writes the per-area table; returns the overall pass rate of the measured checks (null when none).</summary>
    public static double? WriteReport(IReadOnlyList<(FidelityCheck Check, CheckResult Result)> results, TextWriter o)
    {
        o.WriteLine($"{"area",-12} {"checks",6} {"measured",8} {"pass",5} {"fail",5} {"pass %",7} {"coverage",8}");
        foreach (string area in Areas)
        {
            var r = results.Where(x => x.Check.Area == area).ToList();
            Row(area, r, o);
        }

        Row("total", results, o);
        o.WriteLine();
        foreach ((FidelityCheck check, CheckResult result) in results)
        {
            o.WriteLine($"{result.Status.ToString().ToUpperInvariant(),-7} {check.Id,-30} {check.Unknown,-8} {result.Detail}");
        }

        int measured = results.Count(x => x.Result.Status != CheckStatus.Pending);
        return measured == 0 ? null : results.Count(x => x.Result.Status == CheckStatus.Pass) / (double)measured;
    }

    private static void Row(string name, IReadOnlyCollection<(FidelityCheck Check, CheckResult Result)> r, TextWriter o)
    {
        int measured = r.Count(x => x.Result.Status != CheckStatus.Pending);
        int pass = r.Count(x => x.Result.Status == CheckStatus.Pass);
        int fail = r.Count(x => x.Result.Status == CheckStatus.Fail);
        string rate = measured == 0 ? "-" : (100.0 * pass / measured).ToString("0", CultureInfo.InvariantCulture) + " %";
        string coverage = r.Count == 0 ? "-" : (100.0 * measured / r.Count).ToString("0", CultureInfo.InvariantCulture) + " %";
        o.WriteLine($"{name,-12} {r.Count,6} {measured,8} {pass,5} {fail,5} {rate,7} {coverage,8}");
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

        double expected = FactsIndex.Number(m.Get("expected_s"))!.Value * EngineModel.MsPerTimeUnit / 1000.0;
        double measured = FactsIndex.Number(m.Get("last_event_s"))!.Value - 2;   // the builder's ~2 s walk (scenario build-time)
        return Verdict(Within(measured, expected, BuildTolerance), $"{FactsIndex.Text(m.Get("subject"))}: {measured:0.#} s measured (walk removed), engine {expected:0.#} s");
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
