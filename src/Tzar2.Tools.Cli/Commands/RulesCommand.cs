using System.IO;
using System.Linq;
using Tzar2.Tools.Cli.Rules;

namespace Tzar2.Tools.Cli.Commands;

/// <summary>
/// <c>t2 rules</c>: the ledger of the original's rules (ADR 0181).
/// <list type="bullet">
/// <item><c>check</c>: validates <c>docs/rules/*.yaml</c>, the tests' rule ids and the README's report.</item>
/// <item><c>report</c>: writes the README's generated part.</item>
/// <item><c>list [status...]</c>: prints the rules, optionally only those with the given statuses.</item>
/// </list>
/// </summary>
internal static class RulesCommand
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" || args[0] is not ("check" or "report" or "list"))
        {
            bool help = args.Length > 0 && args[0] is "-h" or "--help";
            PrintUsage(help ? stdout : stderr);
            return help ? ExitCodes.Success : ExitCodes.UsageError;
        }

        string? root = FidelityCommand.RepositoryRoot();
        if (root is null)
        {
            stderr.WriteLine("t2 rules: run it inside the repository (no Tzar2.sln found above the current directory).");
            return ExitCodes.UsageError;
        }

        RulesLedger ledger = RulesLedger.Load(root);
        switch (args[0])
        {
            case "report":
                if (ledger.Errors.Count == 0)
                {
                    string readme = File.Exists(ledger.ReadmePath) ? File.ReadAllText(ledger.ReadmePath) : "# Rules of the original\n";
                    File.WriteAllText(ledger.ReadmePath, RulesLedger.WithReport(readme, ledger.Report()));
                    stdout.WriteLine($"Wrote the report to {ledger.ReadmePath}.");
                }

                break;
            case "list":
                foreach (LedgerRule rule in ledger.Chapters.SelectMany(c => c.Rules))
                {
                    if (args.Length == 1 || args.Skip(1).Contains(rule.Status.ToString().ToLowerInvariant()))
                    {
                        stdout.WriteLine($"{rule.Id} [{rule.Status.ToString().ToLowerInvariant()}] {rule.Text}");
                    }
                }

                break;
            default:
                ledger.CheckReport();
                break;
        }

        foreach (string error in ledger.Errors)
        {
            stderr.WriteLine(error);
        }

        int rules = ledger.Chapters.Sum(c => c.Rules.Count);
        int tested = ledger.Chapters.Sum(c => c.Rules.Count(r => r.Status == RuleStatus.Tested));
        stdout.WriteLine($"{rules} rules in {ledger.Chapters.Count} chapters, {tested} tested by {ledger.Tests.Count} test link(s); {ledger.Errors.Count} problem(s).");
        return ledger.Errors.Count == 0 ? ExitCodes.Success : ExitCodes.Mismatch;
    }

    private static void PrintUsage(TextWriter o)
    {
        o.WriteLine("Usage: t2 rules check | report | list [status...]");
        o.WriteLine();
        o.WriteLine("The ledger of the original's rules (docs/rules, ADR 0181).");
        o.WriteLine("  check    validate the chapters, the tests' [Trait(\"Rule\", ...)] ids and the README's report");
        o.WriteLine("  report   write the README's generated report");
        o.WriteLine("  list     print the rules; give statuses (tested, implemented, partial, missing, deviation, unclear) to filter");
    }
}
