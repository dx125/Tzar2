using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Tzar2.AI.Scenarios;
using Tzar2.Sim;
using Tzar2.Sim.Scenarios;

namespace Tzar2.Tools.Cli.Commands;

/// <summary>
/// <c>t2 bench</c>: runs the synthetic sim scenario headless and reports tick-time percentiles, allocations and the
/// final state hash (TDD §21 B3/B4-like; the determinism CI compares the hash across OSes, §22.2).
/// </summary>
internal static class BenchCommand
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var options = new SyntheticScenarioOptions();
        string scenarioName = "synthetic";
        int? ticksOption = null;
        int? warmupOption = null;
        int group = 50;
        int? mapOption = null;
        int? entitiesOption = null;
        int? budget = null;
        int workers = Math.Clamp(Environment.ProcessorCount, 1, 8);
        string? hashStream = null;
        bool trace = false;
        ulong? expectHash = null;
        ulong? seedOption = null;
        int? playersOption = null;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-h" or "--help")
            {
                PrintUsage(stdout);
                return ExitCodes.Success;
            }

            if (arg == "--trace")
            {
                trace = true;
                continue;
            }

            if (i + 1 >= args.Length)
            {
                stderr.WriteLine($"t2 bench: unknown or incomplete option '{arg}'.");
                return ExitCodes.UsageError;
            }

            string value = args[++i];
            bool ok = true;
            switch (arg)
            {
                case "--scenario": scenarioName = value; ok = value is "synthetic" or "b3" or "b3-groups" or "b4" or "gameplay" or "ai"; break;
                case "--group": ok = TryInt(value, 1, 4096, v => group = v); break;
                case "--path-budget": ok = TryInt(value, 1, int.MaxValue, v => budget = v); break;
                case "--entities": ok = TryInt(value, 0, 1_000_000, v => entitiesOption = v); break;
                case "--map": ok = TryInt(value, 8, 1024, v => mapOption = v); break;
                case "--players": ok = TryInt(value, 1, 16, v => options = options with { Players = v }); playersOption = options.Players; break;
                case "--orders": ok = TryInt(value, 0, 4096, v => options = options with { OrdersPerPlayerPerTick = v }); break;
                case "--churn": ok = TryInt(value, 0, 64, v => options = options with { ChurnPerPlayerPerTick = v }); break;
                case "--ticks": ok = TryInt(value, 1, 10_000_000, v => ticksOption = v); break;
                case "--warmup": ok = TryInt(value, 0, 1_000_000, v => warmupOption = v); break;
                case "--workers": ok = TryInt(value, 1, 64, v => workers = v); break;
                case "--seed":
                    ok = ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong seed);
                    options = options with { Seed = seed };
                    seedOption = seed;
                    break;
                case "--hash-stream": hashStream = value; break;
                case "--expect-hash":
                    ok = ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong expected);
                    expectHash = expected;
                    break;
                default:
                    stderr.WriteLine($"t2 bench: unknown option '{arg}'.");
                    return ExitCodes.UsageError;
            }

            if (!ok)
            {
                stderr.WriteLine($"t2 bench: invalid value '{value}' for {arg}.");
                return ExitCodes.UsageError;
            }
        }

        int pathBudget = budget ?? SimulationConfig.DefaultPathBudgetPerTick;
        options = options with
        {
            MapSize = mapOption ?? options.MapSize,
            EntityCount = entitiesOption ?? options.EntityCount,
            PathBudgetPerTick = pathBudget,
        };
        IBenchScenario Create() => scenarioName switch
        {
            "b3" or "b3-groups" => new MazeMoveScenario(
                new MazeMoveScenarioOptions
                {
                    MapSize = mapOption ?? 512,
                    Units = entitiesOption ?? 500,
                    GroupSize = scenarioName == "b3" ? 1 : group,
                    PathBudgetPerTick = pathBudget,
                },
                workers),
            "b4" => new MassDeathScenario(new MassDeathScenarioOptions { MapSize = mapOption ?? 256, PathBudgetPerTick = pathBudget }, workers),
            "gameplay" => new GameplayScenario(
                GameplayRuleset.Value,
                new GameplayScenarioOptions
                {
                    MapSize = mapOption ?? 192,
                    Players = Math.Min(playersOption ?? 4, 8),
                    SoldiersPerPlayer = entitiesOption is { } n ? n / Math.Min(playersOption ?? 4, 8) : 300,
                    Seed = seedOption ?? new GameplayScenarioOptions().Seed,
                },
                workers),
            "ai" => new AiSkirmishScenario(
                GameplayRuleset.Value,
                DefaultAiScript.Value,
                new AiSkirmishOptions
                {
                    MapSize = mapOption ?? 256,
                    Players = Math.Min(playersOption ?? 8, 8),
                    Seed = seedOption ?? new AiSkirmishOptions().Seed,
                },
                workers),
            _ => new SyntheticScenario(options, workers),
        };

        if (scenarioName != "synthetic")
        {
            // B3/B4 measure from tick 0, so warm the JIT up on a throwaway copy first (tier-0 code is several times
            // slower; the synthetic scenario uses --warmup ticks instead).
            for (int round = 0; round < 3; round++)
            {
                using IBenchScenario warm = Create();
                for (int t = 0; t < 30; t++)
                {
                    warm.RunTick();
                }

                // Let tiered compilation finish promoting the hot methods in the background.
                System.Threading.Thread.Sleep(200);
            }
        }

        using IBenchScenario scenario = Create();
        Tzar2.Sim.Systems.PathRequestSystem? pathSystem = null;
        scenario.Simulation.Systems.Seal();
        foreach (Tzar2.Sim.Systems.ISimSystem system in scenario.Simulation.Systems.Ordered)
        {
            pathSystem ??= system as Tzar2.Sim.Systems.PathRequestSystem;
        }
        int ticks = ticksOption ?? scenarioName switch { "b3" or "b3-groups" => 600, "b4" => 3600, "gameplay" => 2400, "ai" => 12_000, _ => 1000 };
        int warmup = warmupOption ?? (scenarioName == "synthetic" ? 100 : 0);
        // "\n" line ends, so hash streams from Windows and Unix runners compare byte for byte.
        using StreamWriter? stream = hashStream is null ? null : new StreamWriter(hashStream) { NewLine = "\n" };
        for (int t = 0; t < warmup; t++)
        {
            Write(stream, scenario.RunTick());
        }

        double[] samples = new double[ticks];
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gcBefore = GC.CollectionCount(0);
        long simTicks = 0;
        TickResult last = default;
        for (int t = 0; t < ticks; t++)
        {
            scenario.GenerateCommands();
            long start = Stopwatch.GetTimestamp();
            last = scenario.Simulation.Tick(scenario.LastCommands);
            long elapsed = Stopwatch.GetTimestamp() - start;
            scenario.AfterTick();
            simTicks += elapsed;
            samples[t] = elapsed * 1000.0 / Stopwatch.Frequency;
            if (trace && pathSystem is not null)
            {
                stdout.WriteLine(string.Create(CultureInfo.InvariantCulture, $"trace      tick {last.Tick.Value}: {samples[t]:F3} ms, paths accepted {pathSystem.LastAccepted}, spent {pathSystem.LastSpent}, computed {pathSystem.LastComputed}, inline {pathSystem.LastInline}, queue {scenario.Simulation.World.PathQueue.Count}"));
            }
            Write(stream, last);
        }

        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        int gcs = GC.CollectionCount(0) - gcBefore;
        Array.Sort(samples);
        ulong finalHash = scenario.Simulation.ComputeFullHash();
        World world = scenario.Simulation.World;
        int moving = 0;
        foreach (Tzar2.Sim.Components.Motion m in world.Motions.Components)
        {
            moving += m.State == Tzar2.Sim.Components.MotionState.Moving ? 1 : 0;
        }

        var inv = CultureInfo.InvariantCulture;
        stdout.WriteLine(scenarioName == "synthetic"
            ? string.Create(inv, $"scenario   synthetic: map {options.MapSize}x{options.MapSize}, {options.Players} players, seed {options.Seed}, orders {options.OrdersPerPlayerPerTick}/player/tick, churn {options.ChurnPerPlayerPerTick}/player/tick")
            : $"scenario   {scenario.Describe()}");
        stdout.WriteLine(string.Create(inv, $"paths      budget {pathBudget} expansions/tick; flow fields cached {world.FlowFields.Count}, built {world.FlowFields.Builds}, objects {world.FlowFields.FieldsAllocated}"));
        stdout.WriteLine(string.Create(inv, $"runtime    {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim()} {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, workers {workers}, overflow policy {Core.Numerics.OverflowPolicy.Mode}"));
        stdout.WriteLine(string.Create(inv, $"entities   {world.Entities.AliveCount} alive, {moving} moving at the end"));
        stdout.WriteLine(string.Create(inv, $"ticks      {ticks} measured after {warmup} warm-up"));
        stdout.WriteLine(string.Create(inv, $"tick ms    p50 {Percentile(samples, 50):F3}  p99 {Percentile(samples, 99):F3}  max {samples[^1]:F3}  mean {simTicks * 1000.0 / Stopwatch.Frequency / ticks:F3}"));
        stdout.WriteLine(string.Create(inv, $"alloc      {allocated / (double)ticks:F1} B/tick (incl. bot command generation), gen0 GCs {gcs}"));
        stdout.WriteLine(string.Create(inv, $"final tick {world.Tick.Value}"));
        stdout.WriteLine(string.Create(inv, $"final hash {finalHash:X16}"));
        if (expectHash is { } expect && expect != finalHash)
        {
            stderr.WriteLine(string.Create(inv, $"t2 bench: final hash {finalHash:X16} differs from the expected {expect:X16} (determinism failure)."));
            return ExitCodes.Mismatch;
        }

        // The functional part of the B3/B4 budgets (TDD §21); tick times are gated by the caller (CI) from the output.
        string? violation = scenario switch
        {
            MazeMoveScenario b3 when b3.PlannedAfterTick is < 0 or > 2 => "B3: the 500 move orders were not all planned within 3 ticks",
            MassDeathScenario b4 when b4.Blocked > 0 => "B4: units got stuck in the traffic through the mass-death area",
            MassDeathScenario when world.Map.ObstacleVersion != 0 => "B4: corpses changed passability",
            GameplayScenario g when g.Delivered == 0 || g.Kills == 0 || g.Trained == 0 => "gameplay: no resources were delivered, nobody fought or nobody was trained",
            AiSkirmishScenario a when AiLagging(a) is { } p => $"ai: AI player {p} did not gather, build, train and fight",
            _ => null,
        };
        if (violation is not null)
        {
            stderr.WriteLine($"t2 bench: {violation}.");
            return ExitCodes.Mismatch;
        }

        return ExitCodes.Success;
    }

    // The first AI player that has not delivered, built, trained and (in a run of 10 minutes or more of game time, the
    // first wave leaves after 4) dealt damage, or null.
    private static int? AiLagging(AiSkirmishScenario scenario)
    {
        bool fights = (long)scenario.Simulation.World.Tick.Value * scenario.Simulation.World.Config.TickLengthMilliseconds >= 600_000;
        for (int p = 0; p < scenario.Options.Players; p++)
        {
            if (scenario.Delivered(p) == 0 || scenario.Built(p) == 0 || scenario.Trained(p) == 0 || (fights && scenario.DamageDealt(p) == 0))
            {
                return p;
            }
        }

        return null;
    }

    private static void Write(StreamWriter? stream, TickResult result)
    {
        if (stream is null)
        {
            return;
        }

        stream.Write(string.Create(CultureInfo.InvariantCulture, $"{result.Tick.Value}\t{result.StateHash:X16}"));
        stream.WriteLine(result.FullHash is { } full ? string.Create(CultureInfo.InvariantCulture, $"\t{full:X16}") : string.Empty);
    }

    private static double Percentile(double[] sorted, int percentile)
    {
        int index = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static bool TryInt(string text, int min, int max, Action<int> set)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
        {
            return false;
        }

        set(value);
        return true;
    }

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("Usage: t2 bench [options]");
        output.WriteLine();
        output.WriteLine("Runs a sim scenario headless and reports tick times (TDD §21).");
        output.WriteLine();
        output.WriteLine("  --scenario <name>   synthetic (bot orders on a generated map, default), b3 (500 individual move");
        output.WriteLine("                      orders on a 512² maze), b3-groups (the same in groups, see --group), b4 (mass");
        output.WriteLine("                      death: 2,000 kills in 60 s, then traffic across the bridge), gameplay (economy and");
        output.WriteLine("                      combat: bases, harvesting, building, training and a battle of all kingdoms; the");
        output.WriteLine("                      classic ruleset compiled from synthetic tables; --players, --entities = soldiers),");
        output.WriteLine("                      ai (AI against AI with the engine-default script: castles and peasants only, the");
        output.WriteLine("                      legacy AI does everything else; --players default 8, --map default 256, 12,000 ticks)");
        output.WriteLine("  --group <n>         units per order in b3-groups (default 50)");
        output.WriteLine("  --path-budget <n>   path-request budget in node expansions per tick (match setting)");
        output.WriteLine("  --trace             print per-tick time and path-request counters");
        output.WriteLine("  --entities <n>      units at tick 0 (default 4000; b3: ordered units, default 500)");
        output.WriteLine("  --map <n>           map side in tiles, 8-1024 (default 512)");
        output.WriteLine("  --players <n>       players, 1-16 (default 8)");
        output.WriteLine("  --orders <n>        units ordered per player per tick (default 16)");
        output.WriteLine("  --churn <n>         units deleted and respawned per player per tick (default 0; B4-like)");
        output.WriteLine("  --ticks <n>         measured ticks (default 1000)");
        output.WriteLine("  --warmup <n>        unmeasured ticks first (default 100)");
        output.WriteLine("  --workers <n>       sim worker threads (default: cores, max 8); never changes the result");
        output.WriteLine("  --seed <n>          scenario and match seed");
        output.WriteLine("  --hash-stream <f>   write 'tick<TAB>hash[<TAB>full hash]' per tick to a file");
        output.WriteLine("  --expect-hash <hex> exit 1 if the final full-state hash differs");
    }

    // The engine-default AI script (Tzar2's own, content/engine/ai/skirmish.ais), compiled once.
    private static readonly Lazy<Tzar2.AI.Legacy.Program.AiProgram> DefaultAiScript = new(() => Tzar2.AI.Legacy.Program.AiCompiler.CompileText(
        "skirmish.ais", System.Text.Encoding.UTF8.GetString(new Tzar2.Content.Rules.EngineDefaultsSource().ReadAllBytes("ai/skirmish.ais"))));

    // The classic ruleset from the synthetic tables (no original data needed, REQ-LEGAL-03), compiled once.
    private static readonly Lazy<Tzar2.Content.Compiled.Ruleset> GameplayRuleset = new(() => Tzar2.Content.Rules.SyntheticLegacyContent.CompileRuleset());
}
