using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tzar2.Tools.Cli.Fidelity.Analysis;
using Tzar2.Tools.Cli.Fidelity.Yaml;

namespace Tzar2.Tools.Cli.Fidelity.Scenarios;

/// <summary>Malformed session or scenario file; the message names the file and line.</summary>
internal sealed class ScenarioException(string message) : Exception(message);

/// <summary>Loads and validates <c>session.yaml</c> and scenario files (unknown keys are errors, to catch typos).</summary>
internal static partial class ScenarioLoader
{
    public const string SessionSchema = "tzar2-capture-session/1";
    public const string ScenarioSchema = "tzar2-capture-scenario/1";

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex VarPattern();

    public static SessionConfig LoadSession(string path) => ParseSession(File.ReadAllText(path), Path.GetFileName(path));

    public static SessionConfig ParseSession(string text, string fileName)
    {
        try
        {
            YamlMapping root = MiniYaml.AsMapping(MiniYaml.Parse(text), "session");
            CheckKeys(root, "session", "schema", "ini", "wrapper", "vars", "layout", "timing");
            RequireSchema(root, SessionSchema);
            YamlMapping layout = MiniYaml.AsMapping(Require(root, "layout"), "layout");
            CheckKeys(layout, "layout", "name", "size", "view", "minimap", "park");
            int[] size = Ints(Require(layout, "size"), "layout.size", 2);
            var gameLayout = new GameLayout(
                MiniYaml.AsString(Require(layout, "name"), "layout.name"),
                size[0],
                size[1],
                Rect(Require(layout, "view"), "layout.view"),
                Rect(Require(layout, "minimap"), "layout.minimap"),
                Point(Require(layout, "park"), "layout.park"));
            YamlMapping timing = root.Get("timing") is { } t ? MiniYaml.AsMapping(t, "timing") : new YamlMapping([]);
            CheckKeys(timing, "timing", "key_hold_ms", "step_gap_ms", "chat_open_ms", "snapshot_timeout_ms", "window_timeout_s", "load_s", "close_timeout_s");
            var sessionTiming = new SessionTiming(
                IntOr(timing, "key_hold_ms", 60),
                IntOr(timing, "step_gap_ms", 250),
                IntOr(timing, "chat_open_ms", 400),
                IntOr(timing, "snapshot_timeout_ms", 5000),
                IntOr(timing, "window_timeout_s", 90),
                IntOr(timing, "load_s", 25),
                IntOr(timing, "close_timeout_s", 8));
            return new SessionConfig(
                StringMap(root.Get("ini"), "ini", iniKeys: true),
                StringMap(root.Get("wrapper"), "wrapper", iniKeys: false),
                StringMap(root.Get("vars"), "vars", iniKeys: false),
                gameLayout,
                sessionTiming);
        }
        catch (YamlException ex)
        {
            throw new ScenarioException($"{fileName}: {ex.Message}");
        }
    }

    /// <summary>Loads every <c>*.yaml</c> scenario in a directory, ordered by file name.</summary>
    public static IReadOnlyList<ScenarioSpec> LoadDirectory(string directory) =>
        Directory.GetFiles(directory, "*.yaml")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Select(f => ParseScenario(File.ReadAllText(f), Path.GetFileName(f)))
            .ToList();

    public static ScenarioSpec ParseScenario(string text, string fileName)
    {
        try
        {
            return Parse(text, fileName);
        }
        catch (YamlException ex)
        {
            throw new ScenarioException($"{fileName}: {ex.Message}");
        }
    }

    /// <summary>Replaces <c>${name}</c> with the variable's value; unknown variables are an error.</summary>
    public static string Expand(string text, IReadOnlyDictionary<string, string> vars) =>
        VarPattern().Replace(text, m => vars.TryGetValue(m.Groups[1].Value, out string? v)
            ? v
            : throw new ScenarioException($"unknown variable '${{{m.Groups[1].Value}}}'"));

