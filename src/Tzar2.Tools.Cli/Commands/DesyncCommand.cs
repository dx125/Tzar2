using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tzar2.Content.Rules;
using Tzar2.Game.Multiplayer;
using Tzar2.Game.Session;
using Tzar2.Net.Relay;
using Tzar2.Replay;
using Tzar2.Sim.Snapshots;

namespace Tzar2.Tools.Cli.Commands;

/// <summary>
/// <c>t2 desync diff|extract</c> (TDD §8.8, ADR 0165): a component-by-component diff of two snapshots — the "after" (or
/// "before") snapshots of two peers in a desync report, or two snapshot files of the same match with the match taken from
/// a replay or a report — and extraction of a report's snapshots as files.
/// </summary>
internal static class DesyncCommand
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Usage(args.Length == 0 ? stderr : stdout);
            return args.Length == 0 ? ExitCodes.UsageError : ExitCodes.Success;
        }

        try
        {
            return args[0] switch
            {
                "diff" => Diff(args[1..], stdout, stderr),
                "extract" => Extract(args[1..], stdout, stderr),
                _ => Unknown(args[0], stderr),
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or RulesException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            stderr.WriteLine($"t2 desync {args[0]}: {e.Message}");
            return ExitCodes.UsageError;
        }
    }

    private static int Unknown(string verb, TextWriter stderr)
    {
        stderr.WriteLine($"t2 desync: unknown command '{verb}'.");
        Usage(stderr);
        return ExitCodes.UsageError;
    }

    private static void Usage(TextWriter output)
    {
        output.WriteLine("Usage: t2 desync diff <report.t2desync> [--slots <a>,<b>] [--before] [--max <n>] [--package <base.t2pkg> | --synthetic]");
        output.WriteLine("       t2 desync diff <a.snap> <b.snap> --setup <replay.t2rep|report.t2desync> [--max <n>] [--package <base.t2pkg> | --synthetic]");
        output.WriteLine("       t2 desync extract <report.t2desync> --out <dir>");
        output.WriteLine("Exit code 0: no difference; 1: differences listed.");
    }

    private static int Diff(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var files = new List<string>();
        string? package = MatchContent.EnvironmentPackage();
        string? setupFile = null;
        (byte A, byte B)? slots = null;
        bool before = false;
        int max = 500;
        for (int i = 0; i < args.Length; i++)
        {
            string value = i + 1 < args.Length ? args[i + 1] : string.Empty;
            switch (args[i])
            {
                case "--package": package = value; i++; break;
                case "--synthetic": package = null; break;
                case "--setup": setupFile = value; i++; break;
                case "--before": before = true; break;
                case "--max": max = int.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--slots":
                    string[] parts = value.Split(',');
                    slots = (byte.Parse(parts[0], CultureInfo.InvariantCulture), byte.Parse(parts[1], CultureInfo.InvariantCulture));
                    i++;
                    break;
                default: files.Add(args[i]); break;
            }
        }

        MatchContent content = MatchContent.Load(package);
        IReadOnlyList<SnapshotDifference> diff;
        if (files.Count == 1)
        {
            DesyncBundle bundle = DesyncReports.Read(File.ReadAllBytes(files[0]));
            Describe(bundle, stdout);
            (byte a, byte b) = slots ?? DefaultPair(bundle);
            stdout.WriteLine($"diff of slot {a} ({Name(bundle, a)}) against slot {b} ({Name(bundle, b)}), {(before ? "before" : "after")} snapshots:");
            diff = before ? DesyncReports.DiffBefore(bundle, content.Ruleset, content.Ai, a, b, max) : DesyncReports.Diff(bundle, content.Ruleset, content.Ai, a, b, max);
        }
        else if (files.Count == 2 && setupFile is not null)
        {
            MatchSetup setup = SetupOf(setupFile, content);
            diff = DesyncReports.Diff(setup, content.Ruleset, File.ReadAllBytes(files[0]), File.ReadAllBytes(files[1]), max);
        }
        else
        {
            stderr.WriteLine("t2 desync diff: a report, or two snapshots and --setup.");
            return ExitCodes.UsageError;
        }

        stdout.Write(SnapshotDiff.Format(diff));
        stdout.WriteLine(diff.Count == 0 ? "no differences" : string.Create(CultureInfo.InvariantCulture, $"{diff.Count} differences"));
        return diff.Count == 0 ? ExitCodes.Success : ExitCodes.Mismatch;
    }

    private static MatchSetup SetupOf(string file, MatchContent content)
    {
        byte[] bytes = File.ReadAllBytes(file);
        if (file.EndsWith(".t2desync", StringComparison.OrdinalIgnoreCase))
        {
            return DesyncReports.Setup(DesyncReports.Read(bytes), content.Ruleset, content.Ai);
        }

        return ReplayPlayback.SetupOf(ReplayFile.Parse(bytes).Header, content.Ai);
    }

    // The first odd slot against the first slot of the majority.
    private static (byte, byte) DefaultPair(DesyncBundle bundle)
    {
        byte odd = bundle.OddSlots.Length > 0 ? bundle.OddSlots[0] : bundle.Slots[0].Slot;
        byte majority = bundle.Slots.Select(s => s.Slot).FirstOrDefault(s => !bundle.OddSlots.Contains(s), bundle.Slots[^1].Slot);
        return (majority, odd);
    }

    private static string Name(DesyncBundle bundle, byte slot) => bundle.Slots.FirstOrDefault(s => s.Slot == slot)?.Name ?? "?";

    private static void Describe(DesyncBundle bundle, TextWriter stdout)
    {
        stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"desync report: lobby {bundle.Lobby}, match {bundle.MatchId:X16}, tick {bundle.Tick}, majority {bundle.MajorityHash:X16}, odd slots [{string.Join(",", bundle.OddSlots)}], {bundle.Log.Length} ticks of commands"));
        foreach (DesyncSlotReport s in bundle.Slots)
        {
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  slot {s.Slot} {s.Name}: reported {s.ReportedHash:X16}; before tick {s.BeforeTick} ({s.Before.Length} B), after tick {s.AfterTick} ({s.After.Length} B, {s.AfterHash:X16})"));
        }
    }

    private static int Extract(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? output = null;
        string? input = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
            {
                output = args[++i];
            }
            else
            {
                input = args[i];
            }
        }

        if (input is null || output is null)
        {
            stderr.WriteLine("t2 desync extract: a report and --out <dir>.");
            return ExitCodes.UsageError;
        }

        DesyncBundle bundle = DesyncReports.Read(File.ReadAllBytes(input));
        Directory.CreateDirectory(output);
        foreach (DesyncSlotReport s in bundle.Slots)
        {
            if (s.Before.Length > 0)
            {
                File.WriteAllBytes(Path.Combine(output, string.Create(CultureInfo.InvariantCulture, $"slot{s.Slot}-before-t{s.BeforeTick}.snap")), s.Before);
            }

            File.WriteAllBytes(Path.Combine(output, string.Create(CultureInfo.InvariantCulture, $"slot{s.Slot}-after-t{s.AfterTick}.snap")), s.After);
        }

        stdout.WriteLine($"wrote {bundle.Slots.Length} slots' snapshots to {output}");
        return ExitCodes.Success;
    }
}
