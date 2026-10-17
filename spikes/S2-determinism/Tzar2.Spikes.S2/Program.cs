using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Tzar2.Core.Hashing;

namespace Tzar2.Spikes.S2;

/// <summary>
/// Spike S2 harness. Usage: <c>Tzar2.Spikes.S2 [--ticks N] [--units N] [--seed N] [--out DIR] [--label NAME] [--probes 0|1]</c>.
/// Writes <c>LABEL.ticks.txt</c> (one xxHash64 per tick), <c>LABEL.probes.txt</c> and <c>LABEL.summary.txt</c>.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        int ticks = 2000;
        int units = 4096;
        ulong seed = 0x7A42_2026UL;
        string outDir = ".";
        string? label = null;
        bool runProbes = true;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--ticks": ticks = int.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
                case "--units": units = int.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
                case "--seed": seed = ulong.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
                case "--out": outDir = args[i + 1]; break;
                case "--label": label = args[i + 1]; break;
                case "--probes": runProbes = args[i + 1] != "0"; break;
                default: Console.Error.WriteLine("unknown option " + args[i]); return 2;
            }
        }

#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        bool aot = !RuntimeFeature.IsDynamicCodeSupported;
        string runtime = RuntimeInformation.FrameworkDescription;
        string arch = RuntimeInformation.ProcessArchitecture.ToString();
        label ??= $"{arch}-{Environment.Version.Major}-{configuration}{(aot ? "-aot" : string.Empty)}".ToLowerInvariant();
        Directory.CreateDirectory(outDir);

        long start = Stopwatch.GetTimestamp();
        var sim = new ToySim(seed, units);
        var final = new StateHasher();
        var tickLines = new StringBuilder();
        for (int t = 0; t < ticks; t++)
        {
            ulong h = sim.Step(t);
            final.Add(h);
            tickLines.Append(t.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(h.ToString("X16", CultureInfo.InvariantCulture)).Append('\n');
        }

        double simMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        ulong finalHash = final.GetCurrentHash();
        File.WriteAllText(Path.Combine(outDir, label + ".ticks.txt"), tickLines.ToString());

        List<(string Name, ulong Hash, List<string> Values)> probes = runProbes ? Probes.Run() : new();
        var probeText = new StringBuilder();
        foreach ((string name, ulong hash, List<string> values) in probes)
        {
            probeText.Append("## ").Append(name).Append('\n');
            for (int i = 0; i < values.Count; i++)
            {
                probeText.Append(i.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(values[i]).Append('\n');
            }
        }

        File.WriteAllText(Path.Combine(outDir, label + ".probes.txt"), probeText.ToString());

        var summary = new StringBuilder();
        summary.Append(CultureInfo.InvariantCulture, $"label\t{label}\n");
        summary.Append(CultureInfo.InvariantCulture, $"runtime\t{runtime}\n");
        summary.Append(CultureInfo.InvariantCulture, $"arch\t{arch} (OS {RuntimeInformation.OSArchitecture}, {RuntimeInformation.OSDescription})\n");
        summary.Append(CultureInfo.InvariantCulture, $"config\t{configuration}{(aot ? " NativeAOT" : string.Empty)}\n");
        summary.Append(CultureInfo.InvariantCulture, $"workload\t{ticks} ticks, {units} units, {ToySim.Players} Lua scripts, seed {seed}\n");
        summary.Append(CultureInfo.InvariantCulture, $"lua\tbudget aborts {sim.BudgetAborts}, script errors {sim.ScriptErrors}\n");
        summary.Append(CultureInfo.InvariantCulture, $"sim_ms\t{simMs:F0}\n");
        summary.Append(CultureInfo.InvariantCulture, $"final\t{finalHash:X16}\n");
        foreach ((string name, ulong hash, List<string> values) in probes)
        {
            summary.Append(CultureInfo.InvariantCulture, $"probe\t{hash:X16}\t{values.Count}\t{name}\n");
        }

        File.WriteAllText(Path.Combine(outDir, label + ".summary.txt"), summary.ToString());
        Console.Write(summary.ToString());
        return 0;
    }
}
