using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tzar2.Formats.Ini;
using Tzar2.Formats.Text;

namespace Tzar2.Tools.Cli.Rules;

/// <summary>How a file of the original's data is accounted for in <c>docs/rules/data-usage.yaml</c> (ADR 0181).</summary>
internal enum DataAccount
{
    /// <summary>Each key name (over all sections) is used, restated, ignored or to do.</summary>
    Keys,

    /// <summary>Each section name is used, restated, ignored or to do.</summary>
    Sections,

    /// <summary>Each column of a tab-separated table is.</summary>
    Columns,

    /// <summary>A reader takes the whole file as a table of names (cards, sounds, icons); nothing to account one by one.</summary>
    Whole,

    /// <summary>Nothing reads the file; the entry says why, or what will.</summary>
    Unused,
}

/// <summary>One file (or a glob of files) of the original's data and how Tzar2 uses it.</summary>
/// <param name="Path">The install-relative path, upper case; <c>*</c> matches within one path segment.</param>
/// <param name="Account">How it is accounted for.</param>
/// <param name="Reader">What reads it.</param>
/// <param name="Reason">Why a whole file is unused, or how a whole file is read.</param>
/// <param name="Used">Names read by the code (each must appear as a literal in <c>src</c> or <c>content</c>, except the
/// columns of tables, which are read by position).</param>
/// <param name="Restated">Names whose facts are typed into code or rules by hand, with where.</param>
/// <param name="Ignored">Names Tzar2 does not need, with why.</param>
/// <param name="Todo">Names Tzar2 should read, with the rule id or what is missing.</param>
/// <param name="Line">1-based line of the entry.</param>
internal sealed record DataFile(
    string Path,
    DataAccount Account,
    string Reader,
    string Reason,
    IReadOnlyList<string> Used,
    IReadOnlyDictionary<string, string> Restated,
    IReadOnlyDictionary<string, string> Ignored,
    IReadOnlyDictionary<string, string> Todo,
    int Line);

/// <summary>
/// The account of the original's data files (ADR 0181, decision 5): <c>docs/rules/data-usage.yaml</c>, checked against
/// the source on every run and, with an install, against every section, key and column of the reference build.
/// </summary>
internal sealed partial class DataUsage
{
    /// <summary>The schema line of the file.</summary>
    public const string Schema = "tzar2-data-usage/1";

    /// <summary>The ledger file, relative to the repository.</summary>
    public const string RelativePath = "docs/rules/data-usage.yaml";

    private static readonly string[] EntryKeys = ["path", "reader", "account", "reason", "used", "restated", "ignored", "todo"];

    private readonly List<string> _errors = [];

    private DataUsage(string root)
    {
        Root = root;
    }

    /// <summary>The repository root.</summary>
    public string Root { get; }

    /// <summary>The entries in file order.</summary>
    public IReadOnlyList<DataFile> Files { get; private set; } = [];

    /// <summary>Problems found, each with its place.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>
    /// Loads the ledger and checks it against the source: every used name must appear as a literal in the code or the
    /// rules, and every rule a to-do names must exist.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <param name="ruleIds">The ids of the rules ledger.</param>
    public static DataUsage Load(string root, IReadOnlySet<string> ruleIds)
    {
        var usage = new DataUsage(root);
        string path = System.IO.Path.Combine(root, RelativePath);
        if (!File.Exists(path))
        {
            return usage; // nothing accounted for yet; the repository's own check asserts that the file is there
        }

        usage.Read(File.ReadAllText(path));
        usage.CheckSource(ruleIds);
        return usage;
    }

