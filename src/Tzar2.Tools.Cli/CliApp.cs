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
        ("import", "M1", "import an original install into a base package"),
        ("convert", "M4", "convert legacy .wmp maps and .wcm campaigns (§18.5)"),
        ("validate", "M3", "validate packages, rules and maps"),
        ("pack", "M1", "build a package from a directory"),
        ("replay verify", "M6", "verify the state hashes of a replay headlessly"),
        ("desync diff", "M6", "diff two desync snapshots"),
        ("bench", "M2", "run benchmark scenarios"),
        ("fidelity capture", "M3", "capture fidelity facts from the original"),
        ("dump", "M1", "dump the structure of a legacy file"),
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
