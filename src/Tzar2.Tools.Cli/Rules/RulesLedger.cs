using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Tzar2.Tools.Cli.Rules;

/// <summary>Where a rule of the original stands in Tzar2 (ADR 0181).</summary>
internal enum RuleStatus
{
    /// <summary>Done, and at least one test carries the rule's id.</summary>
    Tested,

    /// <summary>Done as far as the code shows; no test names it yet.</summary>
    Implemented,

    /// <summary>Done in part; the note says what is missing.</summary>
    Partial,

    /// <summary>Not done.</summary>
    Missing,

    /// <summary>Tzar2 does it differently on purpose (an ADR or an owner decision; the note says which).</summary>
    Deviation,

    /// <summary>The sources disagree or say too little; the note says what would settle it.</summary>
    Unclear,
}

/// <summary>One rule of the original game.</summary>
/// <param name="Id">Its id, <c>PREFIX-nnn</c>.</param>
/// <param name="Text">The rule in our own words.</param>
/// <param name="Sources">Where it comes from (each starts with a <see cref="RulesLedger.SourceKinds">kind</see>).</param>
/// <param name="Status">Where it stands.</param>
/// <param name="Adrs">ADR numbers that decide it.</param>
/// <param name="Note">What is missing, different or unclear.</param>
/// <param name="Work">The work-queue item, issue or milestone that will close it.</param>
/// <param name="Line">1-based line in its chapter file.</param>
internal sealed record LedgerRule(
    string Id,
    string Text,
    IReadOnlyList<string> Sources,
    RuleStatus Status,
    IReadOnlyList<string> Adrs,
    string Note,
    string Work,
    int Line);

/// <summary>A chapter file of the ledger, <c>docs/rules/&lt;name&gt;.yaml</c>.</summary>
/// <param name="File">The file name.</param>
/// <param name="Prefix">The id prefix of its rules.</param>
/// <param name="Title">Its title.</param>
/// <param name="Rules">Its rules in file order.</param>
internal sealed record LedgerChapter(string File, string Prefix, string Title, IReadOnlyList<LedgerRule> Rules);

/// <summary>A test that names a rule with <c>[Trait("Rule", "ID")]</c>.</summary>
/// <param name="RuleId">The rule.</param>
/// <param name="File">The test file, relative to the repository, with forward slashes.</param>
/// <param name="Line">1-based line of the test's declaration.</param>
/// <param name="Member">The test method, or the class when the trait is on a class.</param>
internal sealed record RuleTest(string RuleId, string File, int Line, string Member);

/// <summary>
/// The ledger of the original's rules (ADR 0181): the chapters in <c>docs/rules/*.yaml</c>, the tests that name their
/// rules, and the checks and report of <c>t2 rules</c>.
/// </summary>
internal sealed partial class RulesLedger
{
    /// <summary>The schema line every chapter starts with.</summary>
    public const string Schema = "tzar2-rules/1";

    /// <summary>The first line of the generated part of <c>docs/rules/README.md</c>.</summary>
    public const string ReportBegin = "<!-- BEGIN t2 rules report: generated, do not edit -->";

    /// <summary>The last line of the generated part.</summary>
    public const string ReportEnd = "<!-- END t2 rules report -->";

    /// <summary>
    /// The kinds of source a rule may cite, as the first word of each source: the original's manual, strategy guide and
    /// editor manual, its patch notes (<c>TZAR110.TXT</c>, <c>DATA/CHANGES.TXT</c>), its data files, its in-game help
    /// and strings, the owner's knowledge from playing it, a capture of the running game, and community knowledge.
    /// </summary>
    public static readonly IReadOnlyList<string> SourceKinds =
        ["manual", "guide", "editor-manual", "patch", "data", "help", "strings", "owner", "capture", "community"];

    private static readonly string[] RuleKeys = ["rule", "source", "status", "adr", "note", "work"];

    private readonly List<string> _errors = [];

    private RulesLedger(string root)
    {
        Root = root;
    }

