using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Tzar2.Tests.Shared;
using Tzar2.Tools.Cli.Fidelity.Analysis;
using Tzar2.Tools.Cli.Fidelity.Suite;
using Xunit;

namespace Tzar2.Tools.Cli.Tests.Fidelity;

/// <summary>Synthetic truths for the analyses and suite checks of batches E-I (ADR 0113).</summary>
public class LaterBatchAnalysisTests
{
    private static readonly Lazy<EngineModel> LazyEngine = new(() => EngineModel.Load(OriginPaths.RepositoryRoot), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private static EngineModel Engine => LazyEngine.Value;

    /// <summary>A complete <c>sources</c> entry for a hand-read value (ADR 0113).</summary>
    private static string Src(string frame) => "{session: capture-test, trial: experience/main, frame: " + frame + ", read_by: test, read_utc: 2026-10-18}";

    [Fact]
    public void Counter_phases_report_steps_and_rates()
    {
        long[] times = [.. Enumerable.Range(0, 61).Select(i => (long)i * 1_000_000)];   // 60 s, one sample per second
        long[] hp = [.. Enumerable.Range(0, 61).Select(i => (long)(800 - (i / 10 * 50)))];   // -50 every 10 s
        CounterRate.Phase p = CounterRate.Measure(times, hp);
        Assert.Equal((800, 500), (p.Start, p.End));
        Assert.Equal(-300, p.RatePerMinute, 6);
        Assert.Equal(6, p.Steps.Count);
        Assert.All(p.Steps, s => Assert.Equal(-50, s.Size));
        Assert.Equal(10.0, p.Steps[0].Seconds, 6);
    }

    [Fact]
    public void Combat_spots_and_winners()
    {
        Assert.Equal([(450, 290), (570, 370)], CombatOutcomes.ParseSpots("450,290; 570,370;bad").ToArray());
        Assert.Equal('a', CombatOutcomes.Winner(90, 10));
        Assert.Equal('b', CombatOutcomes.Winner(0, 25));
        Assert.Equal('?', CombatOutcomes.Winner(40, 30));
        Assert.Equal('?', CombatOutcomes.Winner(5, 0));
    }

    [Fact]
    public void Engine_duels_follow_the_flat_rules()
    {
        // Mirror pairs fall together; a knight beats a footman; a pikeman's +600 % beats a knight.
        Assert.Equal(0.5, FidelitySuite.EngineDuel(Engine, (65, 6, 3, 5, 0), (65, 6, 3, 5, 0)));
        Assert.Equal(1, FidelitySuite.EngineDuel(Engine, (120, 12, 6, 4, 0), (65, 6, 3, 5, 0)));
        Assert.Equal(1, FidelitySuite.EngineDuel(Engine, (75, 8, 4, 2, 600), (120, 12, 6, 4, 0)));
        Assert.Equal(50, FidelitySuite.EngineDamage(Engine, 8, 6, 600, 0));
        Assert.Equal(11, FidelitySuite.EngineDamage(Engine, 12, 0, 0, 80));
    }

    [Fact]
    public void Every_later_batch_check_passes_on_engine_consistent_facts_and_reads_engine_values()
    {
        using var dir = new TempDir();
        void Facts(string scenario, string analysis, string body) => File.WriteAllText(Path.Combine(dir.Path, scenario + ".yaml"),
            $"schema: tzar2-fidelity-facts/1\nscenario: {scenario}\nanalyses:\n  {analysis}:\n    status: ok\n    facts:\n" +
            string.Join("\n", body.Trim('\n').Split('\n').Select(l => "      " + l)) + "\n");

        string I(double v) => v.ToString(CultureInfo.InvariantCulture);
        Facts("unit-speed", "unit-speed", """
            measurements:
              - {unit: peon, speed_stat: "4", px_per_s: 48, r2: 0.99}
              - {unit: knight, speed_stat: "6", px_per_s: 72, r2: 0.99}
              - {unit: pikeman, speed_stat: "2", px_per_s: 24, r2: 0.99}
              - {unit: group, speed_stat: none, px_per_s: 24.5, r2: 0.97}
            """);
        Facts("attack-timing", "attack-timing", """
            measurements:
              - {trial: pike-knight, rate_stat: "2", attack_stat: "8", defence_stat: "6", bonus_percent: "600", resistance: "0", hits: 3, period_ms_median: 3000, sample_interval_ms: 10, damage_values: [50x2]}
              - {trial: knight-lumber, rate_stat: "4", attack_stat: "12", defence_stat: "0", bonus_percent: "0", resistance: "80", hits: 9, period_ms_median: 1500, sample_interval_ms: 10, damage_values: [11x9]}
            """);
        Facts("combat-outcomes", "combat-outcomes", """
            measurements:
              - {trial: footman-vs-footman, a_hp: "65", a_attack: "6", a_defence: "3", a_rate: "5", a_bonus: "0", b_hp: "65", b_attack: "6", b_defence: "3", b_rate: "5", b_bonus: "0", a_wins: 4, b_wins: 4, a_win_share: 0.5}
              - {trial: knight-vs-footman, a_hp: "120", a_attack: "12", a_defence: "6", a_rate: "4", a_bonus: "0", b_hp: "65", b_attack: "6", b_defence: "3", b_rate: "5", b_bonus: "0", a_wins: 8, b_wins: 0, a_win_share: 1}
            """);
        Facts("gather-wood-far", "gather-rates", $"""
            measurements:
              - {"{"}trial: wood-far, resource: wood, load: {Engine.PeasantLoad}, trip_s_median: {I((2 * Math.Sqrt((325 * 325) + (235 * 235)) / 48) + (Engine.PeasantLoad!.Value * Engine.PeasantGatherMs("wood")!.Value / 1000.0))}, target_xy: "465,325", dropoff_xy: "790,560"{"}"}
            """);
        Facts("research-feudalism", "region-timing", """
            measurements:
              - {measure: research, subject: te_feudalism, expected_s: "30", last_event_s: 30.4, events_s: [0.5, 30.4]}
            """);
        Facts("capture-time-1", "region-timing", """
            measurements:
              - {measure: capture, subject: house, n: "1", resistance: "50", last_event_s: 5.3, events_s: [5.3]}
            """);
        Facts("counters", "counter-rate", $$"""
            measurements:
              - {subject: lumber, phases: [{sample: hp-attack, start: 800, end: 300, rate_per_min: -1500, steps: 40}, {sample: hp-burn, start: 300, end: 260, rate_per_min: -{{I(Engine.Rule("gameplay", "burnDamagePerMinute")!.Value)}}, steps: 40}, {sample: hp-repair, start: 260, end: 800, rate_per_min: 480, steps: 40, step_values: ["{{Engine.Ruleset.Gameplay.RepairBlowHp(800, 37_500)}}x30", "{{2 * Engine.Ruleset.Gameplay.RepairBlowHp(800, 37_500)}}x10"]}]}
              - {subject: lumber, phases: [{sample: hp-attack, start: 800, end: 600, rate_per_min: -1500, steps: 20}, {sample: hp-burn, start: 600, end: 600, rate_per_min: 0, steps: 0}]}
              - {subject: peon, phases: [{sample: hp-held, start: 20, end: 50, rate_per_min: {{I(Engine.Rule("^class.castle", "Holder", "healPerMinute")!.Value)}}, steps: 30}]}
              - {subject: field, phases: [{sample: field-stable-series, start: 100, end: 0, duration_s: 300, first_step_s: {{I(Engine.Rule("^class.field", "Decay", "afterMs")!.Value / 1000)}}, rate_per_min: -{{I(Engine.Rule("^class.field", "Decay", "healthPerMinute")!.Value * (300 - (Engine.Rule("^class.field", "Decay", "afterMs")!.Value / 1000)) / 300)}}, steps: 100}]}
              - {subject: te_feudalism, phases: [{sample: food-series, start: 950, end: 910, rate_per_min: -40, steps: 1, step_values: ["{{I(-50 * (100 + Engine.Rule("te_feudalism", "Technology", "effects", "0", "percent")!.Value) / 100)}}x1"]}]}
              - {subject: spell_lightning, phases: [{sample: tgt-series, start: 50, end: 10, rate_per_min: -400, steps: 1, step_values: ["-{{I(Engine.Rule("spell_lightning", "Spell", "amount")!.Value)}}x1"]}]}
              - {subject: mage, phases: [{sample: mana-series, start: 70, end: 100, rate_per_min: {{I(Engine.Rule("gameplay", "manaRegenPerMinute")!.Value)}}, steps: 30}]}
              - {subject: loan, phases: [{sample: gold-series, start: 1000, end: 2000, rate_per_min: 3000, steps: 1, step_values: ["{{I(Engine.Rule("world", "loanStartAmount")!.Value)}}x1"]}]}
              - {subject: trade, load: "100", distance_tiles: "8", phases: [{sample: gold-series, start: 1000, end: 1008, rate_per_min: 2, steps: 1, step_values: ["{{I(100 * Engine.Rule("world", "tradeProfitPerTileBasisPoints")!.Value * 8 / 10000)}}x1"]}]}
              - {subject: gamble, odds: "2", phases: [{sample: gold-series, start: 1000, end: 960, rate_per_min: -10, steps: 40, step_values: ["-100x22", "100x18"]}]}
            """);
        double perKill = (50.0 * Engine.Ruleset.Combat.ExperiencePerDamagePercent / 100) + Engine.Ruleset.Combat.ExperiencePerKill;
        File.WriteAllText(Path.Combine(dir.Path, "manual.yaml"), $"""
            schema: tzar2-fidelity-facts/1
            scenario: manual
            analyses:
              experience:
                status: ok
                facts:
                  xp_after_kills: [0, {I(perKill)}, {I(2 * perKill)}, {I(3 * perKill)}]
                  attack_per_level: {Engine.Ruleset.Combat.AttackPerLevel}
                  defence_per_level: {I(0.5 * Engine.Ruleset.Combat.DefenceHalvesPerLevel)}
                  sources:
                    xp_after_kills: {Src("window/panel-0.bmp … window/panel-3.bmp")}
                    attack_per_level: {Src("window/panel-2.bmp")}
                    defence_per_level: {Src("window/panel-2.bmp")}
              score:
                status: ok
                facts:
                  gathered: 0
                  killed_value: 0
                  built_value: 150
                  technologies: 0
                  total: 15
                  sources:
                    gathered: {Src("window/end-1.bmp")}
                    killed_value: {Src("window/end-1.bmp")}
                    built_value: {Src("window/end-1.bmp")}
                    technologies: {Src("window/end-1.bmp")}
                    total: {Src("window/end-1.bmp")}
            """);

        var results = FidelitySuite.Evaluate(new FactsIndex(dir.Path), Engine).ToDictionary(r => r.Check.Id, r => r.Result);
        string[] expected =
        [
            "m2.combat-outcomes", "m2.damage-bonuses", "m2.group-move-speed", "m2.gather-time", "m2.research-time", "m2.repair-rate",
            "m2.xp-gain", "m2.level-effects", "m2.healing", "m3.capture-time", "m3.trade-profit", "m3.loan-growth", "m3.gambling",
            "m3.spell-effects", "m3.mana-regeneration", "m3.technology-effects", "m3.field-decay", "m3.fire", "m3.score",
        ];
        Assert.All(expected, id => Assert.True(results[id].Status == CheckStatus.Pass, $"{id}: {results[id].Status} {results[id].Detail}"));
        Assert.Equal(CheckStatus.Pending, results["ai.script-semantics"].Status);
        Assert.Equal(CheckStatus.Pending, results["mapgen.statistics"].Status);
    }

    [Fact]
    public void Random_map_statistics_compare_the_originals_with_our_generator()
    {
        Assert.Equal(0, RandomMapStats.MeanNearest([(1, 1)]));
        Assert.Equal(5, RandomMapStats.MeanNearest([(0, 0), (3, 4)]));

        // Facts equal to our own generator's numbers pass; a map with castles twice as far apart fails.
        var ours = Enumerable.Range(1, 3).Select(seed => Tzar2.Content.MapGen.MapGenerator.Generate(new Tzar2.Content.MapGen.MapGenOptions
        {
            Type = Tzar2.Content.MapGen.RandomMapType.Continental,
            Size = Tzar2.Content.MapGen.RandomMapSize.Medium,
            Players = 2,
            Seed = (ulong)seed,
        })).ToList();
        double spacing = AttackTiming.Median([.. ours.Select(g => RandomMapStats.MeanNearest([.. g.Starts.Select(s => (s.TileX, s.TileY))]) / 128.0)]);
        double gold = AttackTiming.Median([.. ours.Select(g => g.Stats.Count(Tzar2.Content.MapGen.MapObjectRole.Gold) / 2.0)]);
        CheckStatus Evaluate(double s)
        {
            using var dir = new TempDir();
            File.WriteAllText(Path.Combine(dir.Path, "random-maps.yaml"), $$"""
                schema: tzar2-fidelity-facts/1
                scenario: random-maps
                analyses:
                  random-maps:
                    status: ok
                    facts:
                      measurements:
                        - {file: continental-1.wmp, type: continental, side: 128, castles: 2, castle_spacing_share: {{s.ToString(CultureInfo.InvariantCulture)}}, gold_per_kingdom: {{gold.ToString(CultureInfo.InvariantCulture)}}}
                """);
            return FidelitySuite.Evaluate(new FactsIndex(dir.Path), Engine).Single(r => r.Check.Id == "mapgen.statistics").Result.Status;
        }

        Assert.Equal(CheckStatus.Pass, Evaluate(spacing));
        Assert.Equal(CheckStatus.Fail, Evaluate(spacing * 2));
    }

    [Fact]
    public void Hand_read_values_without_full_provenance_are_ignored()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "manual.yaml"), $"""
            schema: tzar2-fidelity-facts/1
            scenario: manual
            analyses:
              setup-screen:
                status: ok
                facts:
                  population_limit_default: 200
                  sources:
                    population_limit_default: {"{"}session: capture-test, trial: smoke/plain, frame: window/setup.bmp, read_utc: 2026-10-18{"}"}
            """);
        var facts = new FactsIndex(dir.Path);
        Assert.Equal(["population_limit_default: no read_by"], ManualProvenance.Missing(facts.Ok("manual", "setup-screen")!));
        Assert.Equal(CheckStatus.Pending, FidelitySuite.Evaluate(facts, Engine).Single(r => r.Check.Id == "m2.population-limit").Result.Status);
    }

    [Fact]
    public void A_disagreeing_fact_fails_and_a_missing_one_stays_pending()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "counters.yaml"), """
            schema: tzar2-fidelity-facts/1
            scenario: loans
            analyses:
              counter-rate:
                status: ok
                facts:
                  measurements:
                    - {subject: loan, phases: [{sample: gold-series, start: 1000, end: 1500, rate_per_min: 1500, steps: 1, step_values: ["500x1"]}]}
            """);
        var results = FidelitySuite.Evaluate(new FactsIndex(dir.Path), Engine).ToDictionary(r => r.Check.Id, r => r.Result.Status);
        Assert.Equal(CheckStatus.Fail, results["m3.loan-growth"]);
        Assert.Equal(CheckStatus.Pending, results["m3.fire"]);
        Assert.Equal(CheckStatus.Pending, results["m2.gather-time"]);
    }
}
