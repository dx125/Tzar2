using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Tzar2.Tests.Shared;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>Backs <c>docs/mechanics/editor-parity.md</c> (TDD §25 M5: "parity checklist 100 %").</summary>
public class EditorParityTests
{
    private static readonly string[] Statuses = ["done", "blocked", "planned"];

    internal sealed record Row(string Id, string Feature, string Status, string Evidence, string Notes, int Line);

    internal static string ChecklistPath => Path.Combine(OriginPaths.RepositoryRoot, "docs", "mechanics", "editor-parity.md");

    internal static List<Row> Rows()
    {
        var rows = new List<Row>();
        string[] lines = File.ReadAllLines(ChecklistPath);
        bool inSummary = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Contains("summary:start", StringComparison.Ordinal))
            {
                inSummary = true;
            }
            else if (line.Contains("summary:end", StringComparison.Ordinal))
            {
                inSummary = false;
            }

            if (inSummary || !line.StartsWith('|') || line.StartsWith("| Id ", StringComparison.Ordinal) || line.StartsWith("|---", StringComparison.Ordinal))
            {
                continue;
            }

            string[] cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            Assert.True(cells.Length == 5, $"editor-parity.md:{i + 1}: expected 5 columns, got {cells.Length}.");
            rows.Add(new Row(cells[0], cells[1], cells[2], cells[3], cells[4], i + 1));
        }

        return rows;
    }

    [Fact]
    public void The_checklist_is_well_formed_and_backed_by_tests()
    {
        List<Row> rows = Rows();
        Assert.NotEmpty(rows);
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
        string clientTests = string.Join("\n", Directory.EnumerateFiles(Path.Combine(OriginPaths.RepositoryRoot, "tests", "Tzar2.Client.Tests"), "*.cs").Select(File.ReadAllText));
        var problems = new List<string>();
        foreach (Row r in rows)
        {
            if (!Statuses.Contains(r.Status))
            {
                problems.Add($"line {r.Line} ({r.Id}): unknown status '{r.Status}'");
                continue;
            }

            if (r.Status == "done")
            {
                if (r.Evidence.Length == 0)
                {
                    problems.Add($"line {r.Line} ({r.Id}): done without evidence");
                }

                foreach (string evidence in r.Evidence.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!EvidenceExists(evidence, clientTests))
                    {
                        problems.Add($"line {r.Line} ({r.Id}): no test '{evidence}'");
                    }
                }
            }

            if (r.Status == "blocked" && r.Notes.Length < 10)
            {
                problems.Add($"line {r.Line} ({r.Id}): blocked without a reason");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));

        // The summary block matches the rows.
        string text = File.ReadAllText(ChecklistPath);
        int done = rows.Count(r => r.Status == "done"), blocked = rows.Count(r => r.Status == "blocked"), planned = rows.Count(r => r.Status == "planned");
        string expected = $"| All | {rows.Count} | {done} | {blocked} | {planned} |";
        Assert.True(text.Contains(expected, StringComparison.Ordinal), $"The summary should read: {expected}");
        string percent = $"Parity: **{done * 100 / rows.Count} %** done.";
        Assert.True(text.Contains(percent, StringComparison.Ordinal), $"The summary should read: {percent}");
    }

    private static bool EvidenceExists(string evidence, string clientTests)
    {
        if (evidence.StartsWith("Client:", StringComparison.Ordinal))
        {
            return clientTests.Contains(evidence["Client:".Length..].Trim(), StringComparison.Ordinal);
        }

        Match m = Regex.Match(evidence, @"^([A-Za-z0-9_]+)\.([A-Za-z0-9_]+)$");
        if (!m.Success)
        {
            return false;
        }

        Type? type = typeof(EditorParityTests).Assembly.GetTypes().FirstOrDefault(t => t.Name == m.Groups[1].Value);
        return type?.GetMethod(m.Groups[2].Value, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static) is { } method
            && method.GetCustomAttributes().Any(a => a is FactAttribute);
    }
}
