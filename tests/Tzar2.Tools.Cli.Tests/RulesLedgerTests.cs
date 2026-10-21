using System;
using System.IO;
using System.Linq;
using Tzar2.Tools.Cli.Commands;
using Tzar2.Tools.Cli.Rules;
using Xunit;

namespace Tzar2.Tools.Cli.Tests;

public sealed class RulesLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "t2-rules-" + Guid.NewGuid().ToString("N"));

    public RulesLedgerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "rules"));
        Directory.CreateDirectory(Path.Combine(_root, "docs", "decisions"));
        Directory.CreateDirectory(Path.Combine(_root, "tests", "Some.Tests", "bin"));
        File.WriteAllText(Path.Combine(_root, "docs", "decisions", "0180-third-play-test-rules.md"), "# 0180\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void The_repository_ledger_is_consistent_and_its_report_is_current()
    {
        string root = FidelityCommand.RepositoryRoot() ?? throw new InvalidOperationException("no repository above the tests");
        RulesLedger ledger = RulesLedger.Load(root);
        ledger.CheckReport();

        Assert.True(ledger.Errors.Count == 0, string.Join(Environment.NewLine, ledger.Errors));
        Assert.NotEmpty(ledger.Chapters);
    }

    [Fact]
    public void Tests_link_to_rules_by_trait_and_statuses_must_agree_with_them()
    {
        Chapter("production.yaml", """
            schema: tzar2-rules/1
            prefix: PROD
            title: Production
            rules:
              PROD-001:
                rule: A queue takes any number of any units.
                source: [owner 2026-10-20]
                status: tested
                adr: [0180]
              PROD-002:
                rule: Tested, but nothing names it.
                source: manual ch. 5
                status: tested
              PROD-003:
                rule: Implemented, but a test names it.
                source: [guide p. 3]
                status: implemented
              PROD-004:
                rule: Missing, but a test names it.
                source: [data UNITS.TXT]
                status: missing
              PROD-005:
                rule: Partial with a test.
                source: [owner 2026-10-21]
                status: partial
                note: The count on the button is missing.
            """);
        Test("QueueTests.cs", """
            public class QueueTests
            {
                [Fact]
                [RULE("PROD-001")]
                public void A_queue_takes_any_number() { }

                [Theory, RULE("PROD-003"), RULE("PROD-004")]
                [InlineData(1)]
                public async Task Two_rules(int n) { }

                [Fact]
                [RULE("PROD-099")]
                public void Unknown() { }

                [RULE("PROD-005")]
                public void Partial() { }
            }

            [RULE("PROD-001")]
            public sealed class WholeClass
            {
            }
            """);
        // Build output is never scanned.
        File.WriteAllText(Path.Combine(_root, "tests", "Some.Tests", "bin", "Copy.cs"), Traits("[RULE(\"PROD-777\")] void X() { }"));

        RulesLedger ledger = RulesLedger.Load(_root);

        Assert.Equal(["A_queue_takes_any_number", "WholeClass"], ledger.TestsOf("PROD-001").Select(t => t.Member));
        RuleTest two = Assert.Single(ledger.TestsOf("PROD-003"));
        Assert.Equal(("Two_rules", "tests/Some.Tests/QueueTests.cs", 9), (two.Member, two.File, two.Line));
        Assert.Collection(
            ledger.Errors,
            e => Assert.Equal("tests/Some.Tests/QueueTests.cs:13: Unknown names unknown rule PROD-099", e),
            e => Assert.Contains("PROD-002: status 'tested' but no test carries", e, StringComparison.Ordinal),
            e => Assert.Contains("PROD-003: 1 test(s) name it; mark it 'tested'", e, StringComparison.Ordinal),
            e => Assert.Contains("PROD-004: a missing rule cannot have tests", e, StringComparison.Ordinal));
    }

    [Fact]
    public void Malformed_chapters_and_rules_are_reported_with_their_place()
    {
        Chapter("a.yaml", """
            schema: tzar2-rules/1
            prefix: ECO
            title: Economy
            colour: red
            rules:
              ECO-1:
                rule: Short id.
                source: [manual]
                status: tested
              ECO-002:
                source: [rumour heard somewhere]
                status: done
              ECO-003:
                rule: A difference without a reason.
                source: [owner 2026-10-21]
                status: deviation
                adr: [0999, 12]
                when: always
            """);
        Chapter("b.yaml", "schema: tzar2-rules/1\nprefix: ECO\ntitle: Again\nrules: {}\n");
        Chapter("c.yaml", "schema: other\nprefix: X\n");

        RulesLedger ledger = RulesLedger.Load(_root);

        Assert.Equal(
            [
                "docs/rules/a.yaml:4: unknown key 'colour'",
                "docs/rules/a.yaml:7: rule id 'ECO-1' must be ECO-nnn",
                "docs/rules/a.yaml:11: ECO-002: 'rule' is required",
                "docs/rules/a.yaml:11: ECO-002: source 'rumour heard somewhere' must start with one of manual, guide, editor-manual, patch, data, help, strings, owner, capture, community",
                "docs/rules/a.yaml:11: ECO-002: status 'done' must be one of tested, implemented, partial, missing, deviation, unclear",
                "docs/rules/a.yaml:14: ECO-003: unknown key 'when'",
                "docs/rules/a.yaml:14: ECO-003: ADR 0999 does not exist",
                "docs/rules/a.yaml:14: ECO-003: adr '12' must be a four-digit ADR number",
                "docs/rules/a.yaml:14: ECO-003: a deviation rule needs a 'note'",
                "docs/rules/b.yaml: prefix ECO is also used by a.yaml",
                "docs/rules/c.yaml: 'schema: tzar2-rules/1' is required",
            ],
            ledger.Errors);
    }

    [Fact]
    public void The_report_counts_statuses_by_chapter_and_lists_what_is_not_done()
    {
        Chapter("production.yaml", """
            schema: tzar2-rules/1
            prefix: PROD
            title: Production
            rules:
              PROD-001:
                rule: Units appear around the building.
                source: [owner 2026-10-20]
                status: deviation
                adr: [0180]
                note: Never in a pocket shut in by walls.
              PROD-002:
                rule: The count shows on the button.
                source: [owner 2026-10-21]
                status: missing
                work: STATUS work queue 2
              PROD-003:
                rule: Done.
                source: [manual ch. 5]
                status: implemented
            """);

        RulesLedger ledger = RulesLedger.Load(_root);
        string report = ledger.Report();

        Assert.Empty(ledger.Errors);
        Assert.Contains("| [Production](production.yaml) (PROD) | 3 | 0 | 1 | 0 | 1 | 1 | 0 |", report, StringComparison.Ordinal);
        Assert.Contains("| **All** | 3 | 0 | 1 | 0 | 1 | 1 | 0 |", report, StringComparison.Ordinal);
        Assert.Contains("* **PROD-002**: The count shows on the button. Work: STATUS work queue 2.", report, StringComparison.Ordinal);
        Assert.Contains("* **PROD-001**: Units appear around the building. *Never in a pocket shut in by walls.* (ADR 0180)", report, StringComparison.Ordinal);
        Assert.DoesNotContain("## Unclear", report, StringComparison.Ordinal);

        File.WriteAllText(ledger.ReadmePath, "# Rules\n\nIntro.\n\n" + RulesLedger.ReportBegin + "\nold\n" + RulesLedger.ReportEnd + "\nTail.\n");
        Assert.False(ledger.CheckReport());
        Assert.Equal("docs/rules/README.md: the report is out of date; run 't2 rules report'", Assert.Single(ledger.Errors));

        string updated = RulesLedger.WithReport(File.ReadAllText(ledger.ReadmePath), report);
        Assert.Equal("# Rules\n\nIntro.\n\n" + report + "Tail.\n", updated);
        File.WriteAllText(ledger.ReadmePath, updated.Replace("\n", "\r\n", StringComparison.Ordinal));
        RulesLedger again = RulesLedger.Load(_root);
        Assert.True(again.CheckReport());
    }

    private void Chapter(string name, string yaml) => File.WriteAllText(Path.Combine(_root, "docs", "rules", name), yaml);

    // The samples spell the trait RULE(...) so that the scan of this repository does not take them for real links.
    private static string Traits(string code) => code.Replace("RULE(", "Trait(\"Rule\", ", StringComparison.Ordinal);

    private void Test(string name, string code) => File.WriteAllText(Path.Combine(_root, "tests", "Some.Tests", name), Traits(code));
}