    /// <summary>The repository root.</summary>
    public string Root { get; }

    /// <summary>The chapters, ordered by file name.</summary>
    public IReadOnlyList<LedgerChapter> Chapters { get; private set; } = [];

    /// <summary>The tests that name rules, ordered by file and line.</summary>
    public IReadOnlyList<RuleTest> Tests { get; private set; } = [];

    /// <summary>Problems found while loading and checking, each with its file and line.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>The account of the original's data files (<c>docs/rules/data-usage.yaml</c>).</summary>
    public DataUsage? Data { get; private set; }

    /// <summary>The directory of the chapters.</summary>
    public string RulesDirectory => Path.Combine(Root, "docs", "rules");

    /// <summary>The README whose generated part is the report.</summary>
    public string ReadmePath => Path.Combine(RulesDirectory, "README.md");

    /// <summary>Loads the chapters under <c>docs/rules</c>, scans <c>tests</c> and checks both.</summary>
    /// <param name="root">The repository root.</param>
    public static RulesLedger Load(string root)
    {
        var ledger = new RulesLedger(root);
        ledger.LoadChapters();
        ledger.ScanTests();
        ledger.CheckLinks();
        ledger.Data = DataUsage.Load(root, new HashSet<string>(ledger.Chapters.SelectMany(c => c.Rules).Select(r => r.Id), StringComparer.Ordinal));
        ledger._errors.AddRange(ledger.Data.Errors);
        return ledger;
    }

    /// <summary>
    /// Checks the data account against an install (local only): every data file of the reference build has an entry and
    /// every name is accounted for. Adds the problems to <see cref="Errors"/>.
    /// </summary>
    /// <param name="origin">The original install.</param>
    public void CheckInstall(string origin)
    {
        string manifest = Path.Combine(Root, "docs", "original-build", "manifests", "gog-1.10-en.reference.tsv");
        IEnumerable<string> files = File.ReadLines(manifest).Skip(1).Select(l => l.Split('\t')[0])
            .Where(f => f.Length > 0 && File.Exists(Path.Combine(origin, f)));
        int before = Data!.Errors.Count;
        Data.CheckInstall(origin, files);
        _errors.AddRange(Data.Errors.Skip(before));
    }

    /// <summary>The tests that name <paramref name="ruleId"/>.</summary>
    public IEnumerable<RuleTest> TestsOf(string ruleId) => Tests.Where(t => t.RuleId == ruleId);

    /// <summary>The generated part of the README, markers included, with <c>\n</c> line ends.</summary>
    public string Report()
    {
        var o = new StringBuilder();
        o.Append(ReportBegin).Append('\n');
        o.Append('\n');
        o.Append("| Chapter | Rules | Tested | Implemented | Partial | Missing | Deviation | Unclear |\n");
        o.Append("|---|---|---|---|---|---|---|---|\n");
        foreach (LedgerChapter c in Chapters)
        {
            o.Append(CultureInfo.InvariantCulture, $"| [{c.Title}]({c.File}) ({c.Prefix}) | {Row(c.Rules)} |\n");
        }

        o.Append(CultureInfo.InvariantCulture, $"| **All** | {Row([.. Chapters.SelectMany(c => c.Rules)])} |\n");
        Section(o, "Not done yet", "Missing and partial rules.", r => r.Status is RuleStatus.Missing or RuleStatus.Partial);
        Section(o, "Unclear", "The sources disagree or say too little.", r => r.Status == RuleStatus.Unclear);
        Section(o, "Deliberate differences", "Tzar2 does these differently on purpose.", r => r.Status == RuleStatus.Deviation);
        if (Data is { Files.Count: > 0 })
        {
            o.Append(Data.Report());
        }

        o.Append('\n').Append(ReportEnd).Append('\n');
        return o.ToString();

        static string Row(IReadOnlyCollection<LedgerRule> rules)
            => string.Join(" | ", new[] { rules.Count }.Concat(Enum.GetValues<RuleStatus>().Select(s => rules.Count(r => r.Status == s))));
    }

