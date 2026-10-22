using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Client.Tests;

/// <summary>
/// The open layout findings of the UI review (docs/reviews/2026-10-ui-review.md, ADR 0200) that the UI smoke test still
/// reports. Every reported issue must belong to one of them, and, on the synthetic package, each of them must still be
/// reported: a fix removes its entry here in the same PR, so the list only shrinks.
/// </summary>
internal static class UiFindings
{
    /// <summary>A known finding: its id in the review, and the issues it covers.</summary>
    /// <param name="Id">The finding's id in the review (UI-L…).</param>
    /// <param name="Display">A pattern the display's name matches (<c>1280x720-authentic</c>).</param>
    /// <param name="Screen">A pattern the screen's name matches.</param>
    /// <param name="Problem">The audit's problem (OffScreen, Overlap, TextOverflow).</param>
    /// <param name="Path">A pattern the control's path matches.</param>
    public sealed record Known(string Id, string Display, string Screen, string Problem, string Path)
    {
        public bool Covers(Issue issue)
            => Regex.IsMatch(issue.Display, "^(" + Display + ")$") && Regex.IsMatch(issue.Screen, "^(" + Screen + ")$")
               && issue.Problem == Problem && Regex.IsMatch(issue.Path, Path);
    }

    /// <summary>One <c>UI ISSUE</c> line of the smoke test.</summary>
    public sealed record Issue(string Display, string Screen, string Problem, string Path, string Line);

    /// <summary>The open findings.</summary>
    public static readonly Known[] Open =
    [
    ];

    /// <summary>The issues in the smoke test's output.</summary>
    public static List<Issue> Parse(string output)
    {
        var issues = new List<Issue>();
        foreach (string line in output.Split('\n'))
        {
            Match m = Regex.Match(line, @"^UI ISSUE (\S+) (\S+) (\S+) ([^:]+):");
            if (m.Success)
            {
                issues.Add(new Issue(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, line.TrimEnd()));
            }
        }

        return issues;
    }

    /// <summary>Fails on an issue no open finding covers, and (with <paramref name="everyKnownSeen"/>) on a finding no longer reported.</summary>
    public static void AssertOnlyKnown(string output, ITestOutputHelper log, bool everyKnownSeen)
    {
        List<Issue> issues = Parse(output);
        foreach (string line in output.Split('\n').Where(l => l.StartsWith("UI SMOKE", StringComparison.Ordinal) || l.StartsWith("UI SKIP", StringComparison.Ordinal)))
        {
            log.WriteLine(line.TrimEnd());
        }

        List<Issue> unknown = issues.Where(i => !Open.Any(k => k.Covers(i))).ToList();
        foreach (IGrouping<string, Issue> group in issues.Where(i => Open.Any(k => k.Covers(i))).GroupBy(i => Open.First(k => k.Covers(i)).Id))
        {
            log.WriteLine($"known {group.Key}: {group.Count()} issues");
        }

        string[] gone = everyKnownSeen ? [.. Open.Where(k => !issues.Any(k.Covers)).Select(k => k.Id).Distinct()] : [];
        Assert.True(unknown.Count == 0, $"{unknown.Count} new layout issues (fix them, or add a finding to the review and here):\n" + string.Join("\n", unknown.Take(60).Select(i => i.Line)));
        Assert.True(gone.Length == 0, "Findings no longer reported (remove them here and mark them fixed in the review): " + string.Join(", ", gone));
    }
}
