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
using Tzar2.Tools.Cli.Fidelity.Analysis;
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
    private EngineModel(Ruleset ruleset, YamlMapping rules, YamlMapping gameplay, double? shadowStrength)
    {
        Rules = rules;
        Ruleset = ruleset;
        Gameplay = gameplay;
        ShadowStrength = shadowStrength;
    }

    public Ruleset Ruleset { get; }

    private YamlMapping Gameplay { get; }

    /// <summary>The engine's own <c>rules/classic.yaml</c>.</summary>
    private YamlMapping Rules { get; }

    /// <summary>A value of the engine's rules file by path (entry, trait, field...; a number index for lists).</summary>
    public double? Rule(params string[] path)
    {
        YamlNode? node = Rules;
        foreach (string key in path)
        {
            node = node switch
            {
                YamlMapping m => m.Get(key),
                YamlSequence q when int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i < q.Items.Count => q.Items[i],
                _ => null,
            };
        }

        return FactsIndex.Number(node);
    }

    /// <summary>The engine's group-move rule (<c>gameplay.groupMoveSpeed</c>).</summary>
    public string GroupMoveSpeed => FactsIndex.Text(Gameplay.Get("groupMoveSpeed")) ?? "slowest";

    /// <summary>Gathering time per unit of a resource for the European peasant, or null.</summary>
    public int? PeasantGatherMs(string resource) =>
        Ruleset.EntityTypes.FirstOrDefault(t => t.Key == "euro.peon")?.Traits.Harvester is { } h && Ruleset.ResourceId(resource) is >= 0 and var id
            ? h.GatherMs[id]
            : null;

    /// <summary>The client's shadow mask strength (<c>shadow_composite.gdshader</c>), or null outside the repository.</summary>
    public double? ShadowStrength { get; }

    /// <summary>Legacy TIME column → milliseconds, the importer's own constant (<see cref="Tzar2.Content.Rules.LegacyTime"/>).</summary>
    public const int MsPerTimeUnit = Tzar2.Content.Rules.LegacyTime.MsPerTimeUnit;

    /// <summary>
    /// The engine's walking speed (px/s) of a legacy class as the captures measure it, on the European unit where the class
    /// has one: the compiled type's speed, else the class template's <c>Mobile.pixelsPerSecond</c> in classic.yaml (classes
    /// the synthetic tables lack, e.g. longbowman), else null (ADR 0172).
    /// </summary>
    public double? PixelsPerSecondOf(string unit)
    {
        EntityTypeDefinition? type = Ruleset.EntityTypes.FirstOrDefault(t => t.Traits.LegacyClass == unit && t.Traits.Civilization == "euro")
            ?? Ruleset.EntityTypes.FirstOrDefault(t => t.Traits.LegacyClass == unit);
        return type is { SpeedUnitsPerSecond: > 0 } ? type.SpeedUnitsPerSecond / 64.0 : Rule("^class." + unit, "Mobile", "pixelsPerSecond");
    }

    public double AttackCycleMs => FactsIndex.Number(Gameplay.Get("attackCycleMs")) ?? 6000;

    /// <summary>The engine's flat damage rule for a unit target without bonuses (m2-placeholders "Damage").</summary>
    public int Damage(int attack, int defence) => Math.Max(Ruleset.Gameplay.MinimumDamage, attack - defence);

    /// <summary>The European peasant's load (<c>^class.peon.Harvester.capacity</c>).</summary>
    public int? PeasantLoad => Ruleset.EntityTypes.FirstOrDefault(t => t.Key == "euro.peon")?.Traits.Harvester?.Capacity;

    /// <summary>
    /// The peasant's load from a node of legacy class <paramref name="nodeClass"/> (a gather scenario's target, e.g.
    /// <c>cow</c>): the node's own load where it has one (ADR 0170), else <see cref="PeasantLoad"/>.
    /// </summary>
    public int? PeasantLoadFrom(string? nodeClass) =>
        Ruleset.EntityTypes.FirstOrDefault(t => nodeClass is not null && t.Traits.LegacyClass == nodeClass && t.Traits.ResourceNode is { CarryLoad: > 0 })
            ?.Traits.ResourceNode!.CarryLoad ?? PeasantLoad;

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

        return new EngineModel(ruleset, rules, gameplay, strength);
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
        new("m2.speed-per-point", "22.3", "m2-gameplay", "UNK-12", "Unit speed of every measured class matches the engine's (the original ignores SPEED, ADR 0172)", SpeedPerPoint),
        new("m2.attack-cycle", "22.3", "m2-gameplay", "UNK-07", "Time between hits = attackCycleMs / RATE (all measured pairs)", AttackCycle),
        new("m2.damage", "22.3", "m2-gameplay", "UNK-08", "Damage per hit = max(minimum, ATTACK - DEFENCE) (all measured pairs)", DamagePerHit),
        new("m2.carry-capacity", "22.3", "m2-gameplay", "UNK-17", "Load per trip = the peasant's Harvester capacity (every resource)", CarryCapacity),
        new("m2.population-limit", "22.3", "m2-gameplay", "UNK-20", "Default population limit = gameplay.populationLimit", PopulationLimit),
        new("m2.train-time", "22.3", "m2-gameplay", "UNK-20", "Production time = TIME seconds", TrainTime),
        new("m2.build-time", "22.3", "m2-gameplay", "UNK-18", "Construction with one builder = TIME seconds", BuildTime),
        new("m3.day-length", "22.3", "m3-gameplay", "UNK-32", "Day length = world.dayLengthMs", DayLength),
        new("m3.night-share", "22.3", "m3-gameplay", "UNK-32", "Night share of the day = world night hours / 24", NightShare),

        // §22.3: in scope, no scenario or analysis yet (pending until one exists; the reason says what would measure it).
        new("m2.combat-outcomes", "22.3", "m2-gameplay", "UNK-08", "Per-pair combat outcomes within 5 % (S4; 8 duels per game, deterministic engine model)", CombatOutcomes),
        new("m2.damage-bonuses", "22.3", "m2-gameplay", "UNK-08", "Damage with BONUS.INI matches and building RESISTANCE", DamageBonuses),
        new("m2.group-move-speed", "22.3", "m2-gameplay", "UNK-12", "A mixed group moves at its slowest member's speed", GroupMoveSpeed),
        new("m2.gather-time", "22.3", "m2-gameplay", "UNK-17", "Gathering time per unit (trip minus the measured walk)", GatherTime),
        new("m2.research-time", "22.3", "m2-gameplay", "UNK-21", "Research time = TIME (TECHNOLOGIES.INI)", ResearchTime),
        new("m2.repair-rate", "22.3", "m2-gameplay", "UNK-18", "Repair: hit points per blow of a repairer (BLD-022)", RepairRate),
        new("m2.xp-gain", "22.3", "m2-gameplay", "UNK-23", "Experience per kill (read from the selection panel)", XpGain),
        new("m2.level-effects", "22.3", "m2-gameplay", "UNK-23", "Attack and defence per experience level (selection panel)", LevelEffects),
        new("m2.healing", "22.3", "m2-gameplay", "UNK-24", "Healing inside the castle = Holder.healPerMinute", Healing),
        new("m3.capture-time", "22.3", "m3-gameplay", "UNK-19", "Capture time = RESISTANCE / (capturePointsPerSecond x peasants)", CaptureTime),
        new("m3.trade-profit", "22.3", "m3-gameplay", "UNK-28", "Trade profit per delivery = load x tiles x tradeProfitPercent / (map side x 100)", TradeProfit),
        new("m3.loan-growth", "22.3", "m3-gameplay", "UNK-28", "The first loan = world.loanStartAmount", LoanGrowth),
        new("m3.gambling", "22.3", "m3-gameplay", "UNK-28", "Gambling win share = (1 - edge) / odds (40 bets or more)", Gambling),
        new("m3.spell-effects", "22.3", "m3-gameplay", "UNK-26", "Lightning damage = spell_lightning amount", SpellEffects),
        new("m3.mana-regeneration", "22.3", "m3-gameplay", "UNK-26", "Mana regained per minute = gameplay.manaRegenPerMinute", ManaRegeneration),
        new("m3.technology-effects", "22.3", "m3-gameplay", "UNK-21", "Feudalism: peasant cost change = te_feudalism percent", TechnologyEffects),
        new("m3.field-decay", "22.3", "m3-gameplay", "UNK-17", "Field decay: start after Decay.afterMs, Decay.healthPerMinute", FieldDecay),
        new("m3.fire", "22.3", "m3-gameplay", "UNK-42", "Burning below burnThresholdPercent at burnDamagePerMinute", Fire),
        new("m3.score", "22.3", "m3-gameplay", "UNK-29", "Score = gathered/10 + killed/10 + built/10 + 100 per technology (end screen)", Score),
        new("mapgen.statistics", "22.3", "mapgen", "RE-10", "Random maps per type: castle spacing and gold per kingdom like the original's", MapgenStatistics),
        NotYet("ai.script-semantics", "ai", "UNK-35", "Legacy AI script semantics and executor behaviour (RE-07)",
            "out of reach of the capture batches (26 behaviours, each a test script run by the original's AI); stays pending by owner decision (ADR 0113)"),

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

    // The id keeps its first name; the check no longer assumes speed = SPEED x a factor, which the original refuted: every
    // foot class walks 40 px/s whatever its SPEED (2-4) and the mounted ones 72 (ADR 0172). Each measured class is compared
    // with the engine's speed for that class.
    private static CheckResult SpeedPerPoint(FactsIndex f, EngineModel e)
    {
        var classes = CleanSpeeds(f)
            .Where(m => FactsIndex.Text(m.Get("unit")) is { } u && u != "group" && FactsIndex.Number(m.Get("px_per_s")) is not null)
            .GroupBy(m => FactsIndex.Text(m.Get("unit"))!)
            .Select(g => (Unit: g.Key, Measured: AttackTiming.Median([.. g.Select(m => FactsIndex.Number(m.Get("px_per_s"))!.Value)])))
            .OrderBy(c => c.Unit, StringComparer.Ordinal)
            .ToList();
        if (classes.Count < 3)
        {
            return Pending($"{classes.Count} classes with a clean speed measurement (needs 3)");
        }

        var off = classes.Where(c => e.PixelsPerSecondOf(c.Unit) is not { } engine || !Within(c.Measured, engine, SpeedTolerance))
            .Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Unit} {c.Measured:0.#} px/s (engine {e.PixelsPerSecondOf(c.Unit)?.ToString("0.#", CultureInfo.InvariantCulture) ?? "none"})"))
            .ToList();
        return Verdict(off.Count == 0, off.Count == 0
            ? $"{classes.Count} classes within {SpeedTolerance:P0} of the engine's speed"
            : $"misses: {string.Join(", ", off.Take(6))}");
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
        // Plain pairs only: those with a BONUS.INI match or against a building are m2.damage-bonuses.
        var series = HitSeries(f).Where(m => FactsIndex.Number(m.Get("attack_stat")) is not null && FactsIndex.Number(m.Get("defence_stat")) is not null &&
                                             (FactsIndex.Number(m.Get("bonus_percent")) ?? 0) == 0 && (FactsIndex.Number(m.Get("resistance")) ?? 0) == 0).ToList();
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

        // Each load against the engine's load from that kind of node (cows carry their own, ADR 0170).
        var off = loads.Where(m => (int)FactsIndex.Number(m.Get("load"))! != (e.PeasantLoadFrom(FactsIndex.Text(m.Get("target"))) ?? engine))
            .Select(m => $"{FactsIndex.Text(m.Get("resource")) ?? FactsIndex.Text(m.Get("target"))} {FactsIndex.Number(m.Get("load"))} (engine {e.PeasantLoadFrom(FactsIndex.Text(m.Get("target"))) ?? engine})").ToList();
        string carried = string.Join(", ", loads.Select(m => $"{FactsIndex.Text(m.Get("resource")) ?? FactsIndex.Text(m.Get("target"))} {FactsIndex.Number(m.Get("load"))}"));
        return Verdict(off.Count == 0, off.Count == 0 ? $"{loads.Count} loads match the engine: {carried}" : $"measured {string.Join(", ", off)}");
    }

    private static CheckResult PopulationLimit(FactsIndex f, EngineModel e)
    {
        if (Manual(f, "setup-screen") is not { } facts || FactsIndex.Number(facts.Get("population_limit_default")) is not { } measured)
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
        // Preferred: the health bar's initial (one builder) rate, which needs no walk and is not disturbed by peasants
        // joining later (batch A: the rate doubled after 22 s).
        var bar = Timings(f, "build").FirstOrDefault(x => FactsIndex.Number(x.Get("one_builder_full_s")) is not null && FactsIndex.Number(x.Get("expected_s")) is not null);
        if (bar is not null)
        {
            double full = FactsIndex.Number(bar.Get("expected_s"))!.Value * EngineModel.MsPerTimeUnit / 1000.0;
            double rate = FactsIndex.Number(bar.Get("one_builder_full_s"))!.Value;
            return Verdict(Within(rate, full, TimeTolerance),
                $"{FactsIndex.Text(bar.Get("subject"))}: one builder fills the health bar in {rate:0.##} s, engine {full:0.##} s");
        }

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
        double[] v = [.. CleanSpeeds(f)
            .Where(m => FactsIndex.Text(m.Get("unit")) == unit)
            .Select(m => FactsIndex.Number(m.Get("px_per_s"))).OfType<double>()];
        return v.Length == 0 ? null : Tzar2.Tools.Cli.Fidelity.Analysis.AttackTiming.Median(v);
    }

    /// <summary>Unit-speed fits with r² ≥ 0.95 that the analysis did not reject (too short, or against the order).</summary>
    private static IEnumerable<YamlMapping> CleanSpeeds(FactsIndex f) =>
        Measurements(f, "unit-speed").Where(m => FactsIndex.Number(m.Get("r2")) >= 0.95 && m.Get("rejected") is null);

    private static IEnumerable<YamlMapping> Measurements(FactsIndex f, string analysis) =>
        f.AllOk(analysis).SelectMany(x => FactsIndex.Items(x.Facts, "measurements"));

    private static IEnumerable<YamlMapping> Counters(FactsIndex f, string subject) =>
        Measurements(f, "counter-rate").Where(m => FactsIndex.Text(m.Get("subject")) == subject && m.Get("phases") is YamlSequence);

    private static YamlMapping? PhaseOf(YamlMapping counter, string sample) =>
        FactsIndex.Items(counter, "phases").FirstOrDefault(p => FactsIndex.Text(p.Get("sample")) == sample);

    private static double Num(YamlMapping m, string key) => FactsIndex.Number(m.Get(key)) ?? double.NaN;

    /// <summary>The engine's damage per hit with a bonus (percent) and a building's RESISTANCE (0 for units).</summary>
    public static int EngineDamage(EngineModel e, int attack, int defence, int bonusPercent, int resistance)
    {
        long raw = (long)attack * (100 + bonusPercent) / 100 - defence;
        if (resistance > 0)
        {
            raw = raw * e.Ruleset.Gameplay.ResistanceScale / (e.Ruleset.Gameplay.ResistanceScale + resistance);
        }

        return (int)Math.Max(e.Ruleset.Gameplay.MinimumDamage, raw);
    }

    /// <summary>
    /// The engine's duel (deterministic: flat damage, a hit every attackCycleMs / RATE, the first one after the hit
    /// delay, both sides starting together): 1 when a wins, 0 when b wins, 0.5 when both fall in the same tick.
    /// </summary>
    public static double EngineDuel(EngineModel e, (int Hp, int Attack, int Defence, int Rate, int Bonus) a, (int Hp, int Attack, int Defence, int Rate, int Bonus) b)
    {
        double TimeToKill((int Hp, int Attack, int Defence, int Rate, int Bonus) x, (int Hp, int Attack, int Defence, int Rate, int Bonus) y)
        {
            int damage = EngineDamage(e, x.Attack, y.Defence, x.Bonus, 0);
            int hits = (y.Hp + damage - 1) / damage;
            double cooldown = e.AttackCycleMs / Math.Max(1, x.Rate);
            double delay = cooldown * (e.Rule("gameplay", "hitDelayPercent") ?? 50) / 100;
            return ((hits - 1) * cooldown) + delay;
        }

        double ta = TimeToKill(a, b), tb = TimeToKill(b, a);
        return Math.Abs(ta - tb) < 50 ? 0.5 : ta < tb ? 1 : 0;   // within one 50 ms tick: the same tick
    }

    private static CheckResult CombatOutcomes(FactsIndex f, EngineModel e)
    {
        var pairs = Measurements(f, "combat-outcomes").Where(m => Num(m, "a_wins") + Num(m, "b_wins") >= 8).ToList();
        if (pairs.Count == 0)
        {
            return Pending("no pair with 8 decided duels yet (combat-outcomes, batch H)");
        }

        var off = new List<string>();
        foreach (YamlMapping m in pairs)
        {
            (int, int, int, int, int) Side(string p) => ((int)Num(m, p + "_hp"), (int)Num(m, p + "_attack"), (int)Num(m, p + "_defence"), (int)Num(m, p + "_rate"), (int)(FactsIndex.Number(m.Get(p + "_bonus")) ?? 0));
            double expected = EngineDuel(e, Side("a"), Side("b"));
            double share = Num(m, "a_win_share");
            bool ok = expected == 0.5 ? Math.Abs(share - 0.5) <= 0.25 : Math.Abs(share - expected) <= 0.05;
            if (!ok)
            {
                off.Add($"{FactsIndex.Text(m.Get("trial"))}: a wins {share:P0}, engine {expected:P0}");
            }
        }

        return Verdict(off.Count == 0, off.Count == 0 ? $"{pairs.Count} pairs match the engine's duel" : $"misses: {string.Join("; ", off.Take(6))}");
    }

    private static CheckResult DamageBonuses(FactsIndex f, EngineModel e)
    {
        var series = HitSeries(f).Where(m => FactsIndex.Number(m.Get("bonus_percent")) is > 0 || FactsIndex.Number(m.Get("resistance")) is > 0).ToList();
        if (series.Count == 0)
        {
            return Pending("no attack with a bonus or against a building measured (damage-bonuses, damage-building: batch E)");
        }

        var off = new List<string>();
        foreach (YamlMapping m in series)
        {
            int expected = EngineDamage(e, (int)Num(m, "attack_stat"), (int)Num(m, "defence_stat"), (int)(FactsIndex.Number(m.Get("bonus_percent")) ?? 0), (int)(FactsIndex.Number(m.Get("resistance")) ?? 0));
            string? mode = (m.Get("damage_values") as YamlSequence)?.Items.Select(FactsIndex.Text).OfType<string>()
                .Select(v => v.Split('x')).OrderByDescending(p => int.Parse(p[1], CultureInfo.InvariantCulture)).Select(p => p[0]).FirstOrDefault();
            if (mode != expected.ToString(CultureInfo.InvariantCulture))
            {
                off.Add($"{FactsIndex.Text(m.Get("trial"))}: {mode} vs {expected}");
            }
        }

        return Verdict(off.Count == 0, off.Count == 0 ? $"{series.Count} attacks match bonus and resistance rules" : $"misses: {string.Join(", ", off)}");
    }

    private static CheckResult GroupMoveSpeed(FactsIndex f, EngineModel e)
    {
        var group = Measurements(f, "unit-speed").Where(m => FactsIndex.Text(m.Get("unit")) == "group" && Num(m, "r2") >= 0.9 && m.Get("rejected") is null).ToList();
        if (group.Count == 0)
        {
            return Pending("no clean group-speed fit yet (group-speed, batch E)");
        }

        if (MeasuredSpeed(f, "knight") is not { } fast || MeasuredSpeed(f, "pikeman") is not { } slow)
        {
            return Pending("needs the knight's and the pikeman's own measured speeds (unit-speed, batches B and C)");
        }

        double v = AttackTiming.Median([.. group.Select(m => Num(m, "px_per_s"))]);
        string observed = Within(v, slow, SpeedTolerance) ? "slowest" : Within(v, fast, SpeedTolerance) ? "own" : "other";
        return Verdict(observed == e.GroupMoveSpeed, $"group {v:0.#} px/s, pikeman {slow:0.#}, knight {fast:0.#}: original '{observed}', engine '{e.GroupMoveSpeed}'");
    }

    private static CheckResult GatherTime(FactsIndex f, EngineModel e)
    {
        var trips = Measurements(f, "gather-rates").Where(m => Point(m.Get("target_xy")) is not null && Point(m.Get("dropoff_xy")) is not null && FactsIndex.Number(m.Get("trip_s_median")) is not null).ToList();
        if (trips.Count == 0)
        {
            return Pending("no gather trip with recorded distances (gather-wood-far, batch E)");
        }

        if (MeasuredSpeed(f, "peon") is not { } speed)
        {
            return Pending("needs the peasant's measured speed (unit-speed, batch B) to remove the walk");
        }

        var off = new List<string>();
        foreach (YamlMapping m in trips)
        {
            (double X, double Y) a = Point(m.Get("target_xy"))!.Value, b = Point(m.Get("dropoff_xy"))!.Value;
            double walk = 2 * Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y))) / speed;
            double load = Num(m, "load");
            double perUnitMs = (Num(m, "trip_s_median") - walk) / load * 1000;
            string resource = FactsIndex.Text(m.Get("resource")) ?? "wood";
            if (e.PeasantGatherMs(resource) is not { } engine || !Within(perUnitMs, engine, 0.15))
            {
                off.Add($"{resource}: {perUnitMs:0} ms per unit measured, engine {e.PeasantGatherMs(resource)?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
            }
        }

        return Verdict(off.Count == 0, off.Count == 0 ? $"{trips.Count} trips within 15 % of the engine's gathering time" : string.Join("; ", off));
    }

    private static CheckResult ResearchTime(FactsIndex f, EngineModel e)
    {
        var m = Timings(f, "research").FirstOrDefault(x => FactsIndex.Number(x.Get("last_event_s")) is not null && FactsIndex.Number(x.Get("expected_s")) is not null);
        if (m is null)
        {
            return Pending("no research timing yet (research-feudalism, batch E)");
        }

        double expected = Num(m, "expected_s"), measured = Num(m, "last_event_s");
        return Verdict(Math.Abs(measured - expected) <= Math.Max(TimeTolerance * expected, 1.0), $"{FactsIndex.Text(m.Get("subject"))}: {measured:0.#} s measured, engine (TIME) {expected:0.#} s");
    }

    private static CheckResult RepairRate(FactsIndex f, EngineModel e)
    {
        // The commonest rising step of a repair phase is one blow; larger ones are blows of several repairers landing in
        // one sample (batch J: our idle peasants come to help), a smaller last one stops at the maximum. HEALTH and TIME
        // are the reference build's.
        var buildings = new (string Subject, string Name, int Health, int Time)[] { ("lumber", "lumber mill", 800, 30), ("barrack", "barracks", 1400, 60), ("castle", "castle", 8000, 120) };
        var found = new List<string>();
        var off = new List<string>();
        foreach ((string subject, string name, int health, int time) in buildings)
        {
            int? blow = Counters(f, subject).Select(c => PhaseOf(c, "hp-repair")).OfType<YamlMapping>().Select(CommonestRise).OfType<int>().Cast<int?>().FirstOrDefault();
            if (blow is not { } measured)
            {
                continue;
            }

            int engine = e.Ruleset.Gameplay.RepairBlowHp(health, time * 1250);
            found.Add($"{name} {measured} (engine {engine})");
            if (measured != engine)
            {
                off.Add($"{name}: {measured} HP per blow measured, engine {engine}");
            }
        }

        return found.Count == 0
            ? Pending("no repair phase with rising hit points yet (repair-mill, batch J)")
            : Verdict(off.Count == 0, off.Count == 0 ? "hit points per blow: " + string.Join(", ", found) : string.Join("; ", off));
    }

    // ["6x24", "12x18"] -> 6: the positive step of a phase's step_values that occurs most often.
    private static int? CommonestRise(YamlMapping phase)
    {
        if (phase.Get("step_values") is not YamlSequence steps)
        {
            return null;
        }

        (int Size, int Count)? best = null;
        foreach (string text in steps.Items.Select(i => FactsIndex.Text(i) ?? string.Empty))
        {
            string[] parts = text.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) && size > 0
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && (best is null || count > best.Value.Count))
            {
                best = (size, count);
            }
        }

        return best?.Size;
    }

    /// <summary>A hand-read block of <c>manual.yaml</c>, only when every value in it has full provenance.</summary>
    private static YamlMapping? Manual(FactsIndex f, string analysis) =>
        f.Ok("manual", analysis) is { } facts && ManualProvenance.Missing(facts).Count == 0 ? facts : null;

    private static CheckResult XpGain(FactsIndex f, EngineModel e)
    {
        if (Manual(f, "experience") is not { } m || m.Get("xp_after_kills") is not YamlSequence xp || xp.Items.Count < 3)
        {
            return Pending("no experience read from the panel yet (experience, batch F; manual.yaml)");
        }

        double[] values = [.. xp.Items.Select(FactsIndex.Number).OfType<double>()];
        double perKill = AttackTiming.Median([.. values.Zip(values.Skip(1), (a, b) => b - a)]);
        // Engine: a kill of a peasant (50 HP) gives its damage x ExperiencePerDamagePercent plus ExperiencePerKill.
        double engine = (50.0 * e.Ruleset.Combat.ExperiencePerDamagePercent / 100) + e.Ruleset.Combat.ExperiencePerKill;
        return Verdict(Within(perKill, engine, 0.05), $"{perKill:0.#} per peasant killed, engine {engine:0.#}");
    }

    private static CheckResult LevelEffects(FactsIndex f, EngineModel e)
    {
        if (Manual(f, "experience") is not { } m || FactsIndex.Number(m.Get("attack_per_level")) is not { } attack || FactsIndex.Number(m.Get("defence_per_level")) is not { } defence)
        {
            return Pending("no level change read from the panel yet (experience, batch F; manual.yaml)");
        }

        double engineAttack = e.Ruleset.Combat.AttackPerLevel, engineDefence = 0.5 * e.Ruleset.Combat.DefenceHalvesPerLevel;
        return Verdict(Math.Abs(attack - engineAttack) < 0.01 && Math.Abs(defence - engineDefence) < 0.01, $"+{attack} attack, +{defence} defence per level; engine +{engineAttack}, +{engineDefence}");
    }

    private static CheckResult Healing(FactsIndex f, EngineModel e)
    {
        var rates = Counters(f, "peon").Select(c => PhaseOf(c, "hp-held")).OfType<YamlMapping>().Where(p => Num(p, "rate_per_min") > 0).Select(p => Num(p, "rate_per_min")).ToList();
        if (rates.Count == 0)
        {
            return Pending("no rising hit points inside the castle yet (healing, batch F)");
        }

        double engine = e.Rule("^class.castle", "Holder", "healPerMinute") ?? double.NaN;
        double measured = AttackTiming.Median([.. rates]);
        return Verdict(Within(measured, engine, 0.15), $"{measured:0} HP/min in the castle, engine {engine:0}");
    }

    private static CheckResult CaptureTime(FactsIndex f, EngineModel e)
    {
        var times = Timings(f, "capture").Where(m => FactsIndex.Number(m.Get("last_event_s")) is not null && FactsIndex.Number(m.Get("n")) is not null).ToList();
        if (times.Count == 0)
        {
            return Pending("no owner change timed yet (capture-time-1/-3, batch E)");
        }

        double rate = e.Ruleset.Gameplay.CapturePointsPerSecond;
        var off = new List<string>();
        foreach (YamlMapping m in times)
        {
            double expected = Num(m, "resistance") / (rate * Num(m, "n"));
            double measured = FactsIndex.Number(m.Get("events_s") is YamlSequence s && s.Items.Count > 0 ? s.Items[0] : null) ?? Num(m, "last_event_s");
            if (!Within(measured, expected, 0.30))
            {
                off.Add($"{Num(m, "n")} peasant(s): {measured:0.#} s, engine {expected:0.#} s");
            }
        }

        return Verdict(off.Count == 0, off.Count == 0 ? $"{times.Count} captures within 30 % of RESISTANCE / ({rate} x peasants)" : string.Join("; ", off));
    }

    private static CheckResult TradeProfit(FactsIndex f, EngineModel e)
    {
        var steps = Counters(f, "trade").Select(c => (Counter: c, Phase: PhaseOf(c, "gold-series"))).Where(x => x.Phase is not null && Num(x.Phase!, "steps") >= 1).ToList();
        if (steps.Count == 0 || steps.Any(x => FactsIndex.Number(x.Counter.Get("load")) is null || FactsIndex.Number(x.Counter.Get("map_tiles")) is null))
        {
            return Pending("no trade delivery with a known load yet (trade, batch I: a discovery run first)");
        }

        var off = new List<string>();
        foreach ((YamlMapping c, YamlMapping? p) in steps)
        {
            double expected = Num(c, "load") * Num(c, "distance_tiles") * (e.Rule("world", "tradeProfitPercent") ?? 40) / (Num(c, "map_tiles") * 100);
            double measured = FirstStep(p!);
            if (!Within(measured, expected, 0.15))
            {
                off.Add($"{measured} per delivery, engine {expected:0}");
            }
        }

        return Verdict(off.Count == 0, off.Count == 0 ? "trade profit within 15 %" : string.Join("; ", off));
    }

    private static double FirstStep(YamlMapping phase) =>
        (phase.Get("step_values") as YamlSequence)?.Items.Select(FactsIndex.Text).OfType<string>().Select(v => double.Parse(v.Split('x')[0], CultureInfo.InvariantCulture)).FirstOrDefault() ?? double.NaN;

    private static CheckResult LoanGrowth(FactsIndex f, EngineModel e)
    {
        var loan = Counters(f, "loan").Select(c => PhaseOf(c, "gold-series")).OfType<YamlMapping>().Where(p => Num(p, "steps") >= 1).ToList();
        if (loan.Count == 0)
        {
            return Pending("no loan step yet (loans, batch G)");
        }

        double measured = loan.Max(FirstStep);
        double engine = e.Rule("world", "loanStartAmount") ?? double.NaN;
        return Verdict(Math.Abs(measured - engine) < 0.5, $"first loan {measured:0}, engine {engine:0}");
    }

    private static CheckResult Gambling(FactsIndex f, EngineModel e)
    {
        var bets = Counters(f, "gamble").Where(c => FactsIndex.Number(c.Get("odds")) is not null).Select(c => (Counter: c, Phase: PhaseOf(c, "gold-series"))).Where(x => x.Phase is not null).ToList();
        int total = bets.Sum(b => (int)Num(b.Phase!, "steps"));
        if (bets.Count == 0 || total < 40)
        {
            return Pending($"{total} bets with known odds (needs 40; gamble, batch I: a discovery run first)");
        }

        double odds = Num(bets[0].Counter, "odds");
        int wins = bets.Sum(b => (b.Phase!.Get("step_values") as YamlSequence)?.Items.Select(FactsIndex.Text).OfType<string>()
            .Where(v => double.Parse(v.Split('x')[0], CultureInfo.InvariantCulture) > 0).Sum(v => int.Parse(v.Split('x')[1], CultureInfo.InvariantCulture)) ?? 0);
        double expected = (1 - ((e.Rule("world", "gambleHouseEdgePercent") ?? 10) / 100)) / odds;
        double sigma = Math.Sqrt(expected * (1 - expected) / total);
        return Verdict(Math.Abs((wins / (double)total) - expected) <= 2 * sigma, $"{wins} of {total} won at odds {odds}, engine {expected:P1}");
    }

    private static CheckResult SpellEffects(FactsIndex f, EngineModel e)
    {
        var hits = Counters(f, "spell_lightning").Select(c => PhaseOf(c, "tgt-series")).OfType<YamlMapping>().Where(p => Num(p, "steps") >= 1).ToList();
        if (hits.Count == 0)
        {
            return Pending("no lightning hit measured yet (mana-spell, batch G)");
        }

        double largest = hits.Max(p => (p.Get("step_values") as YamlSequence)?.Items.Select(FactsIndex.Text).OfType<string>()
            .Select(v => -double.Parse(v.Split('x')[0], CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max() ?? 0);
        double engine = e.Rule("spell_lightning", "Spell", "amount") ?? double.NaN;
        return Verdict(Within(largest, engine, 0.10), $"largest hit {largest:0}, engine lightning {engine:0}");
    }

    private static CheckResult ManaRegeneration(FactsIndex f, EngineModel e)
    {
        var rates = Counters(f, "mage").Select(c => PhaseOf(c, "mana-series")).OfType<YamlMapping>().Where(p => Num(p, "rate_per_min") > 0).Select(p => Num(p, "rate_per_min")).ToList();
        if (rates.Count == 0)
        {
            return Pending("no refilling mana measured yet (mana-spell, batch G)");
        }

        double engine = e.Rule("gameplay", "manaRegenPerMinute") ?? double.NaN;
        double measured = AttackTiming.Median([.. rates]);
        return Verdict(Within(measured, engine, 0.15), $"{measured:0.#} mana/min, engine {engine:0}");
    }

    private static CheckResult TechnologyEffects(FactsIndex f, EngineModel e)
    {
        var counter = Counters(f, "te_feudalism").FirstOrDefault();
        YamlMapping? phase = counter is null ? null : PhaseOf(counter, "food-series");
        if (counter is null || phase is null || Num(phase, "steps") < 1)
        {
            return Pending("no peasant trained after Feudalism yet (research-feudalism, batch E)");
        }

        double before = 1000 - Num(phase, "start");   // MP2 starts with 1000 food; the counter starts after the first training
        double after = -FirstStep(phase);
        double measuredPercent = (after / before - 1) * 100;
        double engine = e.Rule("te_feudalism", "Technology", "effects", "0", "percent") ?? double.NaN;
        return Verdict(Math.Abs(measuredPercent - engine) <= 2, $"peasant cost {before:0} -> {after:0} ({measuredPercent:+0;-0} %), engine {engine:+0;-0} %");
    }

    private static CheckResult FieldDecay(FactsIndex f, EngineModel e)
    {
        var phase = Counters(f, "field").Select(c => PhaseOf(c, "field-stable-series")).OfType<YamlMapping>().FirstOrDefault(p => Num(p, "steps") >= 2);
        if (phase is null)
        {
            return Pending("no decaying field measured yet (field-decay, batch F)");
        }

        double start = Num(phase, "first_step_s"), rate = -Num(phase, "rate_per_min") * Num(phase, "duration_s") / Math.Max(1, Num(phase, "duration_s") - start);
        double engineStart = (e.Rule("^class.field", "Decay", "afterMs") ?? double.NaN) / 1000, engineRate = e.Rule("^class.field", "Decay", "healthPerMinute") ?? double.NaN;
        return Verdict(Math.Abs(start - engineStart) <= 15 && Within(rate, engineRate, 0.15), $"decay from {start:0} s at {rate:0.#} HP/min; engine {engineStart:0} s, {engineRate:0} HP/min");
    }

    private static CheckResult Fire(FactsIndex f, EngineModel e)
    {
        var trials = Counters(f, "lumber").Select(c => (Attack: PhaseOf(c, "hp-attack"), Burn: PhaseOf(c, "hp-burn"))).Where(x => x.Attack is not null && x.Burn is not null).ToList();
        if (trials.Count < 2)
        {
            return Pending($"{trials.Count} attacked-then-left mills (needs 2 or more; fire-repair, batch F)");
        }

        double threshold = (e.Rule("gameplay", "burnThresholdPercent") ?? 50) / 100, engineRate = e.Rule("gameplay", "burnDamagePerMinute") ?? 60;
        var off = new List<string>();
        var burning = new List<double>();
        foreach ((YamlMapping? attack, YamlMapping? burn) in trials)
        {
            double share = Num(attack!, "end") / 800, rate = -Num(burn!, "rate_per_min");
            bool burns = rate > 1;
            if (burns)
            {
                burning.Add(rate);
            }

            if (burns != share < threshold && Math.Abs(share - threshold) > 0.05)
            {
                off.Add($"left at {share:P0}: {(burns ? "burns" : "does not burn")}");
            }
        }

        bool rateOk = burning.Count == 0 || Within(AttackTiming.Median([.. burning]), engineRate, 0.15);
        return Verdict(off.Count == 0 && rateOk, $"{string.Join(", ", off.DefaultIfEmpty("threshold consistent"))}; burn {(burning.Count == 0 ? "-" : AttackTiming.Median([.. burning]).ToString("0", CultureInfo.InvariantCulture))} HP/min, engine below {threshold:P0} at {engineRate} HP/min");
    }

    private static CheckResult Score(FactsIndex f, EngineModel e)
    {
        if (Manual(f, "score") is not { } m || FactsIndex.Number(m.Get("total")) is not { } total)
        {
            return Pending("no end screen read yet (score, batch G; manual.yaml)");
        }

        double Get(string k) => FactsIndex.Number(m.Get(k)) ?? 0;
        double engine = Math.Floor(Get("gathered") / (e.Rule("world", "scoreGatherDivisor") ?? 10)) + Math.Floor(Get("killed_value") / (e.Rule("world", "scoreKillDivisor") ?? 10)) +
                        Math.Floor(Get("built_value") / (e.Rule("world", "scoreBuildDivisor") ?? 10)) + (Get("technologies") * (e.Rule("world", "scorePerTechnology") ?? 100));
        return Verdict(Math.Abs(total - engine) < 0.5, $"end screen {total:0}, engine formula on its parts {engine:0}");
    }

    private static CheckResult MapgenStatistics(FactsIndex f, EngineModel e)
    {
        var maps = Measurements(f, "random-maps").Where(m => Num(m, "castles") >= 2).ToList();
        if (maps.Count == 0)
        {
            return Pending("no random maps from the original yet (editor session, ADR 0113: t2 fidelity maps)");
        }

        var off = new List<string>();
        foreach (var byType in maps.GroupBy(m => FactsIndex.Text(m.Get("type"))))
        {
            if (!Tzar2.Content.MapGen.RandomMapNames.TryParse(byType.Key, out Tzar2.Content.MapGen.RandomMapType type))
            {
                continue;
            }

            YamlMapping first = byType.First();
            int side = (int)Num(first, "side");
            var size = Enum.GetValues<Tzar2.Content.MapGen.RandomMapSize>().FirstOrDefault(z => Tzar2.Content.MapGen.RandomMapNames.Side(z) == side);
            int players = Math.Clamp((int)Num(first, "castles"), 2, 8);
            var ours = Enumerable.Range(1, 3).Select(seed => Tzar2.Content.MapGen.MapGenerator.Generate(new Tzar2.Content.MapGen.MapGenOptions { Type = type, Size = size, Players = players, Seed = (ulong)seed })).ToList();
            double ourSpacing = AttackTiming.Median([.. ours.Select(g => RandomMapStats.MeanNearest([.. g.Starts.Select(st => (st.TileX, st.TileY))]) / side)]);
            double ourGold = AttackTiming.Median([.. ours.Select(g => g.Stats.Count(Tzar2.Content.MapGen.MapObjectRole.Gold) / (double)players)]);
            double theirSpacing = AttackTiming.Median([.. byType.Select(m => Num(m, "castle_spacing_share"))]);
            double theirGold = AttackTiming.Median([.. byType.Select(m => Num(m, "gold_per_kingdom"))]);
            if (!Within(ourSpacing, theirSpacing, 0.25) || !Within(ourGold, theirGold, 0.5))
            {
                off.Add($"{byType.Key}: spacing {ourSpacing:0.00} vs {theirSpacing:0.00}, gold/kingdom {ourGold:0.#} vs {theirGold:0.#}");
            }
        }

        return Verdict(off.Count == 0, off.Count == 0 ? $"{maps.Count} original maps: castle spacing within 25 %, gold within 50 % of ours" : string.Join("; ", off));
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