    /// <summary>Replaces the generated part of <paramref name="readme"/> with <paramref name="report"/> (appends it when absent).</summary>
    public static string WithReport(string readme, string report)
    {
        string text = readme.Replace("\r\n", "\n", StringComparison.Ordinal);
        int begin = text.IndexOf(ReportBegin, StringComparison.Ordinal);
        int end = text.IndexOf(ReportEnd, StringComparison.Ordinal);
        if (begin < 0 || end < begin)
        {
            return text.TrimEnd('\n') + "\n\n" + report;
        }

        int after = end + ReportEnd.Length;
        if (after < text.Length && text[after] == '\n')
        {
            after++;
        }

        return text[..begin] + report + text[after..];
    }

    /// <summary>Whether the README's generated part equals <see cref="Report"/>; adds an error when it does not.</summary>
    public bool CheckReport()
    {
        string readme = File.Exists(ReadmePath) ? File.ReadAllText(ReadmePath) : string.Empty;
        string current = readme.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (current == WithReport(current, Report()))
        {
            return true;
        }

        _errors.Add($"docs/rules/README.md: the report is out of date; run 't2 rules report'");
        return false;
    }

    private void Section(StringBuilder o, string title, string intro, Func<LedgerRule, bool> which)
    {
        if (!Chapters.Any(c => c.Rules.Any(which)))
        {
            return;
        }

        o.Append('\n').Append("## ").Append(title).Append('\n').Append('\n').Append(intro).Append('\n');
        foreach (LedgerChapter c in Chapters)
        {
            List<LedgerRule> rules = [.. c.Rules.Where(which)];
            if (rules.Count == 0)
            {
                continue;
            }

            o.Append('\n').Append("### ").Append(c.Title).Append('\n').Append('\n');
            foreach (LedgerRule r in rules)
            {
                o.Append(CultureInfo.InvariantCulture, $"* **{r.Id}**");
                if (r.Status is RuleStatus.Partial)
                {
                    o.Append(" (partial)");
                }

                o.Append(": ").Append(r.Text);
                if (r.Note.Length > 0)
                {
                    o.Append(" *").Append(r.Note).Append('*');
                }

                if (r.Adrs.Count > 0)
                {
                    o.Append(" (ADR ").Append(string.Join(", ", r.Adrs)).Append(')');
                }

                if (r.Work.Length > 0)
                {
                    o.Append(" Work: ").Append(r.Work).Append('.');
                }

                o.Append('\n');
            }
        }
    }

    // ---- chapters ---------------------------------------------------------------------------------------------------

    private void LoadChapters()
    {
        if (!Directory.Exists(RulesDirectory))
        {
            _errors.Add("docs/rules: the directory is missing");
            return;
        }

        var chapters = new List<LedgerChapter>();
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles(RulesDirectory, "*.yaml").OrderBy(p => p, StringComparer.Ordinal))
        {
            string file = Path.GetFileName(path);
            if (file == Path.GetFileName(DataUsage.RelativePath))
            {
                continue;
            }

            LedgerChapter? chapter = LoadChapter(path, file);
            if (chapter is null)
            {
                continue;
            }

            if (prefixes.TryGetValue(chapter.Prefix, out string? other))
            {
                _errors.Add($"docs/rules/{file}: prefix {chapter.Prefix} is also used by {other}");
                continue;
            }

            prefixes[chapter.Prefix] = file;
            chapters.Add(chapter);
        }

