using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
        int ticks = 1000;
        int warmup = 100;
        int workers = Math.Clamp(Environment.ProcessorCount, 1, 8);
        string? hashStream = null;
        ulong? expectHash = null;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-h" or "--help")
            {
                PrintUsage(stdout);
                return ExitCodes.Success;
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
                case "--entities": ok = TryInt(value, 0, 1_000_000, v => options = options with { EntityCount = v }); break;
                case "--map": ok = TryInt(value, 8, 1024, v => options = options with { MapSize = v }); break;
                case "--players": ok = TryInt(value, 1, 16, v => options = options with { Players = v }); break;
                case "--orders": ok = TryInt(value, 0, 4096, v => options = options with { OrdersPerPlayerPerTick = v }); break;
                case "--churn": ok = TryInt(value, 0, 64, v => options = options with { ChurnPerPlayerPerTick = v }); break;
                case "--ticks": ok = TryInt(value, 1, 10_000_000, v => ticks = v); break;
                case "--warmup": ok = TryInt(value, 0, 1_000_000, v => warmup = v); break;
                case "--workers": ok = TryInt(value, 1, 64, v => workers = v); break;
                case "--seed":
                    ok = ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong seed);
                    options = options with { Seed = seed };
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

        using var scenario = new SyntheticScenario(options, workers);
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
            simTicks += elapsed;
            samples[t] = elapsed * 1000.0 / Stopwatch.Frequency;
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
        stdout.WriteLine(string.Create(inv, $"scenario   map {options.MapSize}x{options.MapSize}, {options.Players} players, seed {options.Seed}, orders {options.OrdersPerPlayerPerTick}/player/tick, churn {options.ChurnPerPlayerPerTick}/player/tick"));
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

        return ExitCodes.Success;
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
        output.WriteLine("Runs the synthetic sim scenario (bot move orders on a generated map) and reports tick times.");
        output.WriteLine();
        output.WriteLine("  --entities <n>      units at tick 0 (default 4000)");
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
}
