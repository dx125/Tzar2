using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Tools.Cli.Commands;

namespace Tzar2.Tools.Cli;

/// <summary>Command dispatcher for <c>t2</c> (TDD §7.2).</summary>
internal static class CliApp
{
    // Subcommands of TDD §7.2 that later milestones implement.
    private static readonly (string Name, string Milestone, string Summary)[] Planned =
    [
        ("validate", "M3", "validate packages, rules and maps"),
        ("pack", "M1", "build a package from a directory"),
    ];

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            PrintUsage(stderr);
            return ExitCodes.UsageError;
        }

        if (args[0] is "-h" or "--help" or "help")
        {
            PrintUsage(stdout);
            return ExitCodes.Success;
        }

        if (args[0] == "verify-original")
        {
            return VerifyOriginalCommand.Run(args.AsSpan(1).ToArray(), stdout, stderr);
        }

        if (args[0] == "import")
        {
            return ImportCommand.Run(args.AsSpan(1).ToArray(), stdout, stderr);
        }

        if (args[0] == "bench")
        {
            return BenchCommand.Run(args.AsSpan(1).ToArray(), stdout, stderr);
        }

        if (args[0] == "fidelity")
        {
            return FidelityCommand.Run(args.AsSpan(1).ToArray(), stdout, stderr);
        }

        if (args[0] == "mapgen")
        {
            return MapGenCommand.Run(args.AsSpan(1).ToArray(), stdout, stderr);
        }

        if (args[0] == "dump")
        {
            return DumpCommand.Run(args.AsSpan(1).ToArray(), stdout, stderr);
        }

        if (args[0] == "convert")
        {
            return ConvertCommand.Run(args.AsSpan(1).ToArray(), stdout, stderr);
        }

        if (args[0] == "campaign")
        {
            return CampaignCommand.Run(args.AsSpan(1).ToArray(), stdout, stderr);
        }

        if (args[0] == "ai")
        {
            return AiCommand.Run(args.AsSpan(1).ToArray(), stdout, stderr);
        }

        if (args[0] == "replay")
        {
            return ReplayCommand.Run(args.AsSpan(1).ToArray(), stdout, stderr);
        }

        if (args[0] == "desync")
        {
            return DesyncCommand.Run(args.AsSpan(1).ToArray(), stdout, stderr);
        }

        if (args[0] == "soak")
        {
            return SoakCommand.Run(args.AsSpan(1).ToArray(), stdout, stderr);
        }

        if (args[0] == "rules")
        {
            return RulesCommand.Run(args.AsSpan(1).ToArray(), stdout, stderr);
        }

        foreach ((string name, string milestone, _) in Planned)
        {
            string[] words = name.Split(' ');
            if (args.Length >= words.Length && words.SequenceEqual(args.Take(words.Length), StringComparer.Ordinal))
            {
                stderr.WriteLine($"t2 {name}: not implemented ({milestone})");
                return ExitCodes.NotImplemented;
            }
        }

        stderr.WriteLine($"t2: unknown command '{args[0]}'. Run 't2 --help' for the list of commands.");
        return ExitCodes.UsageError;
    }

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("Usage: t2 <command> [options]");
        output.WriteLine();
        output.WriteLine("Commands:");
        var lines = new List<(string, string)>
        {
            ("verify-original <path>", "check a local original install against the reference manifest"),
            ("import <origin> <outDir>", "import an original install into a base package"),
            ("dump <file> [entries]", "dump a package or legacy file; --out <dir> writes PNGs"),
            ("mapgen [options]", "generate a random map; --out <dir> writes a debug dump, a summary and a PNG preview"),
            ("bench [options]", "run the synthetic sim benchmark; reports tick p50/p99 and the final hash"),
            ("convert <file|folder>", "convert legacy .wmp maps and .wcm campaigns into .t2map/.t2camp (§18.5)"),
            ("ai check <folder|package>", "parse and compile legacy AI scripts; prints located messages"),
            ("campaign run (--demo | <wcm>)", "play campaigns headless with the scripted solver; reports which missions complete"),
            ("fidelity capture|analyze|check|scenarios|selftest", "capture harness: measure the original game (Windows), turn captures into facts"),
            ("replay verify|record|info", "verify replays headlessly (every stored hash), record a computer match, show a header"),
            ("desync diff|extract", "component diff of two snapshots or a desync report; extract a report's snapshots"),
            ("soak [options]", "multiplayer soak: matches through a real relay with impaired UDP and disconnects"),
            ("rules check|report|list", "the ledger of the original's rules: check it against the tests, write its report"),
        };
        lines.AddRange(Planned.Select(p => (p.Name, $"{p.Summary} [not implemented, {p.Milestone}]")));
        int width = lines.Max(l => l.Item1.Length) + 2;
        foreach ((string command, string summary) in lines)
        {
            output.WriteLine($"  {command.PadRight(width)}{summary}");
        }

        output.WriteLine();
        output.WriteLine("Run 't2 verify-original --help' for its options.");
    }
}