        Chapters = chapters;
    }

    private LedgerChapter? LoadChapter(string path, string file)
    {
        string where = "docs/rules/" + file;
        YamlMapping root;
        try
        {
            root = RulesYaml.AsMapping(RulesYaml.Parse(File.ReadAllText(path)), file);
        }
        catch (YamlException ex)
        {
            _errors.Add($"{where}: {ex.Message}");
            return null;
        }

        foreach (KeyValuePair<string, YamlNode> entry in root.Entries)
        {
            if (entry.Key is not ("schema" or "prefix" or "title" or "rules"))
            {
                _errors.Add($"{where}:{entry.Value.Line}: unknown key '{entry.Key}'");
            }
        }

        if (Scalar(root.Get("schema")) != Schema)
        {
            _errors.Add($"{where}: 'schema: {Schema}' is required");
            return null;
        }

        string prefix = Scalar(root.Get("prefix"));
        string title = Scalar(root.Get("title"));
        if (!PrefixPattern().IsMatch(prefix))
        {
            _errors.Add($"{where}: 'prefix' must be 2-6 capital letters");
            return null;
        }

        if (title.Length == 0)
        {
            _errors.Add($"{where}: 'title' is required");
        }

        var rules = new List<LedgerRule>();
        if (root.Get("rules") is not YamlMapping list)
        {
            _errors.Add($"{where}: 'rules' must be a mapping of rule ids");
            return new LedgerChapter(file, prefix, title, rules);
        }

        foreach (KeyValuePair<string, YamlNode> entry in list.Entries)
        {
            if (!Regex.IsMatch(entry.Key, "^" + prefix + @"-\d{3}$", RegexOptions.CultureInvariant))
            {
                _errors.Add($"{where}:{entry.Value.Line}: rule id '{entry.Key}' must be {prefix}-nnn");
                continue;
            }

            if (ReadRule(entry.Key, entry.Value, where) is { } rule)
            {
                rules.Add(rule);
            }
        }

        return new LedgerChapter(file, prefix, title, rules);
    }

    private LedgerRule? ReadRule(string id, YamlNode node, string where)
    {
        string at = $"{where}:{node.Line}: {id}";
        if (node is not YamlMapping m)
        {
            _errors.Add($"{at}: must be a mapping");
            return null;
        }

        foreach (KeyValuePair<string, YamlNode> entry in m.Entries)
        {
            if (!RuleKeys.Contains(entry.Key))
            {
                _errors.Add($"{at}: unknown key '{entry.Key}'");
            }
        }

        string text = Scalar(m.Get("rule"));
        if (text.Length == 0)
        {
            _errors.Add($"{at}: 'rule' is required");
        }

        List<string> sources = List(m.Get("source"));
        if (sources.Count == 0)
        {
            _errors.Add($"{at}: 'source' is required");
        }

        foreach (string source in sources)
        {
            string kind = source.Split(' ', 2)[0];
            if (!SourceKinds.Contains(kind))
            {
                _errors.Add($"{at}: source '{source}' must start with one of {string.Join(", ", SourceKinds)}");
            }
        }

        string statusText = Scalar(m.Get("status"));
        if (!TryStatus(statusText, out RuleStatus status))
        {
            _errors.Add($"{at}: status '{statusText}' must be one of {string.Join(", ", Enum.GetNames<RuleStatus>().Select(n => n.ToLowerInvariant()))}");
            return null;
        }

        List<string> adrs = List(m.Get("adr"));
        foreach (string adr in adrs)
        {
            if (!Regex.IsMatch(adr, @"^\d{4}$", RegexOptions.CultureInvariant))
            {
                _errors.Add($"{at}: adr '{adr}' must be a four-digit ADR number");
            }
            else if (!Directory.GetFiles(Path.Combine(Root, "docs", "decisions"), adr + "-*.md").Any())
            {
                _errors.Add($"{at}: ADR {adr} does not exist");
            }
        }

        string note = Scalar(m.Get("note"));
        if (note.Length == 0 && status is RuleStatus.Partial or RuleStatus.Deviation or RuleStatus.Unclear)
        {
            _errors.Add($"{at}: a {statusText} rule needs a 'note'");
        }

        return new LedgerRule(id, text, sources, status, adrs, note, Scalar(m.Get("work")), node.Line);
    }

    private static bool TryStatus(string text, out RuleStatus status)
    {
        foreach (RuleStatus s in Enum.GetValues<RuleStatus>())
        {
            if (text == s.ToString().ToLowerInvariant())
            {
                status = s;
                return true;
            }
        }

        status = default;
        return false;
    }

    private static string Scalar(YamlNode? node) => node is YamlScalar { IsNull: false } s ? s.Value.Trim() : string.Empty;

    private static List<string> List(YamlNode? node) => node switch
    {
        YamlSequence s => [.. s.Items.Select(Scalar).Where(v => v.Length > 0)],
        YamlScalar { IsNull: false } v when v.Value.Trim().Length > 0 => [v.Value.Trim()],
        _ => [],
    };

    // ---- tests ------------------------------------------------------------------------------------------------------

    private void ScanTests()
    {
        string testsDir = Path.Combine(Root, "tests");
        var found = new List<RuleTest>();
        if (!Directory.Exists(testsDir))
        {
            Tests = found;
            return;
        }

        foreach (string path in Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsBuildOutput(Path.GetRelativePath(testsDir, p)))
            .OrderBy(p => p, StringComparer.Ordinal))
        {
            string file = Path.GetRelativePath(Root, path).Replace('\\', '/');
            var pending = new List<(string Id, int Line)>();
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match match in TraitPattern().Matches(lines[i]))
                {
                    pending.Add((match.Groups[1].Value, i + 1));
                }

                if (pending.Count == 0)
                {
                    continue;
                }

                Match declaration = DeclarationPattern().Match(TraitPattern().Replace(lines[i], string.Empty));
                if (declaration.Success)
                {
                    foreach ((string id, _) in pending.DistinctBy(p => p.Id))
                    {
                        found.Add(new RuleTest(id, file, i + 1, declaration.Groups[1].Value));
                    }

                    pending.Clear();
                }
            }

            foreach ((string id, int line) in pending)
            {
                _errors.Add($"{file}:{line}: [Trait(\"Rule\", \"{id}\")] is not on a test method or class");
            }
        }

        Tests = found;
    }

    private static bool IsBuildOutput(string relative)
    {
        string[] parts = relative.Replace('\\', '/').Split('/');
        return parts.Any(p => p is "bin" or "obj");
    }

    private void CheckLinks()
    {
        var rules = Chapters.SelectMany(c => c.Rules.Select(r => (Chapter: c, Rule: r))).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach ((LedgerChapter chapter, LedgerRule rule) in rules)
        {
            if (!seen.Add(rule.Id))
            {
                _errors.Add($"docs/rules/{chapter.File}:{rule.Line}: {rule.Id} is listed twice");
            }
        }

        foreach (RuleTest test in Tests)
        {
            if (!seen.Contains(test.RuleId))
            {
                _errors.Add($"{test.File}:{test.Line}: {test.Member} names unknown rule {test.RuleId}");
            }
        }

        foreach ((LedgerChapter chapter, LedgerRule rule) in rules)
        {
            int count = TestsOf(rule.Id).Count();
            string at = $"docs/rules/{chapter.File}:{rule.Line}: {rule.Id}";
            if (rule.Status == RuleStatus.Tested && count == 0)
            {
                _errors.Add($"{at}: status 'tested' but no test carries [Trait(\"Rule\", \"{rule.Id}\")]");
            }
            else if (rule.Status == RuleStatus.Implemented && count > 0)
            {
                _errors.Add($"{at}: {count} test(s) name it; mark it 'tested'");
            }
            else if (rule.Status is RuleStatus.Missing or RuleStatus.Unclear && count > 0)
            {
                _errors.Add($"{at}: a {rule.Status.ToString().ToLowerInvariant()} rule cannot have tests");
            }
        }
    }

    [GeneratedRegex("^[A-Z]{2,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();

    [GeneratedRegex("""Trait\(\s*"Rule"\s*,\s*"([^"]*)"\s*\)""", RegexOptions.CultureInvariant)]
    private static partial Regex TraitPattern();

    [GeneratedRegex(@"\b(?:void|Task|class)\s+(\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex DeclarationPattern();
}