    /// <summary>The variables used in a text.</summary>
    public static IEnumerable<string> VariablesIn(string text) => VarPattern().Matches(text).Select(m => m.Groups[1].Value);

    private static ScenarioSpec Parse(string text, string fileName)
    {
        YamlMapping root = MiniYaml.AsMapping(MiniYaml.Parse(text), "scenario");
        CheckKeys(root, "scenario", "schema", "id", "title", "resolves", "priority", "minutes", "default", "experimental",
            "description", "ini", "wrapper", "launch", "load_s", "trials", "steps", "analysis");
        RequireSchema(root, ScenarioSchema);
        string id = MiniYaml.AsString(Require(root, "id"), "id");
        if (!IdPattern().IsMatch(id))
        {
            throw new YamlException($"id '{id}' must be lower-case letters, digits and '-'", Require(root, "id").Line);
        }

        var trials = new List<TrialSpec>();
        if (root.Get("trials") is { } trialNode)
        {
            foreach (YamlNode item in MiniYaml.AsSequence(trialNode, "trials").Items)
            {
                YamlMapping trial = MiniYaml.AsMapping(item, "trial");
                CheckKeys(trial, "trial", "name", "vars", "ini", "wrapper");
                string name = MiniYaml.AsString(Require(trial, "name"), "trial.name");
                if (!IdPattern().IsMatch(name) || trials.Exists(t => t.Name == name))
                {
                    throw new YamlException($"trial name '{name}' must be unique, lower-case letters, digits and '-'", trial.Line);
                }

                trials.Add(new TrialSpec(name, StringMap(trial.Get("vars"), "trial.vars", false), StringMap(trial.Get("ini"), "trial.ini", true),
                    StringMap(trial.Get("wrapper"), "trial.wrapper", false)));
            }
        }

        if (trials.Count == 0)
        {
            trials.Add(new TrialSpec("main", new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>()));
        }

        var steps = new List<StepSpec>();
        foreach (YamlNode item in MiniYaml.AsSequence(Require(root, "steps"), "steps").Items)
        {
            steps.Add(ParseStep(item));
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var scans = new HashSet<string>(StringComparer.Ordinal);
        foreach (StepSpec step in steps)
        {
            string? name = step switch
            {
                SnapshotStep s => s.Name,
                BurstStep b => b.Name,
                MemScanStep ms => ms.Name,
                MemSampleStep mp => mp.Name,
                _ => null,
            };
            if (name is not null && !IdPattern().IsMatch(name.Replace("${", "x", StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal)))
            {
                throw new YamlException($"capture name '{name}' must be lower-case letters, digits and '-'", step.Line);
            }

            if (name is not null && !names.Add(name))
            {
                throw new YamlException($"duplicate capture name '{name}'", step.Line);
            }

            if (step is MemScanStep { From: { } from } && !scans.Contains(from))
            {
                throw new YamlException($"memscan 'from: {from}' does not name an earlier memscan", step.Line);
            }

            if (step is MemSampleStep sample && !scans.Contains(sample.Scan))
            {
                throw new YamlException($"memsample 'scan: {sample.Scan}' does not name an earlier memscan", step.Line);
            }

            if (step is MemScanStep scan)
            {
                scans.Add(scan.Name);
            }
        }

        var analysis = new List<string>();
        if (root.Get("analysis") is { } analysisNode)
        {
            foreach (YamlNode item in MiniYaml.AsSequence(analysisNode, "analysis").Items)
            {
                string kind = MiniYaml.AsString(item, "analysis");
                if (!AnalysisCatalog.Names.Contains(kind))
                {
                    throw new YamlException($"unknown analysis '{kind}' (known: {string.Join(", ", AnalysisCatalog.Names)})", item.Line);
                }

                analysis.Add(kind);
            }
        }

        return new ScenarioSpec(
            id,
            MiniYaml.AsString(Require(root, "title"), "title"),
            root.Get("resolves") is { } r ? MiniYaml.AsSequence(r, "resolves").Items.Select(i => MiniYaml.AsString(i, "resolves")).ToList() : [],
            root.Get("priority") is { } p ? MiniYaml.AsInt(p, "priority") : 9,
            root.Get("minutes") is { } m ? MiniYaml.AsDouble(m, "minutes") : 1,
            root.Get("default") is not { } d || MiniYaml.AsBool(d, "default"),
            root.Get("experimental") is { } e && MiniYaml.AsBool(e, "experimental"),
            root.Get("description") is { } desc ? MiniYaml.AsString(desc, "description") : string.Empty,
            StringMap(root.Get("ini"), "ini", true),
            StringMap(root.Get("wrapper"), "wrapper", false),
            root.Get("launch") is { } l ? MiniYaml.AsSequence(l, "launch").Items.Select(i => MiniYaml.AsString(i, "launch")).ToList() : [],
            root.Get("load_s") is { } ls ? MiniYaml.AsInt(ls, "load_s") : null,
            trials,
            steps,
            analysis,
            fileName,
            text);
    }

    private static StepSpec ParseStep(YamlNode item)
    {
        YamlMapping map = MiniYaml.AsMapping(item, "step");
        if (map.Entries.Count != 1)
        {
            throw new YamlException("a step is a mapping with exactly one key (the step kind)", map.Line);
        }

        (string kind, YamlNode value) = (map.Entries[0].Key, map.Entries[0].Value);
        int line = map.Line;
        YamlMapping? args = value as YamlMapping;
        switch (kind)
        {
            case "wait":
                double seconds = MiniYaml.AsDouble(value, "wait");
                return seconds is >= 0 and <= 600 ? new WaitStep(line, seconds) : throw new YamlException("wait must be 0–600 s", line);
            case "key":
            {
                if (args is null)
                {
                    return new KeyStep(line, KeyName(value), false, false, false, 0, 1);
                }

                CheckKeys(args, "key", "key", "ctrl", "shift", "alt", "hold_ms", "repeat");
                return new KeyStep(line, KeyName(Require(args, "key")), BoolOr(args, "ctrl"), BoolOr(args, "shift"), BoolOr(args, "alt"),
                    Range(IntOr(args, "hold_ms", 0), 0, 30000, "hold_ms", line), Range(IntOr(args, "repeat", 1), 1, 100, "repeat", line));
            }

            case "text":
                return new TextStep(line, MiniYaml.AsString(value, "text"));
            case "chat":
                return new ChatStep(line, MiniYaml.AsString(value, "chat"));
            case "note":
                return new NoteStep(line, MiniYaml.AsString(value, "note"));
            case "move":
                return new MoveStep(line, Point(value, "move"));
            case "minimap":
            {
                YamlSequence uv = MiniYaml.AsSequence(value, "minimap");
                double u = uv.Items.Count == 2 ? MiniYaml.AsDouble(uv.Items[0], "minimap") : -1;
                double v = uv.Items.Count == 2 ? MiniYaml.AsDouble(uv.Items[1], "minimap") : -1;
                return u is >= 0 and <= 1 && v is >= 0 and <= 1
                    ? new MinimapStep(line, u, v)
                    : throw new YamlException("minimap takes [u, v] fractions between 0 and 1", line);
            }

            case "click":
            {
                if (args is null)
                {
                    return new ClickStep(line, Point(value, "click"), MouseButton.Left, false, false, false, false);
                }

                CheckKeys(args, "click", "at", "button", "double", "ctrl", "shift", "alt");
                return new ClickStep(line, Point(Require(args, "at"), "click.at"), Button(args.Get("button")), BoolOr(args, "double"),
                    BoolOr(args, "ctrl"), BoolOr(args, "shift"), BoolOr(args, "alt"));
            }

            case "drag":
            {
                YamlMapping a = MiniYaml.AsMapping(value, "drag");
                CheckKeys(a, "drag", "from", "to", "button");
                return new DragStep(line, Point(Require(a, "from"), "drag.from"), Point(Require(a, "to"), "drag.to"), Button(a.Get("button")));
            }

            case "snapshot":
            {
                if (args is null)
                {
                    return new SnapshotStep(line, MiniYaml.AsString(value, "snapshot"), true, true, new Dictionary<string, string>());
                }

                CheckKeys(args, "snapshot", "name", "game", "window", "tags");
                bool game = args.Get("game") is not { } g || MiniYaml.AsBool(g, "snapshot.game");
                bool window = args.Get("window") is not { } w || MiniYaml.AsBool(w, "snapshot.window");
                if (!game && !window)
                {
                    throw new YamlException("snapshot needs game and/or window", line);
                }

                return new SnapshotStep(line, MiniYaml.AsString(Require(args, "name"), "snapshot.name"), game, window,
                    StringMap(args.Get("tags"), "snapshot.tags", false));
            }

            case "burst":
            {
                YamlMapping a = MiniYaml.AsMapping(value, "burst");
                CheckKeys(a, "burst", "name", "count", "interval_ms", "region", "tags");
                return new BurstStep(line, MiniYaml.AsString(Require(a, "name"), "burst.name"),
                    Range(MiniYaml.AsInt(Require(a, "count"), "burst.count"), 2, 2000, "count", line),
                    Range(MiniYaml.AsInt(Require(a, "interval_ms"), "burst.interval_ms"), 10, 10000, "interval_ms", line),
                    a.Get("region") is { } region ? Rect(region, "burst.region") : null,
                    StringMap(a.Get("tags"), "burst.tags", false));
            }

            case "memscan":
            {
                YamlMapping a = MiniYaml.AsMapping(value, "memscan");
                CheckKeys(a, "memscan", "name", "value", "width", "from", "mode", "max_results");
                ScanMode mode = a.Get("mode") is { } modeNode
                    ? MiniYaml.AsString(modeNode, "memscan.mode") switch
                    {
                        "exact" => ScanMode.Exact,
                        "changed" => ScanMode.Changed,
                        "unchanged" => ScanMode.Unchanged,
                        "decreased" => ScanMode.Decreased,
                        "increased" => ScanMode.Increased,
                        { } other => throw new YamlException($"unknown memscan mode '{other}'", modeNode.Line),
                    }
                    : ScanMode.Exact;
                string? scanValue = a.Get("value") is { } v ? MiniYaml.AsString(v, "memscan.value") : null;
                if (scanValue is not null && !scanValue.Contains("${", StringComparison.Ordinal))
                {
                    MiniYaml.AsInt(a.Get("value")!, "memscan.value");
                }

                string? from = a.Get("from") is { } f ? MiniYaml.AsString(f, "memscan.from") : null;
                if (mode == ScanMode.Exact && scanValue is null)
                {
                    throw new YamlException("memscan mode 'exact' needs a value", line);
                }

                if (mode != ScanMode.Exact && from is null)
                {
                    throw new YamlException($"memscan mode '{mode.ToString().ToLowerInvariant()}' needs 'from'", line);
                }

                int width = IntOr(a, "width", 4);
                if (width is not (1 or 2 or 4))
                {
                    throw new YamlException("memscan width must be 1, 2 or 4 bytes", line);
                }

                return new MemScanStep(line, MiniYaml.AsString(Require(a, "name"), "memscan.name"), scanValue, width, from, mode,
                    Range(IntOr(a, "max_results", 200000), 1, 5000000, "max_results", line));
            }

            case "memsample":
            {
                YamlMapping a = MiniYaml.AsMapping(value, "memsample");
                CheckKeys(a, "memsample", "name", "scan", "max_addresses", "interval_ms", "duration_s");
                return new MemSampleStep(line, MiniYaml.AsString(Require(a, "name"), "memsample.name"), MiniYaml.AsString(Require(a, "scan"), "memsample.scan"),
                    Range(IntOr(a, "max_addresses", 256), 1, 65536, "max_addresses", line),
                    Range(IntOr(a, "interval_ms", 20), 1, 10000, "interval_ms", line),
                    a.Get("duration_s") is { } ds ? MiniYaml.AsDouble(ds, "memsample.duration_s") : 10);
            }

            default:
                throw new YamlException($"unknown step '{kind}' (known: wait, key, text, chat, note, move, minimap, click, drag, snapshot, burst, memscan, memsample)", line);
        }
    }

    private static string KeyName(YamlNode node)
    {
        string name = MiniYaml.AsString(node, "key");
        return KeyNames.TryGet(name, out _) ? name : throw new YamlException($"unknown key '{name}'", node.Line);
    }

    private static MouseButton Button(YamlNode? node) => node is null
        ? MouseButton.Left
        : MiniYaml.AsString(node, "button") switch
        {
            "left" => MouseButton.Left,
            "right" => MouseButton.Right,
            { } other => throw new YamlException($"button must be left or right, not '{other}'", node.Line),
        };

    private static void RequireSchema(YamlMapping root, string schema)
    {
        string actual = MiniYaml.AsString(Require(root, "schema"), "schema");
        if (actual != schema)
        {
            throw new YamlException($"schema '{actual}' is not '{schema}'", root.Get("schema")!.Line);
        }
    }

    private static YamlNode Require(YamlMapping map, string key) => map.Get(key) ?? throw new YamlException($"missing '{key}'", map.Line);

    private static void CheckKeys(YamlMapping map, string what, params string[] allowed)
    {
        foreach (KeyValuePair<string, YamlNode> entry in map.Entries)
        {
            if (Array.IndexOf(allowed, entry.Key) < 0)
            {
                throw new YamlException($"{what}: unknown key '{entry.Key}' (allowed: {string.Join(", ", allowed)})", entry.Value.Line > 0 ? entry.Value.Line : map.Line);
            }
        }
    }

    private static int IntOr(YamlMapping map, string key, int fallback) => map.Get(key) is { } n ? MiniYaml.AsInt(n, key) : fallback;

    private static bool BoolOr(YamlMapping map, string key) => map.Get(key) is { } n && MiniYaml.AsBool(n, key);

    private static int Range(int value, int min, int max, string what, int line) =>
        value >= min && value <= max ? value : throw new YamlException($"{what} must be {min}–{max}, not {value}", line);

    private static int[] Ints(YamlNode node, string what, int count)
    {
        YamlSequence seq = MiniYaml.AsSequence(node, what);
        if (seq.Items.Count != count)
        {
            throw new YamlException($"{what}: expected {count} integers", node.Line);
        }

        return seq.Items.Select(i => MiniYaml.AsInt(i, what)).ToArray();
    }

    private static ScreenPoint Point(YamlNode node, string what)
    {
        int[] v = Ints(node, what, 2);
        return v[0] is >= 0 and < 8192 && v[1] is >= 0 and < 8192 ? new ScreenPoint(v[0], v[1]) : throw new YamlException($"{what}: point out of range", node.Line);
    }

    private static ScreenRect Rect(YamlNode node, string what)
    {
        int[] v = Ints(node, what, 4);
        return v[0] >= 0 && v[1] >= 0 && v[2] > 0 && v[3] > 0 ? new ScreenRect(v[0], v[1], v[2], v[3]) : throw new YamlException($"{what}: invalid rectangle", node.Line);
    }

    private static Dictionary<string, string> StringMap(YamlNode? node, string what, bool iniKeys)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is null || node is YamlScalar { IsNull: true })
        {
            return result;
        }

        foreach (KeyValuePair<string, YamlNode> entry in MiniYaml.AsMapping(node, what).Entries)
        {
            if (iniKeys && (entry.Key.Split('.') is not [{ Length: > 0 }, { Length: > 0 }]))
            {
                throw new YamlException($"{what}: key '{entry.Key}' must be SECTION.KEY", entry.Value.Line);
            }

            result[entry.Key] = MiniYaml.AsString(entry.Value, $"{what}.{entry.Key}");
        }

        return result;
    }
}