    /// <summary>
    /// Checks the ledger against an install: every reference-build file of the original's data has an entry, and every
    /// name of a keys, sections or columns entry is accounted for; names the install does not have are stale.
    /// </summary>
    /// <param name="origin">The install.</param>
    /// <param name="files">The install-relative paths to check (the reference manifest's).</param>
    public void CheckInstall(string origin, IEnumerable<string> files)
    {
        var names = new Dictionary<DataFile, SortedSet<string>>();
        foreach (string relative in files.Select(f => f.Replace('\\', '/')).Where(IsDataFile).OrderBy(f => f, StringComparer.Ordinal))
        {
            List<DataFile> matches = [.. Files.Where(f => Matches(f.Path, relative))];
            if (matches.Count != 1)
            {
                _errors.Add(matches.Count == 0
                    ? $"{RelativePath}: {relative} has no entry"
                    : $"{RelativePath}: {relative} matches {matches.Count} entries ({string.Join(", ", matches.Select(m => m.Path))})");
                continue;
            }

            DataFile entry = matches[0];
            if (entry.Account is DataAccount.Whole or DataAccount.Unused)
            {
                continue;
            }

            if (!names.TryGetValue(entry, out SortedSet<string>? set))
            {
                names[entry] = set = new SortedSet<string>(StringComparer.Ordinal);
            }

            set.UnionWith(NamesIn(System.IO.Path.Combine(origin, relative), entry.Account));
        }

        foreach (DataFile entry in Files.Where(names.ContainsKey))
        {
            SortedSet<string> found = names[entry];
            var known = new HashSet<string>(entry.Used, StringComparer.Ordinal);
            known.UnionWith(entry.Restated.Keys);
            known.UnionWith(entry.Ignored.Keys);
            known.UnionWith(entry.Todo.Keys);
            foreach (string name in found.Where(n => !known.Contains(n)))
            {
                _errors.Add($"{RelativePath}:{entry.Line}: {entry.Path}: '{name}' is not accounted for (used, restated, ignored or todo)");
            }

            foreach (string name in known.Where(n => !found.Contains(n)).OrderBy(n => n, StringComparer.Ordinal))
            {
                _errors.Add($"{RelativePath}:{entry.Line}: {entry.Path}: '{name}' is not in the install");
            }
        }
    }

    /// <summary>The report's part on the data: totals and the to-do names.</summary>
    public string Report()
    {
        var o = new StringBuilder();
        int used = Files.Sum(f => f.Used.Count), restated = Files.Sum(f => f.Restated.Count), ignored = Files.Sum(f => f.Ignored.Count);
        int todo = Files.Sum(f => f.Todo.Count + (f.Account == DataAccount.Unused && f.Todo.Count == 0 && IsTodo(f.Reason) ? 1 : 0));
        o.Append("\n## The original's data\n\n");
        o.Append($"[data-usage.yaml](data-usage.yaml) accounts for {Files.Count} files or groups of files: ");
        o.Append($"{Files.Count(f => f.Account == DataAccount.Whole)} read whole, {Files.Count(f => f.Account == DataAccount.Unused)} not read, ");
        o.Append($"and of the names in the others {used} used, {restated} restated by hand, {ignored} not needed and {todo} to do.\n");
        if (Files.Any(f => f.Todo.Count > 0 || (f.Account == DataAccount.Unused && IsTodo(f.Reason))))
        {
            o.Append("\nTo do:\n\n");
            foreach (DataFile f in Files)
            {
                if (f.Account == DataAccount.Unused && IsTodo(f.Reason))
                {
                    o.Append($"* `{f.Path}` (the whole file): {f.Reason.Substring(5).TrimStart()}\n");
                }

                foreach ((string name, string why) in f.Todo)
                {
                    o.Append($"* `{f.Path}` `{name}`: {why}\n");
                }
            }
        }

        return o.ToString();
    }

    /// <summary>Whether an install-relative path is one of the data files the ledger accounts for.</summary>
    public static bool IsDataFile(string relative)
    {
        string upper = relative.ToUpperInvariant();
        if (!upper.Contains('/', StringComparison.Ordinal))
        {
            return false; // the install's root: Tzar.ini, the GOG and uninstaller files
        }

        return upper.EndsWith(".INI", StringComparison.Ordinal)
            || (upper.StartsWith("DATA/", StringComparison.Ordinal) && (upper.EndsWith(" UNITS.TXT", StringComparison.Ordinal) || upper.EndsWith(" BUILDINGS.TXT", StringComparison.Ordinal)));
    }

    private static bool IsTodo(string reason) => reason.StartsWith("todo:", StringComparison.Ordinal);

    private static bool Matches(string pattern, string relative)
        => FileSystemName.MatchesSimpleExpression(pattern, relative.ToUpperInvariant(), ignoreCase: false)
            && pattern.Count(c => c == '/') == relative.Count(c => c == '/');

    private static IEnumerable<string> NamesIn(string path, DataAccount account)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (account == DataAccount.Columns)
        {
            string first = LegacyText.Decode(bytes, CodePage.Windows1252).Split('\n')[0].TrimEnd('\r');
            return first.Split('\t').Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0);
        }

        IniDocument ini = IniDocument.Parse(bytes, CodePage.Windows1252);
        return account == DataAccount.Sections
            ? ini.Sections.Select(s => (s.Name ?? string.Empty).Trim().ToLowerInvariant()).Where(n => n.Length > 0)
            : ini.Sections.SelectMany(s => s.Entries).Select(e => e.Key?.Trim().ToLowerInvariant() ?? string.Empty).Where(n => n.Length > 0);
    }

    // ---- reading ----------------------------------------------------------------------------------------------------

    private void Read(string text)
    {
        YamlMapping root;
        try
        {
            root = RulesYaml.AsMapping(RulesYaml.Parse(text), RelativePath);
        }
        catch (YamlException ex)
        {
            _errors.Add($"{RelativePath}: {ex.Message}");
            return;
        }

        if (root.Get("schema") is not YamlScalar { Value: Schema })
        {
            _errors.Add($"{RelativePath}: 'schema: {Schema}' is required");
            return;
        }

        if (root.Get("files") is not YamlSequence files)
        {
            _errors.Add($"{RelativePath}: 'files' must be a list");
            return;
        }

        var list = new List<DataFile>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (YamlNode node in files.Items)
        {
            if (ReadEntry(node) is { } entry)
            {
                if (!paths.Add(entry.Path))
                {
                    _errors.Add($"{RelativePath}:{entry.Line}: {entry.Path} is listed twice");
                }

                list.Add(entry);
            }
        }

        Files = list;
    }

    private DataFile? ReadEntry(YamlNode node)
    {
        if (node is not YamlMapping m)
        {
            _errors.Add($"{RelativePath}:{node.Line}: an entry must be a mapping");
            return null;
        }

        string at = $"{RelativePath}:{m.Line}";
        foreach (KeyValuePair<string, YamlNode> e in m.Entries.Where(e => !EntryKeys.Contains(e.Key)))
        {
            _errors.Add($"{at}: unknown key '{e.Key}'");
        }

        string path = Scalar(m.Get("path"));
        if (path.Length == 0 || path != path.ToUpperInvariant())
        {
            _errors.Add($"{at}: 'path' is required, in upper case as in the install");
            return null;
        }

        string accountText = Scalar(m.Get("account"));
        DataAccount? account = Enum.GetValues<DataAccount>().Cast<DataAccount?>().FirstOrDefault(a => a.ToString()!.ToLowerInvariant() == accountText);
        if (account is null)
        {
            _errors.Add($"{at}: {path}: account '{accountText}' must be one of {string.Join(", ", Enum.GetNames<DataAccount>().Select(n => n.ToLowerInvariant()))}");
            return null;
        }

        var entry = new DataFile(path, account.Value, Scalar(m.Get("reader")), Scalar(m.Get("reason")), Names(m.Get("used"), at, path),
            Map(m.Get("restated"), at, path, "restated"), Map(m.Get("ignored"), at, path, "ignored"), Map(m.Get("todo"), at, path, "todo"), m.Line);
        bool named = entry.Used.Count + entry.Restated.Count + entry.Ignored.Count + entry.Todo.Count > 0;
        if (account is DataAccount.Whole or DataAccount.Unused)
        {
            if (named)
            {
                _errors.Add($"{at}: {path}: a {accountText} entry lists no names");
            }

            if (entry.Reason.Length == 0)
            {
                _errors.Add($"{at}: {path}: a {accountText} entry needs a 'reason'");
            }
        }
        else if (entry.Reader.Length == 0 && entry.Used.Count > 0)
        {
            _errors.Add($"{at}: {path}: an entry with used names needs a 'reader'");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in entry.Used.Concat(entry.Restated.Keys).Concat(entry.Ignored.Keys).Concat(entry.Todo.Keys))
        {
            if (!seen.Add(name))
            {
                _errors.Add($"{at}: {path}: '{name}' is listed twice");
            }
        }

        return entry;
    }

    private List<string> Names(YamlNode? node, string at, string path)
    {
        if (node is null)
        {
            return [];
        }

        if (node is not YamlSequence s)
        {
            _errors.Add($"{at}: {path}: 'used' must be a list");
            return [];
        }

        return [.. s.Items.Select(Scalar).Select(n => n.ToLowerInvariant())];
    }

    private SortedDictionary<string, string> Map(YamlNode? node, string at, string path, string what)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (node is null)
        {
            return map;
        }

        if (node is not YamlMapping m)
        {
            _errors.Add($"{at}: {path}: '{what}' must map names to reasons");
            return map;
        }

        foreach (KeyValuePair<string, YamlNode> e in m.Entries)
        {
            string why = Scalar(e.Value);
            if (why.Length == 0)
            {
                _errors.Add($"{at}: {path}: {what} '{e.Key}' needs a reason");
            }

            map[e.Key.ToLowerInvariant()] = why;
        }

        return map;
    }

    private static string Scalar(YamlNode? node) => node is YamlScalar { IsNull: false } s ? s.Value.Trim() : string.Empty;

    // ---- the source check -------------------------------------------------------------------------------------------

    private void CheckSource(IReadOnlySet<string> ruleIds)
    {
        string code = Corpus(System.IO.Path.Combine(Root, "src"), "*.cs");
        string rules = Corpus(System.IO.Path.Combine(Root, "content"), "*.yaml");
        foreach (DataFile f in Files)
        {
            // Tables are read by column position, so their headers are not quoted anywhere.
            foreach (string name in f.Account == DataAccount.Columns ? [] : f.Used)
            {
                if (!code.Contains('"' + name + '"', StringComparison.Ordinal) && !Regex.IsMatch(rules, @"(?<![\w])" + Regex.Escape(name) + @"(?![\w])", RegexOptions.CultureInvariant))
                {
                    _errors.Add($"{RelativePath}:{f.Line}: {f.Path}: '{name}' is listed as used but no code quotes it");
                }
            }

            foreach ((string name, string why) in f.Todo.Append(new KeyValuePair<string, string>(string.Empty, f.Reason)))
            {
                foreach (Match id in RuleId().Matches(why))
                {
                    if (!ruleIds.Contains(id.Value))
                    {
                        _errors.Add($"{RelativePath}:{f.Line}: {f.Path}: {(name.Length > 0 ? $"'{name}' names" : "the reason names")} unknown rule {id.Value}");
                    }
                }
            }
        }
    }

    private static string Corpus(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (string file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            .Where(p => !p.Replace('\\', '/').Split('/').Any(part => part is "bin" or "obj"))
            .OrderBy(p => p, StringComparer.Ordinal))
        {
            text.Append(File.ReadAllText(file).ToLowerInvariant()).Append('\n');
        }

        return text.ToString();
    }

    [GeneratedRegex(@"\b[A-Z]{2,6}-\d{3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex RuleId();
}
